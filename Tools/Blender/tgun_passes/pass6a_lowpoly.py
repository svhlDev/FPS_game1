# Pass 6a: low-poly for the game. Each part's blocks are united (no hidden inner faces), the only real cuts kept are
# the seam recess and the handle slits (they hold the light parts); everything else gets baked. Built in the same
# units and place as the high-poly, open, so the bake lines up. export_as names what each stands in for.
import bmesh
set_open(1.0)
scene_coll = bpy.context.scene.collection
ex = bpy.data.collections.get("TGun_Export") or bpy.data.collections.new("TGun_Export")
if ex.name not in [c.name for c in scene_coll.children]:
    scene_coll.children.link(ex)
tmp = bpy.data.collections.new("TGun_Tmp"); scene_coll.children.link(tmp)
ops = bpy.data.collections.new("TGun_TmpOps"); tmp.children.link(ops)
dg = bpy.context.evaluated_depsgraph_get()

parts = {"Receiver": [x for x in blocks() if x.name.startswith("Receiver_")],
         "Upper": [x for x in blocks() if x.name.startswith("Upper_")] + [o["Upper_StemHidden"]]}
KEEP_CUTS = {"Receiver_Bar": ("Seam_R", "Seam_L"), "Receiver_Grip": ("Slit_R", "Slit_L")}
report = {}


def base_copy(x, name, coll):
    me = x.data.copy(); me.name = name; me.transform(x.matrix_world); me.materials.clear()
    c = o.new(name, me); coll.objects.link(c)
    return c


for part, members in parts.items():
    name = "EX_" + part
    copies = [base_copy(x, f"LPsrc_{x.name}", tmp) for x in members]
    base = copies[0]
    for c in copies[1:]:
        tmp.objects.unlink(c); ops.objects.link(c)
    m = base.modifiers.new("Union", "BOOLEAN"); m.operation = "UNION"; m.operand_type = "COLLECTION"; m.collection = ops; m.solver = "EXACT"
    bpy.context.view_layer.update(); dg = bpy.context.evaluated_depsgraph_get()
    united = bpy.data.meshes.new_from_object(base.evaluated_get(dg))
    for c in copies:
        mm = c.data; o.remove(c, do_unlink=True); bpy.data.meshes.remove(mm)
    # the cuts that stay real
    holder = o.new("LPtmp", united); tmp.objects.link(holder)
    for tgt, names in KEEP_CUTS.items():
        if not tgt.startswith(part):
            continue
        for n in names:
            cutter = o[f"{tgt}__{n}"]
            mod = holder.modifiers.new(n, "BOOLEAN"); mod.operation = "DIFFERENCE"; mod.object = cutter; mod.solver = "EXACT"
    bpy.context.view_layer.update(); dg = bpy.context.evaluated_depsgraph_get()
    cut = bpy.data.meshes.new_from_object(holder.evaluated_get(dg))
    o.remove(holder, do_unlink=True); bpy.data.meshes.remove(united)
    bm = bmesh.new(); bm.from_mesh(cut)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0005)
    bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(0.5), verts=bm.verts, edges=bm.edges)
    bm.to_mesh(cut); bm.free()
    for p in cut.polygons:
        p.use_smooth = True
    old = o.get(name)
    if old is not None:
        om = old.data; o.remove(old, do_unlink=True); bpy.data.meshes.remove(om)
    cut.name = name
    lp = o.new(name, cut); ex.objects.link(lp); lp["export_as"] = part
    b = lp.modifiers.new("Bevel_Edge", "BEVEL"); b.width = 0.025; b.segments = 1; b.limit_method = "ANGLE"; b.angle_limit = math.radians(30); b.use_clamp_overlap = True
    w = lp.modifiers.new("WeightedNormal", "WEIGHTED_NORMAL"); w.mode = "FACE_AREA"; w.weight = 50
    lp.hide_render = True
    bpy.context.view_layer.update()
    report[part] = {"faces_base": len(cut.polygons), "tris": tri_count(lp)}

bpy.data.collections.remove(ops); bpy.data.collections.remove(tmp)

# Deviation from the block-out: every block vertex should lie on or inside the low-poly's surface region.
from mathutils.bvhtree import BVHTree
dg = bpy.context.evaluated_depsgraph_get()
for part, members in parts.items():
    lp = o["EX_" + part]; e = lp.evaluated_get(dg); me = e.to_mesh(); me.calc_loop_triangles(); mw = lp.matrix_world
    T = BVHTree.FromPolygons([mw @ v.co for v in me.vertices], [tuple(t.vertices) for t in me.loop_triangles])
    lo = [mw @ v.co for v in me.vertices]; bm = bmesh.new(); bm.from_mesh(me)
    report[part]["nonmanifold"] = sum(1 for ed in bm.edges if not ed.is_manifold); bm.free(); e.to_mesh_clear()
    blo = Vector((1e9,) * 3); bhi = Vector((-1e9,) * 3)
    for x in members:
        a0, a1 = aabb(x); blo = Vector(map(min, blo, a0)); bhi = Vector(map(max, bhi, a1))
    llo = Vector([min(p[i] for p in lo) for i in range(3)]); lhi = Vector([max(p[i] for p in lo) for i in range(3)])
    report[part]["bounds_change"] = round(max((llo - blo).length, (lhi - bhi).length), 4)

# UVs for both parts together, one shared sheet.
lps = [o["EX_Receiver"], o["EX_Upper"]]
for x in bpy.context.selected_objects:
    x.select_set(False)
for x in lps:
    x.select_set(True)
    if not x.data.uv_layers:
        x.data.uv_layers.new(name="UVMap")
bpy.context.view_layer.objects.active = lps[0]
with bpy.context.temp_override(active_object=lps[0], object=lps[0], selected_objects=lps, selected_editable_objects=lps):
    bpy.ops.object.mode_set(mode="EDIT"); bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.003, area_weight=0.0, correct_aspect=True, scale_to_bounds=False)
    try:
        bpy.ops.uv.pack_islands(rotate=True, margin=0.004)
    except Exception as err:
        print("pack:", err)
    bpy.ops.object.mode_set(mode="OBJECT")
area3 = area2 = 0.0; inside = True
for lp in lps:
    me = lp.data; uv = me.uv_layers.active.data
    for p in me.polygons:
        area3 += p.area; pts = [uv[i].uv for i in p.loop_indices]; a = 0.0
        for i in range(len(pts)):
            x1, y1 = pts[i]; x2, y2 = pts[(i + 1) % len(pts)]; a += x1 * y2 - x2 * y1
        area2 += abs(a) / 2
    inside = inside and all(0 <= d.uv.x <= 1 and 0 <= d.uv.y <= 1 for d in uv)
texel_unit = math.sqrt(area2 * 2048 * 2048 / area3)
report["uv"] = {"fill_pct": round(area2 * 100, 1), "surface_units2": round(area3, 1), "px_per_unit_2048": round(texel_unit, 1), "mm_per_px": round(UNIT * 1000 / texel_unit, 3), "in_0_1": inside}
worst, missing = check_blockout(); report["blockout_vertex_change"] = round(worst, 6)
set_open(0.0)
print(report)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(DIR, "tgun_v7_lowpoly.blend"), check_existing=False)
print(bpy.data.filepath)
