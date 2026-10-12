"""Builds the Dverger Grinder model in a headless Blender.

blender -b --factory-startup --python scripts/blender/build_grinder.py -- <out dir> <render dir> [<rip Texture2D dir>]

Writes <out dir>/DvergerGrinder.blend and .fbx, and review renders into <render dir>. The renders use the
game's own textures when the rip folder is given; the exported model never carries a texture.

This is the starting geometry only. The committed model, DvergerAutomationUnity/Assets/Custom/Models/
DvergerGrinder.blend, has since been cleaned up, re-unwrapped and painted by hand (Materials/Grinder/
Grinder.spp); writing this script's output over it would throw that work away. Only the rollers still
carry the UVs laid out here. The icon now comes from render_grinder_icon.py.

The machine follows the eitr refinery's grinder: a feed hopper over two spiked crusher rollers, carried on
heavy cheek plates above an iron machine body. Ten Molten Cores sit in a power panel on the front, a tank
of lava stands beside the body and is piped into it, and the results hatch is low on the front.
Metres, Z up, front facing -Y, the body's centre on the origin.

Names the Unity builder (BuildGrinderPrefab) relies on:
- materials Iron, IronDark, Rust, Stone, Lava, MoltenCore. In game Iron, IronDark and Rust are tinted copies of
  vanilla's IronGrill texture, Stone of Grausten, and Lava and MoltenCore are vanilla's own materials.
  The UVs below are laid into the regions of those vanilla textures each part should show;
- meshes Body, RollerA, RollerB (origins on their axles), core1-10;
- empties AddCore1-10, Funnel, StoreDoor, DustPoint, LavaPoint, under the root GrinderModel.
"""
import math
import os
import random
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
scene.unit_settings.system = 'METRIC'
scene.unit_settings.scale_length = 1.0
rng = random.Random(7)

# ---- texture regions -----------------------------------------------------------------------------
# IronGrill_d (128px) is plain iron over its left two thirds with a rust streak down the right edge.
IRON_PLAIN = (0.03, 0.03, 0.66, 0.97)
IRON_RUST = (0.86, 0.36, 0.95, 0.97)

# ---- materials ---------------------------------------------------------------------------------------

