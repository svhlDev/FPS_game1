# Pass 3: functional greebles, concentrated near the gap and the front; the rear of the bars stays calm.
# Vent slats and grip ribs are cut in (no silhouette change). Bolts, pins and the sight sit on the surfaces.
import bmesh
set_open(1.0)
root_c = bpy.data.collections["TGun_Cutters"]
A = {x.name: aabb(x) for x in blocks()}
ub, lb = A["Upper_Bar"], A["Receiver_Bar"]


def box(lo, hi):
    bm = bmesh.new(); lo, hi = Vector(lo), Vector(hi); c = (lo + hi) / 2; s = hi - lo
    bmesh.ops.create_cube(bm, size=1.0, matrix=Matrix.Translation(c) @ Matrix.Diagonal((*s, 1)))
    return bm


def add_cuts(target, items):
    x = o[target]; name = "Cut_" + target
    c = bpy.data.collections.get(name) or bpy.data.collections.new(name)
    if c.name not in [k.name for k in root_c.children]:
        root_c.children.link(c)
    for ob in list(c.objects):
        if "__G3_" in ob.name:
            m = ob.data; o.remove(ob, do_unlink=True); bpy.data.meshes.remove(m)
    part = o["Upper"] if target.startswith("Upper") else o["Receiver"]
    for nm, bm in items:
        me = bpy.data.meshes.new(nm); bm.to_mesh(me); bm.free()
        ob = o.new(f"{target}__G3_{nm}", me); c.objects.link(ob)
        ob.parent = part; ob.matrix_parent_inverse = part.matrix_world.inverted(); ob.matrix_basis = Matrix.Identity(4)
        ob.display_type = "WIRE"; ob.hide_render = True; ob.hide_set(True)
    if "Grooves" not in x.modifiers:
        m = x.modifiers.new("Grooves", "BOOLEAN"); m.operation = "DIFFERENCE"; m.operand_type = "COLLECTION"; m.collection = c; m.solver = "EXACT"
        with bpy.context.temp_override(object=x):
            bpy.ops.object.modifier_move_to_index(modifier="Grooves", index=0)


# ---- vent slats: heat from the beam, on the bars' sides just behind the gap's front ----
SLOT_W, SLOT_D, SLOT_H, PITCH = 0.09, 0.06, 0.36, 0.22
def vents(a, x0, n, zc):
    items = []
    for i in range(n):
        x = x0 + i * PITCH
        items.append((f"VentR{i}", box((x, a[0].y - 0.05, zc - SLOT_H / 2), (x + SLOT_W, a[0].y + SLOT_D, zc + SLOT_H / 2))))
        items.append((f"VentL{i}", box((x, a[1].y - SLOT_D, zc - SLOT_H / 2), (x + SLOT_W, a[1].y + 0.05, zc + SLOT_H / 2))))
    return items
add_cuts("Upper_Bar", vents(ub, 5.3, 6, (ub[0].z + ub[1].z) / 2))
add_cuts("Receiver_Bar", vents(lb, 5.2, 5, (lb[0].z + lb[1].z - 0.05) / 2))

# ---- grip texture band: ribs on the lower grip plates, where the grip shows below the hand ----
RIB_W, RIB_D, RIB_P = 0.05, 0.03, 0.14
zs = [0.25 + i * RIB_P for i in range(9)]
for side, nm in ((-1, "Receiver_GripSideLow_R"), (1, "Receiver_GripSideLow_L")):
    a = A[nm]; face = a[0].y if side < 0 else a[1].y
    y0, y1 = (face - 0.05, face + RIB_D) if side < 0 else (face - RIB_D, face + 0.05)
    add_cuts(nm, [(f"Rib{i}", box((a[0].x - 0.05, y0, z), (a[1].x + 0.05, y1, z + RIB_W))) for i, z in enumerate(zs)])
a = A["Receiver_GripBack_A"]
add_cuts("Receiver_GripBack_A", [(f"Rib{i}", box((a[0].x - 0.05, a[0].y - 0.05, z), (a[0].x + RIB_D, a[1].y + 0.05, z + RIB_W))) for i, z in enumerate(zs)])

# ---- bolts, sight, charge port pins ----
def hex_bolt(bm, c, n, r=0.1, h=0.035, segs=6):
    n = Vector(n).normalized(); M = n.to_track_quat("Z", "Y").to_matrix().to_4x4(); M.translation = Vector(c) + n * (h / 2 - 0.005)
    bmesh.ops.create_cone(bm, cap_ends=True, segments=segs, radius1=r, radius2=r, depth=h, matrix=M)


