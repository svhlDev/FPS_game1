# Procedural "sausage body" generator for Blender (4.x / 5.x)
# Run: Scripting tab > Text Editor > New > paste > Run Script (Alt+P)
#
# How it works:
# 1. Core layer: a skeleton graph (points + lines) is wrapped by the Skin modifier
#    and rounded by Subdivision Surface. This is the "bone + base flesh" tube body.
# 2. Muscle layer: a table of muscles, each a tapered spindle running from an
#    origin to an insertion point on the skeleton. Every muscle is seated on the
#    LEAN (fat = 0) core surface and mostly buried, so it reads as a swell under
#    the skin. Fat grows the core over the muscles and hides them.
# 3. Fusion: core + muscles are joined and Voxel Remeshed into one surface (a
#    volume union). The seams are then smoothed so muscles blend in. Metaballs
#    were not used: they cannot union with the Skin mesh, and turning the core
#    into metaballs inflates every joint and melts the fingers together.
# 4. Fat layer: in fat regions (belly, hips, butt, thighs, chest) the surface is
#    inflated and smoothed in proportion to fat, which buries muscle shapes.
# 5. Final mesh: the default body is Quadriflow remeshed to a clean quad mesh.
#    That is the Basis.
# 6. Shape keys: each variant (Fat, Muscle, Skinny, STR) builds its own volume.
#    The Basis vertices are first moved along with the variant skeleton (so
#    longer STR limbs are followed), then Shrinkwrapped to the nearest point on
#    the variant surface. Topology stays identical, so the keys work as sliders
#    and export to Unity as blendshapes.

import bpy
import bmesh
import math
import os
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import intersect_point_line

# ---------------------------------------------------------------------------
# Parameters
# ---------------------------------------------------------------------------

NAME = "SausageBody"          # generated objects/meshes all start with this

# Body shape of the Basis. 0.3 / 0.3 = house style "pretty thick".
DEFAULT = dict(fat=0.3, muscle=0.3, strength=0.0)
# Shape key variants. Each is a full parameter set, morphed to from DEFAULT.
VARIANTS = {
    "Fat": dict(fat=1.0, muscle=0.3, strength=0.0),
    "Muscle": dict(fat=0.3, muscle=1.0, strength=0.0),
    "Skinny": dict(fat=0.0, muscle=0.0, strength=0.0),
    "STR": dict(fat=0.3, muscle=0.3, strength=1.0),
}

# Core layer
SUBDIV_LEVELS = 2             # Subdivision Surface levels on the Skin tubes

# Hands. 1.0 = the old comically small hand. House style stays slightly small for the body.
HAND_SCALE = 1.6
FINGER_LENGTH = 1.0           # multiplier on finger + thumb length
FINGER_THICKNESS = 1.0        # multiplier on finger + thumb radius

# Muscle layer
MUSCLE_BASE = 0.45            # muscle size factor at muscle = 0
MUSCLE_GAIN = 0.9             # added size factor per unit of muscle (STR adds 0.6 muscle)
MUSCLE_PROTRUDE = 0.45        # fraction of a muscle's depth that sits outside the lean core
MUSCLE_RINGS = 16             # spindle resolution along its length
MUSCLE_SIDES = 16             # spindle resolution around

# Fusion
VOXEL_SIZE = 0.004            # metres; voxel remesh resolution (fingers are ~27 mm thick)
BASE_SMOOTH_ITERS = 2         # light volume-preserving smooth everywhere (removes voxel steps)
SEAM_WIDTH = 0.015            # metres; how far from a core/muscle seam the blend reaches
SEAM_SMOOTH_ITERS = 10        # Laplacian passes on the seams (fills the crease like a fillet)
CREASE_SMOOTH_ITERS = 20      # Laplacian passes on sharp concave creases (crotch, armpits)
CREASE_THRESHOLD = 0.05       # concavity (umbrella offset / edge length) where crease smoothing starts
CREASE_FULL = 0.25            # concavity that gets full smoothing
HAND_GUARD = 0.17             # metres from the palm where crease smoothing is off (keeps finger gaps)

# Fat layer
FAT_INFLATE = 0.03            # metres of extra inflation at fat = 1 in a full-weight region
FAT_SMOOTH_ITERS = 60         # Laplacian passes at fat = 1 (scaled by fat and region weight)

# Final mesh
TARGET_FACES = 15000          # Quadriflow target; it lands ~20% under (about 12k)
# The shape is mirror-exact (the core is symmetrized) but the quad layout is not:
# Quadriflow's symmetry mode leaves an unwelded, non-manifold seam at the crotch.
PRE_DECIMATE = 0.25           # collapse ratio before Quadriflow; removes the near-zero edges
                              # Quadriflow rejects, and makes it ~5x faster
BASIS_RELAX_ITERS = 8         # smooth + snap back to the surface; evens out sliver quads in folds
OFFSET_SMOOTH_ITERS = 3       # smoothing of the Shrinkwrap offsets (removes projection jitter;
                              # more blurs the valleys between muscles)
# Fold cleanup: nearest-point projection piles vertices up in concave folds
# (crotch, armpits). Faces that flip or edges that collapse get their key offset
# smoothed in a small patch until the key is clean.
FIX_ROUNDS = 30               # max cleanup rounds per key
FIX_RINGS = 2                 # patch size around a bad face, in edge rings
FIX_SMOOTH_ITERS = 10         # offset smoothing passes per round
MIN_EDGE_RATIO = 0.25         # key edge shorter than this x its Basis length = collapsed
SPIKE_FACTOR = 3.0            # vertex sticks out more than this x its Basis bumpiness...
SPIKE_MIN = 0.002             # ...plus this many metres = spike

