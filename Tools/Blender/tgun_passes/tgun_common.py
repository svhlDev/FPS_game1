# Shared helpers for the T-gun passes. Run a pass from Blender's Python with:
#   exec(open(r"...\tgun_passes\passN.py").read())
# Blender +X is the front of the gun, +Z up, +Y left. 1 block-out unit = 1.25 cm in Unity.
import bpy, json, math, os
from mathutils import Matrix, Vector

UNIT = 0.0125
DIR = os.path.dirname(bpy.data.filepath)
PASS_DIR = os.path.join(DIR, "tgun_passes")
SNAPSHOT = os.path.join(PASS_DIR, "blockout_snapshot.json")
o = bpy.data.objects

# Spacing as given, underside of the upper bar to the top of the lower bar. This is the open gap.
OPEN_GAP = 4.6247
# Gap left between the bars at rest (2 mm in Unity).
REST_GAP = 0.16
TRAVEL = OPEN_GAP - REST_GAP

RENAME = {
    "Cube": "Receiver_Grip", "Cube.001": "Receiver_GripSide_R", "Cube.002": "Receiver_GripSide_L",
    "Cube.003": "Receiver_Trigger_A", "Cube.004": "Receiver_Trigger_B", "Cube.005": "Receiver_Cap",
    "Cube.006": "Receiver_Bar", "Cube.012": "Receiver_TopPlate_MidR", "Cube.013": "Receiver_TopPlate_MidL",
    "Cube.015": "Receiver_TopPlate_Rear", "Cube.018": "Receiver_UnderPlate", "Cube.021": "Receiver_FrontBlock_R",
    "Cube.022": "Receiver_FrontBlock_L", "Cube.023": "Receiver_FrontTab_L", "Cube.024": "Receiver_FrontTab_R",
    "Cube.026": "Receiver_TopPlate_Front", "Cube.029": "Receiver_GripBack", "Cube.030": "Receiver_GripBack_A",
    "Cube.031": "Receiver_GripBack_B", "Cube.032": "Receiver_GripBack_C", "Cube.033": "Receiver_GripSideLow_R",
    "Cube.034": "Receiver_GripSideLow_L", "Cube.035": "Receiver_GripFront_A", "Cube.036": "Receiver_GripFront_B",
    "Cube.037": "Receiver_GripFront_C", "Cube.038": "Receiver_GripFront_D",
    "Cube.007": "Upper_Bar", "Cube.008": "Upper_Stem", "Cube.009": "Upper_StemPlate_A", "Cube.010": "Upper_StemPlate_B",
    "Cube.011": "Upper_UnderFin", "Cube.014": "Upper_UnderPlate_Rear", "Cube.016": "Upper_TopPlate",
    "Cube.017": "Upper_TopPlate_Small", "Cube.019": "Upper_Prong_R", "Cube.020": "Upper_Prong_L",
    "Cube.025": "Upper_UnderPlate_Front", "Cube.027": "Upper_UnderRail_L", "Cube.028": "Upper_UnderRail_R",
}


def blocks():
    """Simon's blocks, identified by the blockout_name property."""
    return [x for x in o if x.type == "MESH" and "blockout_name" in x]


def world_verts(x):
    return [x.matrix_world @ v.co for v in x.data.vertices]


def aabb(x, evaluated=False):
    if evaluated:
        e = x.evaluated_get(bpy.context.evaluated_depsgraph_get()); me = e.to_mesh()
        p = [x.matrix_world @ v.co for v in me.vertices]; e.to_mesh_clear()
    else:
        p = world_verts(x)
    return Vector([min(q[i] for q in p) for i in range(3)]), Vector([max(q[i] for q in p) for i in range(3)])


def set_open(value):
    """0 = collapsed (rest), 1 = open (the block-out as given)."""
    o["TGun"]["open"] = float(value)
    o["TGun"].update_tag(); o["Upper"].update_tag()
    for x in o:
        if "gap_glow" in x or "open_only" in x:      # these only exist while the gun is open
            x.hide_render = value < 0.01
    bpy.context.view_layer.update()


def set_mode(lethal):
    """Swap the shared mode materials on the light parts (what Weapon.SetMode does in Unity)."""
    suffix = "Lethal" if lethal else "Stun"
    for x in o:
        if "mode_light" in x:
            x.data.materials[0] = bpy.data.materials["TG_Mode_" + suffix]
        if "gap_glow" in x:
            x.data.materials[0] = bpy.data.materials["TG_Gap_" + suffix]