def preview_material(name, tex=None, tint=(1, 1, 1), metallic=0.0, roughness=0.6, emission=None, strength=4.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Base Color"].default_value = (*tint, 1.0)
    path = os.path.join(RIP_TEX, tex) if (RIP_TEX and tex) else None
    if path and os.path.exists(path):
        img = bpy.data.images.load(path)
        node = nt.nodes.new("ShaderNodeTexImage")
        node.image = img
        node.interpolation = 'Closest'
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = 'RGBA'
        mix.blend_type = 'MULTIPLY'
        mix.inputs["Factor"].default_value = 1.0
        mix.inputs["B"].default_value = (*tint, 1.0)
        nt.links.new(node.outputs["Color"], mix.inputs["A"])
        nt.links.new(mix.outputs["Result"], bsdf.inputs["Base Color"])
        if emission is not None:
            nt.links.new(mix.outputs["Result"], bsdf.inputs["Emission Color"])
    if emission is not None:
        if not (path and os.path.exists(path)):
            bsdf.inputs["Emission Color"].default_value = (*emission, 1.0)
        bsdf.inputs["Emission Strength"].default_value = strength
    m.diffuse_color = (*tint, 1.0)
    return m

MAT = {
    "Iron": preview_material("Iron", "IronGrill_d.png", (0.50, 0.47, 0.45), metallic=0.8, roughness=0.45),
    "IronDark": preview_material("IronDark", "IronGrill_d.png", (0.26, 0.24, 0.23), metallic=0.8, roughness=0.5),
    "Rust": preview_material("Rust", "IronGrill_d.png", (0.55, 0.40, 0.34), metallic=0.7, roughness=0.55),
    "Stone": preview_material("Stone", "Grausten_d.png", (0.28, 0.27, 0.27), roughness=0.9),
    "Lava": preview_material("Lava", "BlackForgeLava_d.png", (1.0, 0.55, 0.15), emission=(1.0, 0.35, 0.05), strength=6.0),
    "MoltenCore": preview_material("MoltenCore", None, (1.0, 0.35, 0.05), emission=(1.0, 0.32, 0.04), strength=8.0),
}

# ---- parts --------------------------------------------------------------------------------------------
# Each part is built in its own bmesh at its final position, given UVs and one material, then turned
# into an object. Parts are joined into the exported objects at the end.

PARTS = {}


def add_part(group, bm, mat, uv="box", region=None, density=1.6):
    """uv: 'box' tiles the whole texture (Stone), 'region' fits each face into a texture region."""
    bm.normal_update()
    uvl = bm.loops.layers.uv.verify()
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        a, b = ((1, 2), (0, 2), (0, 1))[ax]
        pts = [(l.vert.co[a] * density, l.vert.co[b] * density) for l in f.loops]
        if uv == "box" or region is None:
            for l, p in zip(f.loops, pts):
                l[uvl].uv = p
            continue
        u0, v0, u1, v1 = region
        mu = min(p[0] for p in pts)
        mv = min(p[1] for p in pts)
        su = max(p[0] for p in pts) - mu
        sv = max(p[1] for p in pts) - mv
        k = min(1.0, 1.0 / max(su, sv, 1e-6))
        ou = rng.random() * max(0.0, 1.0 - su * k)
        ov = rng.random() * max(0.0, 1.0 - sv * k)
        for l, (pu, pv) in zip(f.loops, pts):
            uu = ou + (pu - mu) * k
            vv = ov + (pv - mv) * k
            l[uvl].uv = (u0 + uu * (u1 - u0), v0 + vv * (v1 - v0))
    me = bpy.data.meshes.new(f"{group}_part")
    bm.to_mesh(me)
    bm.free()
    me.materials.append(MAT[mat])
    ob = bpy.data.objects.new(me.name, me)
    scene.collection.objects.link(ob)
    PARTS.setdefault(group, []).append(ob)
    return ob


def box(center, size, bevel=0.0):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=Vector(size), verts=bm.verts)
    bmesh.ops.translate(bm, vec=Vector(center), verts=bm.verts)
    if bevel > 0:
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=1, affect='EDGES', profile=0.5)
    return bm


def hexa(corners):
    """A solid from 8 corners: bottom 0-3 then top 4-7, both counter-clockwise from above."""
    bm = bmesh.new()
    v = [bm.verts.new(Vector(c)) for c in corners]
    for idx in ((0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)):
        bm.faces.new([v[i] for i in idx])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def axis_matrix(axis):
    if axis == 'X':
        return Matrix.Rotation(math.radians(90), 4, 'Y')
    if axis == 'Y':
        return Matrix.Rotation(math.radians(90), 4, 'X')
    return Matrix.Identity(4)


def cylinder(center, radius, depth, segments=12, axis='Z', radius2=None, cap=True):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=cap, cap_tris=False, segments=segments, radius1=radius,
                          radius2=radius if radius2 is None else radius2, depth=depth)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector(center)) @ axis_matrix(axis), verts=bm.verts)
    return bm


def gear(center, radius, teeth, thickness, tooth, axis='X', phase=0.0):
    """A chunky spur gear: a toothed profile extruded along the axis."""
    bm = bmesh.new()
    prof = []
    step = 2 * math.pi / teeth
    for i in range(teeth):
        a = phase + i * step
        for frac, r in ((0.0, radius - tooth), (0.18, radius), (0.5, radius), (0.68, radius - tooth)):
            ang = a + frac * step
            prof.append(bm.verts.new((math.cos(ang) * r, math.sin(ang) * r, -thickness / 2)))
    face = bm.faces.new(prof)
    ext = bmesh.ops.extrude_face_region(bm, geom=[face])
    top = [e for e in ext["geom"] if isinstance(e, bmesh.types.BMVert)]
    bmesh.ops.translate(bm, vec=(0, 0, thickness), verts=top)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])
    bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector(center)) @ axis_matrix(axis), verts=bm.verts)
    return bm


