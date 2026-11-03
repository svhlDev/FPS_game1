"""
Canopy seal: one tight silicone / urethane bead along the seam where the
glass canopy meets the car body, and nowhere else.

HOW TO RUN
  Blender > Scripting tab > open this file > Run Script.
  - Nothing selected: the canopy is auto-detected (glass material or a name
    like "canopy" / "glass" / "dome").
  - Or select the canopy first to force it.
  - Precise override: go into Edit Mode on the canopy, Alt+click the exact
    edge loop you want sealed, then run.
  Re-running replaces the previous seal, so tweak the numbers below and rerun.

WHAT IT BUILDS
  A closed tube swept along the glass's bottom edge. Its cross-section is a
  concave fillet (a "meniscus") on each side of the glass: it climbs a little
  way up the glass, then curves down and feathers out onto the body deck,
  meeting both surfaces almost tangentially, like caulk smoothed with a finger.
  The bead is sunk a hair into the glass and the deck so no light leaks through.

CHANGES IN V2 (same bead, same profile, same settings)
  - Speed: all body meshes go into one merged BVH, so each deck probe is one
    ray walk instead of one per object. The closed-glass slicer finds the
    contact height with a coarse scan plus bisection instead of 80 slices.
  - Only meshes that render count as body: objects hidden in render, in
    excluded or render-disabled collections, and volume-only objects (smoke,
    steam, flames) are ignored, so the bead can't sit on helpers.
  - Buried glass edge: if the glass's bottom edge sits inside the body, the
    seam is moved up the glass to where it actually comes out of the body.
  - The seam is resampled every SAMPLE_MM so long straight edges stay smooth
    and the bead hugs the deck between the original vertices.
  - Parts of the glass edge with no deck under them are skipped (see
    SEAL_FREE_EDGES), height jumps split the bead instead of stretching
    rubber up the glass, and overlapping seams are de-duplicated.
  - The outward side comes from the glass normal, so open bead segments and
    non-convex outlines never flip the bead to the inside.
  - Scene scale comes from the whole car (the canopy's top parent), not only
    the parts the edge happens to touch.
  - The seal is always parented to the chassis (the body it touches most, or
    the canopy's parent).
  - The report gives contact as a percentage, segment count and run time.
"""

import bpy
import bmesh
import math
import time
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

# ----------------------------------------------------------------------------
# Settings. Sizes are real-world millimetres for a car CAR_LENGTH_M long,
# scaled to your scene automatically.
# ----------------------------------------------------------------------------
CANOPY_NAME   = ""      # force a canopy by object name, "" = auto-detect
CAR_LENGTH_M  = 4.91    # how long the car is meant to be in real life
OUT_REACH_MM  = 16.0    # how far the bead spreads over the deck, outside
OUT_HEIGHT_MM = 8.0     # how far it climbs the outside of the glass
IN_REACH_MM   = 6.0     # inner bead, seen through the glass
IN_HEIGHT_MM  = 7.0
EMBED_MM      = 0.8     # sink into glass + deck (kills light leaks, z-fighting)
MAX_GAP_MM    = 60.0    # biggest glass-to-deck gap the bead will bridge
CONCAVITY     = 0.85    # 1 = fully feathered concave smear, 0 = straight chamfer
ARC_SEGMENTS  = 8       # smoothness of each fillet
SEAL_NAME     = "Canopy_Seal"
MAT_NAME      = "Seal_Rubber"

SAMPLE_MM       = 8.0   # resample the seam this often
EMERGE_STEP_MM  = 10.0  # step when walking up a buried glass edge
JUMP_MM         = 40.0  # split the bead where the seam height jumps more than this
SEAL_FREE_EDGES = False # True = also bead glass edges that have no deck under them
TOUCH_MM        = 8.0   # a glass edge this close to the body counts as touching it

UP = Vector((0.0, 0.0, 1.0))
GLASS_WORDS = ("canopy", "glass", "dome", "bubble", "windshield",
               "windscreen", "cockpit", "glas")
SEAL_WORDS = ("seal", "gasket", "rubber", "membrane", "weatherstrip")


# ----------------------------------------------------------------------------
# helpers
# ----------------------------------------------------------------------------
def _socket(node, names):
    for n in names:
        if n in node.inputs:
            return node.inputs[n]
    return None


def set_input(node, names, value):
    s = _socket(node, names)
    if s is not None:
        try:
            s.default_value = value
        except Exception:
            pass
    return s


def material_is_glass(mat):
    if mat is None:
        return False
    if any(w in mat.name.lower() for w in GLASS_WORDS):
        return True
    nt = getattr(mat, "node_tree", None)
    if not nt:
        return False
    for n in nt.nodes:
        if n.type == 'BSDF_GLASS':
            return True
        if n.type == 'BSDF_PRINCIPLED':
            s = _socket(n, ("Transmission Weight", "Transmission"))
            if s is not None and not s.is_linked and s.default_value >= 0.5:
                return True
    return False


