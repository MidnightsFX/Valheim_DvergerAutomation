"""Builds the Dverger Seeking Table model in a headless Blender.

blender -b --factory-startup --python scripts/blender/build_seeking_table.py -- <out dir> <render dir>

Writes <out dir>/DvergerSeekingTable.blend and .fbx, and review renders into <render dir>. This is the
starting geometry only: the committed model is DvergerAutomationUnity/Assets/Custom/Models/
DvergerSeekingTable.blend, and once that has been worked on by hand, re-running this would throw the
work away. The names the Unity builder (BuildSeekingTablePrefab) relies on are the materials Stone,
Metal, Runes and BlackCore, the meshes core1-4, and the empties AddCore1-4 and OrbPoint.
Metres, Z up, the table's centre on the origin. Four pillars on the diagonals each hold a Black Core
in a copper cup, facing a floating orb whose centre is the OrbPoint empty.
"""
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:]
OUT_DIR, RENDER_DIR = argv[0], argv[1]
os.makedirs(RENDER_DIR, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.unit_settings.system = 'METRIC'
scene.unit_settings.scale_length = 1.0

# ---- materials: one per Substance texture set, plus the core ------------------------------

def make_material(name, color, metallic=0.0, roughness=0.6, emission=None):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    if emission is not None:
        bsdf.inputs["Emission Color"].default_value = (*emission, 1.0)
        bsdf.inputs["Emission Strength"].default_value = 4.0
    m.diffuse_color = (*(emission or color), 1.0)
    return m

MATERIALS = [
    make_material("Stone", (0.20, 0.22, 0.26), roughness=0.85),
    make_material("Metal", (0.72, 0.40, 0.20), metallic=1.0, roughness=0.35),
    make_material("Runes", (0.05, 0.08, 0.12), emission=(0.15, 0.55, 1.0)),
]
STONE, METAL, RUNES = 0, 1, 2
CORE_MAT = make_material("BlackCore", (0.03, 0.03, 0.05), roughness=0.3, emission=(0.05, 0.2, 0.6))

# ---- geometry helpers --------------------------------------------------------------------------

bm = bmesh.new()


def tag(verts, mat_index):
    faces = set()
    for v in verts:
        faces.update(v.link_faces)
    for f in faces:
        f.material_index = mat_index
    return faces


def prism(sides, r_bottom, r_top, z0, z1, mat_index, matrix=Matrix.Identity(4), spin=0.0):
    """A closed N-sided prism or frustum standing from z0 to z1, its radii to the corners."""
    depth = z1 - z0
    m = matrix @ Matrix.Translation((0, 0, z0 + depth / 2)) @ Matrix.Rotation(math.radians(spin), 4, 'Z')
    ret = bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=sides,
                                radius1=r_bottom, radius2=r_top, depth=depth, matrix=m)
    return tag(ret["verts"], mat_index)


def box(size, center, mat_index, matrix=Matrix.Identity(4)):
    m = matrix @ Matrix.Translation(center) @ Matrix.Diagonal((*size, 1.0))
    ret = bmesh.ops.create_cube(bm, size=1.0, matrix=m)
    return tag(ret["verts"], mat_index)


def bevel_top_edges(faces, width):
    """Chamfers the rim of a prism's top cap, so the stone reads as dressed rather than cut."""
    top = max(faces, key=lambda f: f.calc_center_median().z)
    edges = [e for e in top.edges]
    bmesh.ops.bevel(bm, geom=edges, offset=width, offset_type='OFFSET', segments=1, profile=0.5, affect='EDGES')


def ring(sides, r_outer, r_inner, z, height, mat_index, spin=0.0):
    """A flat polygonal ring raised a little off a surface: the rune channel round the dais."""
    rot = math.radians(spin)
    outer_lo, inner_lo, outer_hi, inner_hi = [], [], [], []
    for i in range(sides):
        a = rot + 2 * math.pi * i / sides
        c, s = math.cos(a), math.sin(a)
        outer_lo.append(bm.verts.new((r_outer * c, r_outer * s, z)))
        inner_lo.append(bm.verts.new((r_inner * c, r_inner * s, z)))
        outer_hi.append(bm.verts.new((r_outer * c, r_outer * s, z + height)))
        inner_hi.append(bm.verts.new((r_inner * c, r_inner * s, z + height)))
    faces = []
    for i in range(sides):
        j = (i + 1) % sides
        faces.append(bm.faces.new((outer_hi[i], outer_hi[j], inner_hi[j], inner_hi[i])))   # top
        faces.append(bm.faces.new((outer_lo[i], outer_lo[j], outer_hi[j], outer_hi[i])))   # outer wall
        faces.append(bm.faces.new((inner_lo[j], inner_lo[i], inner_hi[i], inner_hi[j])))   # inner wall
    for f in faces:
        f.material_index = mat_index
    return faces

# ---- the base ------------------------------------------------------------------------------------

TIER1_TOP = 0.22
TIER2_TOP = 0.40