def spike(base_center, direction, size, height):
    """A four-sided spike standing on base_center, pointing along direction."""
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=4, radius1=size, radius2=0.0, depth=height)
    d = Vector(direction).normalized()
    rot = d.to_track_quat('Z', 'Y').to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector(base_center) + d * (height / 2)) @ rot, verts=bm.verts)
    return bm


def ring_of_boxes(center, radius, count, size, axis='X'):
    """A polygonal ring made of blocks (a flywheel rim, a valve wheel)."""
    bms = []
    m_axis = axis_matrix(axis)
    for i in range(count):
        ang = 2 * math.pi * i / count
        seg_len = 2 * radius * math.tan(math.pi / count) * 1.04
        bm = box((0, 0, 0), (seg_len, size[0], size[1]))
        bmesh.ops.transform(bm, matrix=Matrix.Rotation(ang + math.pi / 2, 4, 'Z'), verts=bm.verts)
        bmesh.ops.translate(bm, vec=(math.cos(ang) * radius, math.sin(ang) * radius, 0), verts=bm.verts)
        bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector(center)) @ m_axis, verts=bm.verts)
        bms.append(bm)
    return bms


def lump(center, radius):
    """A molten core: a lumpy rock."""
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=1, radius=radius)
    for v in bm.verts:
        v.co *= 0.82 + rng.random() * 0.32
    bmesh.ops.translate(bm, vec=Vector(center), verts=bm.verts)
    return bm

# ---- dimensions ---------------------------------------------------------------------------------------

BASE_TOP = 0.12
BODY_TOP = 0.85
BODY_HALF_X, BODY_HALF_Y = 0.45, 0.38
ROLL_Z = 1.13
ROLL_Y = 0.135          # the two rollers sit at -ROLL_Y (A, front) and +ROLL_Y (B, back)
ROLL_R = 0.105
ROLL_HALF_LEN = 0.30
CHEEK_X = 0.37
HOPPER_Z0, HOPPER_Z1 = 1.30, 1.55
TANK = Vector((0.84, 0.12, BASE_TOP))
TANK_R, TANK_H = 0.24, 0.80
CORE_ROWS = (0.735, 0.595)
CORE_XS = (-0.28, -0.14, 0.0, 0.14, 0.28)
PANEL_Y = -BODY_HALF_Y - 0.035

# ---- base and body -------------------------------------------------------------------------------------

add_part("Body", box((0.28, 0.0, BASE_TOP / 2), (1.72, 0.90, BASE_TOP), bevel=0.025), "Stone", density=1.4)

# The body tapers a little toward the top, like the dverger machines.
bt, tp = BODY_HALF_X, BODY_HALF_X - 0.04
by, ty = BODY_HALF_Y, BODY_HALF_Y - 0.03
add_part("Body", hexa([(-bt, -by, BASE_TOP), (bt, -by, BASE_TOP), (bt, by, BASE_TOP), (-bt, by, BASE_TOP),
                       (-tp, -ty, BODY_TOP), (tp, -ty, BODY_TOP), (tp, ty, BODY_TOP), (-tp, ty, BODY_TOP)]),
         "Iron", uv="region", region=IRON_PLAIN)

# Corner posts carry the crusher deck.
for sx in (-1, 1):
    for sy in (-1, 1):
        add_part("Body", box((sx * (BODY_HALF_X - 0.015), sy * (BODY_HALF_Y - 0.01), (BASE_TOP + BODY_TOP + 0.06) / 2),
                             (0.11, 0.11, BODY_TOP - BASE_TOP + 0.06), bevel=0.012), "IronDark", uv="region", region=IRON_PLAIN)

# Rusted straps round the body, with rivets.
for z in (0.22, 0.47):
    add_part("Body", box((0, 0, z), (2 * BODY_HALF_X + 0.03, 2 * BODY_HALF_Y + 0.03, 0.05)), "Rust", uv="region", region=IRON_RUST)
    for x in (-0.33, -0.11, 0.11, 0.33):
        for sy in (-1, 1):
            add_part("Body", cylinder((x, sy * (BODY_HALF_Y + 0.02), z), 0.016, 0.02, segments=6, axis='Y'), "IronDark",
                     uv="region", region=IRON_PLAIN)

