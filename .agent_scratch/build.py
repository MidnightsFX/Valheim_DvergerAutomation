import bpy, bmesh, math, sys, os, importlib
D = os.path.dirname(bpy.data.texts.get("__agentdir__").as_string()) if bpy.data.texts.get("__agentdir__") else None
HERE = r"C:\Users\carls\Documents\projects\Valheim_Stuff\Valheim_DvergerAutomation\DvergerAutomationUnity\Assets\Custom\Models\_agent_tmp"
if HERE not in sys.path: sys.path.insert(0, HERE)
import lib; importlib.reload(lib)
from lib import Builder, polar, strip
from mathutils import Vector

LEGS   = [45, 135, 225, 315]
POSTS  = [0, 90, 270]          # az 180 carries the eitr pod instead
XPANEL = [0, 270]
RAILS  = [90]

# ---------------------------------------------------------------- MOUNT
def build_mount(ob):
    B = Builder(ob)

    # M1 masonry course around the plinth
    for i in range(10):
        az = i * 36.0 + 18.0
        a = math.radians(az)
        er = Vector((math.cos(a), math.sin(a), 0)); et = Vector((-math.sin(a), math.cos(a), 0))
        r0, r1 = 0.872, 0.944 + (0.012 if i % 2 else -0.008)
        hw = 0.245
        z0, z1 = 0.005, 0.186
        c = [er*r0 - et*hw + Vector((0,0,z0)), er*r0 + et*hw + Vector((0,0,z0)),
             er*r1 + et*hw*0.92 + Vector((0,0,z0)), er*r1 - et*hw*0.92 + Vector((0,0,z0))]
        d = [p + Vector((0,0,z1-z0)) for p in [c[0],c[1],c[2],c[3]]]
        B.box_corners(c[0], c[1], c[2], c[3], d[0], d[1], d[2], d[3])
        if i % 2 == 0:
            B.stud(er*(r1+0.001) + Vector((0,0,0.095)), er, r=0.028, h=0.026)

    # M2 top lip / turntable rim
    B.ring(0.186, 0.252, 0.775, 0.958, n=10, rot=math.radians(18))

    # M3 buttress feet under the legs
    for az in LEGS:
        B.beam(polar(az, 0.74, 0.08), polar(az, 0.925, -0.095), 0.26, 0.135)

    # M4 gantry posts standing in the open faces
    for az in POSTS:
        B.seg_beam(polar(az, 0.745, 0.19), polar(az, 0.505, 1.175), 0.115, 0.10,
                   blocks=3, gap=0.022, jitter=0.014, seed=int(az))
        a = math.radians(az); er = Vector((math.cos(a), math.sin(a), 0))
        B.stud(polar(az, 0.803, 0.30), er, r=0.027, h=0.025)
        B.stud(polar(az, 0.565, 1.09), er, r=0.027, h=0.025)

    # M5 X-braced panels
    def P(az, u, z):
        a = math.radians(az)
        rp = 0.72 - 0.20 * (z - 0.20) / 0.96 - 0.055
        er = Vector((math.cos(a), math.sin(a), 0)); et = Vector((-math.sin(a), math.cos(a), 0))
        return er * rp + et * u + Vector((0, 0, z))
    for az in XPANEL:
        B.beam(P(az, -0.40, 0.35), P(az,  0.40, 1.01), 0.062, 0.052)
        B.beam(P(az,  0.40, 0.35), P(az, -0.40, 1.01), 0.062, 0.052)
        B.beam(P(az, -0.45, 0.35), P(az,  0.45, 0.35), 0.070, 0.058)
        B.beam(P(az, -0.42, 1.01), P(az,  0.42, 1.01), 0.070, 0.058)
        a = math.radians(az); et = Vector((-math.sin(a), math.cos(a), 0))
        for u, z in ((-0.45, 0.35), (0.45, 0.35), (-0.42, 1.01), (0.42, 1.01)):
            B.stud(P(az, u, z), et * (1 if u > 0 else -1), r=0.026, h=0.022)
    for az in RAILS:
        B.beam(P(az, -0.300, 0.42), P(az, 0.300, 0.42), 0.070, 0.058)
        B.beam(P(az, -0.285, 0.95), P(az, 0.285, 0.95), 0.070, 0.058)

    # M7 top collar ring
    B.ring(1.125, 1.225, 0.285, 0.505, n=8, rot=math.radians(22.5))
    for az in POSTS:
        a = math.radians(az); er = Vector((math.cos(a), math.sin(a), 0))
        B.stud(polar(az, 0.512, 1.175), er, r=0.026, h=0.024)
        # gusset wedge tucked under the collar where the post lands
        B.beam(polar(az, 0.615, 0.995), polar(az, 0.455, 1.130), 0.135, 0.072)

    # M8 crown struts: collar -> base of the flue flare
    for az in (0, 90, 180, 270):
        B.beam(polar(az, 0.455, 1.205), polar(az, 0.245, 1.435), 0.075, 0.060)
        B.beam(polar(az + 45, 0.395, 1.215), polar(az + 45, 0.255, 1.400), 0.055, 0.048)

    return B.commit("Dverger")