def check_blockout():
    """Largest distance (units) of any base mesh vertex from Simon's block-out, measured open."""
    with open(SNAPSHOT) as f:
        snap = json.load(f)
    was = o["TGun"]["open"]
    set_open(1.0)
    worst = 0.0; missing = []
    for name, verts in snap.items():
        x = next((b for b in blocks() if b["blockout_name"] == name), None)
        if x is None:
            missing.append(name); continue
        now = world_verts(x)
        if len(now) != len(verts):
            missing.append(name + " (vertex count)"); continue
        # Applying scale keeps vertex order, so compare one to one.
        worst = max(worst, max((a - Vector(b)).length for a, b in zip(now, verts)))
    set_open(was)
    return worst, missing


def tri_count(x):
    e = x.evaluated_get(bpy.context.evaluated_depsgraph_get()); me = e.to_mesh()
    me.calc_loop_triangles(); n = len(me.loop_triangles); e.to_mesh_clear(); return n


def views_sheet(folder, tag, engine="BLENDER_WORKBENCH", samples=64, show_hand=False, only=None):
    """Front, side, top and 3/4 at rest, plus side and 3/4 open. Writes PNGs for the contact sheet."""
    sc = bpy.context.scene; r = sc.render
    out = os.path.join(DIR, "renders", "tgun", folder); os.makedirs(out, exist_ok=True)
    keep = dict(engine=r.engine, rx=r.resolution_x, ry=r.resolution_y, fp=r.filepath, fmt=r.image_settings.file_format, cam=sc.camera, film=r.film_transparent)
    s = sc.display.shading
    keep_s = dict(light=s.light, ct=s.color_type, cav=s.show_cavity, spec=s.show_specular_highlight, ol=s.show_object_outline, sh=s.show_shadows)
    cd = bpy.data.cameras.new("Tmp_TG"); cam = o.new("Tmp_TG", cd); sc.collection.objects.link(cam)
    cd.type = "ORTHO"; cd.clip_start = 0.1; cd.clip_end = 500
    hand = o.get("Reference_Hand"); hand_keep = hand.hide_render if hand else None
    was = o["TGun"]["open"]
    shots = [("rest_side", (0, -1, 0), 23, (0, 0, 4.8), 0.0), ("rest_front", (1, 0, 0), 13, (0, 0, 4.8), 0.0), ("rest_top", (0, 0, 1), 23, (0, 0, 0), 0.0),
             ("rest_3q", (0.8, -0.7, 0.45), 24, (0, 0, 4.8), 0.0), ("open_side", (0, -1, 0), 23, (0, 0, 7.2), 1.0), ("open_3q", (0.8, -0.7, 0.45), 26, (0, 0, 7.2), 1.0)]
    files = []
    try:
        if hand: hand.hide_render = not show_hand
        r.engine = engine; r.resolution_x, r.resolution_y = 1100, 700; r.image_settings.file_format = "PNG"; sc.camera = cam; r.film_transparent = False
        s.light = "STUDIO"; s.color_type = "MATERIAL"; s.show_cavity = False; s.show_specular_highlight = True; s.show_object_outline = False; s.show_shadows = False
        if engine == "CYCLES":
            prefs = bpy.context.preferences.addons["cycles"].preferences
            prefs.compute_device_type = "OPTIX"; prefs.get_devices()
            for dv in prefs.devices: dv.use = True
            sc.cycles.device = "GPU"; sc.cycles.samples = samples; sc.cycles.use_denoising = True
        for name, d, ortho, tgt, op in shots:
            if only and name not in only:
                continue
            set_open(op)
            w = Vector(d).normalized(); cam.location = Vector(tgt) + w * 60; cam.rotation_euler = (-w).to_track_quat("-Z", "Y").to_euler(); cd.ortho_scale = ortho
            r.filepath = os.path.join(out, f"{tag}_{name}.png"); bpy.ops.render.render(write_still=True); files.append(r.filepath)
    finally:
        set_open(was)
        if engine == "CYCLES":
            bpy.context.preferences.addons["cycles"].preferences.compute_device_type = "NONE"
        r.engine = keep["engine"]; r.resolution_x, r.resolution_y = keep["rx"], keep["ry"]; r.filepath = keep["fp"]; r.image_settings.file_format = keep["fmt"]; sc.camera = keep["cam"]; r.film_transparent = keep["film"]
        s.light = keep_s["light"]; s.color_type = keep_s["ct"]; s.show_cavity = keep_s["cav"]; s.show_specular_highlight = keep_s["spec"]; s.show_object_outline = keep_s["ol"]; s.show_shadows = keep_s["sh"]
        if hand: hand.hide_render = hand_keep
        o.remove(cam, do_unlink=True); bpy.data.cameras.remove(cd)
    return files
