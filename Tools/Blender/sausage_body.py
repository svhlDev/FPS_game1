# Procedural "sausage body" generator for Blender (4.x / 5.x)
# Run: Scripting tab > Text Editor > New > paste > Run Script (Alt+P)
#
# How it works:
# 1. A skeleton is built as a graph of points (vertices) and lines (edges).
# 2. The Skin modifier wraps a tube around every edge; each vertex has a radius,
#    so thick vertices = fat sausages, thin = skinny. Branch points get blended.
# 3. Subdivision Surface rounds the blocky tubes into soft organic shapes.
# 4. Variants (fat, muscle, STR) are NOT re-skinned: the Skin modifier's branch
#    hulls change topology when radii change. Instead every base vertex is bound
#    to its nearest skeleton edges, then moved with the variant skeleton and
#    scaled by the variant radius. Topology stays identical, so the variants
#    become SHAPE KEYS on one mesh: sliders from 0 to 1 that morph the body.
#    These export to Unity as blendshapes.

import bpy
import math
from mathutils import Vector
from mathutils.geometry import intersect_point_line

NAME = "SausageBody"
SUBDIV_LEVELS = 2
BIND_SOFTNESS = 0.015  # metres; blend width between neighbouring bones at joints
BIND_MAX_EDGES = 6


def build_points(fat=0.3, muscle=0.3, strength=0.0):
    """Return (verts, edges, radii). Default fat/muscle 0.3 = house style 'pretty thick'."""
    L = 1.0 + 0.12 * strength          # STR: longer limbs, taller
    M = muscle + 0.6 * strength        # STR: more muscle
    verts, edges, radii = [], [], []

    def add(p, r, parent=None):
        verts.append(Vector(p))
        radii.append(r)
        i = len(verts) - 1
        if parent is not None:
            edges.append((parent, i))
        return i

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

    for s in (1, -1):  # left / right
        # Arm (A-pose)
        sh = add((s * 0.21 * L, 0, z(1.38)), 0.075 + 0.02 * fat + 0.04 * M, chest)
        el = add((s * 0.36 * L, 0, z(1.12)), 0.06 + 0.02 * fat + 0.03 * M, sh)
        wr = add((s * 0.47 * L, 0, z(0.92)), 0.04 + 0.01 * fat + 0.01 * M, el)
        # Comically small sausage hand: palm block + 4 fingers + thumb
        palm = add((s * 0.50 * L, 0, z(0.86)), 0.032, wr)
        for k, dy in enumerate((-0.024, -0.008, 0.008, 0.024)):
            add((s * 0.52 * L, dy, z(0.80)), 0.011, palm)
        add((s * 0.50 * L, -0.04, z(0.85)), 0.012, palm)  # thumb, forward

        # Leg
        hip = add((s * 0.10, 0, z(0.88)), 0.11 + 0.07 * fat + 0.03 * M, pelvis)
        kn = add((s * 0.11, 0, z(0.50)), 0.075 + 0.03 * fat + 0.02 * M, hip)
        an = add((s * 0.11, 0, z(0.10)), 0.05 + 0.01 * fat, kn)
        add((s * 0.11, -0.08, 0.04), 0.035, an)  # tiny foot, stays on ground

    return verts, edges, radii


def make_skin_mesh(name, verts, edges, radii):
    me = bpy.data.meshes.new(name + "_skel")
    me.from_pydata([v[:] for v in verts], edges, [])
    obj = bpy.data.objects.new(name, me)
    bpy.context.collection.objects.link(obj)

    obj.modifiers.new("Skin", "SKIN")
    sv = me.skin_vertices[0].data
    for i, r in enumerate(radii):
        sv[i].radius = (r, r)
    sv[0].use_root = True

    sub = obj.modifiers.new("Subdiv", "SUBSURF")
    sub.levels = SUBDIV_LEVELS
    sub.render_levels = SUBDIV_LEVELS

    # Bake modifiers into a real mesh
    dg = bpy.context.evaluated_depsgraph_get()
    baked = bpy.data.meshes.new_from_object(obj.evaluated_get(dg))
    bpy.data.objects.remove(obj, do_unlink=True)
    bpy.data.meshes.remove(me)
    return baked