# Skeleton binding used to carry Basis vertices onto a variant skeleton
BIND_SOFTNESS = 0.015         # metres; blend width between neighbouring bones at joints
BIND_MAX_EDGES = 6

# Previews
RENDER_PREVIEWS = True
PREVIEW_DIR = ""              # empty: "sausage_previews" next to the .blend (temp dir if unsaved)
PREVIEW_STATES = ["Basis", "Fat", "Muscle", "Skinny"]

# Muscle table. One row per muscle, mirrored to both sides (offset x is flipped).
#   origin / insertion: (landmark, (dx, dy, dz)). The landmark is a skeleton
#     point name, or ("a", "b", t) for a point t of the way from a to b. The
#     offset is in metres (scaled with STR height). +X = body's right, -Y = front.
#   belly: where the muscle is thickest, 0 = origin, 1 = insertion.
#   width, depth: full size of the belly across and out of the body, in metres.
#   strength: per-muscle size weight on top of the global muscle factor.
# Landmarks: pelvis, belly, chest, neck, h1 (head base), and per side
#   sh (shoulder), el (elbow), wr (wrist), hip, kn (knee), an (ankle).
MUSCLES = [
    # name               origin                                   insertion                                 belly width  depth  strength
    ("neck_side",        ("h1", (0.05, 0.02, -0.04)),             ("neck", (0.025, -0.06, -0.04)),          0.5,  0.035, 0.03,  0.8),
    ("trapezius",        ("neck", (0.0, 0.05, 0.04)),             ("sh", (-0.05, 0.03, 0.07)),              0.4,  0.09,  0.05,  1.0),
    ("deltoid_front",    ("sh", (-0.03, -0.07, 0.04)),            (("sh", "el", 0.45), (0.0, -0.04, 0.0)),  0.4,  0.06,  0.045, 1.0),
    ("deltoid_side",     ("sh", (0.03, 0.0, 0.07)),               (("sh", "el", 0.45), (0.045, 0.0, 0.01)), 0.4,  0.065, 0.045, 1.0),
    ("deltoid_rear",     ("sh", (-0.03, 0.07, 0.04)),             (("sh", "el", 0.45), (0.0, 0.04, 0.0)),   0.4,  0.06,  0.04,  0.9),
    ("pectoral",         ("chest", (0.03, -0.18, 0.05)),          (("sh", "el", 0.2), (-0.03, -0.05, -0.01)), 0.45, 0.14, 0.05, 1.0),
    ("lat",              ("belly", (0.07, 0.17, 0.0)),            (("sh", "el", 0.15), (-0.04, 0.04, -0.03)), 0.55, 0.13, 0.04, 1.0),
    ("abs_upper",        ("belly", (0.035, -0.19, 0.145)),        ("belly", (0.035, -0.19, 0.075)),         0.5,  0.055, 0.022, 1.0),
    ("abs_mid",          ("belly", (0.035, -0.19, 0.065)),        ("belly", (0.035, -0.19, -0.005)),        0.5,  0.055, 0.022, 1.0),
    ("abs_lower",        ("belly", (0.035, -0.19, -0.015)),       ("belly", (0.035, -0.19, -0.085)),        0.5,  0.055, 0.022, 1.0),
    ("oblique",          ("belly", (0.15, -0.09, 0.12)),          ("pelvis", (0.13, -0.09, 0.06)),          0.5,  0.07,  0.03,  1.0),
    ("biceps",           (("sh", "el", 0.25), (0.0, -0.065, 0.0)), ("el", (0.0, -0.055, 0.01)),             0.55, 0.05,  0.045, 1.0),
    ("triceps",          (("sh", "el", 0.15), (0.0, 0.065, 0.0)),  ("el", (0.0, 0.055, 0.03)),              0.45, 0.055, 0.045, 1.0),
    ("forearm_flexor",   ("el", (-0.04, -0.02, -0.02)),           (("el", "wr", 0.75), (-0.03, -0.01, -0.015)), 0.3, 0.045, 0.03, 1.0),
    ("forearm_extensor", ("el", (0.04, 0.01, 0.02)),              (("el", "wr", 0.75), (0.03, 0.01, 0.015)), 0.3,  0.045, 0.03,  1.0),
    ("glute",            ("pelvis", (0.06, 0.17, 0.06)),          (("hip", "kn", 0.15), (0.08, 0.06, 0.0)), 0.45, 0.15,  0.07,  1.0),
    ("quad_outer",       (("hip", "kn", 0.1), (0.11, -0.04, 0.0)), ("kn", (0.04, -0.07, 0.04)),             0.45, 0.08,  0.055, 1.0),
    ("quad_inner",       (("hip", "kn", 0.45), (-0.07, -0.06, 0.0)), ("kn", (-0.03, -0.07, 0.03)),          0.65, 0.07,  0.05,  1.0),
    ("hamstring",        (("hip", "kn", 0.05), (0.0, 0.10, 0.0)), ("kn", (0.0, 0.07, 0.03)),                0.5,  0.09,  0.05,  1.0),
    ("calf_medial",      ("kn", (-0.03, 0.06, -0.03)),            (("kn", "an", 0.55), (-0.01, 0.05, 0.0)), 0.3,  0.05,  0.045, 1.0),
    ("calf_lateral",     ("kn", (0.03, 0.06, -0.03)),             (("kn", "an", 0.5), (0.01, 0.05, 0.0)),   0.3,  0.045, 0.04,  1.0),
]

