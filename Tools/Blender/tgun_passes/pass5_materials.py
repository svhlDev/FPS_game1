# Pass 5: materials. Gunmetal: painted, worn to bare metal on the corners. Brushed steel on the inner plates and
# the stem. Polymer grip. Edge wear uses the Bevel node, so it shows in Cycles and gets baked for Unity.
GUNMETAL = {"Upper_Bar", "Receiver_Bar", "Upper_TopPlate", "Upper_TopPlate_Small", "Receiver_TopPlate_MidR", "Receiver_TopPlate_MidL", "Receiver_TopPlate_Rear",
            "Receiver_TopPlate_Front", "Receiver_Cap", "Receiver_UnderPlate", "Receiver_FrontBlock_R", "Receiver_FrontBlock_L", "Receiver_FrontTab_L", "Receiver_FrontTab_R",
            "Upper_Prong_R", "Upper_Prong_L"}
BRUSHED = {"Upper_Stem", "Upper_StemHidden", "Upper_StemPlate_A", "Upper_StemPlate_B", "Upper_UnderFin", "Upper_UnderPlate_Rear", "Upper_UnderPlate_Front",
           "Upper_UnderRail_L", "Upper_UnderRail_R", "Receiver_Trigger_A", "Receiver_Trigger_B"}
POLYMER = {"Receiver_Grip", "Receiver_GripSide_R", "Receiver_GripSide_L", "Receiver_GripSideLow_R", "Receiver_GripSideLow_L", "Receiver_GripBack", "Receiver_GripBack_A",
           "Receiver_GripBack_B", "Receiver_GripBack_C", "Receiver_GripFront_A", "Receiver_GripFront_B", "Receiver_GripFront_C", "Receiver_GripFront_D"}
STEEL = {"Upper_Bolts", "Receiver_Bolts", "Upper_SightPost"}


def new_mat(name):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True; nt = m.node_tree; nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial"); p = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(p.outputs[0], out.inputs["Surface"])
    return m, nt, p


def mix_color(nt, fac, a, b):
    n = nt.nodes.new("ShaderNodeMix"); n.data_type = "RGBA"
    nt.links.new(fac, n.inputs[0]); n.inputs[6].default_value = (*a, 1); n.inputs[7].default_value = (*b, 1)
    return n.outputs[2]


def mix_val(nt, fac, a, b):
    n = nt.nodes.new("ShaderNodeMix"); n.data_type = "FLOAT"
    nt.links.new(fac, n.inputs[0]); n.inputs[2].default_value = a; n.inputs[3].default_value = b
    return n.outputs[0]


def math_(nt, op, a, b=None, clamp=False):
    n = nt.nodes.new("ShaderNodeMath"); n.operation = op; n.use_clamp = clamp
    for i, v in enumerate((a, b)):
        if v is None: continue
        if isinstance(v, (int, float)): n.inputs[i].default_value = v
        else: nt.links.new(v, n.inputs[i])
    return n.outputs[0]


def edge_mask(nt, radius=0.035, gain=7.0, patch_scale=3.5, patch_lo=0.42, patch_hi=0.62):
    """1 on worn corners: Bevel normal against the true normal, broken up by object-space noise."""
    bev = nt.nodes.new("ShaderNodeBevel"); bev.samples = 6; bev.inputs["Radius"].default_value = radius
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    dot = nt.nodes.new("ShaderNodeVectorMath"); dot.operation = "DOT_PRODUCT"
    nt.links.new(bev.outputs[0], dot.inputs[0]); nt.links.new(geo.outputs["True Normal"], dot.inputs[1])
    curv = math_(nt, "SUBTRACT", 1.0, dot.outputs["Value"], clamp=True)
    tc = nt.nodes.new("ShaderNodeTexCoord")
    noise = nt.nodes.new("ShaderNodeTexNoise"); noise.inputs["Scale"].default_value = patch_scale; noise.inputs["Detail"].default_value = 6.0; noise.inputs["Roughness"].default_value = 0.6
    nt.links.new(tc.outputs["Object"], noise.inputs["Vector"])
    ramp = nt.nodes.new("ShaderNodeMapRange"); ramp.inputs["From Min"].default_value = patch_lo; ramp.inputs["From Max"].default_value = patch_hi
    nt.links.new(noise.outputs[0], ramp.inputs["Value"])
    return math_(nt, "MULTIPLY", math_(nt, "MULTIPLY", curv, gain, clamp=True), ramp.outputs[0], clamp=True), tc