def finish(name, bm, part, mats):
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    for mn in mats:
        me.materials.append(bpy.data.materials.get(mn) or bpy.data.materials.new(mn))
    ob = o.get(name)
    if ob is None:
        ob = o.new(name, me); o["Upper"].users_collection[0].objects.link(ob)
    else:
        old = ob.data; ob.data = me; bpy.data.meshes.remove(old)
    p = o[part]; ob.parent = p; ob.matrix_parent_inverse = p.matrix_world.inverted(); ob.matrix_basis = Matrix.Identity(4)
    for f in me.polygons:
        f.use_smooth = False
    return ob


tp = A["Upper_TopPlate"]; UP = Vector((0, 0, 1)); yr, yl = tp[0].y + 0.095, tp[1].y - 0.095
bu = bmesh.new()
for x in (5.72, 7.78, 8.82, -9.15):
    for y in (yr, yl):
        hex_bolt(bu, (x, y, tp[1].z), UP)
finish("Upper_Bolts", bu, "Upper", ["TG_Steel"])

# Front sight: a post between the two prongs, inside their height.
pr, pl = A["Upper_Prong_R"], A["Upper_Prong_L"]
yc = (pr[1].y + pl[0].y) / 2
bs = box((9.45, yc - 0.06, ub[1].z - 0.02), (9.7, yc + 0.06, ub[1].z + 0.62))
finish("Upper_SightPost", bs, "Upper", ["TG_Steel"])

br = bmesh.new()
for nm, face, sgn in (("Receiver_FrontBlock_R", A["Receiver_FrontBlock_R"][0].y, -1), ("Receiver_FrontBlock_L", A["Receiver_FrontBlock_L"][1].y, 1)):
    a = A[nm]
    for x in (a[0].x + 0.2, a[1].x - 0.2):
        hex_bolt(br, (x, face, 7.65), (0, sgn, 0), 0.09)
a = A["Receiver_GripFront_D"]
for y in (a[0].y + 0.14, a[1].y - 0.14):
    for z in (a[0].z + 0.16, a[1].z - 0.16):
        hex_bolt(br, (a[1].x, y, z), (1, 0, 0), 0.09)
# Charge port contacts in the recess on the lower bar's rear end.
cy = (lb[0].y + lb[1].y) / 2; cz = (lb[0].z + lb[1].z) / 2 - 0.025
for dy in (-0.24, 0.0, 0.24):
    hex_bolt(br, (lb[0].x + 0.06, cy + dy, cz), (-1, 0, 0), 0.05, 0.05, 8)
finish("Receiver_Bolts", br, "Receiver", ["TG_Steel"])

bpy.context.view_layer.update()
parts = blocks() + [o[n] for n in ("Upper_StemHidden", "Upper_Bolts", "Upper_SightPost", "Receiver_Bolts")]
worst, missing = check_blockout()
# Whole-gun silhouette: evaluated bounds of everything against the block-out's bounds, open.
lo = Vector((1e9,) * 3); hi = Vector((-1e9,) * 3)
for x in parts:
    a0, a1 = aabb(x, True)
    lo = Vector(map(min, lo, a0)); hi = Vector(map(max, hi, a1))
blo = Vector((1e9,) * 3); bhi = Vector((-1e9,) * 3)
for x in blocks():
    a0, a1 = aabb(x)
    blo = Vector(map(min, blo, a0)); bhi = Vector(map(max, bhi, a1))
print({"high_tris": sum(tri_count(x) for x in parts), "blockout_vertex_change": round(worst, 6), "missing": missing,
       "gun_bounds_change": tuple(round(v, 4) for v in (lo - blo)) + tuple(round(v, 4) for v in (hi - bhi)),
       "sight_top_vs_prongs": (round(aabb(o["Upper_SightPost"])[1].z, 3), round(max(pr[1].z, pl[1].z), 3)),
       "bolts": {"upper": len(o["Upper_Bolts"].data.polygons), "receiver": len(o["Receiver_Bolts"].data.polygons)}})
set_open(0.0)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(DIR, "tgun_v5_greebles.blend"), check_existing=False)
print(bpy.data.filepath, len(views_sheet("pass3", "greebles")))
