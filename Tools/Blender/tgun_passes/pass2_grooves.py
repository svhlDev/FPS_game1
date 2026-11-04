# Pass 2: panel grooves, plate pockets, stem slot, handle slits, seam recess. All are boolean cutters, so no
# block's base mesh changes. Coordinates are world units with the gun open (the block-out as given).
import bmesh
set_open(1.0)
GW, GD = 0.07, 0.04          # groove width and depth (0.9 mm x 0.5 mm)
CLEAR = 0.02                 # clearance round a plate or the stem
scene_coll = bpy.context.scene.collection
root_c = bpy.data.collections.get("TGun_Cutters") or bpy.data.collections.new("TGun_Cutters")
if root_c.name not in [c.name for c in scene_coll.children]:
    scene_coll.children.link(root_c)


def cutter_coll(target):
    name = "Cut_" + target
    c = bpy.data.collections.get(name) or bpy.data.collections.new(name)
    if c.name not in [k.name for k in root_c.children]:
        root_c.children.link(c)
    for x in list(c.objects):
        m = x.data; o.remove(x, do_unlink=True); bpy.data.meshes.remove(m)
    return c


def add_cutter(name, bm, target, coll):
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    ob = o.new(name, me); coll.objects.link(ob)
    part = o["Upper"] if target.startswith("Upper") else o["Receiver"]
    ob.parent = part; ob.matrix_parent_inverse = part.matrix_world.inverted(); ob.matrix_basis = Matrix.Identity(4)
    ob.display_type = "WIRE"; ob.hide_render = True
    return ob


def box(lo, hi):
    bm = bmesh.new(); lo, hi = Vector(lo), Vector(hi); c = (lo + hi) / 2; s = hi - lo
    bmesh.ops.create_cube(bm, size=1.0, matrix=Matrix.Translation(c) @ Matrix.Diagonal((*s, 1)))
    return bm