# Deck plate the crusher stands on.
add_part("Body", box((0, 0, BODY_TOP + 0.025), (2 * BODY_HALF_X + 0.05, 2 * BODY_HALF_Y + 0.05, 0.05), bevel=0.01),
         "IronDark", uv="region", region=IRON_PLAIN)

# ---- the Molten Core power panel ---------------------------------------------------------------------

add_part("Body", box((0, -BODY_HALF_Y - 0.005, 0.665), (0.78, 0.05, 0.30), bevel=0.012), "IronDark", uv="region", region=IRON_PLAIN)
CORE_SPOTS = []
for z in CORE_ROWS:
    for x in CORE_XS:
        add_part("Body", cylinder((x, PANEL_Y - 0.01, z), 0.055, 0.05, segments=8, axis='Y'), "Rust", uv="region", region=IRON_RUST)
        CORE_SPOTS.append(Vector((x, PANEL_Y - 0.045, z)))

# ---- the results hatch -----------------------------------------------------------------------------------

HATCH_Z = 0.33
add_part("Body", box((0, -BODY_HALF_Y - 0.02, HATCH_Z), (0.42, 0.05, 0.27), bevel=0.01), "Rust", uv="region", region=IRON_RUST)
add_part("Body", box((0, -BODY_HALF_Y - 0.045, HATCH_Z), (0.34, 0.03, 0.20), bevel=0.008), "IronDark", uv="region", region=IRON_PLAIN)
for x in (-0.11, -0.055, 0.0, 0.055, 0.11):
    add_part("Body", box((x, -BODY_HALF_Y - 0.065, HATCH_Z), (0.022, 0.02, 0.19)), "Iron", uv="region", region=IRON_PLAIN)
add_part("Body", box((0, -BODY_HALF_Y - 0.085, HATCH_Z + 0.02), (0.18, 0.025, 0.025)), "IronDark", uv="region", region=IRON_PLAIN)
STORE_DOOR = Vector((0, -BODY_HALF_Y - 0.1, HATCH_Z))

# ---- the crusher ------------------------------------------------------------------------------------------

# Cheek plates carry the roller axles; the walls between them stop low so the rollers show.
for sx in (-1, 1):
    add_part("Body", box((sx * CHEEK_X, 0, (BODY_TOP + 0.05 + HOPPER_Z0) / 2), (0.07, 0.62, HOPPER_Z0 - BODY_TOP - 0.05), bevel=0.012),
             "Iron", uv="region", region=IRON_PLAIN)
    for y in (-0.24, 0.24):
        for z in (0.98, 1.22):
            add_part("Body", cylinder((sx * (CHEEK_X + 0.04), y, z), 0.022, 0.025, segments=6, axis='X'), "IronDark",
                     uv="region", region=IRON_PLAIN)
for sy in (-1, 1):
    add_part("Body", box((0, sy * 0.30, 0.99), (2 * CHEEK_X - 0.06, 0.05, 0.18), bevel=0.01), "IronDark", uv="region", region=IRON_PLAIN)

# The lava bed under the rollers.
add_part("Body", box((0, 0, BODY_TOP + 0.055), (2 * CHEEK_X - 0.08, 0.56, 0.01)), "Lava", density=2.0)

# The feed hopper: four flared plates, a rusted rim, and spikes on the corners.
b0x, b0y = CHEEK_X - 0.02, 0.29
t0x, t0y = 0.50, 0.44
w = 0.035
z0, z1 = HOPPER_Z0, HOPPER_Z1
for sy in (-1, 1):
    add_part("Body", hexa([(-b0x, sy * b0y, z0), (b0x, sy * b0y, z0), (b0x, sy * (b0y - w), z0), (-b0x, sy * (b0y - w), z0),
                           (-t0x, sy * t0y, z1), (t0x, sy * t0y, z1), (t0x, sy * (t0y - w), z1), (-t0x, sy * (t0y - w), z1)]),
             "Iron", uv="region", region=IRON_PLAIN)
for sx in (-1, 1):
    add_part("Body", hexa([(sx * b0x, -b0y, z0), (sx * b0x, b0y, z0), (sx * (b0x - w), b0y, z0), (sx * (b0x - w), -b0y, z0),
                           (sx * t0x, -t0y, z1), (sx * t0x, t0y, z1), (sx * (t0x - w), t0y, z1), (sx * (t0x - w), -t0y, z1)]),
             "Iron", uv="region", region=IRON_PLAIN)
