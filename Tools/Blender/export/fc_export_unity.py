# Unity FBX export for the flying car (Blender 5.x)
# Run: open the latest flying_car_v*.blend (v14_export or newer), Scripting tab >
#      Text Editor > fc_export_unity.py > Run Script (Alt+P). Writes
#      export/flying_car_game.fbx next to the .blend.
#
# How it works:
# 1. Swap: every object in the Export collection with an "export_as" property
#    (Body_LP, Canopy_Seal_EX, *_EX) stands in for the high-poly object of that
#    name. The originals are renamed to <name>__HP for the duration of the
#    export, the stand-ins take the final names, then everything is renamed back.
#    Anything whose modifiers depend on objects outside the Car_Root hierarchy
#    needs a stand-in, because step 2 moves the car away from those objects.
#    (The canopy is a plain 5 mm shell since v12 and exports directly.)
# 2. Root: Car_Root is reset to identity while exporting, so the car sits at the
#    origin in Unity with no root rotation or scale. Exported objects whose
#    parent is not exported (steam locators on vents that are baked into the
#    body) are parented to Car_Root for the export, keeping their placement.
# 3. Axes: forward X / up Y with "Apply Transform" (bake_space_transform), which
#    bakes the Blender to Unity axis change into meshes and transforms.
# 4. Fix: Blender's stock exporter composes local matrices wrongly for objects
#    nested more than one level below the root when Apply Transform is on (it
#    misplaces sockets, kit pieces and flame locators). While exporting, the
#    local matrix of every parented object is rebuilt from the FBX world
#    matrices of the object and its parent.
# 5. Header: with forward X the stock exporter labels the file "front = -X".
#    The data is laid out like a standard Blender to Unity export (nose +Z,
#    up +Y, right handed), so the header is written as the standard Y up / Z
#    front one. Unity then applies no extra axis rotation.

import bpy
import os
from mathutils import Matrix
from io_scene_fbx import fbx_utils

# ---------------------------------------------------------------------------
# What goes into the file
# ---------------------------------------------------------------------------

ROOT = "Car_Root"

# Exported as they are (no low-poly stand-in)
DIRECT = [
    "Car_Root", "Fin_L", "Fin_R", "Light_Side_L", "Light_Side_R", "Light_Tail",
    "Canopy",
    "Greeble_Antenna_Cluster", "Greeble_LightBar", "Greeble_Pipes_L", "Greeble_Pipes_R",
    "Cabin_Origin", "Socket_Seat_L", "Socket_Seat_R", "Socket_Dash", "Socket_Yoke",
    "Kit_Seat_L", "Kit_Seat_R", "Kit_Dash", "Kit_Yoke",
]
# Exported with all their mesh and empty descendants (volume-only meshes skipped)
DIRECT_TREES = ["Fusion_Generator_Body"]
FX_COLLECTION = "FX"                                     # flame and steam locator empties
LIGHT_PREFIXES = ("Light_Glow", "Light_Beam", "Light_Cabin")


def swap_table():
    """Stand-in name -> final name, from the export_as properties."""
    return {o.name: o["export_as"] for o in bpy.data.objects if "export_as" in o}


def is_volume_only(o):
    """Mesh whose materials only have a volume shader (steam containers)."""
    mats = [m for m in o.data.materials if m] if o.type == "MESH" else []
    if not mats:
        return False
    for m in mats:
        out = next((n for n in m.node_tree.nodes if n.type == "OUTPUT_MATERIAL"), None) if m.use_nodes else None
        if out is None or out.inputs["Surface"].is_linked or not out.inputs["Volume"].is_linked:
            return False
    return True


def direct_names():
    names = list(DIRECT)
    for top in DIRECT_TREES:
        o = bpy.data.objects[top]
        names += [x.name for x in [o] + list(o.children_recursive)
                  if x.type in {"MESH", "EMPTY"} and not x.hide_render and not is_volume_only(x)]
    names += [o.name for o in bpy.data.collections[FX_COLLECTION].objects if o.type == "EMPTY"]
    names += [o.name for o in bpy.data.objects if o.type == "LIGHT" and o.name.startswith(LIGHT_PREFIXES)]
    return list(dict.fromkeys(names))


# ---------------------------------------------------------------------------
# Exporter fix for nested objects
# ---------------------------------------------------------------------------

