"""Renders the Dverger Seeking Table's 256x256 build-menu icon from its .blend, as it looks in game.

blender -b <DvergerSeekingTable.blend> --factory-startup --python scripts/blender/render_seeking_icon.py -- <out png> <Substance texture dir>

Unity's icon exporter can't show this piece whole: the orb is particles, which a single editor render
does not run, and the cores wear a stand-in for the vanilla Black Core Jotunn swaps in at runtime. So the
icon is rendered here: the body with the Substance exports (DvergerSeekingTable_<set>_AlbedoTransparency.png),
the runes lit in their painted colour, the orb as a glowing blue ball, and all four cores in, dark and
glowing purple like vanilla's. Nothing is saved back to the .blend. Use absolute paths.
"""
import math
import os
import sys

import bmesh
import bpy
from mathutils import Vector

out_png, tex_dir = sys.argv[sys.argv.index("--") + 1:][:2]
scene = bpy.context.scene


def material(name, image=None, tint=(1, 1, 1), metallic=0.0, roughness=0.6, emission=None, strength=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Base Color"].default_value = (*tint, 1)
    if image and os.path.exists(image):
        node = nt.nodes.new("ShaderNodeTexImage")
        node.image = bpy.data.images.load(image)
        nt.links.new(node.outputs["Color"], bsdf.inputs["Base Color"])
        if emission is not None:
            # The glow is the painted colour times the tint, as BuildSeekingTablePrefab sets it up.
            mix = nt.nodes.new("ShaderNodeMix")
            mix.data_type = 'RGBA'
            mix.blend_type = 'MULTIPLY'
            mix.inputs["Factor"].default_value = 1.0
            mix.inputs["B"].default_value = (*emission, 1)
            nt.links.new(node.outputs["Color"], mix.inputs["A"])
            nt.links.new(mix.outputs["Result"], bsdf.inputs["Emission Color"])
    elif emission is not None:
        bsdf.inputs["Emission Color"].default_value = (*emission, 1)
    if emission is not None:
        bsdf.inputs["Emission Strength"].default_value = strength
    return m


painted = {
    "Stone": material("icon_Stone", os.path.join(tex_dir, "DvergerSeekingTable_Stone_AlbedoTransparency.png"), roughness=0.85),
    "Metal": material("icon_Metal", os.path.join(tex_dir, "DvergerSeekingTable_Metal_AlbedoTransparency.png"), metallic=0.8, roughness=0.4),
    "Runes": material("icon_Runes", os.path.join(tex_dir, "DvergerSeekingTable_Runes_AlbedoTransparency.png"),
                      emission=(0.12, 0.35, 1.0), strength=1.2),
}
# Vanilla's BlackCore_mat: near black, emission (2.67, 0, 1.05).
core = material("icon_BlackCore", None, (0.03, 0.03, 0.04), roughness=0.3, emission=(1.0, 0.0, 0.39), strength=1.2)

for ob in scene.objects:
    if ob.type != 'MESH':
        continue
    for slot in ob.material_slots:
        name = slot.material.name if slot.material else ""
        if name == "BlackCore":
            slot.link = 'OBJECT'
            slot.material = core
        elif name in painted:
            slot.link = 'OBJECT'
            slot.material = painted[name]

# The orb.
orb_z = bpy.data.objects["OrbPoint"].location.z
bm = bmesh.new()
bmesh.ops.create_uvsphere(bm, u_segments=32, v_segments=16, radius=0.3)
mesh = bpy.data.meshes.new("icon_orb")
bm.to_mesh(mesh)
bm.free()
for poly in mesh.polygons:
    poly.use_smooth = True
mesh.materials.append(material("icon_Orb", None, (0.02, 0.12, 0.45), emission=(0.08, 0.38, 1.0), strength=1.6))
orb = bpy.data.objects.new("icon_orb", mesh)
orb.location = (0, 0, orb_z)
scene.collection.objects.link(orb)

scene.render.engine = 'BLENDER_EEVEE'
scene.render.resolution_x = scene.render.resolution_y = 256
scene.render.film_transparent = True
world = bpy.data.worlds.new("IconWorld")
scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (0.16, 0.17, 0.19, 1)
sun = bpy.data.objects.new("IconSun", bpy.data.lights.new("IconSun", 'SUN'))
sun.data.energy = 4.0
sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(-40))
scene.collection.objects.link(sun)
fill = bpy.data.objects.new("IconFill", bpy.data.lights.new("IconFill", 'AREA'))
fill.data.energy = 400
fill.data.size = 5
fill.location = (-4, -4, 4)
fill.rotation_euler = (math.radians(55), 0, math.radians(-45))
scene.collection.objects.link(fill)
cam = bpy.data.objects.new("IconCam", bpy.data.cameras.new("IconCam"))
cam.data.lens = 72
scene.collection.objects.link(cam)
scene.camera = cam
# Between two pillars, so the orb is not hidden behind one.
cam.location = (2.2, -6.75, 3.3)
cam.rotation_euler = (Vector((0, 0, 1.25)) - cam.location).to_track_quat('-Z', 'Y').to_euler()
scene.render.filepath = out_png
bpy.ops.render.render(write_still=True)
print("[seeking-icon] " + out_png)