bevel_top_edges(prism(8, 1.55, 1.50, 0.0, TIER1_TOP, STONE), 0.04)
bevel_top_edges(prism(8, 1.10, 1.06, TIER1_TOP, TIER2_TOP, STONE), 0.03)
# A copper band round the lower tier's lip.
ring(8, 1.565, 1.50, TIER1_TOP - 0.07, 0.06, METAL)
# The rune channel on the upper tier, circling the dais.
ring(8, 0.86, 0.78, TIER2_TOP, 0.006, RUNES, spin=22.5)

# The dais the orb drips onto: a hexagonal plinth, a rune band, a copper cap.
DAIS_TOP = 0.78
prism(6, 0.42, 0.38, TIER2_TOP, DAIS_TOP, STONE, spin=30)
ring(6, 0.445, 0.385, 0.55, 0.06, RUNES, spin=30)
bevel_top_edges(prism(6, 0.47, 0.44, DAIS_TOP, DAIS_TOP + 0.06, METAL, spin=30), 0.015)

# ---- the pillars -----------------------------------------------------------------------------------

PILLAR_RADIUS = 1.35      # from the centre to a pillar's foot, along the diagonal
LEAN = math.radians(8.0)  # every pillar leans in towards the orb
SHAFT_BOTTOM, SHAFT_TOP = 0.20, 1.98
SHAFT_R0, SHAFT_R1 = 0.20, 0.15
CORE_LOCAL = Vector((-0.32, 0.0, 1.78))   # in the pillar's frame: x points out from the centre
CORE_RADIUS = 0.11


def shaft_radius(z):
    t = (z - SHAFT_BOTTOM) / (SHAFT_TOP - SHAFT_BOTTOM)
    return SHAFT_R0 + (SHAFT_R1 - SHAFT_R0) * t


core_points = []
for n, deg in enumerate((45, 135, 225, 315), start=1):
    theta = math.radians(deg)
    frame = (Matrix.Translation((PILLAR_RADIUS * math.cos(theta), PILLAR_RADIUS * math.sin(theta), TIER1_TOP))
             @ Matrix.Rotation(theta, 4, 'Z')
             @ Matrix.Rotation(-LEAN, 4, 'Y'))

    # Foot and its copper plate.
    box((0.46, 0.46, 0.16), (0, 0, 0.08), STONE, frame)
    box((0.50, 0.50, 0.04), (0, 0, 0.18), METAL, frame)
    # The shaft: square, tapering, its faces square to the centre.
    prism(4, SHAFT_R0, SHAFT_R1, SHAFT_BOTTOM, SHAFT_TOP, STONE, frame, spin=45)
    # Copper bands.
    for z in (0.55, 1.50):
        r = shaft_radius(z) + 0.025
        prism(4, r, r, z, z + 0.07, METAL, frame, spin=45)
    # A rune channel down the inner face, glowing towards the orb.
    apothem = shaft_radius(1.08) * math.cos(math.radians(45))
    box((0.012, 0.06, 0.72), (-apothem - 0.003, 0, 1.08), RUNES, frame)
    # Capital and a pyramidal finial.
    prism(4, 0.21, 0.21, SHAFT_TOP, SHAFT_TOP + 0.09, METAL, frame, spin=45)
    prism(4, 0.17, 0.015, SHAFT_TOP + 0.09, SHAFT_TOP + 0.45, METAL, frame, spin=45)
    # The arm reaching in, and the copper cup the core sits in.
    arm_len = -CORE_LOCAL.x - apothem
    box((arm_len + 0.04, 0.06, 0.05), (-(apothem + arm_len / 2), 0, CORE_LOCAL.z - 0.13), METAL, frame)
    prism(8, 0.05, 0.12, CORE_LOCAL.z - 0.16, CORE_LOCAL.z - 0.05, METAL,
          frame @ Matrix.Translation((CORE_LOCAL.x, 0, 0)), spin=22.5)
    # A thin prong over the top, so the core is held rather than balanced.
    box((0.03, 0.03, 0.22), (CORE_LOCAL.x + 0.02, 0, CORE_LOCAL.z + 0.02), METAL,
        frame @ Matrix.Translation((0, 0.0, 0)) @ Matrix.Translation((0, 0.13, 0)))
    box((0.03, 0.03, 0.22), (CORE_LOCAL.x + 0.02, 0, CORE_LOCAL.z + 0.02), METAL,
        frame @ Matrix.Translation((0, -0.13, 0)))

    core_points.append((n, frame @ CORE_LOCAL))

bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

mesh = bpy.data.meshes.new("SeekingTable")
bm.to_mesh(mesh)
bm.free()
for m in MATERIALS:
    mesh.materials.append(m)
table = bpy.data.objects.new("SeekingTable", mesh)
scene.collection.objects.link(table)

# ---- cores, sockets and the orb point -----------------------------------------------------------

