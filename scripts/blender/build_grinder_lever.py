"""Builds the Dverger Grinder's start lever in a headless Blender.

blender -b --factory-startup --python scripts/blender/build_grinder_lever.py -- <out dir> [<render dir> <rip Texture2D dir>]

Writes <out dir>/DvergerGrinderLever.blend and .fbx. The lever is modelled in the Grinder model's own space
(metres, Z up, front -Y, the Grinder's body centred on the origin), on the body's left flank between the
upper strap and the deck, so the Unity builder can drop it in beside DvergerGrinder.fbx and the two line up.

Objects:
- LeverBase: the mounting plate, its bolts and the pivot boss. Static.
- LeverArm: the shaft and grip, with its origin on the pivot so it can swing about X. Modelled standing
  straight up; the game tilts it back when idle and throws it forward when pulled.
- LeverGrip: an empty at the grip, for reference.
Materials: Lever (iron) and LeverGrip (the grip). Both are unwrapped properly - every island its own space
in 0-1, nothing overlapping - so they can be painted as texture sets later. Until then the Unity builder
maps them onto regions of vanilla's IronGrill texture with material tiling, not with these UVs.
"""
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT_DIR = argv[0] if len(argv) > 0 else os.getcwd()
RENDER_DIR = argv[1] if len(argv) > 1 else None
RIP_TEX = argv[2] if len(argv) > 2 else None

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

# The body's left face is at x = -0.43; the upper strap tops out at z 0.495 and the deck starts at 0.85.
FACE_X = -0.43
PIVOT = Vector((-0.545, 0.0, 0.70))
ARM_LEN = 0.21


def material(name, color, image=None, region=None):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*color, 1)
    bsdf.inputs["Metallic"].default_value = 0.7
    bsdf.inputs["Roughness"].default_value = 0.5
    if image and os.path.exists(image):
        nt = m.node_tree
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = bpy.data.images.load(image)
        tex.interpolation = 'Closest'
        if region:
            # Preview only: the same squeeze into an atlas region the Unity material's tiling applies.
            uvn = nt.nodes.new("ShaderNodeUVMap")
            mapping = nt.nodes.new("ShaderNodeMapping")
            mapping.inputs["Scale"].default_value = (region[2], region[3], 1)
            mapping.inputs["Location"].default_value = (region[0], region[1], 0)
            nt.links.new(uvn.outputs["UV"], mapping.inputs["Vector"])
            nt.links.new(mapping.outputs["Vector"], tex.inputs["Vector"])
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = 'RGBA'
        mix.blend_type = 'MULTIPLY'
        mix.inputs["Factor"].default_value = 1.0
        mix.inputs["B"].default_value = (*color, 1)
        nt.links.new(tex.outputs["Color"], mix.inputs["A"])
        nt.links.new(mix.outputs["Result"], bsdf.inputs["Base Color"])
    return m


iron_img = os.path.join(RIP_TEX, "IronGrill_d.png") if RIP_TEX else None
MAT_IRON = material("Lever", (0.30, 0.28, 0.27), iron_img, (0.03, 0.03, 0.63, 0.94))
MAT_GRIP = material("LeverGrip", (0.60, 0.44, 0.38), iron_img, (0.86, 0.36, 0.09, 0.61))


def box(bm, center, size, bevel=0.0):
    geom = bmesh.ops.create_cube(bm, size=1.0)
    verts = geom["verts"]
    bmesh.ops.scale(bm, vec=Vector(size), verts=verts)
    bmesh.ops.translate(bm, vec=Vector(center), verts=verts)
    if bevel > 0:
        edges = list({e for v in verts for e in v.link_edges})
        bmesh.ops.bevel(bm, geom=edges, offset=bevel, segments=1, affect='EDGES', profile=0.5)


def cylinder(bm, center, radius, depth, segments, axis):
    geom = bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments, radius1=radius, radius2=radius, depth=depth)
    rot = Matrix.Rotation(math.radians(90), 4, 'Y') if axis == 'X' else Matrix.Identity(4)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector(center)) @ rot, verts=geom["verts"])


def make_object(name, parts):
    """parts: list of (builder, material index). Builds one mesh with two material slots."""
    bm = bmesh.new()
    for build, mat_index in parts:
        before = set(bm.faces)
        build(bm)
        for f in bm.faces:
            if f not in before:
                f.material_index = mat_index
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(MAT_IRON)
    me.materials.append(MAT_GRIP)
    ob = bpy.data.objects.new(name, me)
    scene.collection.objects.link(ob)
    return ob