# Fat regions: (point, radius, weight). Point uses the same landmark format as
# MUSCLES. Rows with dx != 0 are mirrored. Weight 1 = full fat inflate + smooth.
FAT_REGIONS = [
    (("belly", (0.0, -0.16, -0.03)), 0.15, 1.0),         # belly
    (("pelvis", (0.0, -0.15, 0.06)), 0.12, 0.8),         # lower belly
    (("pelvis", (0.16, 0.0, 0.02)), 0.12, 0.7),          # hips
    (("pelvis", (0.09, 0.15, -0.06)), 0.12, 0.9),        # butt
    ((("hip", "kn", 0.35), (0.02, 0.0, 0.0)), 0.13, 0.6),  # thighs
    (("chest", (0.08, -0.15, 0.02)), 0.10, 0.6),         # chest
]


# ---------------------------------------------------------------------------
# Core layer: skeleton + Skin modifier
# ---------------------------------------------------------------------------

class Skeleton:
    """Skin graph. radii are (rx, ry) in the skin node's frame. marks maps
    landmark names (center) or (name, side) to vertex indices."""

    def __init__(self, scale):
        self.verts, self.edges, self.radii, self.marks = [], [], [], {}
        self.scale = scale

    def add(self, p, r, parent=None):
        self.verts.append(Vector(p))
        self.radii.append(r if isinstance(r, tuple) else (r, r))
        i = len(self.verts) - 1
        if parent is not None:
            self.edges.append((parent, i))
        return i

    def mark(self, name, side):
        return self.marks[name] if name in self.marks else self.marks[(name, side)]

    def point(self, spec, side):
        """Resolve a (landmark, offset) spec for one side (+1 right, -1 left)."""
        ref, (dx, dy, dz) = spec
        if isinstance(ref, str):
            p = self.verts[self.mark(ref, side)]
        else:
            a, b, t = ref
            p = self.verts[self.mark(a, side)].lerp(self.verts[self.mark(b, side)], t)
        return p + Vector((dx * side, dy, dz)) * self.scale


def build_points(fat=0.3, muscle=0.3, strength=0.0, hand_scale=HAND_SCALE,
                 finger_length=FINGER_LENGTH, finger_thickness=FINGER_THICKNESS):
    """Return the Skeleton for one body. A float radius means round."""
    L = 1.0 + 0.12 * strength          # STR: longer limbs, taller
    M = muscle + 0.6 * strength        # STR: more muscle
    sk = Skeleton(L)
    add = sk.add

    def z(h):  # vertical positions scale with STR
        return h * L

    # Torso (center line). Pelvis is the skin "root".
    pelvis = add((0, 0, z(0.95)), 0.17 + 0.07 * fat)
    belly = add((0, 0, z(1.10)), 0.16 + 0.10 * fat + 0.01 * M, pelvis)
    chest = add((0, 0, z(1.30)), 0.17 + 0.04 * fat + 0.05 * M, belly)
    neck = add((0, 0, z(1.47)), 0.06 + 0.01 * fat + 0.02 * M, chest)
    # Egg head: chain of shrinking spheres
    h1 = add((0, 0, z(1.58)), 0.105, neck)
    h2 = add((0, 0, z(1.70)), 0.10, h1)
    add((0, 0, z(1.78)), 0.055, h2)
    sk.marks.update(pelvis=pelvis, belly=belly, chest=chest, neck=neck, h1=h1)

    for s in (1, -1):  # left / right
        # Arm (A-pose)
        sh = add((s * 0.21 * L, 0, z(1.38)), 0.075 + 0.02 * fat + 0.04 * M, chest)
        el = add((s * 0.36 * L, 0, z(1.12)), 0.06 + 0.02 * fat + 0.03 * M, sh)
        wr = add((s * 0.47 * L, 0, z(0.92)), 0.04 + 0.01 * fat + 0.01 * M, el)

        # Hand: flat palm block + 4 two-segment fingers + two-segment thumb.
        # Frame: d = down the forearm, w = palm width (front/back), n = palm normal (toward body).
        # Skin radius (rx, ry) on these nodes maps rx -> w, ry -> n.
        H = hand_scale
        d = (sk.verts[wr] - sk.verts[el]).normalized()
        w = Vector((0, 1, 0))
        n = d.cross(w) * -s
        W = sk.verts[wr].copy()

        def hp(down, width=0.0, normal=0.0):
            return W + H * (d * down + w * width + n * normal)

        fr = 0.0085 * H * finger_thickness
        palm_base = add(hp(0.022), (0.026 * H, 0.014 * H), wr)
        knuckles = add(hp(0.052), (0.030 * H, 0.013 * H), palm_base)
        # Index (front, -Y) to pinky (back, +Y); middle finger longest
        for width, length in ((-0.024, 0.040), (-0.008, 0.045), (0.008, 0.042), (0.024, 0.034)):
            seg = 0.5 * length * H * finger_length
            root = hp(0.060, width)
            spread = (d + w * width * 2.5).normalized()      # slight fan
            curl = (spread + n * 0.15).normalized()          # relaxed tip curl
            f0 = add(root, fr, knuckles)
            f1 = add(root + spread * seg, fr * 0.95, f0)    # knuckle joint
            add(root + spread * seg + curl * seg, fr * 0.85, f1)
        # Thumb: side of palm near wrist, angled forward (-Y) and inward (n)
        seg = 0.021 * H * finger_length
        root = hp(0.014, -0.020, 0.004)
        t_dir = (d * 0.65 - w * 0.55 + n * 0.5).normalized()
        t_tip = (d * 0.6 - w * 0.35 + n * 0.75).normalized()
        t0 = add(root, fr * 1.15, palm_base)
        t1 = add(root + t_dir * seg, fr * 1.05, t0)
        add(root + t_dir * seg + t_tip * seg * 0.95, fr * 0.9, t1)

        # Leg
        hip = add((s * 0.10, 0, z(0.88)), 0.11 + 0.07 * fat + 0.03 * M, pelvis)
        kn = add((s * 0.11, 0, z(0.50)), 0.075 + 0.03 * fat + 0.02 * M, hip)
        an = add((s * 0.11, 0, z(0.10)), 0.05 + 0.01 * fat, kn)
        add((s * 0.11, -0.08, 0.04), 0.035, an)  # tiny foot, stays on ground
        sk.marks.update({("sh", s): sh, ("el", s): el, ("wr", s): wr,
                         ("hip", s): hip, ("kn", s): kn, ("an", s): an})

    return sk