orb_z = sum(p.z for _, p in core_points) / len(core_points)
for n, p in core_points:
    cbm = bmesh.new()
    bmesh.ops.create_uvsphere(cbm, u_segments=14, v_segments=8, radius=CORE_RADIUS)
    cmesh = bpy.data.meshes.new("core%d" % n)
    cbm.to_mesh(cmesh)
    cbm.free()
    cmesh.materials.append(CORE_MAT)
    for poly in cmesh.polygons:
        poly.use_smooth = True
    core = bpy.data.objects.new("core%d" % n, cmesh)
    core.location = p
    scene.collection.objects.link(core)

    socket = bpy.data.objects.new("AddCore%d" % n, None)
    socket.empty_display_type = 'SPHERE'
    socket.empty_display_size = 0.2
    socket.location = p
    scene.collection.objects.link(socket)

orb = bpy.data.objects.new("OrbPoint", None)
orb.empty_display_type = 'SPHERE'
orb.empty_display_size = 0.35
orb.location = (0, 0, orb_z)
scene.collection.objects.link(orb)

# ---- UVs: each texture set gets the whole 0-1 square to itself ------------------------------------

def unwrap(obj):
    bpy.context.view_layer.objects.active = obj
    for o in scene.objects:
        o.select_set(o == obj)
    bpy.ops.object.mode_set(mode='EDIT')
    for index in range(len(obj.data.materials)):
        bpy.ops.mesh.select_all(action='DESELECT')
        obj.active_material_index = index
        bpy.ops.object.material_slot_select()
        bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.02, scale_to_bounds=False)
        bpy.ops.uv.pack_islands(margin=0.01)
    bpy.ops.object.mode_set(mode='OBJECT')

unwrap(table)
for o in scene.objects:
    if o.type == 'MESH' and o.name.startswith("core"):
        unwrap(o)

# ---- report ------------------------------------------------------------------------------------------

def tris(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)

total = sum(tris(o) for o in scene.objects if o.type == 'MESH')
dims = table.dimensions
print("SEEK table tris=%d total tris=%d size=%.2f x %.2f x %.2f" % (tris(table), total, dims.x, dims.y, dims.z))
for n, p in core_points:
    print("SEEK core%d at (%.3f, %.3f, %.3f), %.3f m from the orb's axis" % (n, p.x, p.y, p.z, math.hypot(p.x, p.y)))
print("SEEK orb at z=%.3f" % orb_z)

# ---- save and export ---------------------------------------------------------------------------------

blend_path = os.path.join(OUT_DIR, "DvergerSeekingTable.blend")
fbx_path = os.path.join(OUT_DIR, "DvergerSeekingTable.fbx")
bpy.ops.wm.save_as_mainfile(filepath=blend_path, compress=True)
for o in scene.objects:
    o.select_set(True)
bpy.ops.export_scene.fbx(
    filepath=fbx_path, use_selection=False, object_types={'MESH', 'EMPTY'},
    apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
    bake_space_transform=True, mesh_smooth_type='FACE', use_mesh_modifiers=True,
    add_leaf_bones=False, path_mode='STRIP', embed_textures=False)
print("SEEK wrote", blend_path, fbx_path)

# ---- review renders ------------------------------------------------------------------------------------

world = bpy.data.worlds.new("World")
world.color = (0.05, 0.06, 0.08)
scene.world = world
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'STUDIO'
scene.display.shading.color_type = 'MATERIAL'
scene.display.shading.show_cavity = True
scene.display.shading.show_shadows = True
scene.render.resolution_x = 1000
scene.render.resolution_y = 1000
scene.render.film_transparent = False

# A stand-in for the orb in the renders only, so the composition reads.
obm = bmesh.new()
bmesh.ops.create_uvsphere(obm, u_segments=24, v_segments=12, radius=0.3)
omesh = bpy.data.meshes.new("orb_preview")
obm.to_mesh(omesh)
obm.free()
omesh.materials.append(make_material("OrbPreview", (0.1, 0.5, 1.0), emission=(0.1, 0.5, 1.0)))
preview = bpy.data.objects.new("orb_preview", omesh)
preview.location = (0, 0, orb_z)
scene.collection.objects.link(preview)

cam_data = bpy.data.cameras.new("cam")
cam_data.lens = 40
cam = bpy.data.objects.new("cam", cam_data)
scene.collection.objects.link(cam)
scene.camera = cam


def shoot(name, location, target=(0, 0, 1.1)):
    cam.location = location
    direction = Vector(target) - Vector(location)
    cam.rotation_euler = direction.to_track_quat('-Z', 'Y').to_euler()
    scene.render.filepath = os.path.join(RENDER_DIR, name + ".png")
    bpy.ops.render.render(write_still=True)
    print("SEEK render", scene.render.filepath)

shoot("seeking_table_front", (5.2, -3.4, 2.6))
shoot("seeking_table_high", (3.0, -4.2, 4.8))
shoot("seeking_table_side", (0.0, -5.8, 1.6))
