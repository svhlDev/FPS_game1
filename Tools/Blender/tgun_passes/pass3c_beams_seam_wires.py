# Pass 3c (Simon): two energy beams, a chrome seam and a pair of wires along the lower receiver.
# Beams: vertical columns in the mode colour just ahead of the stem, from the lower bar up to the upper bar, between
# steel emitters, only on while open. They belong to the Receiver with their origin on the lower bar's top, and Unity
# stretches them with the gap (TGunAction). Seam: the strips along the lower bar's edges are solid chrome, no glow.
# Wires: black and orange along the lower bar's left side (the side the shooter sees), in steel clips, from a socket
# toward the rear, under the bar's edge and into the under plate. They stop short of the vents.
# All of this is real geometry and is not in the bake.
import bmesh
set_open(1.0)
A = {x.name: aabb(x) for x in blocks()}
under = A["Upper_UnderPlate_Rear"]; bar = A["Receiver_Bar"]; plate = A["Receiver_UnderPlate"]
bar_top = bar[1].z
coll = o["Upper"].users_collection[0]

# Earlier versions are gone.
for n in ("Upper_Wires", "Receiver_WireCollars", "Upper_EnergyBeams", "Upper_BeamEmitters", "Receiver_BeamEmitters", "Receiver_LightBars", "Receiver_LightBarTips", "Receiver_EnergyBeams"):
    x = o.get(n)
    if x is not None:
        m = x.data; o.remove(x, do_unlink=True); bpy.data.meshes.remove(m)


def tube(bm, pts, r, mi, segs=5):
    rings = []; n = len(pts)
    for i in range(n):
        a, b = pts[max(i - 1, 0)], pts[min(i + 1, n - 1)]
        T = (b - a).normalized(); ref = Vector((0, 0, 1)) if abs(T.z) < 0.9 else Vector((1, 0, 0))
        N = (ref - T * ref.dot(T)).normalized(); B = T.cross(N)
        rings.append([bm.verts.new(pts[i] + (N * math.cos(2 * math.pi * k / segs) + B * math.sin(2 * math.pi * k / segs)) * r) for k in range(segs)])
    for i in range(n - 1):
        for k in range(segs):
            f = bm.faces.new((rings[i][k], rings[i + 1][k], rings[i + 1][(k + 1) % segs], rings[i][(k + 1) % segs])); f.material_index = mi; f.smooth = True
    for ring, rev in ((rings[0], True), (rings[-1], False)):
        f = bm.faces.new(list(reversed(ring)) if rev else ring); f.material_index = mi; f.smooth = True


def collar(bm, c, r, h, mi=0, segs=8):
    res = bmesh.ops.create_cone(bm, cap_ends=True, segments=segs, radius1=r, radius2=r, depth=h, matrix=Matrix.Translation(Vector(c)))
    for f in {f for v in res["verts"] for f in v.link_faces}:
        f.material_index = mi


def box(bm, lo, hi, mi=0):
    lo, hi = Vector(lo), Vector(hi); c = (lo + hi) / 2; s = hi - lo
    res = bmesh.ops.create_cube(bm, size=1.0, matrix=Matrix.Translation(c) @ Matrix.Diagonal((*s, 1)))
    for f in {f for v in res["verts"] for f in v.link_faces}:
        f.material_index = mi


def add(name, bm, part, mats):
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    for m in mats:
        me.materials.append(bpy.data.materials[m])
    ob = o.get(name)
    if ob is None:
        ob = o.new(name, me); coll.objects.link(ob)
    else:
        old = ob.data; ob.data = me; bpy.data.meshes.remove(old)
    p = o[part]; ob.parent = p; ob.matrix_parent_inverse = p.matrix_world.inverted(); ob.matrix_basis = Matrix.Identity(4)
    return ob


# ---- chrome seam ----
m = bpy.data.materials.get("TG_Chrome") or bpy.data.materials.new("TG_Chrome")
m.use_nodes = True; pr = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
pr.inputs["Base Color"].default_value = (0.85, 0.86, 0.88, 1); pr.inputs["Metallic"].default_value = 1.0; pr.inputs["Roughness"].default_value = 0.06
seam = o["Receiver_Seam"]
seam.data.materials[0] = m
if "mode_light" in seam:
    del seam["mode_light"]

# ---- energy beams ----
X = 1.0                                  # just ahead of the stem and its plates (front at x 0.71)
Y_A, Y_B = -0.229, 0.159                 # either side of the fin, on the gap glow plates
BOTTOM, TOP = bar_top, A["Upper_Bar"][0].z
R_BEAM = 0.06                            # 1.5 mm thick in Unity
bm = bmesh.new()
for y in (Y_A, Y_B):
    tube(bm, [Vector((X, y, BOTTOM)), Vector((X, y, TOP))], R_BEAM, 0, 6)