add_part("Body", box((0, -t0y + 0.01, z1 + 0.02), (2 * t0x + 0.04, 0.06, 0.05)), "Rust", uv="region", region=IRON_RUST)
add_part("Body", box((0, t0y - 0.01, z1 + 0.02), (2 * t0x + 0.04, 0.06, 0.05)), "Rust", uv="region", region=IRON_RUST)
add_part("Body", box((-t0x + 0.01, 0, z1 + 0.02), (0.06, 2 * t0y, 0.05)), "Rust", uv="region", region=IRON_RUST)
add_part("Body", box((t0x - 0.01, 0, z1 + 0.02), (0.06, 2 * t0y, 0.05)), "Rust", uv="region", region=IRON_RUST)
for sx in (-1, 1):
    for sy in (-1, 1):
        add_part("Body", spike((sx * (t0x - 0.01), sy * (t0y - 0.01), z1 + 0.045), (sx * 0.25, sy * 0.25, 1), 0.035, 0.11),
                 "IronDark", uv="region", region=IRON_PLAIN)
FUNNEL = Vector((0, 0, (z0 + z1) / 2 + 0.02))

# Rollers: a drum carrying rows of spikes, each row turned half a step from the last.


def roller(group, y, phase):
    add_part(group, cylinder((0, y, ROLL_Z), ROLL_R, 2 * ROLL_HALF_LEN, segments=12, axis='X'), "IronDark", uv="region", region=IRON_PLAIN)
    rows = 6
    per_row = 8
    for r in range(rows):
        x = -ROLL_HALF_LEN + (r + 0.5) * (2 * ROLL_HALF_LEN / rows)
        for k in range(per_row):
            ang = phase + (k + (0.5 if r % 2 else 0.0)) * 2 * math.pi / per_row
            d = Vector((0, math.cos(ang), math.sin(ang)))
            add_part(group, spike(Vector((x, y, ROLL_Z)) + d * (ROLL_R - 0.005), d, 0.03, 0.06), "IronDark",
                     uv="region", region=IRON_PLAIN)
    add_part(group, cylinder((0, y, ROLL_Z), 0.035, 2 * CHEEK_X + 0.16, segments=8, axis='X'), "Rust", uv="region", region=IRON_RUST)


roller("RollerA", -ROLL_Y, 0.0)
roller("RollerB", ROLL_Y, math.pi / 8)

# Left: a meshing pair of gears, so the two rollers visibly turn against each other.
GEAR_R = ROLL_Y
add_part("RollerA", gear((-(CHEEK_X + 0.08), -ROLL_Y, ROLL_Z), GEAR_R + 0.012, 12, 0.05, 0.035, phase=0.0), "IronDark",
         uv="region", region=IRON_PLAIN)
add_part("RollerB", gear((-(CHEEK_X + 0.08), ROLL_Y, ROLL_Z), GEAR_R + 0.012, 12, 0.05, 0.035, phase=math.pi / 12), "IronDark",
         uv="region", region=IRON_PLAIN)
for grp, y in (("RollerA", -ROLL_Y), ("RollerB", ROLL_Y)):
    add_part(grp, cylinder((-(CHEEK_X + 0.115), y, ROLL_Z), 0.045, 0.04, segments=6, axis='X'), "Rust", uv="region", region=IRON_RUST)

# Right: a heavy spoked flywheel on the front roller, as on the refinery.
FLY_X = CHEEK_X + 0.15
FLY_R = 0.30
for bm in ring_of_boxes((FLY_X, -ROLL_Y, ROLL_Z), FLY_R - 0.03, 14, (0.07, 0.06), axis='X'):
    add_part("RollerA", bm, "IronDark", uv="region", region=IRON_PLAIN)