def bind_to_skeleton(mesh, verts, edges, radii):
    """Per mesh vertex: [(weight, edge_index, t)] soft-bound to the nearest skeleton edges.
    Distance is measured to the capsule surface (distance to edge minus radius)."""
    binds = []
    for v in mesh.vertices:
        co = v.co
        cands = []
        for ei, (a, b) in enumerate(edges):
            A, B = verts[a], verts[b]
            _, t = intersect_point_line(co, A, B)
            t = min(max(t, 0.0), 1.0)
            r = radii[a] + (radii[b] - radii[a]) * t
            cands.append(((co - A.lerp(B, t)).length - r, ei, t))
        cands.sort()
        best = cands[0][0]
        ws = [(math.exp(-(d - best) / BIND_SOFTNESS), ei, t)
              for d, ei, t in cands[:BIND_MAX_EDGES] if d - best < BIND_SOFTNESS * 5]
        tot = sum(w for w, _, _ in ws)
        binds.append([(w / tot, ei, t) for w, ei, t in ws])
    return binds


def remap(mesh, binds, base, variant):
    """Move each bound vertex from the base skeleton onto the variant skeleton."""
    bv, be, br = base
    vv, _, vr = variant
    out = []
    for v, bind in zip(mesh.vertices, binds):
        acc = Vector()
        for w, ei, t in bind:
            a, b = be[ei]
            pb = bv[a].lerp(bv[b], t)
            rb = br[a] + (br[b] - br[a]) * t
            pv = vv[a].lerp(vv[b], t)
            rv = vr[a] + (vr[b] - vr[a]) * t
            acc += w * (pv + (v.co - pb) * (rv / rb))
        out.append(acc)
    return out


# Clean previous runs
for o in [o for o in bpy.data.objects if o.name.startswith(NAME)]:
    bpy.data.objects.remove(o, do_unlink=True)

base = build_points(fat=0.3, muscle=0.3, strength=0.0)
base_me = make_skin_mesh(NAME + "_base", *base)
body = bpy.data.objects.new(NAME, base_me)
bpy.context.collection.objects.link(body)
for p in base_me.polygons:
    p.use_smooth = True

# Shape keys
body.shape_key_add(name="Basis")
variants = {
    "Fat": dict(fat=1.0, muscle=0.3, strength=0.0),
    "Muscle": dict(fat=0.3, muscle=1.0, strength=0.0),
    "Skinny": dict(fat=0.0, muscle=0.0, strength=0.0),
    "STR": dict(fat=0.3, muscle=0.3, strength=1.0),
}
binds = bind_to_skeleton(base_me, *base)
report = {}
for key_name, kw in variants.items():
    sk = body.shape_key_add(name=key_name, from_mix=False)
    cos = remap(base_me, binds, base, build_points(**kw))
    for i, c in enumerate(cos):
        sk.data[i].co = c
    report[key_name] = round(max((c - v.co).length for c, v in zip(cos, base_me.vertices)), 3)

# Simple skin material
mat = bpy.data.materials.get("SausageSkin") or bpy.data.materials.new("SausageSkin")
mat.use_nodes = True
# Look up by type: node names are localized
bsdf = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
if bsdf:
    bsdf.inputs["Base Color"].default_value = (0.8, 0.55, 0.45, 1)
    bsdf.inputs["Roughness"].default_value = 0.55
body.data.materials.append(mat)

bpy.context.view_layer.objects.active = body
body.select_set(True)
print("SausageBody built:", len(base_me.vertices), "verts, max shape key offset (m):", report)
result = {"verts": len(base_me.vertices), "shape_key_max_offset": report}