def link_temp(name, mesh):
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    return obj


def remove_temp(obj, keep_mesh=False):
    me = obj.data
    bpy.data.objects.remove(obj, do_unlink=True)
    if not keep_mesh and me.users == 0:
        bpy.data.meshes.remove(me)


def bake(obj, name):
    """Evaluate obj's modifiers into a new mesh, then delete obj and its mesh."""
    dg = bpy.context.evaluated_depsgraph_get()
    baked = bpy.data.meshes.new_from_object(obj.evaluated_get(dg))
    baked.name = name
    remove_temp(obj)
    return baked


def make_skin_mesh(sk, name):
    me = bpy.data.meshes.new(name + "_skel")
    me.from_pydata([v[:] for v in sk.verts], sk.edges, [])
    obj = link_temp(name, me)

    obj.modifiers.new("Skin", "SKIN")
    sv = me.skin_vertices[0].data
    for i, r in enumerate(sk.radii):
        sv[i].radius = r
    sv[0].use_root = True

    sub = obj.modifiers.new("Subdiv", "SUBSURF")
    sub.levels = SUBDIV_LEVELS
    sub.render_levels = SUBDIV_LEVELS
    return symmetrize(bake(obj, name))


def symmetrize(me):
    """Skin modifier branch hulls are not mirror-exact (sides differ by up to ~4 cm).
    Keep the +X half and mirror it so both sides match."""
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], dist=1e-6,
                           plane_co=(0, 0, 0), plane_no=(1, 0, 0), clear_inner=True)
    bmesh.ops.mirror(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], axis="X", merge_dist=1e-5)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    bm.to_mesh(me)
    bm.free()
    return me


# ---------------------------------------------------------------------------
# Muscle layer
# ---------------------------------------------------------------------------

def spindle(O, I, belly, half_w, half_d, out, verts, faces):
    """Append a closed tapered spindle from O to I, thickest at belly, with its
    depth axis facing `out`."""
    axis = I - O
    ax = axis.normalized()
    nd = (out - ax * out.dot(ax)).normalized()
    wd = ax.cross(nd)
    base = len(verts)
    verts.append(O[:])
    for i in range(1, MUSCLE_RINGS):
        u = i / MUSCLE_RINGS
        g = 0.5 * u / belly if u < belly else 0.5 + 0.5 * (u - belly) / (1.0 - belly)
        r = math.sin(math.pi * g) ** 0.7
        c = O + axis * u
        for j in range(MUSCLE_SIDES):
            a = 2.0 * math.pi * j / MUSCLE_SIDES
            verts.append((c + wd * (half_w * r * math.cos(a)) + nd * (half_d * r * math.sin(a)))[:])
    verts.append(I[:])
    tip = len(verts) - 1
    S = MUSCLE_SIDES

    def ring(i, j):
        return base + 1 + i * S + j % S

    for j in range(S):
        faces.append((base, ring(0, j + 1), ring(0, j)))
        faces.append((tip, ring(MUSCLE_RINGS - 2, j), ring(MUSCLE_RINGS - 2, j + 1)))
        for i in range(MUSCLE_RINGS - 2):
            faces.append((ring(i, j), ring(i, j + 1), ring(i + 1, j + 1), ring(i + 1, j)))


def muscle_factor(muscle, strength):
    return MUSCLE_BASE + MUSCLE_GAIN * (muscle + 0.6 * strength)


