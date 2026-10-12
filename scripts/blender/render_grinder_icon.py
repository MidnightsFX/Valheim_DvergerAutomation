"""Renders the Dverger Grinder's 256x256 build-menu icon from its .blend, as it looks in game.

blender -b <DvergerGrinder.blend> --python scripts/blender/render_grinder_icon.py -- <out png> <Substance texture dir> <rip Texture2D dir>

Unity's icon exporter can't do this piece: in the editor the rollers, lava and cores wear flat stand-ins
for the vanilla materials Jotunn swaps in at runtime. So the icon is rendered here: the body with the
Substance exports (DvergerGrinder_<set>_AlbedoTransparency.png), the base slab on vanilla Grausten, the
rollers on vanilla's IronGrill_d as the Unity builder tints them, the lava on the black forge's lava
texture, and the cores glowing. The cores are shown inserted. Nothing is saved back to the .blend.
Run it with --factory-startup and absolute paths.
"""
import math
import os
import sys

import bpy
from mathutils import Vector

out_png, tex_dir, rip_dir = sys.argv[sys.argv.index("--") + 1:][:3]
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
        node.interpolation = 'Closest'
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = 'RGBA'
        mix.blend_type = 'MULTIPLY'
        mix.inputs["Factor"].default_value = 1.0
        mix.inputs["B"].default_value = (*tint, 1)
        nt.links.new(node.outputs["Color"], mix.inputs["A"])
        nt.links.new(mix.outputs["Result"], bsdf.inputs["Base Color"])
        if emission is not None:
            nt.links.new(mix.outputs["Result"], bsdf.inputs["Emission Color"])
    elif emission is not None:
        bsdf.inputs["Emission Color"].default_value = (*emission, 1)
    if emission is not None:
        bsdf.inputs["Emission Strength"].default_value = strength
    return m


# Painted sets: the body's own material slots, by name.
painted = {}
for slot in ("Iron", "IronDark", "Rust", "Rust2", "Flametal", "Flametal2"):
    painted[slot] = material("icon_" + slot, os.path.join(tex_dir, f"DvergerGrinder_{slot}_AlbedoTransparency.png"),
                             metallic=0.6, roughness=0.5)
# The base slab is vanilla Grausten, at vanilla's _Color.
painted["Stone"] = material("icon_Stone", os.path.join(rip_dir, "Grausten_d.png"), (0.79, 0.79, 0.79), roughness=0.85)
lava = material("icon_Lava", os.path.join(rip_dir, "BlackForgeLava_d.png"), (1.0, 0.55, 0.15),
                emission=(1.0, 0.35, 0.05), strength=6.0)
core = material("icon_MoltenCore", None, (1.0, 0.35, 0.05), emission=(1.0, 0.32, 0.04), strength=8.0)
# The rollers as BuildGrinderPrefab tints them.
roller = {"IronDark": material("icon_RollerIron", os.path.join(rip_dir, "IronGrill_d.png"), (0.28, 0.26, 0.25), 0.7, 0.5),
          "Rust": material("icon_RollerRust", os.path.join(rip_dir, "IronGrill_d.png"), (0.60, 0.44, 0.38), 0.5, 0.55)}

for ob in scene.objects:
    if ob.type != 'MESH':
        continue
    for slot in ob.material_slots:
        name = slot.material.name if slot.material else ""
        if ob.name.startswith("Roller") and name in roller:
            slot.link = 'OBJECT'
            slot.material = roller[name]
        elif name == "Lava":
            slot.link = 'OBJECT'
            slot.material = lava
        elif name == "MoltenCore":
            slot.link = 'OBJECT'
            slot.material = core
        elif name in painted:
            slot.link = 'OBJECT'
            slot.material = painted[name]

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
fill.data.energy = 250
fill.data.size = 4
fill.location = (-3, -3, 3)
fill.rotation_euler = (math.radians(55), 0, math.radians(-45))
scene.collection.objects.link(fill)
cam = bpy.data.objects.new("IconCam", bpy.data.cameras.new("IconCam"))
cam.data.lens = 50
scene.collection.objects.link(cam)
scene.camera = cam
center = Vector((0.28, 0.0, 0.82))
cam.location = center + Vector((1.6, -2.9, 1.55))
cam.rotation_euler = (center - cam.location).to_track_quat('-Z', 'Y').to_euler()
scene.render.filepath = out_png
bpy.ops.render.render(write_still=True)
print("[grinder-icon] " + out_png)