# Painted gunmetal
m, nt, p = new_mat("TG_Gunmetal")
wear, tc = edge_mask(nt)
nt.links.new(mix_color(nt, wear, (0.040, 0.043, 0.050), (0.55, 0.55, 0.57)), p.inputs["Base Color"])
nt.links.new(mix_val(nt, wear, 0.25, 1.0), p.inputs["Metallic"])
fine = nt.nodes.new("ShaderNodeTexNoise"); fine.inputs["Scale"].default_value = 18.0; fine.inputs["Detail"].default_value = 4.0
nt.links.new(tc.outputs["Object"], fine.inputs["Vector"])
base_r = math_(nt, "ADD", 0.36, math_(nt, "MULTIPLY", fine.outputs[0], 0.16))
mr = nt.nodes.new("ShaderNodeMix"); mr.data_type = "FLOAT"
nt.links.new(wear, mr.inputs[0]); nt.links.new(base_r, mr.inputs[2]); mr.inputs[3].default_value = 0.28
nt.links.new(mr.outputs[0], p.inputs["Roughness"])

# Brushed steel: fine lines along the gun's length
m, nt, p = new_mat("TG_Brushed")
wear, tc = edge_mask(nt, gain=5.0, patch_lo=0.45, patch_hi=0.65)
mp = nt.nodes.new("ShaderNodeMapping"); mp.inputs["Scale"].default_value = (0.25, 30.0, 30.0)
nt.links.new(tc.outputs["Object"], mp.inputs["Vector"])
br = nt.nodes.new("ShaderNodeTexNoise"); br.inputs["Scale"].default_value = 6.0; br.inputs["Detail"].default_value = 3.0
nt.links.new(mp.outputs[0], br.inputs["Vector"])
nt.links.new(mix_color(nt, wear, (0.11, 0.11, 0.12), (0.5, 0.5, 0.52)), p.inputs["Base Color"])
p.inputs["Metallic"].default_value = 1.0
nt.links.new(math_(nt, "ADD", 0.26, math_(nt, "MULTIPLY", br.outputs[0], 0.24)), p.inputs["Roughness"])

# Polymer grip: matte with a fine pebble
m, nt, p = new_mat("TG_Polymer")
wear, tc = edge_mask(nt, gain=3.0, patch_lo=0.5, patch_hi=0.7)
nt.links.new(mix_color(nt, wear, (0.018, 0.018, 0.020), (0.06, 0.06, 0.065)), p.inputs["Base Color"])
p.inputs["Metallic"].default_value = 0.0
pb = nt.nodes.new("ShaderNodeTexNoise"); pb.inputs["Scale"].default_value = 60.0; pb.inputs["Detail"].default_value = 3.0
nt.links.new(tc.outputs["Object"], pb.inputs["Vector"])
nt.links.new(math_(nt, "ADD", 0.5, math_(nt, "MULTIPLY", pb.outputs[0], 0.15)), p.inputs["Roughness"])
bp = nt.nodes.new("ShaderNodeBump"); bp.inputs["Strength"].default_value = 0.25; bp.inputs["Distance"].default_value = 0.01
nt.links.new(pb.outputs[0], bp.inputs["Height"]); nt.links.new(bp.outputs[0], p.inputs["Normal"])

# Steel for bolts and the sight post
m, nt, p = new_mat("TG_Steel")
p.inputs["Base Color"].default_value = (0.4, 0.4, 0.42, 1); p.inputs["Metallic"].default_value = 1.0; p.inputs["Roughness"].default_value = 0.32

for names, mn in ((GUNMETAL, "TG_Gunmetal"), (BRUSHED, "TG_Brushed"), (POLYMER, "TG_Polymer"), (STEEL, "TG_Steel")):
    for n in names:
        me = o[n].data; me.materials.clear(); me.materials.append(bpy.data.materials[mn])
assert all(x.data.materials and x.data.materials[0].name.startswith("TG_") for x in blocks()), "unassigned block"

# Studio: a dark room with a bright overhead so the metal has something to reflect.
w = bpy.data.worlds.get("TG_Studio") or bpy.data.worlds.new("TG_Studio")
w.use_nodes = True; nt = w.node_tree; nt.nodes.clear()
out = nt.nodes.new("ShaderNodeOutputWorld"); bg = nt.nodes.new("ShaderNodeBackground")
tc = nt.nodes.new("ShaderNodeTexCoord"); sep = nt.nodes.new("ShaderNodeSeparateXYZ"); nt.links.new(tc.outputs["Generated"], sep.inputs[0])
mr = nt.nodes.new("ShaderNodeMapRange"); mr.inputs["From Min"].default_value = -0.2; mr.inputs["From Max"].default_value = 0.9; mr.inputs["To Min"].default_value = 0.05; mr.inputs["To Max"].default_value = 2.0
nt.links.new(sep.outputs["Z"], mr.inputs["Value"]); nt.links.new(mr.outputs[0], bg.inputs["Strength"]); bg.inputs["Color"].default_value = (0.8, 0.85, 0.95, 1)
nt.links.new(bg.outputs[0], out.inputs["Surface"])
bpy.context.scene.world = w
bpy.context.view_layer.update()