def make_muscle_mesh(sk, lean_core, muscle, strength, name):
    """All muscles as one mesh, each seated on the lean core surface."""
    bvh = BVHTree.FromPolygons([v.co[:] for v in lean_core.vertices],
                               [p.vertices[:] for p in lean_core.polygons])
    k = muscle_factor(muscle, strength)
    verts, faces = [], []
    for _name, o_spec, i_spec, belly, width, depth, weight in MUSCLES:
        hw, hd = 0.5 * width * k * weight, 0.5 * depth * k * weight
        for s in (1, -1):
            O, I = sk.point(o_spec, s), sk.point(i_spec, s)
            B = O.lerp(I, belly)
            loc, nrm, _, _ = bvh.find_nearest(B)
            if nrm.dot(loc - nearest_bone_point(sk, loc)) < 0:
                nrm = -nrm
            # Outer surface of the belly ends up MUSCLE_PROTRUDE * depth outside the core
            delta = loc + nrm * (2.0 * hd * MUSCLE_PROTRUDE - hd) - B
            spindle(O + delta, I + delta, belly, hw, hd, nrm, verts, faces)
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    return me


def nearest_bone_point(sk, p):
    best, best_d = None, 1e9
    for a, b in sk.edges:
        A, B = sk.verts[a], sk.verts[b]
        _, t = intersect_point_line(p, A, B)
        q = A.lerp(B, min(max(t, 0.0), 1.0))
        dist = (p - q).length
        if dist < best_d:
            best, best_d = q, dist
    return best


# ---------------------------------------------------------------------------
# Mesh helpers (numpy)
# ---------------------------------------------------------------------------

def get_co(me):
    co = np.empty(len(me.vertices) * 3)
    me.vertices.foreach_get("co", co)
    return co.reshape(-1, 3)


def set_co(me, co):
    me.vertices.foreach_set("co", co.ravel())
    me.update()


def get_edges(me):
    ed = np.empty(len(me.edges) * 2, dtype=np.int64)
    me.edges.foreach_get("vertices", ed)
    return ed.reshape(-1, 2)


def get_vertex_normals(me):
    nr = np.empty(len(me.vertices) * 3)
    me.vertices.foreach_get("normal", nr)
    return nr.reshape(-1, 3)


def make_laplacian(ed, n):
    """Return lap(co) = neighbour average minus co (umbrella operator)."""
    i, j = ed[:, 0], ed[:, 1]
    cnt = np.maximum(np.bincount(i, minlength=n) + np.bincount(j, minlength=n), 1)[:, None]

    def lap(co):
        acc = np.empty_like(co)
        for k in range(3):
            acc[:, k] = np.bincount(i, co[j, k], n) + np.bincount(j, co[i, k], n)
        return acc / cnt - co
    return lap


def laplacian_smooth(co, lap, weight, iters, lam=0.5):
    w = (lam * np.asarray(weight, dtype=float) * np.ones(len(co)))[:, None]
    for _ in range(iters):
        co = co + w * lap(co)
    return co


def taubin_smooth(co, lap, iters, lam=0.5, mu=-0.53):
    for _ in range(iters):
        co = co + lam * lap(co)
        co = co + mu * lap(co)
    return co


def nearest_distances(co, me):
    bvh = BVHTree.FromPolygons([v.co[:] for v in me.vertices], [p.vertices[:] for p in me.polygons])
    return np.array([bvh.find_nearest(Vector(p))[3] for p in co])


def fat_weights(co, sk):
    w = np.zeros(len(co))
    for spec, radius, weight in FAT_REGIONS:
        for s in ((1, -1) if spec[1][0] != 0 else (1,)):
            c = np.array(sk.point(spec, s))
            w += weight * np.exp(-np.sum((co - c) ** 2, axis=1) / radius ** 2)
    return np.minimum(w, 1.0)


# ---------------------------------------------------------------------------
# Fusion + fat: one body volume
# ---------------------------------------------------------------------------

