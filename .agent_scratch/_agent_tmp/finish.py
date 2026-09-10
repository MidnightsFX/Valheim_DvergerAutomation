import bpy, bmesh, math, os, sys, importlib, traceback

HERE = r"C:\Users\carls\Documents\projects\Valheim_Stuff\Valheim_DvergerAutomation\DvergerAutomationUnity\Assets\Custom\Models\_agent_tmp"
log = []

def act(ob):
    bpy.ops.object.mode_set(mode='OBJECT')
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob

def shade(ob, deg=40.0):
    act(ob)
    try:
        bpy.ops.object.shade_smooth_by_angle(angle=math.radians(deg))
        log.append(f"{ob.name}: shade_smooth_by_angle({deg})")
    except Exception as e:
        try:
            bpy.ops.object.shade_auto_smooth(angle=math.radians(deg))
            log.append(f"{ob.name}: shade_auto_smooth({deg})")
        except Exception as e2:
            log.append(f"{ob.name}: shading FAILED {e} / {e2}")

def uvs(ob, group="Dverger"):
    act(ob)
    if not ob.data.uv_layers:
        ob.data.uv_layers.new(name="UVMap")
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_mode(type='FACE')
    bpy.ops.mesh.select_all(action='DESELECT')
    vg = ob.vertex_groups.get(group)
    n_new = 0
    if vg:
        ob.vertex_groups.active_index = vg.index
        bpy.ops.object.vertex_group_select()
        bm = bmesh.from_edit_mesh(ob.data)
        n_new = sum(1 for f in bm.faces if f.select)
        if n_new:
            bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.0025,
                                     correct_aspect=True, scale_to_bounds=False)
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.select_all(action='SELECT')
    try:
        bpy.ops.uv.average_islands_scale()
    except Exception as e:
        log.append(f"{ob.name}: avg scale failed {e}")
    packed = False
    for kw in ({"margin": 0.004, "rotate": True, "margin_method": 'SCALED', "shape_method": 'CONCAVE'},
               {"margin": 0.004, "rotate": True},
               {"margin": 0.004}):
        try:
            bpy.ops.uv.pack_islands(**kw); packed = True; break
        except Exception as e:
            log.append(f"{ob.name}: pack {kw} -> {e}")
    bpy.ops.object.mode_set(mode='OBJECT')
    log.append(f"{ob.name}: smart-projected {n_new} new faces, packed={packed}")

def uv_bounds(ob):
    d = ob.data.uv_layers[0].data
    us = [x.uv[0] for x in d]; vs = [x.uv[1] for x in d]
    return [round(min(us), 4), round(min(vs), 4), round(max(us), 4), round(max(vs), 4)]

mo = bpy.data.objects['Mount']; eo = bpy.data.objects['SortingEngine']
for ob in (mo, eo):
    shade(ob)
    uvs(ob)

bpy.ops.object.mode_set(mode='OBJECT')
p = os.path.join(HERE, "preview.obj")
bpy.ops.wm.obj_export(filepath=p, export_selected_objects=False, export_materials=False,
                      export_normals=True, export_uv=True, apply_modifiers=True)

tri = 0
for o in bpy.data.objects:
    if o.type == 'MESH':
        o.data.calc_loop_triangles(); tri += len(o.data.loop_triangles)

result = {"log": log, "tris": tri,
          "uv_mount": uv_bounds(mo), "uv_engine": uv_bounds(eo),
          "verts": {o.name: len(o.data.vertices) for o in (mo, eo)}}