def ring_x(x0, a0, a1, width=GW, depth=GD):
    """Groove belt round a box section at x0: closed frame, width along x, depth into the box."""
    bm = bmesh.new(); e = 0.05
    inner = [(a0.y + depth, a0.z + depth), (a1.y - depth, a0.z + depth), (a1.y - depth, a1.z - depth), (a0.y + depth, a1.z - depth)]
    outer = [(a0.y - e, a0.z - e), (a1.y + e, a0.z - e), (a1.y + e, a1.z + e), (a0.y - e, a1.z + e)]
    loops = [[bm.verts.new((x, y, z)) for (y, z) in pts] for x in (x0 - width / 2, x0 + width / 2) for pts in (inner, outer)]
    i0, o0, i1, o1 = loops
    for k in range(4):
        j = (k + 1) % 4
        bm.faces.new((o0[k], o0[j], o1[j], o1[k])); bm.faces.new((i0[k], i1[k], i1[j], i0[j]))
        bm.faces.new((i0[k], i0[j], o0[j], o0[k])); bm.faces.new((i1[k], o1[k], o1[j], i1[j]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def frame_z(lo, hi, z_top, width=GW, depth=GD):
    """Rectangular groove on a top face: closed frame seen from above."""
    bm = bmesh.new()
    i = [(lo.x, lo.y), (hi.x, lo.y), (hi.x, hi.y), (lo.x, hi.y)]
    w = [(lo.x - width, lo.y - width), (hi.x + width, lo.y - width), (hi.x + width, hi.y + width), (lo.x - width, hi.y + width)]
    loops = [[bm.verts.new((x, y, z)) for (x, y) in pts] for z in (z_top - depth, z_top + 0.05) for pts in (i, w)]
    i0, o0, i1, o1 = loops
    for k in range(4):
        j = (k + 1) % 4
        bm.faces.new((o0[k], o0[j], o1[j], o1[k])); bm.faces.new((i0[k], i1[k], i1[j], i0[j]))
        bm.faces.new((i0[k], i0[j], o0[j], o0[k])); bm.faces.new((i1[k], o1[k], o1[j], i1[j]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def frame_y(lo, hi, y_face, sign, width=GW, depth=GD):
    """Rectangular groove on a side face (sign = +1 for the +Y face)."""
    bm = bmesh.new()
    i = [(lo.x, lo.z), (hi.x, lo.z), (hi.x, hi.z), (lo.x, hi.z)]
    w = [(lo.x - width, lo.z - width), (hi.x + width, lo.z - width), (hi.x + width, hi.z + width), (lo.x - width, hi.z + width)]
    loops = [[bm.verts.new((x, y, z)) for (x, z) in pts] for y in (y_face - sign * depth, y_face + sign * 0.05) for pts in (i, w)]
    i0, o0, i1, o1 = loops
    for k in range(4):
        j = (k + 1) % 4
        bm.faces.new((o0[k], o0[j], o1[j], o1[k])); bm.faces.new((i0[k], i1[k], i1[j], i0[j]))
        bm.faces.new((i0[k], i0[j], o0[j], o0[k])); bm.faces.new((i1[k], o1[k], o1[j], i1[j]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


A = {x.name: aabb(x) for x in blocks() + [o["Upper_StemHidden"]]}
ub, lb = A["Upper_Bar"], A["Receiver_Bar"]
cuts = {n: [] for n in ("Upper_Bar", "Receiver_Bar", "Receiver_Cap", "Receiver_Grip", "Upper_TopPlate", "Receiver_UnderPlate")}

# ---- pockets: plates that sink into the opposite bar at rest ----
for n in ("Upper_UnderFin", "Upper_UnderPlate_Rear", "Upper_UnderRail_L", "Upper_UnderRail_R"):
    a0, a1 = A[n]
    cuts["Receiver_Bar"].append(("Pocket_" + n, box((a0.x - CLEAR, a0.y - CLEAR, a0.z - TRAVEL - CLEAR), (min(a1.x, lb[1].x + 0.05) + CLEAR, a1.y + CLEAR, lb[1].z + 0.05))))
for n in ("Receiver_TopPlate_MidR", "Receiver_TopPlate_MidL", "Receiver_TopPlate_Rear"):
    a0, a1 = A[n]
    cuts["Upper_Bar"].append(("Pocket_" + n, box((a0.x - CLEAR, a0.y - CLEAR, ub[0].z - 0.05), (a1.x + CLEAR, a1.y + CLEAR, a1.z + TRAVEL + CLEAR))))

# ---- stem slot through the bar, cap and grip ----
hid = A["Upper_StemHidden"]; stem = A["Upper_Stem"]; pa = A["Upper_StemPlate_A"]; pb = A["Upper_StemPlate_B"]
top = lb[1].z + 0.05
for tgt in ("Receiver_Bar", "Receiver_Cap", "Receiver_Grip"):
    cuts[tgt].append(("Slot_Stem", box((hid[0].x - CLEAR, hid[0].y - CLEAR, hid[0].z - TRAVEL - 0.03), (hid[1].x + CLEAR, hid[1].y + CLEAR, top))))
    cuts[tgt].append(("Slot_PlateA", box((pa[0].x - CLEAR, pa[0].y - CLEAR, pa[0].z - TRAVEL - 0.03), (pa[1].x + CLEAR, pa[1].y + CLEAR, top))))
    cuts[tgt].append(("Slot_PlateB", box((pb[0].x - CLEAR, pb[0].y - CLEAR, pb[0].z - TRAVEL - 0.03), (pb[1].x + CLEAR, pb[1].y + CLEAR, top))))

# ---- handle slits: between the side plate and the grip's front corner, both sides ----
SLIT_X = (0.865, 0.965); SLIT_Z = (0.45, 7.35); SLIT_DEPTH_TO = 0.55
g0, g1 = A["Receiver_Grip"]
cuts["Receiver_Grip"].append(("Slit_R", box((SLIT_X[0], g0.y - 0.05, SLIT_Z[0]), (SLIT_X[1], -SLIT_DEPTH_TO, SLIT_Z[1]))))
cuts["Receiver_Grip"].append(("Slit_L", box((SLIT_X[0], SLIT_DEPTH_TO, SLIT_Z[0]), (SLIT_X[1], g1.y + 0.05, SLIT_Z[1]))))

# ---- seam recess along both top edges of the lower bar, for the length the bars overlap ----
SEAM = 0.05
cuts["Receiver_Bar"].append(("Seam_R", box((lb[0].x - 0.05, lb[0].y - 0.05, lb[1].z - SEAM), (lb[1].x + 0.05, lb[0].y + SEAM, lb[1].z + 0.05))))
cuts["Receiver_Bar"].append(("Seam_L", box((lb[0].x - 0.05, lb[1].y - SEAM, lb[1].z - SEAM), (lb[1].x + 0.05, lb[1].y + 0.05, lb[1].z + 0.05))))

# ---- panel grooves ----
for i, x in enumerate((-5.2, 4.7, 7.8, 9.1)):
    cuts["Upper_Bar"].append((f"Ring_{i}", ring_x(x, *ub)))
for i, x in enumerate((-4.75, -0.95, 4.78, 7.1)):
    cuts["Receiver_Bar"].append((f"Ring_{i}", ring_x(x, *lb)))
for i, x in enumerate((-3.6, 5.5, 8.0)):
    cuts["Upper_TopPlate"].append((f"Ring_{i}", ring_x(x, *A["Upper_TopPlate"])))
cuts["Receiver_UnderPlate"].append(("Ring_0", ring_x(4.4, *A["Receiver_UnderPlate"])))
# seam where the stem enters: a groove round the slot on the lower bar's top
cuts["Receiver_Bar"].append(("Frame_StemEntry", frame_z(Vector((hid[0].x - 0.12, hid[0].y - 0.12, 0)), Vector((pb[1].x + 0.1, hid[1].y + 0.12, 0)), lb[1].z)))
# access panel on the upper bar's left side, toward the rear
cz = (ub[0].z + ub[1].z) / 2
cuts["Upper_Bar"].append(("Frame_Access", frame_y(Vector((-9.2, 0, cz - 0.15)), Vector((-7.7, 0, cz + 0.15)), ub[1].y, 1)))
# charge port on the lower bar's rear end, facing the shooter
cy = (lb[0].y + lb[1].y) / 2; cz = (lb[0].z + lb[1].z) / 2 - SEAM / 2
cuts["Receiver_Bar"].append(("ChargePort", box((lb[0].x - 0.05, cy - 0.42, cz - 0.13), (lb[0].x + GD * 1.5, cy + 0.42, cz + 0.13))))

for tgt, items in cuts.items():
    c = cutter_coll(tgt)
    for nm, bm in items:
        add_cutter(f"{tgt}__{nm}", bm, tgt, c).hide_set(True)
    x = o[tgt]
    if "Grooves" in x.modifiers:
        x.modifiers.remove(x.modifiers["Grooves"])
    m = x.modifiers.new("Grooves", "BOOLEAN"); m.operation = "DIFFERENCE"; m.operand_type = "COLLECTION"; m.collection = c; m.solver = "EXACT"
    with bpy.context.temp_override(object=x):
        bpy.ops.object.modifier_move_to_index(modifier="Grooves", index=0)
bpy.context.view_layer.update()
root_lc = next(l for l in bpy.context.view_layer.layer_collection.children if l.name == "TGun_Cutters")

# ---- checks ----
import bmesh as _b
def nonmanifold(x):
    e = x.evaluated_get(bpy.context.evaluated_depsgraph_get()); me = e.to_mesh(); bm = _b.new(); bm.from_mesh(me)
    n = sum(1 for ed in bm.edges if not ed.is_manifold); bm.free(); e.to_mesh_clear(); return n

report = {"tris": {n: tri_count(o[n]) for n in cuts}, "nonmanifold": {n: nonmanifold(o[n]) for n in cuts}, "cutters": sum(len(v) for v in cuts.values())}
sil = {}
for n in cuts:
    a0, a1 = aabb(o[n]); b0, b1 = aabb(o[n], True)
    sil[n] = round(max((a0 - b0).length, (a1 - b1).length), 4)
report["silhouette_change"] = sil
report["total_tris_high"] = sum(tri_count(x) for x in blocks() + [o["Upper_StemHidden"]])
worst, missing = check_blockout(); report["blockout_vertex_change"] = round(worst, 6); report["missing"] = missing
print(report)

# Intersections at rest, on the evaluated meshes.
from mathutils.bvhtree import BVHTree
def tree(x):
    dg = bpy.context.evaluated_depsgraph_get(); e = x.evaluated_get(dg); me = e.to_mesh(); me.calc_loop_triangles()
    T = BVHTree.FromPolygons([x.matrix_world @ v.co for v in me.vertices], [tuple(t.vertices) for t in me.loop_triangles]); e.to_mesh_clear(); return T
set_open(0.0)
up = [x for x in o["Upper"].children if x.type == "MESH" and "__" not in x.name]; rc = [x for x in o["Receiver"].children if x.type == "MESH" and "__" not in x.name]
TU = {x.name: tree(x) for x in up}; TR = {x.name: tree(x) for x in rc}
hits = []
for u in up:
    au = aabb(u, True)
    for r in rc:
        ar = aabb(r, True)
        ov = [min(au[1][i], ar[1][i]) - max(au[0][i], ar[0][i]) for i in range(3)]
        if min(ov) > 0 and TU[u.name].overlap(TR[r.name]):
            hits.append((u.name, r.name, tuple(round(v, 3) for v in ov)))
print("intersections at rest:", len(hits))
for h in hits: print(h)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(DIR, "tgun_v4_grooves.blend"), check_existing=False)
files = views_sheet("pass2", "grooves")
print(bpy.data.filepath, len(files))