OW = fbx_utils.ObjectWrapper
_stock_matrix = OW.__dict__["fbx_object_matrix"]
_stock_matrix = getattr(_stock_matrix, "_fc_stock", _stock_matrix)


def _fixed_matrix(self, scene_data, rest=False, local_space=False, global_space=False):
    if (scene_data.settings.bake_space_transform and not local_space and not global_space
            and self._tag == 'OB' and not self.parented_to_armature
            and self.has_valid_parent(scene_data.objects) and self.parent._tag == 'OB'):
        world = _stock_matrix(self, scene_data, rest=rest, global_space=True)
        parent_world = _stock_matrix(self.parent, scene_data, rest=rest, global_space=True)
        return parent_world.inverted_safe() @ world
    return _stock_matrix(self, scene_data, rest=rest, local_space=local_space, global_space=global_space)


_fixed_matrix._fc_stock = _stock_matrix

# Header axes (up, front, coord) written for forward X / up Y
AXES_KEY = ('Y', 'X')
AXES_STANDARD = fbx_utils.RIGHT_HAND_AXES[('Y', '-Z')]


# ---------------------------------------------------------------------------
# Export
# ---------------------------------------------------------------------------

def export_unity(path):
    swap = swap_table()
    root = bpy.data.objects[ROOT]
    renamed = []
    hidden = []
    reparented = []
    root_basis = root.matrix_basis.copy()
    stock_axes = fbx_utils.RIGHT_HAND_AXES[AXES_KEY]
    OW.fbx_object_matrix = _fixed_matrix
    fbx_utils.RIGHT_HAND_AXES[AXES_KEY] = AXES_STANDARD
    try:
        for stand_in, final in swap.items():
            if final in bpy.data.objects:
                bpy.data.objects[final].name = final + "__HP"
                renamed.append((final + "__HP", final))
            bpy.data.objects[stand_in].name = final
            renamed.append((final, stand_in))
        root.matrix_basis = Matrix.Identity(4)
        bpy.context.view_layer.update()

        selection = [bpy.data.objects[n] for n in list(swap.values()) + direct_names()]
        exported = set(selection)
        for o in selection:
            if o.parent is not None and o.parent not in exported:
                reparented.append((o, o.parent, o.parent_type, o.parent_bone,
                                   o.matrix_parent_inverse.copy(), o.matrix_basis.copy()))
                world = o.matrix_world.copy()
                o.parent = root
                o.matrix_parent_inverse = Matrix.Identity(4)
                o.matrix_world = world
        bpy.context.view_layer.update()
        for o in bpy.context.view_layer.objects:
            o.select_set(False)
        hidden = [o for o in selection if o.hide_get()]
        for o in hidden:
            o.hide_set(False)
        for o in selection:
            o.select_set(True)
        bpy.context.view_layer.objects.active = root

        bpy.ops.export_scene.fbx(
            filepath=path, use_selection=True, object_types={"EMPTY", "MESH", "LIGHT"},
            use_mesh_modifiers=True, mesh_smooth_type="FACE", use_triangles=True, use_tspace=True,
            use_custom_props=True, add_leaf_bones=False, bake_anim=False,
            apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL", global_scale=1.0,
            bake_space_transform=True, axis_forward="X", axis_up="Y",
            path_mode="RELATIVE", embed_textures=False)
    finally:
        OW.fbx_object_matrix = _stock_matrix
        fbx_utils.RIGHT_HAND_AXES[AXES_KEY] = stock_axes
        for o in hidden:
            o.hide_set(True)
        for o, parent, ptype, pbone, pinv, basis in reversed(reparented):
            o.parent = parent
            o.parent_type = ptype
            o.parent_bone = pbone
            o.matrix_parent_inverse = pinv
            o.matrix_basis = basis
        root.matrix_basis = root_basis
        for current, original in reversed(renamed):
            if current in bpy.data.objects:
                bpy.data.objects[current].name = original
        for o in bpy.context.view_layer.objects:
            o.select_set(False)
        bpy.context.view_layer.update()


bpy.app.driver_namespace["fc_export_unity"] = export_unity

if __name__ == "__main__":
    out = os.path.join(os.path.dirname(bpy.data.filepath), "export", "flying_car_game.fbx")
    export_unity(out)
    print("Exported", out)
