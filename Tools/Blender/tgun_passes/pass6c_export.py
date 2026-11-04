# Pass 6c: save the baked maps and export the FBX for Unity at 1 unit = 1.25 cm, at rest, with no scale on any object.
# Front of the gun: Blender +X -> Unity +Z. Up: Blender +Z -> Unity +Y. Blender +Y (left) -> Unity -X.
# Unity moves Upper along its local +Y by TRAVEL * UNIT metres.
import numpy as np
from io_scene_fbx import fbx_utils
PROJECT = os.path.dirname(os.path.dirname(DIR))
TEX_DIR = os.path.join(PROJECT, "Assets", "Resources", "Weapons", "TGun")
FBX_PATH = os.path.join(TEX_DIR, "TGun.fbx")
IM = {k: bpy.data.images["TG_Bake_" + k] for k in ("BaseColor", "Normal", "AO", "Roughness", "Metallic")}


def arr(im):
    a = np.empty(im.size[0] * im.size[1] * 4, dtype=np.float32); im.pixels.foreach_get(a)
    return a.reshape(im.size[1], im.size[0], 4)


def save_maps():
    out = {}
    for key, fname in (("BaseColor", "TGun_BaseColor.png"), ("Normal", "TGun_Normal.png"), ("AO", "TGun_AO.png")):
        im = IM[key]; im.filepath_raw = os.path.join(TEX_DIR, fname); im.file_format = "PNG"; im.save()
        out[fname] = os.path.getsize(im.filepath_raw)
    met, rough = arr(IM["Metallic"]), arr(IM["Roughness"])
    ms = np.zeros_like(met); ms[..., 0] = ms[..., 1] = ms[..., 2] = met[..., 0]; ms[..., 3] = np.clip(1.0 - rough[..., 0], 0, 1)
    nm = "TG_Bake_MetallicSmoothness"
    im = bpy.data.images.get(nm)
    if im:
        bpy.data.images.remove(im)
    im = bpy.data.images.new(nm, met.shape[1], met.shape[0], alpha=True); im.colorspace_settings.name = "Non-Color"; im.alpha_mode = "STRAIGHT"
    im.pixels.foreach_set(ms.ravel()); im.filepath_raw = os.path.join(TEX_DIR, "TGun_MetallicSmoothness.png"); im.file_format = "PNG"; im.save()
    out["TGun_MetallicSmoothness.png"] = os.path.getsize(im.filepath_raw)
    out["metal_range"] = (round(float(met[..., 0].min()), 2), round(float(met[..., 0].max()), 2))
    out["smooth_mean"] = round(float(ms[..., 3].mean()), 3)
    return out


def scaled_mesh(src, name, grip_world, evaluated=True):
    """Evaluated mesh in metres, relative to the object's own new origin (grip_world is that origin in units)."""
    dg = bpy.context.evaluated_depsgraph_get()
    was = src.hide_render; src.hide_render = False; bpy.context.view_layer.update(); dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(src.evaluated_get(dg)) if evaluated else src.data.copy()
    src.hide_render = was
    me.name = name
    M = Matrix.Scale(UNIT, 4) @ Matrix.Translation(-grip_world) @ src.matrix_world
    me.transform(M)
    return me


