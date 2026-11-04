# Pass 5b (Simon): the inner parts, connecting down between the bars, are brushed steel. Light bare metal with
# visible brushing, in world space so rotated blocks line up: along the stem on the vertical parts, along the gun
# on the plates. The brushing also goes into the normal and roughness bakes.
VERTICAL = {"Upper_Stem", "Upper_StemHidden", "Upper_StemPlate_A", "Upper_StemPlate_B", "Receiver_Cap"}
LENGTHWISE = {"Upper_UnderFin", "Upper_UnderPlate_Rear", "Upper_UnderPlate_Front", "Upper_UnderRail_L", "Upper_UnderRail_R",
              "Receiver_TopPlate_MidR", "Receiver_TopPlate_MidL", "Receiver_TopPlate_Rear", "Receiver_TopPlate_Front"}
FINE = 22.0      # brush lines per unit across the grain (about 1.8 per mm in Unity)
ALONG = 0.15     # stretch along the grain


def steel(name, scale):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True; nt = m.node_tree; nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial"); p = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(p.outputs[0], out.inputs["Surface"])
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    mp = nt.nodes.new("ShaderNodeMapping"); mp.inputs["Scale"].default_value = scale
    nt.links.new(geo.outputs["Position"], mp.inputs["Vector"])
    n = nt.nodes.new("ShaderNodeTexNoise"); n.inputs["Scale"].default_value = 1.0; n.inputs["Detail"].default_value = 4.0; n.inputs["Roughness"].default_value = 0.55
    nt.links.new(mp.outputs[0], n.inputs["Vector"])
    # Noise sits around 0.5: spread it to the range the base colour and roughness use.
    r = nt.nodes.new("ShaderNodeMapRange"); r.inputs["From Min"].default_value = 0.3; r.inputs["From Max"].default_value = 0.7
    nt.links.new(n.outputs[0], r.inputs["Value"])
    mix = nt.nodes.new("ShaderNodeMix"); mix.data_type = "RGBA"
    nt.links.new(r.outputs[0], mix.inputs[0]); mix.inputs[6].default_value = (0.42, 0.43, 0.45, 1); mix.inputs[7].default_value = (0.62, 0.63, 0.65, 1)
    nt.links.new(mix.outputs[2], p.inputs["Base Color"])
    p.inputs["Metallic"].default_value = 1.0
    rough = nt.nodes.new("ShaderNodeMapRange"); rough.inputs["To Min"].default_value = 0.42; rough.inputs["To Max"].default_value = 0.24
    nt.links.new(r.outputs[0], rough.inputs["Value"])
    nt.links.new(rough.outputs[0], p.inputs["Roughness"])
    b = nt.nodes.new("ShaderNodeBump"); b.inputs["Strength"].default_value = 0.2; b.inputs["Distance"].default_value = 0.006
    nt.links.new(n.outputs[0], b.inputs["Height"]); nt.links.new(b.outputs[0], p.inputs["Normal"])
    return m


vert = steel("TG_BrushedSteel_Vertical", (FINE, FINE, ALONG))
long_ = steel("TG_BrushedSteel_Lengthwise", (ALONG, FINE, FINE))
for names, mat in ((VERTICAL, vert), (LENGTHWISE, long_)):
    for n in names:
        me = o[n].data; me.materials.clear(); me.materials.append(mat)
bpy.context.view_layer.update()
worst, missing = check_blockout()
print({"vertical": len(VERTICAL), "lengthwise": len(LENGTHWISE), "blockout_vertex_change": round(worst, 6), "missing": missing,
       "still_dark_brushed": sorted(x.name for x in blocks() if x.data.materials[0].name == "TG_Brushed")})
set_open(0.0)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(DIR, "tgun_v9_steel.blend"), check_existing=False)
print(bpy.data.filepath)
