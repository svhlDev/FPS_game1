# Step 0 of the block-out notes: snapshot, names, hidden stem, Grip, Muzzle and the open/rest control.
# No block is moved or resized. Scale is applied into the meshes so bevels stay even; world vertices are checked.
import bmesh

# 1. Snapshot of the block-out as given (world vertices), keyed by Simon's object names.
meshes = [x for x in o if x.type == "MESH" and (x.name in RENAME)]
assert len(meshes) == len(RENAME), (len(meshes), len(RENAME))
snap = {x.name: [list(p) for p in world_verts(x)] for x in meshes}
with open(SNAPSHOT, "w") as f:
    json.dump(snap, f)

# 2. Names, kept traceable, and scale applied into the mesh.
for x in meshes:
    x["blockout_name"] = x.name
    if x.data.users > 1:
        x.data = x.data.copy()
    me = x.data; sc_ = x.scale.copy()
    me.transform(Matrix.Diagonal((sc_.x, sc_.y, sc_.z, 1)))
    x.scale = (1, 1, 1)
    new = RENAME[x["blockout_name"]]
    x.name = new; me.name = new
bpy.context.view_layer.update()

# 3. Root at the Grip (hand centre). The 8 cm hand reaches the lower bar's underside and leaves 2.2 cm of grip showing.
U, R = o["Upper"], o["Receiver"]
coll = U.users_collection[0]
grip_lo, grip_hi = aabb(o["Receiver_Grip"])
bar_lo, bar_hi = aabb(o["Receiver_Bar"]); ub_lo, ub_hi = aabb(o["Upper_Bar"]); st_lo, st_hi = aabb(o["Upper_Stem"])
hand_units = 0.08 / UNIT
grip_pt = Vector(((grip_lo.x + grip_hi.x) / 2, (grip_lo.y + grip_hi.y) / 2, bar_lo.z - hand_units / 2 + 0.023))


def empty(name, kind="PLAIN_AXES", size=0.8):
    e = o.get(name) or o.new(name, None)
    if e.name not in coll.objects:
        coll.objects.link(e)
    e.empty_display_type = kind; e.empty_display_size = size
    return e


root = empty("TGun", "ARROWS", 2.0)
root.location = grip_pt; root.rotation_euler = (0, 0, 0); root.scale = (1, 1, 1)
bpy.context.view_layer.update()
for e in (U, R):
    w = e.matrix_world.copy()
    kids = {k: k.matrix_world.copy() for k in e.children}
    e.parent = root; e.matrix_parent_inverse = Matrix.Identity(4); e.location = w.translation - grip_pt
    bpy.context.view_layer.update()

root["open"] = 1.0
root["open_gap_units"] = OPEN_GAP
root["rest_gap_units"] = REST_GAP
root["travel_units"] = TRAVEL
root["unit_m"] = UNIT
root["note"] = "open: 0 at rest, 1 open (block-out as given). Upper moves along local +Z only."
z_open = U.location.z
ui = root.id_properties_ui("open"); ui.update(min=0.0, max=1.0, soft_min=0.0, soft_max=1.0)
U.driver_remove("location", 2)
fc = U.driver_add("location", 2); dr = fc.driver; dr.type = "SCRIPTED"
v = dr.variables.new(); v.name = "open"; v.type = "SINGLE_PROP"; v.targets[0].id = root; v.targets[0].data_path = '["open"]'
dr.expression = "%.6f - (1 - open) * %.6f" % (z_open, TRAVEL)
root.update_tag(); U.update_tag(); bpy.context.view_layer.update()

# 4. Grip and Muzzle. Muzzle: front of the gap, under the upper bar's overhang, centred, mid-height when open.
g = empty("Grip"); g.parent = root; g.matrix_parent_inverse = Matrix.Identity(4); g.location = (0, 0, 0)
muzzle_w = Vector((bar_hi.x, (bar_lo.y + bar_hi.y) / 2, (bar_hi.z + ub_lo.z) / 2))
m = empty("Muzzle"); m.parent = root; m.matrix_parent_inverse = Matrix.Identity(4); m.location = muzzle_w - grip_pt