for i in range(6):
    ang = i * math.pi / 3
    spoke = box((0, 0, 0), (0.04, FLY_R - 0.05, 0.035))
    bmesh.ops.translate(spoke, vec=(0, (FLY_R - 0.05) / 2, 0), verts=spoke.verts)
    bmesh.ops.transform(spoke, matrix=Matrix.Translation((FLY_X, -ROLL_Y, ROLL_Z)) @ Matrix.Rotation(ang, 4, 'X'), verts=spoke.verts)
    add_part("RollerA", spoke, "IronDark", uv="region", region=IRON_PLAIN)
add_part("RollerA", cylinder((FLY_X, -ROLL_Y, ROLL_Z), 0.07, 0.08, segments=8, axis='X'), "Rust", uv="region", region=IRON_RUST)
add_part("RollerB", cylinder((CHEEK_X + 0.06, ROLL_Y, ROLL_Z), 0.05, 0.05, segments=6, axis='X'), "Rust", uv="region", region=IRON_RUST)

# ---- the lava tank ------------------------------------------------------------------------------------------

add_part("Body", cylinder(TANK + Vector((0, 0, TANK_H / 2)), TANK_R, TANK_H, segments=16), "Iron", uv="region", region=IRON_PLAIN)
for z in (0.10, 0.40, 0.70):
    add_part("Body", cylinder(TANK + Vector((0, 0, z)), TANK_R + 0.015, 0.05, segments=16), "Rust", uv="region", region=IRON_RUST)
for bm in ring_of_boxes(TANK + Vector((0, 0, TANK_H + 0.03)), TANK_R + 0.005, 16, (0.06, 0.07), axis='Z'):
    add_part("Body", bm, "IronDark", uv="region", region=IRON_PLAIN)
add_part("Body", cylinder(TANK + Vector((0, 0, TANK_H + 0.025)), TANK_R - 0.015, 0.012, segments=16), "Lava", density=2.0)
for i in range(4):
    ang = math.pi / 4 + i * math.pi / 2
    p = TANK + Vector((math.cos(ang) * (TANK_R + 0.03), math.sin(ang) * (TANK_R + 0.03), TANK_H + 0.07))
    add_part("Body", spike(p, (math.cos(ang) * 0.3, math.sin(ang) * 0.3, 1), 0.025, 0.08), "IronDark", uv="region", region=IRON_PLAIN)
LAVA_POINT = TANK + Vector((0, 0, TANK_H + 0.06))

# Two pipes run from the tank into the body's flank, each with flanges and a glowing sight ring.
x_body = BODY_HALF_X - 0.03
x_tank = TANK.x - TANK_R + 0.02
for z in (0.36, 0.64):
    y = 0.14
    add_part("Body", cylinder(((x_body + x_tank) / 2, y, z), 0.045, x_tank - x_body, segments=10, axis='X'), "IronDark",
             uv="region", region=IRON_PLAIN)
    for x in (x_body + 0.04, x_tank - 0.03):
        add_part("Body", cylinder((x, y, z), 0.07, 0.03, segments=10, axis='X'), "Rust", uv="region", region=IRON_RUST)
    add_part("Body", cylinder(((x_body + x_tank) / 2, y, z), 0.05, 0.035, segments=10, axis='X'), "Lava", density=2.0)
# A valve wheel on the upper pipe.
vx = (x_body + x_tank) / 2 - 0.05
for bm in ring_of_boxes((vx, 0.14, 0.64 + 0.13), 0.07, 8, (0.02, 0.02), axis='Z'):
    add_part("Body", bm, "Rust", uv="region", region=IRON_RUST)
add_part("Body", cylinder((vx, 0.14, 0.64 + 0.065), 0.015, 0.13, segments=6), "IronDark", uv="region", region=IRON_PLAIN)

# ---- the Molten Cores ---------------------------------------------------------------------------------------

for i, p in enumerate(CORE_SPOTS):
    add_part(f"core{i + 1}", lump(p, 0.048), "MoltenCore", density=8.0)

# ---- assemble -----------------------------------------------------------------------------------------------


def join(group, name, origin=None):
    objs = PARTS[group]
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1:
        bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    ob.name = name
    ob.data.name = name
    if origin is None:
        vs = [v.co for v in ob.data.vertices]
        origin = (Vector([min(v[i] for v in vs) for i in range(3)]) + Vector([max(v[i] for v in vs) for i in range(3)])) / 2
    origin = Vector(origin)
    ob.data.transform(Matrix.Translation(-origin))
    ob.location = origin
    return ob


