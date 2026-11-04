# Pass 1: edges. Small bevel on every hard edge plus Weighted Normal, so edges catch a thin highlight.
BEVEL_W = 0.025      # units (0.3 mm in Unity)


def edge_stack(x, width=BEVEL_W, segments=2):
    for m in list(x.modifiers):
        if m.name in ("Bevel_Edge", "WeightedNormal"):
            x.modifiers.remove(m)
    for p in x.data.polygons:
        p.use_smooth = True
    b = x.modifiers.new("Bevel_Edge", "BEVEL")
    b.width = width; b.segments = segments; b.limit_method = "ANGLE"; b.angle_limit = math.radians(30)
    b.use_clamp_overlap = True; b.miter_outer = "MITER_ARC"
    w = x.modifiers.new("WeightedNormal", "WEIGHTED_NORMAL")
    w.mode = "FACE_AREA"; w.weight = 50; w.keep_sharp = False


parts = blocks() + [o["Upper_StemHidden"]]
for x in parts:
    edge_stack(x)
bpy.context.view_layer.update()
set_open(1.0)
sil = 0.0
for x in parts:
    a0, a1 = aabb(x); b0, b1 = aabb(x, True)
    sil = max(sil, (a0 - b0).length, (a1 - b1).length)
tris = sum(tri_count(x) for x in parts)
worst, missing = check_blockout()
set_open(0.0)
print({"parts": len(parts), "tris_high": tris, "silhouette_change_units": round(sil, 5), "blockout_vertex_change": round(worst, 6), "missing": missing})
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(DIR, "tgun_v3_edges.blend"), check_existing=False)
files = views_sheet("pass1", "edges")
print(bpy.data.filepath, len(files))