def export():
    set_open(0.0)   # at rest
    root = o["TGun"]; grip = root.matrix_world.translation.copy()
    # The pivots: on the stem axis, each part's reference height, in the rest pose.
    U, R = o["Upper"], o["Receiver"]
    up = U.matrix_world.translation.copy(); rp = R.matrix_world.translation.copy()
    tmp = bpy.data.collections.new("TGun_Out"); bpy.context.scene.collection.children.link(tmp)
    made = []; renamed = []

    def park(name):
        x = o.get(name)
        if x is not None:
            x.name = name + "__HP"; renamed.append((name + "__HP", name))

    lights = [("Receiver_Seam", "Receiver"), ("Receiver_GapGlow", "Receiver"), ("Receiver_Wires", "Receiver"), ("Receiver_EnergyBeams", "Receiver"), ("Receiver_BeamEmitters", "Receiver"),
              ("Upper_HandleGlow", "Upper"), ("Upper_GapGlow", "Upper"), ("Upper_SightDot", "Upper"), ("Upper_BeamEmitters", "Upper")]
    light_src = {n: o[n] for n, _ in lights}
    for n in ("TGun", "Upper", "Receiver", "Grip", "Muzzle") + tuple(light_src):
        park(n)
    try:
        def new_empty(name, parent, loc):
            e = o.new(name, None); tmp.objects.link(e); e.parent = parent; e.location = loc; made.append(e); return e

        def new_mesh(name, src, parent, pivot_world):
            # pivot_world in units; parent's origin too
            me = scaled_mesh(src, name, pivot_world)
            ob = o.new(name, me); tmp.objects.link(ob); ob.parent = parent
            made.append(ob); return ob

        r_root = new_empty("TGun", None, (0, 0, 0))
        new_empty("Grip", r_root, (0, 0, 0))
        new_empty("Muzzle", r_root, (o["Muzzle__HP"].matrix_world.translation - grip) * UNIT)
        rcv = new_mesh("Receiver", o["EX_Receiver"], r_root, rp); rcv.location = (rp - grip) * UNIT
        upp = new_mesh("Upper", o["EX_Upper"], r_root, up); upp.location = (up - grip) * UNIT
        for n, part in lights:
            new_mesh(n, light_src[n], rcv if part == "Receiver" else upp, rp if part == "Receiver" else up)
        bpy.context.view_layer.update()
        # The exporter fixes from the car: local matrices for nested objects, standard header.
        OW = fbx_utils.ObjectWrapper
        stock = OW.__dict__["fbx_object_matrix"]; stock = getattr(stock, "_fc_stock", stock)

        def fixed(self, scene_data, rest=False, local_space=False, global_space=False):
            if (scene_data.settings.bake_space_transform and not local_space and not global_space and self._tag == 'OB'
                    and not self.parented_to_armature and self.has_valid_parent(scene_data.objects) and self.parent._tag == 'OB'):
                w = stock(self, scene_data, rest=rest, global_space=True); pw = stock(self.parent, scene_data, rest=rest, global_space=True)
                return pw.inverted_safe() @ w
            return stock(self, scene_data, rest=rest, local_space=local_space, global_space=global_space)
        fixed._fc_stock = stock
        key = ('Y', 'X'); stock_axes = fbx_utils.RIGHT_HAND_AXES[key]
        OW.fbx_object_matrix = fixed; fbx_utils.RIGHT_HAND_AXES[key] = fbx_utils.RIGHT_HAND_AXES[('Y', '-Z')]
        try:
            for x in bpy.context.view_layer.objects:
                x.select_set(False)
            for x in made:
                x.select_set(True)
            bpy.context.view_layer.objects.active = r_root
            bpy.ops.export_scene.fbx(filepath=FBX_PATH, use_selection=True, object_types={"EMPTY", "MESH"}, use_mesh_modifiers=False, mesh_smooth_type="FACE", use_triangles=True,
                                     use_tspace=True, use_custom_props=False, add_leaf_bones=False, bake_anim=False, apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL", global_scale=1.0,
                                     bake_space_transform=True, axis_forward="X", axis_up="Y", path_mode="RELATIVE", embed_textures=False)
        finally:
            OW.fbx_object_matrix = stock; fbx_utils.RIGHT_HAND_AXES[key] = stock_axes
        tris = {}
        for x in made:
            if x.type == "MESH":
                x.data.calc_loop_triangles(); tris[x.name] = len(x.data.loop_triangles)
        print({"tris": tris, "total": sum(tris.values()), "fbx_kb": os.path.getsize(FBX_PATH) // 1024,
               "unity_positions_m": {x.name: (round(-x.matrix_world.translation.y, 4), round(x.matrix_world.translation.z, 4), round(x.matrix_world.translation.x, 4)) for x in made if x.parent == r_root},
               "travel_m": round(TRAVEL * UNIT, 4)})
    finally:
        for x in made:
            me = x.data if x.type == "MESH" else None
            o.remove(x, do_unlink=True)
            if me: bpy.data.meshes.remove(me)
        bpy.data.collections.remove(tmp)
        for cur, orig in reversed(renamed):
            o[cur].name = orig
        for x in bpy.context.view_layer.objects:
            x.select_set(False)
        bpy.context.view_layer.update()
