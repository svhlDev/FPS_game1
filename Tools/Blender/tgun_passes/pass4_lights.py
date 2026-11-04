# Pass 4: lights. Seam, handle line and gap glow, on shared mode materials (blue stun, red lethal).
# Colours match LaserWeapon: stun (0.2, 0.6, 1), lethal (1, 0.12, 0.08). Mode x 2.5, gap x 6 (the beam's).
import bmesh
set_open(1.0)
A = {x.name: aabb(x) for x in blocks() + [o["Upper_StemHidden"]]}
ub, lb = A["Upper_Bar"], A["Receiver_Bar"]; gl = A["Receiver_Grip"]
STUN = (0.2, 0.6, 1.0); LETHAL = (1.0, 0.12, 0.08); SIGHT = (0.2, 1.0, 0.35)


def emissive(name, color, strength):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True; nt = m.node_tree; nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial"); em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Color"].default_value = (*color, 1); em.inputs["Strength"].default_value = strength
    nt.links.new(em.outputs[0], out.inputs["Surface"])
    return m


for nm, c in (("Stun", STUN), ("Lethal", LETHAL)):
    emissive("TG_Mode_" + nm, c, 2.5); emissive("TG_Gap_" + nm, c, 6.0)
emissive("TG_Sight", SIGHT, 3.0)


def box_into(bm, lo, hi):
    lo, hi = Vector(lo), Vector(hi); c = (lo + hi) / 2; s = hi - lo
    bmesh.ops.create_cube(bm, size=1.0, matrix=Matrix.Translation(c) @ Matrix.Diagonal((*s, 1)))


def light_part(name, part, mat, prop, boxes):
    bm = bmesh.new()
    for lo, hi in boxes:
        box_into(bm, lo, hi)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    me.materials.append(bpy.data.materials[mat])
    ob = o.get(name)
    if ob is None:
        ob = o.new(name, me); o["Upper"].users_collection[0].objects.link(ob)
    else:
        old = ob.data; ob.data = me; bpy.data.meshes.remove(old)
    p = o[part]; ob.parent = p; ob.matrix_parent_inverse = p.matrix_world.inverted(); ob.matrix_basis = Matrix.Identity(4)
    ob[prop] = 1
    return ob


# Gap seam: along both top edges of the lower bar, in the 0.05 recess, rising 0.09 into the 0.16 rest gap so the
# line reads from the side when collapsed. Upper_Bar's underside stays 0.07 clear of it.
SEAM_INSET, SEAM_W, SEAM_D, SEAM_UP = 0.008, 0.042, 0.05, 0.09
top = lb[1].z
light_part("Receiver_Seam", "Receiver", "TG_Mode_Stun", "mode_light", [
    ((lb[0].x, lb[0].y + SEAM_INSET, top - SEAM_D), (lb[1].x, lb[0].y + SEAM_INSET + SEAM_W, top + SEAM_UP)),
    ((lb[0].x, lb[1].y - SEAM_INSET - SEAM_W, top - SEAM_D), (lb[1].x, lb[1].y - SEAM_INSET, top + SEAM_UP)),
])

# Handle line: glowing fins inside the grip's slits, carried by the upper part, so the glow slides up the slit when
# the gun opens; plus emissive edges on the stem, seen in the gap when open.
SX0, SX1 = 0.875, 0.955
fin_open_lo = gl[0].z + 0.68 + TRAVEL; fin_open_hi = 7.30
sl, sh_ = A["Upper_StemHidden"]; st0, st1 = A["Upper_Stem"]
E, P = 0.03, 0.004
boxes = [((SX0, gl[0].y + 0.08, fin_open_lo), (SX1, -0.6, fin_open_hi)), ((SX0, 0.6, fin_open_lo), (SX1, gl[1].y - 0.08, fin_open_hi))]
for x0, x1 in ((st0.x - P, st0.x - P + E), (st1.x + P - E, st1.x + P)):
    for y0, y1 in ((st0.y - P, st0.y - P + E), (st1.y + P - E, st1.y + P)):
        boxes.append(((x0, y0, sl.z), (x1, y1, ub[0].z)))
light_part("Upper_HandleGlow", "Upper", "TG_Mode_Stun", "mode_light", boxes)

# Gap glow: faces on the lower bar's top and the upper bar's underside, only on while open.
T = 0.012
fin = A["Upper_UnderFin"]
mid = (0.85, 4.6)
lower = [((mid[0], -0.36, top), (mid[1], fin[0].y - 0.04, top + T)), ((mid[0], fin[1].y + 0.04, top), (mid[1], 0.22, top + T))]
pf = A["Receiver_TopPlate_Front"]
lower.append(((5.0, -0.3, pf[1].z), (7.4, 0.2, pf[1].z + T)))
light_part("Receiver_GapGlow", "Receiver", "TG_Gap_Stun", "gap_glow", lower)
und = ub[0].z
upper = [((mid[0], -0.36, und - T), (mid[1], fin[0].y - 0.04, und)), ((mid[0], fin[1].y + 0.04, und - T), (mid[1], 0.22, und))]
uf = A["Upper_UnderPlate_Front"]; rr, rl = A["Upper_UnderRail_R"], A["Upper_UnderRail_L"]
upper.append(((5.0, rr[1].y + 0.02, uf[0].z - T), (9.6, rl[0].y - 0.02, uf[0].z)))
light_part("Upper_GapGlow", "Upper", "TG_Gap_Stun", "gap_glow", upper)

# Sight dot on the post's rear face, facing the shooter.
sp = aabb(o["Upper_SightPost"])
yc = (sp[0].y + sp[1].y) / 2
light_part("Upper_SightDot", "Upper", "TG_Sight", "sight", [((sp[0].x - 0.012, yc - 0.045, sp[1].z - 0.13), (sp[0].x + 0.01, yc + 0.045, sp[1].z - 0.04))])

set_open(0.0)
sm = aabb(o["Receiver_Seam"]); ub_r = aabb(o["Upper_Bar"]); fin_r = aabb(o["Upper_HandleGlow"])
print({"seam_top": round(sm[1].z, 3), "upper_underside_rest": round(ub_r[0].z, 3), "seam_clearance_units": round(ub_r[0].z - sm[1].z, 3),
       "seam_visible_height_mm": round((sm[1].z - (top - SEAM_D)) * UNIT * 1000, 2)})
# Slit lit range at rest and open.
print({"fin_rest_z": (round(gl[0].z + 0.68, 3), round(fin_open_hi - TRAVEL, 3)), "fin_open_z": (round(fin_open_lo, 3), fin_open_hi), "slit_z": (0.45, 7.35), "hand_bottom_z": 1.581})
worst, missing = check_blockout(); print({"blockout_vertex_change": round(worst, 6), "missing": missing})
