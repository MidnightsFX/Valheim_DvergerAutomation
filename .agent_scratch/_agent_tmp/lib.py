# Dverger embellishment primitives. All inputs are WORLD space (Blender Z-up).
import bpy, bmesh, math, random
from mathutils import Vector, Matrix

def V(*a):
    return Vector(a) if len(a) == 3 else Vector(a[0])

class Builder:
    """Accumulates world-space geometry, then bakes it into an object's mesh."""
    def __init__(self, obj):
        self.obj = obj
        self.bm = bmesh.new()
        self.bm.from_mesh(obj.data)
        self.bm.verts.ensure_lookup_table(); self.bm.faces.ensure_lookup_table()
        self.n0_faces = len(self.bm.faces)
        self.n0_verts = len(self.bm.verts)
        self.Minv = obj.matrix_world.inverted()

    # --- low level -------------------------------------------------------
    def _v(self, p):
        return self.bm.verts.new(self.Minv @ Vector(p))

    def quad(self, a, b, c, d):
        try:
            return self.bm.faces.new((a, b, c, d))
        except ValueError:
            return None

    def hull(self, corners, faces):
        vs = [self._v(p) for p in corners]
        out = []
        for f in faces:
            try:
                out.append(self.bm.faces.new([vs[i] for i in f]))
            except ValueError:
                pass
        return out

    # --- primitives ------------------------------------------------------
    def box_corners(self, c0, c1, c2, c3, d0, d1, d2, d3):
        """8 explicit corners: bottom quad then top quad (same winding)."""
        F = [(0,1,2,3)[::-1], (4,5,6,7), (0,4,7,3)[::-1], (1,2,6,5)[::-1], (0,1,5,4)[::-1], (3,7,6,2)[::-1]]
        return self.hull([c0,c1,c2,c3,d0,d1,d2,d3], F)

    def beam(self, p0, p1, w, h, up=None):
        """Rectangular beam from p0 to p1. w = width (across), h = thickness (along up)."""
        p0 = Vector(p0); p1 = Vector(p1)
        ax = (p1 - p0)
        if ax.length < 1e-6: return []
        ax.normalize()
        up = Vector(up) if up else Vector((0, 0, 1))
        if abs(ax.dot(up)) > 0.98:
            up = Vector((0, 1, 0))
        side = ax.cross(up).normalized()
        upn = side.cross(ax).normalized()
        sw = side * (w * 0.5); su = upn * (h * 0.5)
        c = [p0 - sw - su, p0 + sw - su, p0 + sw + su, p0 - sw + su]
        d = [p1 - sw - su, p1 + sw - su, p1 + sw + su, p1 - sw + su]
        return self.box_corners(*c, *d)

    def seg_beam(self, p0, p1, w, h, blocks=3, gap=0.018, jitter=0.012, up=None, seed=0):
        """Beam split into chunky blocks with gaps and per-block size jitter (masonry read)."""
        rnd = random.Random(seed)
        p0 = Vector(p0); p1 = Vector(p1)
        out = []
        for i in range(blocks):
            t0 = i / blocks; t1 = (i + 1) / blocks
            a = p0.lerp(p1, t0); b = p0.lerp(p1, t1)
            ax = (b - a)
            L = ax.length
            if L <= gap * 1.2: continue
            ax.normalize()
            a = a + ax * (gap * 0.5); b = b - ax * (gap * 0.5)
            jw = 1.0 + rnd.uniform(-jitter, jitter) / max(w, 1e-4) * w
            jh = 1.0 + rnd.uniform(-jitter, jitter) / max(h, 1e-4) * h
            out += self.beam(a, b, w * jw, h * jh, up)
        return out

    def prism(self, center, r, h, n=8, rot=0.0, r_top=None, cap_bottom=True, cap_top=True):
        c = Vector(center); rt = r if r_top is None else r_top
        bot, top = [], []
        for i in range(n):
            a = rot + 2 * math.pi * i / n
            bot.append(self._v(c + Vector((r * math.cos(a), r * math.sin(a), -h * 0.5))))
            top.append(self._v(c + Vector((rt * math.cos(a), rt * math.sin(a), h * 0.5))))
        out = []
        for i in range(n):
            j = (i + 1) % n
            try: out.append(self.bm.faces.new((bot[i], bot[j], top[j], top[i])))
            except ValueError: pass
        if cap_top:
            try: out.append(self.bm.faces.new(top))
            except ValueError: pass
        if cap_bottom:
            try: out.append(self.bm.faces.new(list(reversed(bot))))
            except ValueError: pass
        return out

    def stud(self, pos, normal, r=0.026, h=0.024, n=5):
        """A rivet/peg sticking out of a surface along `normal`."""
        nrm = Vector(normal).normalized()
        up = Vector((0, 0, 1))
        if abs(nrm.dot(up)) > 0.95: up = Vector((1, 0, 0))
        s = nrm.cross(up).normalized(); u = s.cross(nrm).normalized()
        base, tip = [], []
        p = Vector(pos)
        for i in range(n):
            a = 2 * math.pi * i / n
            off = s * (r * math.cos(a)) + u * (r * math.sin(a))
            base.append(self._v(p + off))
            tip.append(self._v(p + off * 0.78 + nrm * h))
        out = []
        for i in range(n):
            j = (i + 1) % n
            try: out.append(self.bm.faces.new((base[i], base[j], tip[j], tip[i])))
            except ValueError: pass
        try: out.append(self.bm.faces.new(tip))
        except ValueError: pass
        return out

    def ring(self, z0, z1, r_in, r_out, n=8, rot=0.0, cx=0.0, cy=0.0):
        """Flat annulus band (outer wall, inner wall, top, bottom)."""
        A = []
        for i in range(n):
            a = rot + 2 * math.pi * i / n
            ca, sa = math.cos(a), math.sin(a)
            A.append((
                self._v((cx + r_out * ca, cy + r_out * sa, z0)), self._v((cx + r_out * ca, cy + r_out * sa, z1)),
                self._v((cx + r_in * ca, cy + r_in * sa, z1)), self._v((cx + r_in * ca, cy + r_in * sa, z0))))
        out = []
        for i in range(n):
            o0, t0, i0, b0 = A[i]
            o1, t1, i1, b1 = A[(i + 1) % n]
            for f in ((o0, o1, t1, t0), (t0, t1, i1, i0), (i0, i1, b1, b0), (b0, b1, o1, o0)):
                try: out.append(self.bm.faces.new(f))
                except ValueError: pass
        return out

    def tube(self, path, r=0.055, n=6, cap=True):
        """Swept polygonal tube through a list of world points."""
        path = [Vector(p) for p in path]
        rings = []
        prev_up = Vector((0, 0, 1))
        for i, p in enumerate(path):
            if i == 0: t = (path[1] - path[0])
            elif i == len(path) - 1: t = (path[-1] - path[-2])
            else: t = (path[i + 1] - path[i - 1])
            t.normalize()
            up = prev_up - t * prev_up.dot(t)
            if up.length < 1e-4:
                up = Vector((1, 0, 0)) - t * Vector((1, 0, 0)).dot(t)
            up.normalize(); prev_up = up
            s = t.cross(up).normalized()
            rings.append([self._v(p + s * (r * math.cos(2 * math.pi * k / n)) + up * (r * math.sin(2 * math.pi * k / n))) for k in range(n)])
        out = []
        for i in range(len(rings) - 1):
            for k in range(n):
                j = (k + 1) % n
                try: out.append(self.bm.faces.new((rings[i][k], rings[i][j], rings[i + 1][j], rings[i + 1][k])))
                except ValueError: pass
        if cap:
            try: out.append(self.bm.faces.new(list(reversed(rings[0]))))
            except ValueError: pass
            try: out.append(self.bm.faces.new(rings[-1]))
            except ValueError: pass
        return out

    # --- finish ----------------------------------------------------------
    def commit(self, group_name="Dverger"):
        bm = self.bm
        bm.verts.ensure_lookup_table(); bm.faces.ensure_lookup_table()
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces[self.n0_faces:]))
        bm.verts.ensure_lookup_table(); bm.faces.ensure_lookup_table()
        new_face_idx = [f.index for f in bm.faces[self.n0_faces:]]
        new_vert_idx = [v.index for v in bm.verts[self.n0_verts:]]
        for f in bm.faces[self.n0_faces:]:
            f.smooth = False
        bm.to_mesh(self.obj.data)
        bm.free()
        self.obj.data.update()
        vg = self.obj.vertex_groups.get(group_name) or self.obj.vertex_groups.new(name=group_name)
        vg.add(new_vert_idx, 1.0, 'REPLACE')
        return new_face_idx, new_vert_idx


def polar(az_deg, r, z):
    a = math.radians(az_deg)
    return Vector((r * math.cos(a), r * math.sin(a), z))


def strip(obj, group_name="Dverger"):
    """Delete every vertex assigned to `group_name` (makes the build idempotent)."""
    vg = obj.vertex_groups.get(group_name)
    if not vg:
        return 0
    gi = vg.index
    bm = bmesh.new(); bm.from_mesh(obj.data)
    dl = bm.verts.layers.deform.verify()
    doomed = [v for v in bm.verts if gi in v[dl]]
    n = len(doomed)
    bmesh.ops.delete(bm, geom=doomed, context='VERTS')
    bm.to_mesh(obj.data); bm.free()
    obj.data.update()
    obj.vertex_groups.remove(vg)
    return n