base = make_object("LeverBase", [
    (lambda bm: box(bm, (FACE_X - 0.025, 0.0, 0.675), (0.05, 0.22, 0.30), bevel=0.01), 0),
    (lambda bm: [cylinder(bm, (FACE_X - 0.055, y, z), 0.016, 0.02, 6, 'X') for y in (-0.08, 0.08) for z in (0.555, 0.795)], 0),
    (lambda bm: cylinder(bm, (FACE_X - 0.08, 0.0, PIVOT.z), 0.05, 0.06, 10, 'X'), 0),
])

arm = make_object("LeverArm", [
    (lambda bm: cylinder(bm, (PIVOT.x, 0.0, PIVOT.z), 0.04, 0.04, 8, 'X'), 0),
    (lambda bm: box(bm, (PIVOT.x, 0.0, PIVOT.z + ARM_LEN / 2), (0.032, 0.032, ARM_LEN), bevel=0.004), 0),
    (lambda bm: cylinder(bm, (PIVOT.x, 0.0, PIVOT.z + ARM_LEN + 0.05), 0.026, 0.11, 8, 'Z'), 1),
    (lambda bm: box(bm, (PIVOT.x, 0.0, PIVOT.z + ARM_LEN + 0.115), (0.06, 0.06, 0.03), bevel=0.008), 1),
])
# The arm's origin on the pivot, so it swings about its own X axis.
arm.data.transform(Matrix.Translation(-PIVOT))
arm.location = PIVOT

grip = bpy.data.objects.new("LeverGrip", None)
grip.location = PIVOT + Vector((0, 0, ARM_LEN + 0.06))
scene.collection.objects.link(grip)

# ---- proper unwrap: smart project each object, then pack every island of both into 0-1 together ----------
bpy.context.view_layer.update()
for ob in (base, arm):
    bpy.ops.object.select_all(action='DESELECT')
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.02)
    bpy.ops.object.mode_set(mode='OBJECT')
bpy.ops.object.select_all(action='DESELECT')
for ob in (base, arm):
    ob.select_set(True)
bpy.context.view_layer.objects.active = base
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.pack_islands(margin=0.02, rotate=True)
bpy.ops.object.mode_set(mode='OBJECT')

root = bpy.data.objects.new("LeverModel", None)
scene.collection.objects.link(root)
for ob in (base, arm, grip):
    mw = ob.matrix_world.copy()
    ob.parent = root
    ob.matrix_world = mw

tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in (base, arm))
print(f"[lever] {tris} triangles")

os.makedirs(OUT_DIR, exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT_DIR, "DvergerGrinderLever.blend"))
bpy.ops.object.select_all(action='DESELECT')
for o in (root, base, arm, grip):
    o.select_set(True)
bpy.ops.export_scene.fbx(filepath=os.path.join(OUT_DIR, "DvergerGrinderLever.fbx"), use_selection=True,
                         object_types={'EMPTY', 'MESH'}, mesh_smooth_type='FACE', bake_anim=False,
                         path_mode='AUTO', embed_textures=False)

if RENDER_DIR:
    os.makedirs(RENDER_DIR, exist_ok=True)
    # The Grinder itself, appended after saving so it never ends up in the lever's files: the lever is
    # reviewed where it will stand.
    grinder_blend = os.path.join(OUT_DIR, "DvergerGrinder.blend")
    if os.path.exists(grinder_blend):
        with bpy.data.libraries.load(grinder_blend, link=False) as (src, dst):
            dst.objects = [n for n in src.objects if n in ("Body", "RollerA", "RollerB")]
        for ob in dst.objects:
            if ob is not None:
                scene.collection.objects.link(ob)
    scene.render.engine = 'BLENDER_EEVEE'
    scene.render.resolution_x, scene.render.resolution_y = 600, 600
    world = bpy.data.worlds.new("World")
    scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.16, 0.17, 0.19, 1)
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", 'SUN'))
    sun.data.energy = 3.5
    sun.rotation_euler = (math.radians(50), 0, math.radians(-120))
    scene.collection.objects.link(sun)
    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    scene.collection.objects.link(cam)
    scene.camera = cam
    target = Vector((-0.5, 0.0, 0.78))
    cam.location = target + Vector((-0.9, -0.7, 0.3))
    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
    scene.render.filepath = os.path.join(RENDER_DIR, "lever.png")
    bpy.ops.render.render(write_still=True)
    print("[lever] render in " + RENDER_DIR)