root = bpy.data.objects.new("GrinderModel", None)
scene.collection.objects.link(root)
exported = [join("Body", "Body", origin=(0, 0, 0)),
            join("RollerA", "RollerA", origin=(0, -ROLL_Y, ROLL_Z)),
            join("RollerB", "RollerB", origin=(0, ROLL_Y, ROLL_Z))]
for i in range(len(CORE_SPOTS)):
    exported.append(join(f"core{i + 1}", f"core{i + 1}"))


def empty(name, loc):
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.06
    e.location = loc
    scene.collection.objects.link(e)
    exported.append(e)


for i, p in enumerate(CORE_SPOTS):
    empty(f"AddCore{i + 1}", p + Vector((0, -0.015, 0)))
empty("Funnel", FUNNEL)
empty("StoreDoor", STORE_DOOR)
empty("DustPoint", Vector((0, 0, HOPPER_Z1 + 0.05)))
empty("LavaPoint", LAVA_POINT)
for o in exported:
    o.parent = root

tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in exported if o.type == 'MESH')
print(f"[grinder] {len(exported)} objects, {tris} triangles")

os.makedirs(OUT_DIR, exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT_DIR, "DvergerGrinder.blend"))
bpy.ops.object.select_all(action='DESELECT')
for o in [root] + exported:
    o.select_set(True)
bpy.ops.export_scene.fbx(filepath=os.path.join(OUT_DIR, "DvergerGrinder.fbx"), use_selection=True,
                         object_types={'EMPTY', 'MESH'}, mesh_smooth_type='FACE', bake_anim=False,
                         path_mode='AUTO', embed_textures=False)

# ---- review renders ----------------------------------------------------------------------------------------

if RENDER_DIR:
    os.makedirs(RENDER_DIR, exist_ok=True)
    scene.render.engine = 'BLENDER_EEVEE'
    scene.render.resolution_x, scene.render.resolution_y = 1000, 850
    world = bpy.data.worlds.new("World")
    scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.16, 0.17, 0.19, 1)
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", 'SUN'))
    sun.data.energy = 3.0
    sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(-40))
    scene.collection.objects.link(sun)
    fill = bpy.data.objects.new("Fill", bpy.data.lights.new("Fill", 'AREA'))
    fill.data.energy = 250
    fill.data.size = 4
    fill.location = (-3, -3, 3)
    fill.rotation_euler = (math.radians(55), 0, math.radians(-45))
    scene.collection.objects.link(fill)
    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    cam.data.lens = 45
    scene.collection.objects.link(cam)
    scene.camera = cam
    target = Vector((0.25, 0.0, 0.78))
    for name, pos in (("front", (1.2, -3.2, 1.9)), ("tank", (3.2, 1.0, 1.8)), ("top", (0.5, -1.1, 2.9)), ("left", (-3.0, -1.4, 1.7))):
        cam.location = Vector(pos)
        look = (Vector((0, 0, 1.25)) if name == "top" else target) - cam.location
        cam.rotation_euler = look.to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(RENDER_DIR, f"grinder_{name}.png")
        bpy.ops.render.render(write_still=True)

    # The build menu icon: 256px on a transparent background, three-quarters from the front right so
    # the core panel, the hopper and the lava tank all show. Rendered here rather than with Unity's icon
    # exporter, because in the editor the piece wears flat stand-ins for the game's textures.
    scene.render.resolution_x, scene.render.resolution_y = 256, 256
    scene.render.film_transparent = True
    cam.data.lens = 50
    center = Vector((0.28, 0.0, 0.82))
    cam.location = center + Vector((1.6, -2.9, 1.55))
    cam.rotation_euler = (center - cam.location).to_track_quat('-Z', 'Y').to_euler()
    sun.data.energy = 4.0
    scene.render.filepath = os.path.join(RENDER_DIR, "DA_Grinder.png")
    bpy.ops.render.render(write_still=True)
    print("[grinder] renders in " + RENDER_DIR)