def build_volume(params, tag):
    """Core + muscles, voxel union, seam smoothing, fat. Returns (mesh, skeleton)."""
    sk = build_points(**params)
    core = make_skin_mesh(sk, f"{NAME}_tmp_core_{tag}")
    lean = make_skin_mesh(build_points(**dict(params, fat=0.0)), f"{NAME}_tmp_lean_{tag}")
    muscles = make_muscle_mesh(sk, lean, params["muscle"], params["strength"], f"{NAME}_tmp_mus_{tag}")
    bpy.data.meshes.remove(lean)

    # Join core + muscles into one mesh, then voxel remesh = volume union
    verts = [v.co[:] for v in core.vertices] + [v.co[:] for v in muscles.vertices]
    off = len(core.vertices)
    faces = [p.vertices[:] for p in core.polygons] + \
            [tuple(i + off for i in p.vertices) for p in muscles.polygons]
    joined = bpy.data.meshes.new(f"{NAME}_tmp_join_{tag}")
    joined.from_pydata(verts, [], faces)
    obj = link_temp(f"{NAME}_tmp_join_{tag}", joined)
    rm = obj.modifiers.new("Remesh", "REMESH")
    rm.mode = "VOXEL"
    rm.voxel_size = VOXEL_SIZE
    rm.adaptivity = 0.0
    vol = bake(obj, f"{NAME}_tmp_vol_{tag}")

    co = get_co(vol)
    lap = make_laplacian(get_edges(vol), len(co))
    co = taubin_smooth(co, lap, BASE_SMOOTH_ITERS)

    # Seams: where the surface is close to both the core and a muscle
    d_core = nearest_distances(co, core)
    d_mus = nearest_distances(co, muscles)
    seam = np.exp(-(d_core ** 2 + d_mus ** 2) / SEAM_WIDTH ** 2)
    co = laplacian_smooth(co, lap, seam, SEAM_SMOOTH_ITERS)
    bpy.data.meshes.remove(core)
    bpy.data.meshes.remove(muscles)

    # Concave creases: where limbs overlap the union leaves a sharp V (crotch,
    # armpits). Fillet them, except at the hands.
    ed = get_edges(vol)
    h = np.mean(np.linalg.norm(co[ed[:, 0]] - co[ed[:, 1]], axis=1))
    hand = np.zeros(len(co), dtype=bool)
    for s in (1, -1):
        palm = np.array(sk.point(("wr", (0.0, 0.0, 0.0)), s))
        hand |= np.linalg.norm(co - palm, axis=1) < HAND_GUARD
    for _ in range(4):  # re-measure as creases open up
        set_co(vol, co)
        concave = np.sum(lap(co) * get_vertex_normals(vol), axis=1) / h
        crease = np.clip((concave - CREASE_THRESHOLD) / (CREASE_FULL - CREASE_THRESHOLD), 0.0, 1.0)
        crease[hand] = 0.0
        co = laplacian_smooth(co, lap, crease, CREASE_SMOOTH_ITERS // 4)

    # Fat: inflate, then smooth so muscle shapes get buried
    fat = params["fat"]
    if fat > 0:
        set_co(vol, co)
        wf = fat_weights(co, sk)
        co = co + get_vertex_normals(vol) * (FAT_INFLATE * fat * wf)[:, None]
        co = laplacian_smooth(co, lap, wf, int(round(FAT_SMOOTH_ITERS * fat)))
    set_co(vol, co)
    return vol, sk


# ---------------------------------------------------------------------------
# Final mesh + shape keys
# ---------------------------------------------------------------------------

def quad_remesh(mesh):
    """Decimate, then Quadriflow remesh on a temp object, then relax the quads on the
    surface of mesh. Consumes mesh, returns the quad mesh."""
    surface = BVHTree.FromPolygons([v.co[:] for v in mesh.vertices], [p.vertices[:] for p in mesh.polygons])
    obj = link_temp(f"{NAME}_tmp_dec", mesh)
    dec = obj.modifiers.new("Decimate", "DECIMATE")
    dec.decimate_type = "COLLAPSE"
    dec.ratio = PRE_DECIMATE
    low = bake(obj, f"{NAME}_tmp_low")

    obj = link_temp(f"{NAME}_tmp_quad", low)
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    res = bpy.ops.object.quadriflow_remesh(
        use_mesh_symmetry=False, use_preserve_sharp=False, use_preserve_boundary=False,
        preserve_attributes=False, smooth_normals=False, mode="FACES",
        target_faces=TARGET_FACES, seed=0)
    out = obj.data
    remove_temp(obj, keep_mesh=True)
    if "FINISHED" not in res:
        raise RuntimeError("Quadriflow failed; see the Info log (input must be manifold, no zero-length edges)")
    if low.users == 0 and low != out:
        bpy.data.meshes.remove(low)

    co = get_co(out)
    lap = make_laplacian(get_edges(out), len(co))
    for _ in range(BASIS_RELAX_ITERS):
        co = co + 0.5 * lap(co)
        co = np.array([surface.find_nearest(Vector(p))[0] for p in co])
    set_co(out, co)
    return out


def mean_radii(radii):
    return [(rx + ry) * 0.5 for rx, ry in radii]


def bind_to_skeleton(co, sk):
    """Per vertex: [(weight, edge_index, t)] soft-bound to the nearest skeleton edges.
    Distance is measured to the capsule surface (distance to edge minus mean radius)."""
    radii = mean_radii(sk.radii)
    binds = []
    for p in co:
        p = Vector(p)
        cands = []
        for ei, (a, b) in enumerate(sk.edges):
            A, B = sk.verts[a], sk.verts[b]
            _, t = intersect_point_line(p, A, B)
            t = min(max(t, 0.0), 1.0)
            r = radii[a] + (radii[b] - radii[a]) * t
            cands.append(((p - A.lerp(B, t)).length - r, ei, t))
        cands.sort()
        best = cands[0][0]
        ws = [(math.exp(-(d - best) / BIND_SOFTNESS), ei, t)
              for d, ei, t in cands[:BIND_MAX_EDGES] if d - best < BIND_SOFTNESS * 5]
        tot = sum(w for w, _, _ in ws)
        binds.append([(w / tot, ei, t) for w, ei, t in ws])
    return binds


def remap(co, binds, base, variant):
    """Move each bound vertex from the base skeleton onto the variant skeleton."""
    br, vr = mean_radii(base.radii), mean_radii(variant.radii)
    out = np.empty_like(co)
    for n, (p, bind) in enumerate(zip(co, binds)):
        p = Vector(p)
        acc = Vector()
        for w, ei, t in bind:
            a, b = base.edges[ei]
            pb = base.verts[a].lerp(base.verts[b], t)
            rb = br[a] + (br[b] - br[a]) * t
            pv = variant.verts[a].lerp(variant.verts[b], t)
            rv = vr[a] + (vr[b] - vr[a]) * t
            acc += w * (pv + (p - pb) * (rv / rb))
        out[n] = acc
    return out


def shrinkwrap(src_mesh, co, target_mesh):
    """Nearest-surface-point Shrinkwrap of src_mesh (moved to co) onto target_mesh."""
    tmp = src_mesh.copy()
    set_co(tmp, co)
    obj = link_temp(f"{NAME}_tmp_wrap", tmp)
    tgt = link_temp(f"{NAME}_tmp_target", target_mesh)
    sw = obj.modifiers.new("Shrinkwrap", "SHRINKWRAP")
    sw.wrap_method = "NEAREST_SURFACEPOINT"
    sw.target = tgt
    baked = bake(obj, f"{NAME}_tmp_wrapped")
    out = get_co(baked)
    bpy.data.meshes.remove(baked)
    remove_temp(tgt, keep_mesh=True)
    return out


def face_normals(me, co):
    tmp = me.copy()
    set_co(tmp, co)
    nr = np.empty(len(tmp.polygons) * 3)
    tmp.polygons.foreach_get("normal", nr)
    bpy.data.meshes.remove(tmp)
    return nr.reshape(-1, 3)


def face_vertex_index(me):
    """(face_of_corner, vert_of_corner) arrays for scattering face flags onto vertices."""
    n = len(me.loops)
    lv = np.empty(n, dtype=np.int64)
    me.loops.foreach_get("vertex_index", lv)
    tot = np.empty(len(me.polygons), dtype=np.int64)
    me.polygons.foreach_get("loop_total", tot)
    return np.repeat(np.arange(len(tot)), tot), lv


def bad_vertices(me, lap, ed, basis, co, nb, lb, sb, f_of, v_of):
    """1.0 on vertices of flipped faces, collapsed edges and spikes, else 0.0."""
    mask = np.zeros(len(co))
    flipped = np.sum(face_normals(me, co) * nb, axis=1) < 0.0
    mask[v_of[flipped[f_of]]] = 1.0
    short = np.linalg.norm(co[ed[:, 0]] - co[ed[:, 1]], axis=1) < MIN_EDGE_RATIO * lb
    mask[ed[short].ravel()] = 1.0
    mask[np.linalg.norm(lap(co), axis=1) > SPIKE_FACTOR * sb + SPIKE_MIN] = 1.0
    return mask


def fix_folds(me, lap, ed, basis, co):
    """Smooth the key around flipped faces, collapsed edges and spikes until none are left.
    First half of the rounds smooths the offset from Basis (keeps the shape); the
    second half smooths positions (always untangles, slightly fills the fold)."""
    nb = face_normals(me, basis)
    lb = np.linalg.norm(basis[ed[:, 0]] - basis[ed[:, 1]], axis=1)
    sb = np.linalg.norm(lap(basis), axis=1)
    f_of, v_of = face_vertex_index(me)
    n = len(co)
    for rnd in range(FIX_ROUNDS):
        mask = bad_vertices(me, lap, ed, basis, co, nb, lb, sb, f_of, v_of)
        if not mask.any():
            break
        for _ in range(FIX_RINGS + rnd // 5):
            grow = np.bincount(ed[:, 0], mask[ed[:, 1]], n) + np.bincount(ed[:, 1], mask[ed[:, 0]], n)
            mask = np.maximum(mask, (grow > 0).astype(float))
        if rnd < FIX_ROUNDS // 2:
            co = basis + laplacian_smooth(co - basis, lap, mask, FIX_SMOOTH_ITERS)
        else:
            co = laplacian_smooth(co, lap, mask, FIX_SMOOTH_ITERS // 2)
    return co


def check_key(me, lap, ed, basis, key):
    """Flipped faces, stretched edges and spikes of a key (and its 0.5 mix) vs Basis."""
    nb = face_normals(me, basis)
    lb = np.linalg.norm(basis[ed[:, 0]] - basis[ed[:, 1]], axis=1)
    sb = np.linalg.norm(lap(basis), axis=1)
    rep = {}
    for label, c in (("1.0", key), ("0.5", 0.5 * (basis + key))):
        flips = int(np.sum(np.sum(face_normals(me, c) * nb, axis=1) < 0.0))
        ratio = np.linalg.norm(c[ed[:, 0]] - c[ed[:, 1]], axis=1) / lb
        spikes = int(np.sum(np.linalg.norm(lap(c), axis=1) > SPIKE_FACTOR * sb + SPIKE_MIN))
        rep[label] = dict(flipped_faces=flips, spikes=spikes,
                          edge_ratio=(round(float(ratio.min()), 2), round(float(ratio.max()), 2)))
    return rep


# ---------------------------------------------------------------------------
# Previews
# ---------------------------------------------------------------------------

def render_previews(body, out_dir):
    """Front + side Workbench renders per PREVIEW_STATES, tiled into one sheet.
    Columns follow PREVIEW_STATES, top row = front, bottom row = side."""
    os.makedirs(out_dir, exist_ok=True)
    sc = bpy.context.scene
    keys = body.data.shape_keys.key_blocks
    shading = sc.display.shading
    saved = dict(cam=sc.camera, engine=sc.render.engine, rx=sc.render.resolution_x,
                 ry=sc.render.resolution_y, pct=sc.render.resolution_percentage,
                 fp=sc.render.filepath, light=shading.light, cavity=shading.show_cavity)
    hidden = {o.name: o.hide_render for o in sc.objects}
    for o in sc.objects:
        if o.type == "MESH" and o != body:
            o.hide_render = True

    cd = bpy.data.cameras.new(f"{NAME}_tmp_cam")
    cd.type = "ORTHO"
    cd.ortho_scale = 2.0
    cam = bpy.data.objects.new(f"{NAME}_tmp_cam", cd)
    sc.collection.objects.link(cam)
    sc.camera = cam
    try:
        sc.render.engine = "BLENDER_WORKBENCH"
    except TypeError as e:
        print("Workbench unavailable:", e)
    shading.light = "STUDIO"
    shading.show_cavity = True
    W, H = 480, 800
    sc.render.resolution_x, sc.render.resolution_y, sc.render.resolution_percentage = W, H, 100

    center = Vector((0, 0, 0.92))
    views = [("front", Vector((0, -4, 0))), ("side", Vector((4, 0, 0)))]
    sheet = np.zeros((H * len(views), W * len(PREVIEW_STATES), 4), dtype=np.float32)
    for col, state in enumerate(PREVIEW_STATES):
        for k in keys[1:]:
            k.value = 1.0 if k.name == state else 0.0
        for row, (vname, off) in enumerate(views):
            cam.location = center + off
            cam.rotation_euler = (-off).to_track_quat("-Z", "Y").to_euler()
            path = os.path.join(out_dir, f"{state}_{vname}.png")
            sc.render.filepath = path
            bpy.ops.render.render(write_still=True)
            img = bpy.data.images.load(path, check_existing=False)
            px = np.empty(W * H * 4, dtype=np.float32)
            img.pixels.foreach_get(px)
            bpy.data.images.remove(img)
            # Image pixels start at the bottom row; sheet row 0 is the top
            y0 = (len(views) - 1 - row) * H
            sheet[y0:y0 + H, col * W:(col + 1) * W] = px.reshape(H, W, 4)
    for k in keys[1:]:
        k.value = 0.0

    out = bpy.data.images.new(f"{NAME}_tmp_sheet", W * len(PREVIEW_STATES), H * len(views), alpha=True)
    out.pixels.foreach_set(sheet.ravel())
    sheet_path = os.path.join(out_dir, "compare_sheet.png")
    out.filepath_raw = sheet_path
    out.file_format = "PNG"
    out.save()
    bpy.data.images.remove(out)

    sc.camera = saved["cam"]
    sc.render.engine = saved["engine"]
    sc.render.resolution_x, sc.render.resolution_y = saved["rx"], saved["ry"]
    sc.render.resolution_percentage = saved["pct"]
    sc.render.filepath = saved["fp"]
    shading.light, shading.show_cavity = saved["light"], saved["cavity"]
    for name, h in hidden.items():
        sc.objects[name].hide_render = h
    bpy.data.objects.remove(cam, do_unlink=True)
    bpy.data.cameras.remove(cd)
    return sheet_path


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def clean_previous():
    for o in [o for o in bpy.data.objects if o.name.startswith(NAME)]:
        bpy.data.objects.remove(o, do_unlink=True)
    for me in [m for m in bpy.data.meshes if m.name.startswith(NAME) and m.users == 0]:
        bpy.data.meshes.remove(me)


def main(preview_dir=PREVIEW_DIR):
    clean_previous()

    # Basis: default volume, Quadriflow to a clean quad mesh
    vol, base_sk = build_volume(DEFAULT, "base")
    final = quad_remesh(vol)
    final.name = NAME
    src = final.copy()                 # key-free copy used for projections
    basis = get_co(final)
    ed = get_edges(final)
    lap = make_laplacian(ed, len(basis))

    body = bpy.data.objects.new(NAME, final)
    bpy.context.collection.objects.link(body)
    for p in final.polygons:
        p.use_smooth = True
    body.shape_key_add(name="Basis")

    binds = bind_to_skeleton(basis, base_sk)
    report = {}
    for key_name, params in VARIANTS.items():
        vvol, vsk = build_volume(params, key_name)
        pre = remap(basis, binds, base_sk, vsk)          # follow the variant skeleton
        wrapped = shrinkwrap(src, pre, vvol)             # snap onto the variant surface
        bpy.data.meshes.remove(vvol)
        offset = laplacian_smooth(wrapped - pre, lap, 1.0, OFFSET_SMOOTH_ITERS)
        co = fix_folds(src, lap, ed, basis, pre + offset)
        sk = body.shape_key_add(name=key_name, from_mix=False)
        sk.data.foreach_set("co", co.ravel())
        report[key_name] = check_key(src, lap, ed, basis, co)
    bpy.data.meshes.remove(src)

    # Simple skin material
    mat = bpy.data.materials.get("SausageSkin") or bpy.data.materials.new("SausageSkin")
    mat.use_nodes = True
    mat.diffuse_color = (0.8, 0.55, 0.45, 1)  # viewport / Workbench color
    # Look up by type: node names are localized
    bsdf = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (0.8, 0.55, 0.45, 1)
        bsdf.inputs["Roughness"].default_value = 0.55
    body.data.materials.append(mat)

    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    bpy.context.view_layer.objects.active = body
    body.select_set(True)

    result = {"verts": len(final.vertices), "faces": len(final.polygons), "keys": report}
    if RENDER_PREVIEWS:
        if not preview_dir:
            preview_dir = bpy.path.abspath("//sausage_previews") if bpy.data.filepath \
                else os.path.join(bpy.app.tempdir, "sausage_previews")
        result["preview_sheet"] = render_previews(body, preview_dir)
    print("SausageBody built:", result)
    return result


if __name__ == "__main__":
    result = main()
