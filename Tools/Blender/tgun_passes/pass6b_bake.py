# Pass 6b: bake the high-poly (grooves, pockets, greebles, wear) onto the low-poly. Baked open, the two parts are far
# enough apart that neither shades the other. A temporary joined copy of the low-poly is the bake target.
import time
SIZE = 2048
PROJECT = os.path.dirname(os.path.dirname(DIR))
TEX_DIR = os.path.join(PROJECT, "Assets", "Resources", "Weapons", "TGun")
os.makedirs(TEX_DIR, exist_ok=True)
KEYS = {"BaseColor": ("TG_Bake_BaseColor", True, False), "Normal": ("TG_Bake_Normal", False, True), "AO": ("TG_Bake_AO", False, False),
        "Roughness": ("TG_Bake_Roughness", False, False), "Metallic": ("TG_Bake_Metallic", False, False)}


def setup(keep=()):
    """keep: maps to carry over as they are (a material change leaves the AO alone)."""
    set_open(1.0)
    imgs = {}
    for k, (nm, srgb, fl) in KEYS.items():
        im = bpy.data.images.get(nm)
        if im and k in keep:
            imgs[k] = im
            continue
        if im:
            bpy.data.images.remove(im)
        im = bpy.data.images.new(nm, SIZE, SIZE, alpha=False, float_buffer=fl)
        im.colorspace_settings.name = "sRGB" if srgb else "Non-Color"
        imgs[k] = im
    mat = bpy.data.materials.get("TGun_Baked") or bpy.data.materials.new("TGun_Baked")
    mat.use_nodes = True; nt = mat.node_tree; nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial"); b = nt.nodes.new("ShaderNodeBsdfPrincipled"); nt.links.new(b.outputs[0], out.inputs["Surface"])
    for k, im in imgs.items():
        t = nt.nodes.new("ShaderNodeTexImage"); t.image = im; t.name = "TEX_" + k
    for n in ("EX_Receiver", "EX_Upper"):
        me = o[n].data; me.materials.clear(); me.materials.append(mat)
    # joined copy, with the bevels and custom normals applied
    dg = bpy.context.evaluated_depsgraph_get(); made = []
    for n in ("EX_Receiver", "EX_Upper"):
        src = o[n]; was = src.hide_render; src.hide_render = False; bpy.context.view_layer.update(); dg = bpy.context.evaluated_depsgraph_get()
        me = bpy.data.meshes.new_from_object(src.evaluated_get(dg)); src.hide_render = was
        c = o.new("TG_BakeLP_" + n, me); bpy.context.scene.collection.objects.link(c); c.matrix_world = src.matrix_world.copy(); made.append(c)
    bpy.context.view_layer.update()
    for x in bpy.context.selected_objects:
        x.select_set(False)
    for c in made:
        c.select_set(True)
    bpy.context.view_layer.objects.active = made[0]
    with bpy.context.temp_override(active_object=made[0], selected_editable_objects=made, selected_objects=made):
        bpy.ops.object.join()
    lp = bpy.context.view_layer.objects.active; lp.name = "TG_BakeLP"
    return lp


def sources():
    return blocks() + [o[n] for n in ("Upper_StemHidden", "Upper_Bolts", "Receiver_Bolts", "Upper_SightPost")]


def bake(kind, key, samples, pass_filter=None, ao_dist=None, zero_metal=False, metal_trick=False):
    sc = bpy.context.scene; r = sc.render; cp = bpy.context.preferences.addons["cycles"].preferences
    lp = o["TG_BakeLP"]; mat = lp.data.materials[0]; nt = mat.node_tree
    nt.nodes.active = nt.nodes["TEX_" + key]
    src = sources()
    hide = [x for x in o if x.type == "MESH" and x not in src and x != lp]
    hide_state = {x.name: x.hide_render for x in hide}
    vis = ("visible_diffuse", "visible_glossy", "visible_shadow", "visible_transmission", "visible_volume_scatter")
    lp_vis = {a: getattr(lp, a) for a in vis}
    saved = dict(engine=r.engine, dev=sc.cycles.device, samples=sc.cycles.samples)
    world_ao = sc.world.light_settings.distance
    mats = {m for x in src for m in x.data.materials if m and m.use_nodes}
    undo = []
    try:
        for x in hide: x.hide_render = True
        for a in vis: setattr(lp, a, False)
        lp.hide_render = False
        cp.compute_device_type = "OPTIX"; cp.get_devices()
        for d in cp.devices: d.use = d.type == "OPTIX"
        r.engine = "CYCLES"; sc.cycles.device = "GPU"; sc.cycles.samples = samples
        if ao_dist: sc.world.light_settings.distance = ao_dist
        for m in mats:
            p = next((n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
            if p is None: continue
            met, ec, es = p.inputs["Metallic"], p.inputs["Emission Color"], p.inputs["Emission Strength"]
            undo.append((m, p, [l.from_socket for l in met.links], met.default_value, tuple(ec.default_value), es.default_value))
            if metal_trick:
                es.default_value = 1.0
                if met.links: m.node_tree.links.new(met.links[0].from_socket, ec)
                else: ec.default_value = (met.default_value, met.default_value, met.default_value, 1)
            if zero_metal:
                for l in list(met.links): m.node_tree.links.remove(l)
                met.default_value = 0.0
        rb = r.bake; rb.use_selected_to_active = True; rb.use_cage = False; rb.cage_extrusion = 0.03; rb.max_ray_distance = 0.45; rb.margin = 8; rb.margin_type = "EXTEND"
        rb.normal_space = "TANGENT"; rb.normal_r, rb.normal_g, rb.normal_b = "POS_X", "POS_Y", "POS_Z"
        if pass_filter is not None:
            rb.use_pass_direct = "DIRECT" in pass_filter; rb.use_pass_indirect = "INDIRECT" in pass_filter; rb.use_pass_color = "COLOR" in pass_filter
        for x in bpy.context.view_layer.objects: x.select_set(False)
        for x in src: x.select_set(True)
        lp.select_set(True); bpy.context.view_layer.objects.active = lp
        t = time.time()
        with bpy.context.temp_override(active_object=lp, object=lp, selected_objects=src + [lp], selected_editable_objects=src + [lp]):
            bpy.ops.object.bake(type=kind)
        dt = round(time.time() - t, 1)
    finally:
        for m, p, socks, val, ecv, esv in undo:
            met, ec, es = p.inputs["Metallic"], p.inputs["Emission Color"], p.inputs["Emission Strength"]
            for l in list(ec.links): m.node_tree.links.remove(l)
            ec.default_value = ecv; es.default_value = esv
            met.default_value = val
            if not met.links:
                for s in socks: m.node_tree.links.new(s, met)
        for x in hide: x.hide_render = hide_state[x.name]
        for a, v in lp_vis.items(): setattr(lp, a, v)
        r.engine = saved["engine"]; sc.cycles.device = saved["dev"]; sc.cycles.samples = saved["samples"]; sc.world.light_settings.distance = world_ao
        cp.compute_device_type = "NONE"
        for x in bpy.context.view_layer.objects: x.select_set(False)
    im = nt.nodes["TEX_" + key].image; px = im.pixels[::4 * 991]
    return {"key": key, "s": dt, "mean": round(sum(px) / len(px), 3), "max": round(max(px), 3)}