beams = add("Receiver_EnergyBeams", bm, "Receiver", ["TG_Gap_Stun"])
beams["gap_glow"] = 1
bm = bmesh.new()
for y in (Y_A, Y_B):
    collar(bm, (X, y, TOP - 0.04), 0.095, 0.09)
emit_u = add("Upper_BeamEmitters", bm, "Upper", ["TG_Steel"])
bm = bmesh.new()
for y in (Y_A, Y_B):
    collar(bm, (X, y, bar_top + 0.02), 0.095, 0.05)
emit_r = add("Receiver_BeamEmitters", bm, "Receiver", ["TG_Steel"])
bpy.context.view_layer.update()

# ---- wires along the lower receiver ----
R_WIRE = 0.04                                   # 1 mm thick in Unity
FACE = bar[1].y + R_WIRE - 0.005                # lying on the bar's left side
Z_BLACK, Z_ORANGE = 8.10, 8.24
START, RUN_END = -7.05, 4.0                     # the vents start at x 5.2
CLIPS = (-5.9, -3.3, -0.3, 2.3)
PLATE_Y = plate[1].y


def wire(z, x_end, extra):
    p = [Vector((START, FACE, z))]
    for x in CLIPS:
        p += [Vector((x, FACE, z))]
    p += [Vector((RUN_END, FACE, z))]
    # Round the bar's bottom edge and into the under plate.
    p += [Vector((RUN_END + 0.22, FACE + 0.005, z - 0.14 - extra)), Vector((RUN_END + 0.42, FACE - 0.005, bar[0].z - 0.02 - extra)),
          Vector((RUN_END + 0.62, FACE - 0.08, bar[0].z - 0.14 - extra)), Vector((x_end, PLATE_Y + 0.02, bar[0].z - 0.24 - extra))]
    return p


bm = bmesh.new()
tube(bm, wire(Z_BLACK, RUN_END + 0.85, 0.0), R_WIRE, 0)
tube(bm, wire(Z_ORANGE, RUN_END + 1.05, 0.12), R_WIRE, 1)
for x in CLIPS:
    box(bm, (x - 0.05, bar[1].y - 0.005, Z_BLACK - 0.07), (x + 0.05, bar[1].y + 0.11, Z_ORANGE + 0.07), 2)
# Socket toward the rear where the pair leaves the bar.
box(bm, (START - 0.26, bar[1].y - 0.005, Z_BLACK - 0.09), (START + 0.02, bar[1].y + 0.11, Z_ORANGE + 0.09), 2)
wires = add("Receiver_Wires", bm, "Receiver", ["TG_Wire_Black", "TG_Wire_Orange", "TG_Steel"])
bpy.context.view_layer.update()

# ---- checks ----
set_open(1.0)
lo, hi = aabb(beams); ub, lb = aabb(o["Upper_Bar"]), aabb(o["Receiver_Bar"]); stem_front = aabb(o["Upper_StemPlate_B"])[1].x
print({"beams_open": {"x": (round(lo.x, 3), round(hi.x, 3)), "y": (round(lo.y, 3), round(hi.y, 3)), "z": (round(lo.z, 3), round(hi.z, 3))}, "bar_top": round(lb[1].z, 3), "upper_underside": round(ub[0].z, 3),
       "ahead_of_stem_units": round(lo.x - stem_front, 3), "beams_hidden_open": beams.hide_render, "seam_material": seam.data.materials[0].name})
set_open(0.0)
eu, er = aabb(emit_u), aabb(emit_r)
print({"at_rest": {"beams_hidden": beams.hide_render, "emitters_clear_units": round(eu[0].z - er[1].z, 3)}})
# The wires clear the upper part at rest and the vents, and how far they stand off the side.
wv = world_verts(wires)
ub = aabb(o["Upper_Bar"])
print({"wire_top_z": round(max(v.z for v in wv), 3), "upper_underside_rest": round(ub[0].z, 3), "seam_recess_bottom": round(bar_top - 0.05, 3),
       "wire_x": (round(min(v.x for v in wv), 3), round(max(v.x for v in wv), 3)), "wire_y_max": round(max(v.y for v in wv), 3), "bar_y_max": round(bar[1].y, 3),
       "stand_off_mm": round((max(v.y for v in wv) - bar[1].y) * UNIT * 1000, 2), "hand_top_z": 7.981, "wire_low_z": round(min(v.z for v in wv), 3),
       "end_vs_plate": (round(min(v.y for v in wv if v.x > 4.5), 3), round(PLATE_Y, 3))})
print({"tris": {x.name: tri_count(x) for x in (beams, emit_u, emit_r, wires, seam)}})
worst, missing = check_blockout(); print({"blockout_vertex_change": round(worst, 6), "missing": missing})
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(DIR, "tgun_v13_beams_chrome_seam.blend"), check_existing=False)
print(bpy.data.filepath)