def material_is_volume_only(mat):
    nt = getattr(mat, "node_tree", None)
    if mat is None or not nt:
        return False
    outs = [n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL']
    if not outs:
        return False
    return all(not n.inputs["Surface"].is_linked and n.inputs["Volume"].is_linked for n in outs)


def obj_is_glass(o):
    return any(material_is_glass(s.material) for s in o.material_slots)


def obj_is_volume(o):
    mats = [s.material for s in o.material_slots if s.material]
    return bool(mats) and all(material_is_volume_only(m) for m in mats)


def render_collections(view_layer):
    """Collections whose objects actually render in this view layer."""
    ok = set()

    def walk(lc, parent_ok):
        good = parent_ok and not lc.exclude and not lc.collection.hide_render
        if good:
            ok.add(lc.collection)
        for ch in lc.children:
            walk(ch, good)

    walk(view_layer.layer_collection, True)
    return ok


def renders(o, ok_colls):
    return (not o.hide_render) and any(c in ok_colls for c in o.users_collection)


def bbox_world(o):
    pts = [o.matrix_world @ Vector(c) for c in o.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return lo, hi


def boxes_overlap(a, b, pad):
    (alo, ahi), (blo, bhi) = a, b
    return all(alo[i] - pad <= bhi[i] and blo[i] - pad <= ahi[i] for i in range(3))


def find_canopy():
    if CANOPY_NAME:
        return bpy.data.objects[CANOPY_NAME]
    vl = bpy.context.view_layer
    active = vl.objects.active
    pool = [o for o in vl.objects if o.type == 'MESH' and o.visible_get()
            and o.name != SEAL_NAME and not obj_is_volume(o)]
    best, best_key = None, None
    for o in pool:
        # glass material outweighs a stray selection (e.g. the body selected)
        score = 0
        if obj_is_glass(o):
            score += 5
        if any(w in o.name.lower() for w in GLASS_WORDS):
            score += 2
        if o.select_get():
            score += 3
        if o == active and o.select_get():
            score += 1
        lo, hi = bbox_world(o)
        key = (score, (hi - lo).length)
        if best_key is None or key > best_key:
            best, best_key = o, key
    if best is None or best_key[0] < 3:
        raise RuntimeError("Could not find the canopy. Select it, or set CANOPY_NAME.")
    return best


def evaluated_bmesh(o, keep_solidify):
    """World-space bmesh of what actually renders (subsurf at render level).
    Solidify can be switched off so the glass's open bottom edge survives."""
    saved = []
    for m in o.modifiers:
        if m.type == 'SOLIDIFY' and not keep_solidify and m.show_viewport:
            saved.append((m, "show_viewport", True))
            m.show_viewport = False
        if m.type == 'SUBSURF' and m.levels != m.render_levels:
            saved.append((m, "levels", m.levels))
            m.levels = m.render_levels
    dg = bpy.context.evaluated_depsgraph_get()
    dg.update()
    ev = o.evaluated_get(dg)
    me = ev.to_mesh()
    bm = bmesh.new()
    bm.from_mesh(me)
    ev.to_mesh_clear()
    for m, attr, val in saved:
        setattr(m, attr, val)
    if saved:
        bpy.context.evaluated_depsgraph_get().update()
    bm.transform(o.matrix_world)
    bm.normal_update()
    bm.verts.ensure_lookup_table()
    bm.edges.ensure_lookup_table()
    bm.verts.index_update()
    return bm


def chain_edges(edge_pairs):
    """edge_pairs: list of (vi, vj). Returns [(vertex index list, closed)]."""
    adj = {}
    for k, (a, b) in enumerate(edge_pairs):
        adj.setdefault(a, []).append((k, b))
        adj.setdefault(b, []).append((k, a))
    used = set()
    out = []
    for k0, (a, b) in enumerate(edge_pairs):
        if k0 in used:
            continue
        used.add(k0)
        chain = [a, b]
        closed = False
        while True:
            nxt = [(k, w) for k, w in adj[chain[-1]] if k not in used]
            if not nxt:
                break
            k, w = nxt[0]
            used.add(k)
            if w == chain[0]:
                closed = True
                break
            chain.append(w)
        if not closed:
            while True:
                nxt = [(k, w) for k, w in adj[chain[0]] if k not in used]
                if not nxt:
                    break
                k, w = nxt[0]
                used.add(k)
                chain.insert(0, w)
        out.append((chain, closed))
    return out


def signed_area_xy(pts):
    a = 0.0
    n = len(pts)
    for i in range(n):
        p, q = pts[i], pts[(i + 1) % n]
        a += p.x * q.y - q.x * p.y
    return 0.5 * a


def _nlerp(a, b, t):
    v = a.lerp(b, t)
    return v.normalized() if v.length > 1e-12 else a.copy()


class Bodies:
    """Everything the bead can rest on: rendered meshes near the canopy, in one BVH."""

    def __init__(self, canopy, pad):
        self.items = []          # objects, indexed by self.owner
        self.owner = []          # triangle index -> object index
        self.tris = []           # triangle index -> (v0, v1, v2) in world space
        self.bvh = None
        cb = bbox_world(canopy)
        ok = render_collections(bpy.context.view_layer)
        dg = bpy.context.evaluated_depsgraph_get()
        verts, polys = [], []
        for o in bpy.context.view_layer.objects:
            if o.type != 'MESH' or o == canopy or o.name == SEAL_NAME:
                continue
            if not renders(o, ok) or obj_is_glass(o) or obj_is_volume(o):
                continue
            if not boxes_overlap(cb, bbox_world(o), pad):
                continue
            ev = o.evaluated_get(dg)
            me = ev.to_mesh()
            if len(me.polygons) == 0:
                ev.to_mesh_clear()
                continue
            me.calc_loop_triangles()
            mw = o.matrix_world
            off = len(verts)
            verts.extend(mw @ v.co for v in me.vertices)
            oi = len(self.items)
            self.items.append(o)
            for lt in me.loop_triangles:
                tri = tuple(off + i for i in lt.vertices)
                polys.append(tri)
                self.owner.append(oi)
                self.tris.append((verts[tri[0]], verts[tri[1]], verts[tri[2]]))
            ev.to_mesh_clear()
        if polys:
            self.bvh = BVHTree.FromPolygons(verts, polys)

    def deck_z(self, x, y, z_ref, above, below):
        """Upward-facing surface under (x, y) whose height is closest to z_ref."""
        if self.bvh is None:
            return (None, None)
        best = None
        down = Vector((0, 0, -1))
        z = z_ref + above
        z_lo = z_ref - below
        for _ in range(32):
            if z <= z_lo:
                break
            loc, nor, idx, dist = self.bvh.ray_cast(Vector((x, y, z)), down, z - z_lo)
            if loc is None:
                break
            if nor.z > 0.25:
                d = abs(loc.z - z_ref)
                if best is None or d < best[0]:
                    best = (d, loc.z, idx)
            z = loc.z - 1e-6 - 1e-7 * abs(loc.z)
        if best is None:
            return (None, None)
        return best[1], self.items[self.owner[best[2]]]

    def inside(self, p):
        """Inside any closed body: odd number of crossings of that body going straight up.
        (The nearest-face test is fooled by inner walls such as a cut-out cabin.)"""
        if self.bvh is None:
            return False
        counts = {}
        q = Vector(p)
        for _ in range(64):
            loc, nor, idx, dist = self.bvh.ray_cast(q, UP)
            if loc is None:
                break
            o = self.owner[idx]
            counts[o] = counts.get(o, 0) + 1
            q = loc + UP * (1e-6 + 1e-7 * abs(loc.z))
        return any(c % 2 for c in counts.values())


# ----------------------------------------------------------------------------
# finding the seam
# ----------------------------------------------------------------------------
def solidify_info(o):
    for m in o.modifiers:
        if m.type == 'SOLIDIFY' and m.show_render:
            s = o.matrix_world.to_scale()
            k = (abs(s.x) + abs(s.y) + abs(s.z)) / 3.0
            t = m.thickness * k
            return t * (m.offset - 1) / 2.0, t * (m.offset + 1) / 2.0
    return None


def _seam_from_chain(bm, chain, closed, skip_edge):
    pts, walls, norms = [], [], []
    for vi in chain:
        v = bm.verts[vi]
        best, bestz = None, 0.2
        for e in v.link_edges:
            if skip_edge(e):
                continue
            d = (e.other_vert(v).co - v.co)
            if d.length < 1e-12:
                continue
            d = d.normalized()
            if d.z > bestz:
                best, bestz = d, d.z
        pts.append(v.co.copy())
        walls.append(best if best is not None else UP.copy())
        norms.append(v.normal.copy())
    return dict(pts=pts, walls=walls, norms=norms, closed=closed, inner=None)


def seams_from_boundary(bm):
    """Open-bottom glass shell: its boundary edge loops."""
    pairs = [(e.verts[0].index, e.verts[1].index) for e in bm.edges if e.is_boundary]
    return [_seam_from_chain(bm, chain, closed, lambda e: e.is_boundary)
            for chain, closed in chain_edges(pairs) if len(chain) >= 3]


def seams_from_selection(o):
    o.update_from_editmode()
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bm.transform(o.matrix_world)
    bm.normal_update()
    bm.verts.ensure_lookup_table()
    sel = [e for e in bm.edges if e.select]
    if not sel:
        bm.free()
        return None
    selset = set(sel)
    pairs = [(e.verts[0].index, e.verts[1].index) for e in sel]
    seams = [_seam_from_chain(bm, chain, closed, lambda e: e in selset)
             for chain, closed in chain_edges(pairs)]
    bm.free()
    return seams


def slice_loops(bm_src, z):
    bm = bm_src.copy()
    geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
    r = bmesh.ops.bisect_plane(bm, geom=geom, dist=1e-7, plane_co=(0, 0, z), plane_no=(0, 0, 1))
    bm.verts.index_update()
    cut = [g for g in r["geom_cut"] if isinstance(g, bmesh.types.BMEdge)]
    pairs = [(e.verts[0].index, e.verts[1].index) for e in cut]
    bm.verts.ensure_lookup_table()
    loops = []
    for chain, closed in chain_edges(pairs):
        pts = [bm.verts[i].co.copy() for i in chain]
        if len(pts) >= 3:
            loops.append((pts, closed, abs(signed_area_xy(pts))))
    bm.free()
    loops.sort(key=lambda t: -t[2])
    return loops


def seams_from_slicing(bm, bodies, size):
    """Closed glass solid (no open edge): slice it where it emerges from the body."""
    zs = [v.co.z for v in bm.verts]
    zmin, zmax = min(zs), max(zs)
    h = zmax - zmin

    def emerged(z):
        loops = slice_loops(bm, z)
        if not loops:
            return None
        pts = loops[0][0]
        sample = pts[::max(1, len(pts) // 48)]
        return sum(bodies.inside(p) for p in sample) / len(sample) <= 0.15

    # coarse scan, then bisect between the last buried and the first emerged slice
    contact = None
    prev = zmin
    for k in range(1, 21):
        z = zmin + h * 0.5 * k / 20.0
        e = emerged(z)
        if e:
            lo_z, hi_z = prev, z
            for _ in range(6):
                mid = (lo_z + hi_z) / 2.0
                if emerged(mid):
                    hi_z = mid
                else:
                    lo_z = mid
            contact = hi_z
            break
        if e is not None:
            prev = z
    if contact is None:
        contact = zmin + h * 0.01
    z0 = contact + size * 0.0005
    loops = slice_loops(bm, z0)
    upper = slice_loops(bm, z0 + size * 0.004)
    if not loops:
        return []
    outer_pts, closed, area = loops[0]
    inner = None
    if len(loops) > 1 and loops[1][2] > 0.6 * area:
        inner = loops[1][0]
    kd_up = None
    if upper:
        up_pts = upper[0][0]
        kd_up = KDTree(len(up_pts))
        for i, p in enumerate(up_pts):
            kd_up.insert(p, i)
        kd_up.balance()
    walls = []
    for p in outer_pts:
        w = UP.copy()
        if kd_up:
            q, _, _ = kd_up.find(p)
            d = q - p
            if d.length > 1e-12 and d.normalized().z > 0.2:
                w = d.normalized()
        walls.append(w)
    return [dict(pts=outer_pts, walls=walls, norms=None, closed=closed, inner=inner)]


def resample(seam, ds):
    """Insert points so no gap along the seam is longer than ds."""
    pts, walls, norms, closed = seam["pts"], seam["walls"], seam["norms"], seam["closed"]
    n = len(pts)
    if n < 2 or ds <= 0:
        return seam
    op, ow, on = [], [], []
    for i in range(n if closed else n - 1):
        j = (i + 1) % n
        steps = max(1, int(math.ceil((pts[j] - pts[i]).length / ds)))
        for s in range(steps):
            t = s / steps
            op.append(pts[i].lerp(pts[j], t))
            ow.append(_nlerp(walls[i], walls[j], t))
            if norms:
                on.append(_nlerp(norms[i], norms[j], t))
    if not closed:
        op.append(pts[-1].copy())
        ow.append(walls[-1].copy())
        if norms:
            on.append(norms[-1].copy())
    return dict(pts=op, walls=ow, norms=on or None, closed=closed, inner=seam["inner"])


def split_runs(seam, keep):
    """Cut a seam into the runs of points where keep[i] is True."""
    n = len(seam["pts"])
    if all(keep):
        return [seam]
    runs, cur = [], []
    for i in range(n):
        if keep[i]:
            cur.append(i)
        elif cur:
            runs.append(cur)
            cur = []
    if cur:
        if seam["closed"] and runs and keep[0] and runs[0][0] == 0:
            runs[0] = cur + runs[0]
        else:
            runs.append(cur)
    out = []
    for r in runs:
        if len(r) < 3:
            continue
        out.append(dict(pts=[seam["pts"][i] for i in r], walls=[seam["walls"][i] for i in r],
                        norms=[seam["norms"][i] for i in r] if seam["norms"] else None,
                        closed=False, inner=seam["inner"]))
    return out


def buried_fraction(seams, bodies, probe):
    tot = hit = 0
    for s in seams:
        for i in range(0, len(s["pts"]), max(1, len(s["pts"]) // 40)):
            n = s["norms"][i] if s["norms"] else Vector((0, 0, 0))
            tot += 1
            hit += bodies.inside(s["pts"][i] + n * probe)
    return hit / max(1, tot)


def emerge(seams, bm_glass, bodies, step, probe, jump):
    """Find where the glass comes out of the top of the body: walk down the glass from
    above each edge point until it first goes into the body; the bead goes there.
    Walking down (not up) matters when the glass edge hangs free below a body block."""
    gb = BVHTree.FromBMesh(bm_glass)
    vs = [v.co for v in bm_glass.verts]
    lo = Vector([min(v[i] for v in vs) for i in range(3)])
    hi = Vector([max(v[i] for v in vs) for i in range(3)])
    centre = (lo + hi) / 2.0
    max_rise = (hi.z - lo.z) * 0.9

    def on_glass(q):
        loc, nor, idx, dist = gb.find_nearest(q)
        if nor.dot(loc - centre) < 0:
            nor = -nor
        return loc, nor

    def buried(q):
        loc, nor = on_glass(q)
        return bodies.inside(loc + nor * probe), loc, nor

    out = []
    for s in seams:
        ts = []
        for p0, w in zip(s["pts"], s["walls"]):
            t, free_t, found = max_rise, None, None
            while t >= 0.0:
                if buried(p0 + w * t)[0]:
                    found = t
                    break
                free_t = t
                t -= step
            if found is None or free_t is None:
                # never meets the body (edge hangs free), or buried all the way up
                ts.append(None)
                continue
            a, c = found, free_t          # a buried, c free
            for _ in range(8):
                m = (a + c) / 2.0
                if buried(p0 + w * m)[0]:
                    a = m
                else:
                    c = m
            ts.append(c)
        # median filter so one panel-line groove can't make the seam dip
        n = len(ts)
        sm = []
        for i in range(n):
            win = []
            for k in range(-2, 3):
                j = (i + k) % n if s["closed"] else min(max(i + k, 0), n - 1)
                if ts[j] is not None:
                    win.append(ts[j])
            sm.append(sorted(win)[len(win) // 2] if (win and ts[i] is not None) else None)
        pts, norms, walls = [], [], []
        for p0, w, t in zip(s["pts"], s["walls"], sm):
            if t is None:
                pts.append(p0.copy())
                norms.append(Vector((0, 0, 1)))
                walls.append(w.copy())
            else:
                loc, nor = on_glass(p0 + w * t)
                pts.append(loc)
                norms.append(nor)
                # climb direction: straight up along the glass surface at this height
                up = UP - nor * UP.dot(nor)
                walls.append(up.normalized() if up.length > 1e-6 else w.copy())
        moved = dict(pts=pts, walls=walls, norms=norms, closed=s["closed"], inner=None)
        keep = [t is not None for t in sm]
        # split where the seam climbs a body edge instead of running along a deck
        for i in range(n):
            j = (i + 1) % n
            if (s["closed"] or j > i) and keep[i] and keep[j] and abs(pts[j].z - pts[i].z) > jump:
                keep[i] = False
        out += split_runs(moved, keep)
    return out


def _tri_tri_segment(a, b, eps=1e-9):
    """Segment where triangles a and b cross, or None."""
    a0, a1, a2 = a
    b0, b1, b2 = b
    na = (a1 - a0).cross(a2 - a0)
    nb = (b1 - b0).cross(b2 - b0)
    if na.length < 1e-14 or nb.length < 1e-14:
        return None
    na.normalize()
    nb.normalize()
    da = [nb.dot(p - b0) for p in a]
    if all(d > eps for d in da) or all(d < -eps for d in da):
        return None
    db = [na.dot(p - a0) for p in b]
    if all(d > eps for d in db) or all(d < -eps for d in db):
        return None
    line = na.cross(nb)
    if line.length < 1e-10:
        return None                      # coplanar: touching, not crossing
    line.normalize()

    def crossings(pts, ds):
        out = []
        for i in range(3):
            j = (i + 1) % 3
            di, dj = ds[i], ds[j]
            if abs(di) <= eps:
                out.append(pts[i])
            elif (di > eps and dj < -eps) or (di < -eps and dj > eps):
                out.append(pts[i].lerp(pts[j], di / (di - dj)))
        return out

    ca, cb = crossings(a, da), crossings(b, db)
    if len(ca) < 2 or len(cb) < 2:
        return None
    ta = sorted(line.dot(p) for p in ca)
    tb = sorted(line.dot(p) for p in cb)
    lo, hi = max(ta[0], tb[0]), min(ta[-1], tb[-1])
    if hi - lo < 1e-9:
        return None
    origin = ca[0] - line * line.dot(ca[0])
    return origin + line * lo, origin + line * hi


def seams_from_intersection(bm_glass, bodies, ds, probe, side_step, touch):
    """Exact line where the glass surface crosses the body surfaces, as connected curves.
    Only joints with free glass on one side and buried glass on the other are kept,
    so crossings hidden inside another body are dropped."""
    if bodies.bvh is None:
        return []
    bt = bm_glass.copy()
    bmesh.ops.triangulate(bt, faces=bt.faces[:])
    bt.faces.ensure_lookup_table()
    gtris = [tuple(v.co.copy() for v in f.verts) for f in bt.faces]
    gb = BVHTree.FromBMesh(bt)
    vs = [v.co for v in bt.verts]
    centre = sum(vs, Vector()) / max(1, len(vs))
    bt.free()

    def glass_normal(p):
        loc, nor, idx, dist = gb.find_nearest(p)
        if nor.dot(loc - centre) < 0:
            nor = -nor
        return nor

    segs = []
    for gi, bi in gb.overlap(bodies.bvh):
        s = _tri_tri_segment(gtris[gi], bodies.tris[bi])
        if s is None or (s[1] - s[0]).length < 1e-7:
            continue
        m = (s[0] + s[1]) * 0.5
        T = (s[1] - s[0]).normalized()
        ng = glass_normal(m)
        c = ng.cross(T)
        if c.length < 1e-9:
            continue
        c.normalize()
        a = bodies.inside(m + c * side_step + ng * probe)
        b = bodies.inside(m - c * side_step + ng * probe)
        if a != b:
            segs.append(s)
    if not segs:
        return []
    # weld segment ends and chain them into curves
    tol = max(ds * 0.05, 1e-6)
    kd = KDTree(len(segs) * 2)
    for i, s in enumerate(segs):
        kd.insert(s[0], 2 * i)
        kd.insert(s[1], 2 * i + 1)
    kd.balance()
    rep = {}
    pts = []
    for i in range(len(segs) * 2):
        if i in rep:
            continue
        p = segs[i // 2][i % 2]
        idx = len(pts)
        pts.append(p)
        for (_, j, _) in kd.find_range(p, tol):
            rep.setdefault(j, idx)
    pairs = []
    for i in range(len(segs)):
        a, b = rep[2 * i], rep[2 * i + 1]
        if a != b:
            pairs.append((a, b))
    curves = [([pts[i] for i in chain], closed) for chain, closed in chain_edges(pairs) if len(chain) >= 2]
    # glass edge resting on the body without crossing it (flush contact): seal it too
    for s in seams_from_boundary(bm_glass):
        s = resample(s, ds)
        run = []
        for p in s["pts"] + [None]:
            near = False
            if p is not None:
                loc, nor, idx, dist = bodies.bvh.find_nearest(p)
                # close to a body surface, with free air just outside the glass (not deep inside)
                near = loc is not None and dist <= touch and not bodies.inside(p + glass_normal(p) * touch * 1.25)
            if p is not None and near:
                run.append(p)
            elif len(run) >= 2:
                curves.append((run, False))
                run = []
            else:
                run = []
    # join curves whose ends meet (body parts meeting at the glass leave small gaps)
    join = side_step * 5.0
    open_c = [c for c, cl in curves if not cl]
    done = [(c, True) for c, cl in curves if cl]
    merged = True
    while merged:
        merged = False
        for i in range(len(open_c)):
            a = open_c[i]
            best = None
            for j in range(len(open_c)):
                if j == i:
                    continue
                b = open_c[j]
                for ea, eb, d in ((a[-1], b[0], 0), (a[-1], b[-1], 1), (a[0], b[-1], 2), (a[0], b[0], 3)):
                    g = (ea - eb).length
                    if g < join and (best is None or g < best[0]):
                        best = (g, j, d)
            if best:
                g, j, d = best
                b = open_c[j]
                if d == 0:
                    new = a + b
                elif d == 1:
                    new = a + b[::-1]
                elif d == 2:
                    new = b + a
                else:
                    new = b[::-1] + a
                open_c = [c for k, c in enumerate(open_c) if k not in (i, j)] + [new]
                merged = True
                break
    # an open end that stops next to another seam (a T joint) gets a short bridge to it
    bridge = side_step * 7.5
    for i, c in enumerate(open_c):
        others = [p for j, o in enumerate(open_c) if j != i for p in o] + [p for o, cl in done for p in o]
        if not others:
            continue
        kd_o = KDTree(len(others))
        for j, p in enumerate(others):
            kd_o.insert(p, j)
        kd_o.balance()
        for at_start in (True, False):
            e = c[0] if at_start else c[-1]
            q, _, d = kd_o.find(e)
            if d < 1e-6 or d > bridge:
                continue
            steps = max(1, int(math.ceil(d / ds)))
            seg = []
            for s_ in range(1, steps + 1):
                loc = gb.find_nearest(e.lerp(q, s_ / steps))[0]
                seg.append(loc)
            if at_start:
                c[:0] = seg[::-1]
            else:
                c.extend(seg)
    for c in open_c:
        if len(c) > 2 and (c[0] - c[-1]).length < join:
            done.append((c, True))
        else:
            done.append((c, False))
    seams = []
    for P, closed in done:
        length = sum((P[i + 1] - P[i]).length for i in range(len(P) - 1))
        if length < ds * 12:
            continue                     # tiny nicks (panel grooves at the glass edge)
        seam = dict(pts=P, walls=[UP.copy()] * len(P), norms=[UP.copy()] * len(P), closed=closed, inner=None)
        seam = resample(seam, ds)
        P, cnt = seam["pts"], len(seam["pts"])
        walls, norms = [], []
        for i in range(cnt):
            p = P[i]
            a = P[i - 1] if (closed or i > 0) else p
            b = P[(i + 1) % cnt] if (closed or i < cnt - 1) else p
            T = (b - a)
            T = T.normalized() if T.length > 1e-12 else Vector((1, 0, 0))
            ng = glass_normal(p)
            c = ng.cross(T)
            c = c.normalized() if c.length > 1e-9 else UP.copy()
            # free side of the glass: the bead's glass fillet climbs that way
            if bodies.inside(p + c * side_step + ng * probe):
                c = -c
            walls.append(c)
            norms.append(ng)
        seams.append(dict(pts=P, walls=walls, norms=norms, closed=closed, inner=None, on_body=True))
    return seams


def dedupe(seams, radius):
    """Drop points that repeat a seam already accepted (overlapping edge loops)."""
    out, kd_pts = [], []
    for s in sorted(seams, key=lambda s: -len(s["pts"])):
        if kd_pts:
            kd = KDTree(len(kd_pts))
            for i, p in enumerate(kd_pts):
                kd.insert(p, i)
            kd.balance()
            keep = [kd.find(p)[2] > radius for p in s["pts"]]
        else:
            keep = [True] * len(s["pts"])
        for part in split_runs(s, keep):
            out.append(part)
            kd_pts += part["pts"]
    return out


# ----------------------------------------------------------------------------
# building the bead
# ----------------------------------------------------------------------------
def fillet(u_wall, side, reach, height, emb, n):
    """Concave fillet from the glass (t=0) to the deck (t=1), as (u, v, t).
    u = horizontal distance outward from the glass, v = height above the deck.
    Both ends are pushed EMBED into their surface so the bead cuts in cleanly."""
    out = []
    for k in range(n + 1):
        t = k / n
        phi = t * math.pi / 2.0
        eu = reach * (1.0 - math.cos(phi))
        ev = height * (1.0 - math.sin(phi))
        cu = reach * t
        cv = height * (1.0 - t)
        u = CONCAVITY * eu + (1.0 - CONCAVITY) * cu
        v = CONCAVITY * ev + (1.0 - CONCAVITY) * cv
        u = u_wall + side * u - side * emb * (1.0 - t)
        v = v - emb * t
        out.append((u, v, t))
    return out


def build_seam(seam, bodies, dims, sol, bm_out, uv_layer, stats):
    out_reach, out_h, in_reach, in_h, emb, max_gap = dims
    pts = seam["pts"]
    walls = seam["walls"]
    norms = seam["norms"]
    closed = seam["closed"]

    # drop duplicate points
    keep = [0]
    for i in range(1, len(pts)):
        if (pts[i] - pts[keep[-1]]).length > emb * 0.05:
            keep.append(i)
    if closed and len(keep) > 2 and (pts[keep[-1]] - pts[keep[0]]).length <= emb * 0.05:
        keep.pop()
    pts = [pts[i] for i in keep]
    walls = [walls[i] for i in keep]
    norms = [norms[i] for i in keep] if norms else None
    n = len(pts)
    if n < 3:
        return 0

    ccw = signed_area_xy(pts) > 0 if closed else True

    kd_inner = None
    if seam["inner"]:
        kd_inner = KDTree(len(seam["inner"]))
        for i, p in enumerate(seam["inner"]):
            kd_inner.insert(p, i)
        kd_inner.balance()

    frames = []
    for i in range(n):
        p = pts[i]
        if closed:
            a, b = pts[i - 1], pts[(i + 1) % n]
        else:
            a, b = pts[max(i - 1, 0)], pts[min(i + 1, n - 1)]
        d0 = Vector((p.x - a.x, p.y - a.y, 0))
        d1 = Vector((b.x - p.x, b.y - p.y, 0))
        d0 = d0.normalized() if d0.length > 1e-12 else d1.normalized()
        d1 = d1.normalized() if d1.length > 1e-12 else d0
        t = (d0 + d1)
        t = t.normalized() if t.length > 1e-9 else d1
        miter = 1.0 / max(0.5, t.dot(d1))
        o = t.cross(UP).normalized()
        if norms:
            # the glass normal says which side is outside
            nh = Vector((norms[i].x, norms[i].y, 0.0))
            if nh.length > 1e-6 and o.dot(nh) < 0:
                o = -o
        elif not ccw:
            o = -o
        w = walls[i]
        w = (w - t * w.dot(t))
        w = w.normalized() if w.length > 1e-9 else UP.copy()
        if w.z < 0:
            w = -w

        # glass faces, as horizontal offsets from the bottom edge
        u_out, u_in = 0.0, 0.0
        if sol and norms:
            c = norms[i].dot(o)
            a0, a1 = sol[0] * c, sol[1] * c
            u_in, u_out = min(a0, a1), max(a0, a1)
        elif kd_inner:
            q, _, dist = kd_inner.find(p)
            u_in = -abs((q - p).dot(o))
        frames.append(dict(p=p, o=o, w=w, miter=miter, u_out=u_out, u_in=u_in))

    # where the glass meets the deck
    zs = []
    for f in frames:
        z, ob = bodies.deck_z(f["p"].x, f["p"].y, f["p"].z, max_gap + out_h * 4, max_gap)
        if ob is not None:
            stats["hits"][ob.name] = stats["hits"].get(ob.name, 0) + 1
            stats["hit"] += 1
        stats["total"] += 1
        zs.append(z)
    # light median smoothing so a panel-line groove can't make the bead dip
    sm = []
    for i in range(n):
        win = []
        for k in range(-2, 3):
            j = (i + k) % n if closed else min(max(i + k, 0), n - 1)
            if zs[j] is not None:
                win.append(zs[j])
        sm.append(sorted(win)[len(win) // 2] if win else None)

    solid_glass = (not sol) and (kd_inner is None) and seam["norms"] is None
    profile_cache = None
    rows = []
    for i, f in enumerate(frames):
        p, o, w, m = f["p"], f["o"], f["w"], f["miter"]
        dz = sm[i]
        if dz is None:
            dz = p.z
        # slide down (or up) the glass line to the deck height
        base = p + w * ((dz - p.z) / max(w.z, 0.2))
        gap = max(0.0, p.z - dz)
        stats["max_gap"] = max(stats["max_gap"], gap)
        ho = max(out_h, gap + 0.6 * out_h)
        hi = max(in_h, gap + 0.6 * in_h)
        u_out = f["u_out"]
        u_in = min(f["u_in"], u_out - 2.0 * emb)

        prof = []  # (u, v, h_side)
        for (u, v, t) in fillet(u_out, +1, out_reach, ho, emb, ARC_SEGMENTS):
            prof.append((u, v, ho))
        if solid_glass:
            core = u_out - max(out_reach * 0.6, 3 * emb)
            prof.append((core, -emb, ho))
            prof.append((core, ho * 0.8, ho))
        else:
            for (u, v, t) in reversed(fillet(u_in, -1, in_reach, hi, emb, ARC_SEGMENTS)):
                prof.append((u, v, hi))
        if profile_cache is None:
            profile_cache = len(prof)

        row = []
        for (u, v, hside) in prof:
            q = base + o * (u * m) + w * v
            # let the deck-side part hug the actual deck surface
            wgt = 1.0 - min(max(v / hside, 0.0), 1.0)
            if wgt > 0.0:
                z, ob = bodies.deck_z(q.x, q.y, dz, max_gap * 0.5 + hside, max_gap * 0.5)
                if z is not None:
                    q.z += (z - dz) * wgt
            row.append(bm_out.verts.new(q))
        rows.append(row)

    M = profile_cache
    seg = n if closed else n - 1
    # UVs: U along the seam (in bead widths), V around the profile
    run = [0.0]
    for i in range(1, n + 1 if closed else n):
        run.append(run[-1] + (pts[i % n] - pts[i - 1]).length)
    width = out_reach + in_reach + 4 * emb
    for i in range(seg):
        r0, r1 = rows[i], rows[(i + 1) % n]
        u0, u1 = run[i] / width, run[i + 1] / width
        for j in range(M):
            jn = (j + 1) % M
            face = bm_out.faces.new((r0[j], r1[j], r1[jn], r0[jn]))
            face.smooth = True
            for loop, (uu, vv) in zip(face.loops, ((u0, j / M), (u1, j / M), (u1, (j + 1) / M), (u0, (j + 1) / M))):
                loop[uv_layer].uv = (uu, vv)
    if not closed:
        for row in (rows[0], rows[-1]):
            face = bm_out.faces.new(row)
            face.smooth = True
    return n


def build_seam_contact(seam, bodies, dims, thick, bm_out, uv_layer, stats):
    """Same fillet bead, for glass that meets the body at any angle (a deck, a sloped
    nose face, a side wall). One fillet runs up the glass, the other along the body
    surface the glass touches, found by a short ray down the glass just outside it.
    Points where the glass touches nothing are skipped: only glass-to-body joints."""
    out_reach, out_h, in_reach, in_h, emb, max_gap = dims
    pts, walls, norms, closed = seam["pts"], seam["walls"], seam["norms"], seam["closed"]
    n = len(pts)
    if n < 3:
        return []
    probe = emb * 3.0
    look = max(out_h, in_h) * 3.0 + max_gap

    def body_hit(origin, direction, length=look):
        if bodies.bvh is None:
            return None
        loc, nor, idx, dist = bodies.bvh.ray_cast(origin, direction, length)
        if loc is None:
            return None
        if nor.dot(direction) > 0:
            nor = -nor
        return loc, nor, bodies.items[bodies.owner[idx]]

    frames, keep = [], []
    for i in range(n):
        p, ng, w = pts[i], norms[i], walls[i]
        a = pts[i - 1] if (closed or i > 0) else p
        b = pts[(i + 1) % n] if (closed or i < n - 1) else p
        T = b - a
        T = T.normalized() if T.length > 1e-12 else Vector((1, 0, 0))
        w = (w - T * w.dot(T))
        w = w.normalized() if w.length > 1e-9 else UP.copy()
        if seam.get("on_body"):
            # exact intersection point: it is on the body, read the body normal right here
            loc, nb0, idx, dist = bodies.bvh.find_nearest(p)
            away = p - loc
            if away.length > emb * 0.5:
                if nb0.dot(away) < 0:        # glass edge beside the body: face the glass
                    nb0 = -nb0
            elif nb0.dot(w) < 0:             # glass crossing the body: face the free glass
                nb0 = -nb0
            hit_o = (loc, nb0, bodies.items[bodies.owner[idx]])
        else:
            # touching = body surface within a few mm of the seam, just outside the glass
            hit_o = body_hit(p + ng * probe + w * out_h, -w, out_h + 2.0 * probe)
        if hit_o is None:
            keep.append(False)
            frames.append(None)
            stats["total"] += 1
            continue
        stats["total"] += 1
        stats["hit"] += 1
        nb = hit_o[1]
        stats["hits"][hit_o[2].name] = stats["hits"].get(hit_o[2].name, 0) + 1
        # along the body surface, away from the glass (outside: outward and down)
        side = nb.cross(T)
        side = side.normalized() if side.length > 1e-9 else -w
        d_out = side if side.dot(ng - w) >= 0 else -side
        p_in = p - ng * thick
        hit_i = body_hit(p_in - ng * probe + w * in_h, -w)
        nbi = hit_i[1] if hit_i else nb
        side_i = nbi.cross(T)
        side_i = side_i.normalized() if side_i.length > 1e-9 else -w
        d_in = side_i if side_i.dot(-ng - w) >= 0 else -side_i
        frames.append(dict(p=p, p_in=p_in, w=w, d_out=d_out, d_in=d_in, nb=nb, nbi=nbi, inner_body=hit_i is not None))
        keep.append(True)

    # split into runs of points that touch the body
    runs, cur = [], []
    for i in range(n):
        if keep[i]:
            cur.append(i)
        elif cur:
            runs.append(cur)
            cur = []
    if cur:
        if closed and runs and keep[0] and runs[0][0] == 0:
            runs[0] = cur + runs[0]
        else:
            runs.append(cur)
    whole_loop = closed and all(keep)

    def snap(q, nrm, wgt):
        # pull the body-side part of the profile onto the body surface, sunk EMBED
        if wgt <= 0.0:
            return q
        h = body_hit(q + nrm * look * 0.5, -nrm)
        if h is None:
            return q
        target = h[0] - h[1] * emb
        if (target - q).length > 2.0 * out_reach:
            return q        # surface too far away to belong to this joint
        return q + (target - q) * wgt

    made = 0
    width = out_reach + in_reach + 4 * emb
    for r in runs:
        if len(r) < 3 and not whole_loop:
            continue
        rows = []
        for i in r:
            f = frames[i]
            ring = []
            for (u, v, t) in fillet(0.0, +1, out_reach, out_h, emb, ARC_SEGMENTS):
                q = f["p"] + f["d_out"] * u + f["w"] * v - f["nb"] * (emb * t)
                ring.append(snap(q, f["nb"], 1.0 - min(max(v / out_h, 0.0), 1.0)))
            for (u, v, t) in reversed(fillet(0.0, +1, in_reach, in_h, emb, ARC_SEGMENTS)):
                q = f["p_in"] + f["d_in"] * u + f["w"] * v
                if f["inner_body"]:
                    q = snap(q - f["nbi"] * (emb * t), f["nbi"], 1.0 - min(max(v / in_h, 0.0), 1.0))
                ring.append(q)
            rows.append([bm_out.verts.new(q) for q in ring])
        M = len(rows[0])
        cnt = len(rows)
        loop = whole_loop
        run = [0.0]
        for j in range(1, cnt + (1 if loop else 0)):
            run.append(run[-1] + (pts[r[j % cnt]] - pts[r[j - 1]]).length)
        for j in range(cnt if loop else cnt - 1):
            r0, r1 = rows[j], rows[(j + 1) % cnt]
            u0, u1 = run[j] / width, run[j + 1] / width
            for m in range(M):
                mn = (m + 1) % M
                face = bm_out.faces.new((r0[m], r1[m], r1[mn], r0[mn]))
                face.smooth = True
                for lp, (uu, vv) in zip(face.loops, ((u0, m / M), (u1, m / M), (u1, (m + 1) / M), (u0, (m + 1) / M))):
                    lp[uv_layer].uv = (uu, vv)
        if not loop:
            for row in (rows[0], rows[-1]):
                face = bm_out.faces.new(row)
                face.smooth = True
        made += cnt
    return made


def make_material(bead_width):
    mat = bpy.data.materials.get(MAT_NAME) or bpy.data.materials.new(MAT_NAME)
    try:
        mat.use_nodes = True
    except Exception:
        pass
    mat.diffuse_color = (0.02, 0.02, 0.022, 1.0)
    mat.roughness = 0.45
    nt = mat.node_tree
    nt.nodes.clear()
    N, L = nt.nodes, nt.links
    out = N.new("ShaderNodeOutputMaterial"); out.location = (600, 0)
    bsdf = N.new("ShaderNodeBsdfPrincipled"); bsdf.location = (300, 0)
    set_input(bsdf, ("Base Color",), (0.011, 0.012, 0.013, 1.0))
    set_input(bsdf, ("Specular IOR Level", "Specular"), 0.45)
    set_input(bsdf, ("IOR",), 1.42)
    L.new(bsdf.outputs[0], out.inputs["Surface"])

    tc = N.new("ShaderNodeTexCoord"); tc.location = (-700, 0)
    # fine orange-peel texture of smoothed silicone
    fine = N.new("ShaderNodeTexNoise"); fine.location = (-450, -150)
    set_input(fine, ("Scale",), 3.0 / max(bead_width, 1e-6))
    set_input(fine, ("Detail",), 4.0)
    set_input(fine, ("Roughness",), 0.55)
    L.new(tc.outputs["Object"], fine.inputs["Vector"])
    bump = N.new("ShaderNodeBump"); bump.location = (0, -200)
    set_input(bump, ("Strength",), 0.12)
    L.new(fine.outputs["Fac"], bump.inputs["Height"])
    L.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    # slow roughness variation: smear marks catch the light unevenly
    broad = N.new("ShaderNodeTexNoise"); broad.location = (-450, 150)
    set_input(broad, ("Scale",), 0.25 / max(bead_width, 1e-6))
    L.new(tc.outputs["Object"], broad.inputs["Vector"])
    mr = N.new("ShaderNodeMapRange"); mr.location = (0, 150)
    set_input(mr, ("To Min",), 0.32)
    set_input(mr, ("To Max",), 0.55)
    L.new(broad.outputs["Fac"], mr.inputs[0])
    L.new(mr.outputs[0], bsdf.inputs["Roughness"])
    return mat


def mark_sharp(bm, angle_deg=50.0):
    lim = math.radians(angle_deg)
    for e in bm.edges:
        if len(e.link_faces) == 2 and e.calc_face_angle(0.0) > lim:
            e.smooth = False


def car_length(canopy, touched, fallback):
    """Real car length in scene units: the canopy's whole hierarchy if it has one."""
    ok = render_collections(bpy.context.view_layer)
    top = canopy
    while top.parent:
        top = top.parent
    family = [o for o in [top] + list(top.children_recursive)
              if o.type == 'MESH' and renders(o, ok) and not obj_is_volume(o)]
    if len(family) < 2:
        family = list(touched) + [canopy]
    if len(family) < 2:
        return fallback
    boxes = [bbox_world(o) for o in family]
    lo = Vector([min(b[0][i] for b in boxes) for i in range(3)])
    hi = Vector([max(b[1][i] for b in boxes) for i in range(3)])
    return max(hi.x - lo.x, hi.y - lo.y)


# ----------------------------------------------------------------------------
def main():
    t_start = time.time()
    canopy = find_canopy()
    old = bpy.data.objects.get(SEAL_NAME)
    if old:
        old_me = old.data
        bpy.data.objects.remove(old, do_unlink=True)
        if old_me and old_me.users == 0:
            bpy.data.meshes.remove(old_me)

    lo, hi = bbox_world(canopy)
    csize = max(hi - lo)
    bodies = Bodies(canopy, pad=csize * 0.05)
    if not bodies.items:
        raise RuntimeError("Found the canopy (%s) but no body mesh under it." % canopy.name)

    mode = "boundary"
    seams = None
    if canopy.mode == 'EDIT':
        seams = seams_from_selection(canopy)
        mode = "edit-selection"
    bm_c = None
    if seams is None:
        bm_c = evaluated_bmesh(canopy, keep_solidify=False)
        seams = seams_from_boundary(bm_c)
        if not seams:
            bm_c.free()
            bm_c = evaluated_bmesh(canopy, keep_solidify=True)
            seams = seams_from_slicing(bm_c, bodies, csize)
            mode = "slice (closed glass)"
    if not seams:
        raise RuntimeError("Could not find the glass edge on %s." % canopy.name)

    # scene scale: the whole car
    probe_gap = csize * 0.05
    touched = {}
    for s in seams:
        for p in s["pts"][::max(1, len(s["pts"]) // 40)]:
            z, ob = bodies.deck_z(p.x, p.y, p.z, probe_gap, probe_gap)
            if ob is not None:
                touched[ob] = touched.get(ob, 0) + 1
    car_len = car_length(canopy, touched, csize)
    k = car_len / (CAR_LENGTH_M * 1000.0)
    dims = (OUT_REACH_MM * k, OUT_HEIGHT_MM * k, IN_REACH_MM * k,
            IN_HEIGHT_MM * k, EMBED_MM * k, MAX_GAP_MM * k)

    seams = [resample(s, SAMPLE_MM * k) for s in seams]

    # glass edge buried inside the body: move the seam up to where the glass comes out
    buried = 0.0
    edge_contact = 1.0
    emerge_dropped = 0
    if mode == "boundary":
        buried = buried_fraction(seams, bodies, dims[4] * 3)
        sample = [p for s in seams for p in s["pts"][::max(1, len(s["pts"]) // 40)]]
        edge_contact = sum(bodies.deck_z(p.x, p.y, p.z, dims[5] + dims[1] * 4, dims[5])[1] is not None
                           for p in sample) / max(1, len(sample))
        if buried > 0.25 or edge_contact < 0.5:
            cross = seams_from_intersection(bm_c, bodies, SAMPLE_MM * k, dims[4] * 3, dims[0] * 0.5, TOUCH_MM * k)
            if cross:
                seams = cross
                mode = "boundary, moved up to where the glass leaves the body (exact intersection)"
            else:
                n_before = sum(len(s["pts"]) for s in seams)
                seams = emerge(seams, bm_c, bodies, EMERGE_STEP_MM * k, dims[4] * 3, JUMP_MM * k)
                emerge_dropped = n_before - sum(len(s["pts"]) for s in seams)
                seams = dedupe(seams, dims[0])
                mode = "boundary, moved up to where the glass leaves the body"

    contact_mode = mode.startswith("boundary, moved up")
    # only seal edges that actually sit on the body
    skipped = 0
    if contact_mode:
        pass    # build_seam_contact keeps only points where the glass touches the body
    elif not SEAL_FREE_EDGES:
        kept = []
        for s in seams:
            keep = [bodies.deck_z(p.x, p.y, p.z, dims[5] + dims[1] * 4, dims[5])[1] is not None for p in s["pts"]]
            skipped += keep.count(False)
            kept += split_runs(s, keep)
        if kept:
            seams = kept
    elif mode == "boundary" and len(seams) > 1:
        good = []
        for s in seams:
            sample = s["pts"][::max(1, len(s["pts"]) // 40)]
            h = sum(1 for p in sample if bodies.deck_z(p.x, p.y, p.z, dims[5] + dims[1] * 4, dims[5])[1])
            if h >= 0.5 * len(sample):
                good.append(s)
        if not good:
            good = [min(seams, key=lambda s: sum(p.z for p in s["pts"]) / len(s["pts"]))]
        seams = good

    sol = solidify_info(canopy) if mode.startswith("boundary") else None

    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    stats = dict(hit=0, total=0, max_gap=0.0, hits={})
    verts = 0
    thick = (sol[1] - sol[0]) if sol else 0.0
    for s in seams:
        if contact_mode:
            verts += build_seam_contact(s, bodies, dims, thick, bm, uv, stats)
        else:
            verts += build_seam(s, bodies, dims, sol, bm, uv, stats)
    if contact_mode:
        skipped += emerge_dropped + stats["total"] - stats["hit"]
    if bm_c:
        bm_c.free()
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    mark_sharp(bm)

    me = bpy.data.meshes.new(SEAL_NAME)
    bm.to_mesh(me)
    bm.free()
    if hasattr(me, "use_auto_smooth"):
        me.use_auto_smooth = True
        me.auto_smooth_angle = math.radians(50)
    seal = bpy.data.objects.new(SEAL_NAME, me)
    (canopy.users_collection[0] if canopy.users_collection else bpy.context.scene.collection).objects.link(seal)
    seal.data.materials.append(make_material(dims[0] + dims[2]))

    body = None
    if stats["hits"]:
        body = bpy.data.objects[max(stats["hits"], key=stats["hits"].get)]
    elif canopy.parent:
        body = canopy.parent
    if body:
        seal.parent = body
        seal.matrix_parent_inverse = body.matrix_world.inverted()

    others = [o.name for o in bpy.data.objects
              if o.name != SEAL_NAME and any(w in o.name.lower() for w in SEAL_WORDS)]
    report = dict(
        canopy=canopy.name,
        body=body.name if body else None,
        method=mode,
        seams=len(seams),
        points_along_seam=verts,
        deck_contact="%d/%d (%.1f%%)" % (stats["hit"], stats["total"], 100.0 * stats["hit"] / max(1, stats["total"])),
        glass_edge_buried_pct=round(100.0 * buried, 1),
        glass_edge_deck_contact_pct=round(100.0 * edge_contact, 1),
        skipped_points_not_touching_body=skipped,
        largest_gap_bridged_mm=round(stats["max_gap"] / k, 1),
        bead_outer_reach_units=round(dims[0], 5),
        car_length_units=round(car_len, 4),
        bodies_considered=len(bodies.items),
        seconds=round(time.time() - t_start, 2),
        other_seal_like_objects=others,
    )
    print("[canopy_seal]", report)
    return report


result = main()