# 5. Hidden part of the stem: same section as Upper_Stem, reaches 0.5 units above the grip's base at rest.
stem_bottom_open = grip_lo.z + 0.68 + TRAVEL     # 0.5 above the grip's base when collapsed
me = bpy.data.meshes.new("Upper_StemHidden"); bm = bmesh.new()
c = Vector(((st_lo.x + st_hi.x) / 2, (st_lo.y + st_hi.y) / 2, (stem_bottom_open + st_lo.z + 0.02) / 2))
size = Vector((st_hi.x - st_lo.x, st_hi.y - st_lo.y, st_lo.z + 0.02 - stem_bottom_open))
bmesh.ops.create_cube(bm, size=1.0, matrix=Matrix.Translation(c) @ Matrix.Diagonal((*size, 1)))
bm.to_mesh(me); bm.free()
sh = o.get("Upper_StemHidden")
if sh is None:
    sh = o.new("Upper_StemHidden", me); coll.objects.link(sh)
else:
    sh.data = me
sh.parent = U; sh.matrix_parent_inverse = U.matrix_world.inverted(); sh.matrix_basis = Matrix.Identity(4)
bpy.context.view_layer.update()

# 6. The 8 cm cube hand, for checking the wrap. Never exported.
me = bpy.data.meshes.new("Reference_Hand"); bm = bmesh.new()
bmesh.ops.create_cube(bm, size=hand_units); bm.to_mesh(me); bm.free()
h = o.get("Reference_Hand") or o.new("Reference_Hand", me); h.data = me
if h.name not in coll.objects:
    coll.objects.link(h)
h.parent = root; h.matrix_parent_inverse = Matrix.Identity(4); h.location = (0, 0, 0); h.display_type = "WIRE"; h.hide_render = True
bpy.context.view_layer.update()

worst, missing = check_blockout()
set_open(0.0)
sl, sh_hi = aabb(sh); gl, gh = aabb(o["Receiver_Grip"])
res = {"worst_vertex_change_units": round(worst, 6), "missing": missing, "grip_pt": tuple(round(a, 3) for a in grip_pt), "muzzle_world_open": tuple(round(a, 3) for a in muzzle_w),
       "rest": {"upper_bar_underside": round(aabb(o["Upper_Bar"])[0].z, 3), "lower_bar_top": round(aabb(o["Receiver_Bar"])[1].z, 3), "hidden_stem_bottom": round(sl.z, 3), "grip_base": round(gl.z, 3)}}
set_open(1.0)
sl, sh_hi = aabb(sh)
res["open"] = {"upper_bar_underside": round(aabb(o["Upper_Bar"])[0].z, 3), "hidden_stem_bottom": round(sl.z, 3), "stem_inside_grip_units": round(gh.z - sl.z, 3), "stem_inside_grip_cm": round((gh.z - sl.z) * UNIT * 100, 2)}
res["hand"] = {"bottom_z": round(grip_pt.z - hand_units / 2, 3), "top_z": round(grip_pt.z + hand_units / 2, 3), "grip_showing_cm": round((grip_pt.z - hand_units / 2 - gl.z) * UNIT * 100, 2)}
res["gun_m"] = {"length": round((ub_hi.x - ub_lo.x) * UNIT, 4), "open_gap": round(OPEN_GAP * UNIT, 4), "rest_gap": round(REST_GAP * UNIT, 4), "travel": round(TRAVEL * UNIT, 4), "muzzle_from_grip": tuple(round(a * UNIT, 4) for a in (muzzle_w - grip_pt))}
set_open(0.0)
print(res)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(DIR, "tgun_v2_setup.blend"), check_existing=False)
print(bpy.data.filepath)