# ---------------------------------------------------------- SORTINGENGINE
def build_engine(ob):
    B = Builder(ob)

    # E2 flare skirt where the flue leaves the body
    B.prism((0, -0.030, 1.480), r=0.225, h=0.085, n=8, rot=math.radians(22.5),
            r_top=0.150, cap_bottom=False, cap_top=False)

    # E1 flue collars + top lip
    B.ring(1.500, 1.556, 0.100, 0.158, n=8, rot=math.radians(22.5), cy=-0.030)
    B.ring(1.598, 1.650, 0.100, 0.152, n=8, rot=math.radians(22.5), cy=-0.030)
    B.ring(1.786, 1.845, 0.104, 0.165, n=8, rot=math.radians(22.5), cy=-0.030)
    for i in range(4):
        a = math.radians(45 + i * 90)
        n = Vector((math.cos(a), math.sin(a), 0))
        B.stud(Vector((0.153 * math.cos(a), -0.030 + 0.153 * math.sin(a), 1.528)), n, r=0.020, h=0.018)

    # E3 hopper mouth frame
    yF, x0, zb, zt = 0.812, 0.268, 0.196, 0.632
    B.beam(( x0, yF, zb), (-x0, yF, zb), 0.070, 0.058, up=(0,1,0))
    B.beam(( x0, yF, zt), (-x0, yF, zt), 0.070, 0.058, up=(0,1,0))
    B.beam((-x0, yF, zb), (-x0, yF, zt), 0.070, 0.058, up=(0,1,0))
    B.beam(( x0, yF, zb), ( x0, yF, zt), 0.070, 0.058, up=(0,1,0))
    for sx in (-1, 1):
        for z in (zb, zt):
            B.stud((sx * x0, yF + 0.030, z), (0, 1, 0), r=0.024, h=0.022)
        B.beam((sx * 0.232, 0.760, zb - 0.02), (sx * 0.232, 0.430, zb - 0.10), 0.070, 0.052)

    # E4 bent pipe out to a faceted pod on the open face
    POD = Vector((-0.662, 0.0, 0.636))
    B.prism(POD, r=0.190, h=0.775, n=8, rot=math.radians(22.5), r_top=0.166)
    B.ring(0.286, 0.352, 0.186, 0.238, n=8, rot=math.radians(22.5), cx=POD.x, cy=POD.y)   # foot band
    B.ring(0.905, 0.962, 0.170, 0.214, n=8, rot=math.radians(22.5), cx=POD.x, cy=POD.y)   # neck band
    B.prism(POD + Vector((0, 0, 0.424)), r=0.128, h=0.072, n=8, rot=math.radians(22.5))   # lid
    B.stud(POD + Vector((0, 0, 0.470)), (0, 0, 1), r=0.030, h=0.030)
    # feed pipe: body -> pod flank
    B.tube([(-0.238, 0.060, 0.700), (-0.322, 0.046, 0.782), (-0.404, 0.018, 0.862),
            (-0.492, 0.0, 0.866)], r=0.056, n=6)
    # anchor brackets tying the pod back into the flanking legs
    B.beam(polar(180, 0.600, 0.665), polar(148, 0.706, 0.622), 0.062, 0.052)
    B.beam(polar(180, 0.600, 0.665), polar(212, 0.706, 0.622), 0.062, 0.052)
    for i in range(4):
        a = math.radians(i * 90 + 22.5)
        n = Vector((math.cos(a), math.sin(a), 0))
        B.stud(POD + Vector((0.188 * math.cos(a), 0.188 * math.sin(a), -0.115)), n, r=0.024, h=0.022)

    return B.commit("Dverger")

result = {}
mo = bpy.data.objects['Mount']; eo = bpy.data.objects['SortingEngine']
stripped = (strip(mo), strip(eo))
fm, vm = build_mount(mo)
fe, ve = build_engine(eo)
result = {"stripped": stripped, "mount_new_faces": len(fm), "engine_new_faces": len(fe),
          "mount_total": len(mo.data.polygons), "engine_total": len(eo.data.polygons)}
