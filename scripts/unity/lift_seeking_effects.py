"""Lifts the Galdr table's orb effects and the workbench extension line out of the game rip into the
DvergerAutomation Unity project, recoloured blue, pointed at the project's own materials.

Usage: python -I scripts/unity/lift_seeking_effects.py <rip Assets dir> DvergerAutomationUnity/Assets

Writes Assets/Custom/Effects/DA_SeekingOrb.prefab and DA_RetrievalBeam.prefab, the DA_seek_* materials
in Assets/Custom/Materials/SeekingTable, and the JVLmock_cinder texture stand-in. Re-runnable: every
asset keeps its guid, so the table prefab's references survive. Run the Seeking Table builder after.

The materials are the project's own, not JVLmock_ materials: Jotunn swaps a mock material wholesale for
the vanilla one, which would bring the Galdr table's magenta back. Only their shader and textures are
mocks, which Jotunn resolves one by one and leaves the colours alone.
"""
import colorsys, os, random, re, struct, sys, zlib, math

RIP, UNITY = sys.argv[1], sys.argv[2]
random.seed(20261010)

MOCK_SHADER = "{fileID: 4800000, guid: 8397e7f42fd80a44ab95e11b67689489, type: 3}"  # JVLmock_Particles/Standard Unlit2
GLOW_MOCK_GUID = "9633e8615c29f0b4a8c3b40838848129"                                   # JVLmock_glow.png
RIP_GLOW_GUID = "38b77a51782b5a644ba62ac8741020ca"
RIP_CINDER_GUID = "33a94b37e8d7f314a80f938b9776eab5"
RIP_VALHEIM_GUID = "c1b78fa918b030faf1c1f6f6164daeb2"
PROJECT_VALHEIM_GUID = "20b7f6e4177840c46bb41b6c7cd31fd4"

MAT_DIR = os.path.join(UNITY, "Custom", "Materials", "SeekingTable")
FX_DIR = os.path.join(UNITY, "Custom", "Effects")
MOCK_IMG_DIR = os.path.join(UNITY, "Custom", "Mocks", "Images")


def new_guid():
    return "%032x" % random.getrandbits(128)


def existing_guid(meta_path):
    """Keeps an asset's guid stable across re-runs, so references to it survive."""
    if os.path.exists(meta_path):
        m = re.search(r"^guid: (\w+)", open(meta_path, encoding="utf-8").read(), re.M)
        if m:
            return m.group(1)
    return new_guid()


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


# ---- colour ----------------------------------------------------------------------------------

def blue(r, g, b):
    """Magenta and pink go to the table's blue; the vanilla blues go a shade towards cyan so the
    gradients keep their two tones. Greys, black and white are left alone."""
    peak = max(r, g, b)
    if peak <= 0:
        return r, g, b
    # HDR values: work on the normalised colour and scale back.
    h, s, v = colorsys.rgb_to_hsv(r / peak, g / peak, b / peak)
    if s < 0.08:
        return r, g, b
    deg = h * 360.0
    if 180.0 <= deg < 250.0:
        target = 188.0
    else:
        target = 207.0
    nr, ng, nb = colorsys.hsv_to_rgb(target / 360.0, s, v)
    return nr * peak, ng * peak, nb * peak


def fmt(x):
    return ("%.6f" % x).rstrip("0").rstrip(".") if x != int(x) else str(int(x))


COLOR_RE = re.compile(r"\{r: ([-\d.eE]+), g: ([-\d.eE]+), b: ([-\d.eE]+), a: ([-\d.eE]+)\}")


def recolor_block(text):
    def sub(m):
        r, g, b, a = (float(m.group(i)) for i in range(1, 5))
        nr, ng, nb = blue(r, g, b)
        return "{r: %s, g: %s, b: %s, a: %s}" % (fmt(nr), fmt(ng), fmt(nb), m.group(4))
    text = COLOR_RE.sub(sub, text)

    # Gradient keys are also stored packed, as rgba: <uint32 ABGR>.
    def sub_packed(m):
        v = int(m.group(1))
        r, g, b, a = v & 255, (v >> 8) & 255, (v >> 16) & 255, (v >> 24) & 255
        nr, ng, nb = blue(r / 255.0, g / 255.0, b / 255.0)
        clamp = lambda x: max(0, min(255, int(round(x * 255))))
        return "rgba: %d" % (clamp(nr) | clamp(ng) << 8 | clamp(nb) << 16 | a << 24)
    return re.sub(r"rgba: (\d+)", sub_packed, text)


# ---- Unity YAML ----------------------------------------------------------------------------

DOC_RE = re.compile(r"^--- !u!(\d+) &(-?\d+)(.*)$", re.M)


def parse(path):
    text = open(path, encoding="utf-8", errors="replace").read()
    header = text[:DOC_RE.search(text).start()]
    docs = {}
    order = []
    matches = list(DOC_RE.finditer(text))
    for i, m in enumerate(matches):
        end = matches[i + 1].start() if i + 1 < len(matches) else len(text)
        docs[m.group(2)] = {"cls": m.group(1), "extra": m.group(3), "body": text[m.end():end]}
        order.append(m.group(2))
    return header, docs, order


def go_name(doc):
    return re.search(r"m_Name: (.*)", doc["body"]).group(1).strip()


def components(doc):
    return re.findall(r"component: \{fileID: (-?\d+)\}", doc["body"])


def transform_of(docs, go_id):
    for c in components(docs[go_id]):
        if docs[c]["cls"] in ("4", "224"):
            return c
    return None


def find_go(docs, name):
    for fid, d in docs.items():
        if d["cls"] == "1" and go_name(d) == name:
            return fid
    raise KeyError(name)


def children(docs, tr_id):
    body = docs[tr_id]["body"]
    block = body.split("m_Children:")[1].split("m_Father")[0]
    return re.findall(r"\{fileID: (-?\d+)\}", block)


def go_of_transform(docs, tr_id):
    return re.search(r"m_GameObject: \{fileID: (-?\d+)\}", docs[tr_id]["body"]).group(1)


def subtree_docs(docs, go_id):
    """Every document belonging to a GameObject and its descendants, in a stable order."""
    out = [go_id] + components(docs[go_id])
    for child_tr in children(docs, transform_of(docs, go_id)):
        out += subtree_docs(docs, go_of_transform(docs, child_tr))
    return out


def set_field(body, field, value):
    new, n = re.subn(r"(^\s*%s: ).*$" % re.escape(field), lambda m: m.group(1) + value, body, count=1, flags=re.M)
    if n == 0:
        raise ValueError("no field " + field)
    return new


def set_children(body, ids):
    block = "m_Children:" + ("".join("\n  - {fileID: %s}" % i for i in ids) if ids else " []") + "\n  m_Father"
    return re.sub(r"m_Children:.*?\n  m_Father", lambda m: block, body, count=1, flags=re.S)


def quat_y(deg):
    half = math.radians(deg) / 2.0
    return "{x: 0, y: %s, z: 0, w: %s}" % (fmt(round(math.sin(half), 7)), fmt(round(math.cos(half), 7)))


def emit(path, header, docs, ids):
    text = header
    for fid in ids:
        d = docs[fid]
        text += "--- !u!%s &%s%s%s" % (d["cls"], fid, d["extra"], d["body"])
    write(path, text)


def check_refs(name, docs, ids, allowed_guids):
    """Reports, and blanks, any reference into the rip that the project does not have."""
    for fid in ids:
        body = docs[fid]["body"]

        def sub(m):
            guid = m.group(2)
            if guid in allowed_guids:
                return m.group(0)
            print("  %s: dropped a reference to rip asset %s in doc %s" % (name, guid, fid))
            return "{fileID: 0}"
        docs[fid]["body"] = re.sub(r"\{fileID: (-?\d+), guid: (\w+), type: \d+\}", sub, body)


def prefab_meta(guid, bundle):
    return ("fileFormatVersion: 2\nguid: %s\nPrefabImporter:\n  externalObjects: {}\n  userData: \n"
            "  assetBundleName: %s\n  assetBundleVariant: \n") % (guid, bundle)


def material_meta(guid):
    return ("fileFormatVersion: 2\nguid: %s\nNativeFormatImporter:\n  externalObjects: {}\n"
            "  mainObjectFileID: 2100000\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n") % guid


# ---- the cinder mock texture ---------------------------------------------------------------

def png(width, height, pixel):
    raw = b"".join(b"\x00" + b"".join(bytes(pixel(x, y)) for x in range(width)) for y in range(height))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def make_cinder_mock():
    """A stand-in only: Jotunn swaps in vanilla's own cinder texture at load, by name, so nothing of the
    game's ships in the bundle. This soft dot is what the editor shows."""
    path = os.path.join(MOCK_IMG_DIR, "JVLmock_cinder.png")
    size = 64

    def pixel(x, y):
        d = math.hypot(x - size / 2 + 0.5, y - size / 2 + 0.5) / (size / 2)
        a = int(255 * max(0.0, 1.0 - d) ** 2)
        return (255, 255, 255, a)
    with open(path, "wb") as f:
        f.write(png(size, size, pixel))
    meta_path = path + ".meta"
    guid = existing_guid(meta_path)
    glow_meta = open(os.path.join(MOCK_IMG_DIR, "JVLmock_glow.png.meta"), encoding="utf-8").read()
    meta = re.sub(r"^guid: \w+", "guid: " + guid, glow_meta, count=1, flags=re.M)
    write(meta_path, meta)
    return guid


# ---- materials -------------------------------------------------------------------------------

def make_material(rip_name, name, cinder_guid, emission=None):
    src = open(os.path.join(RIP, "Material", rip_name + ".mat"), encoding="utf-8").read()
    src = set_field(src, "m_Name", name)
    src = set_field(src, "m_Shader", MOCK_SHADER)
    src = src.replace("{fileID: 2800000, guid: %s, type: 3}" % RIP_GLOW_GUID,
                      "{fileID: 2800000, guid: %s, type: 3}" % GLOW_MOCK_GUID)
    src = src.replace("{fileID: 2800000, guid: %s, type: 3}" % RIP_CINDER_GUID,
                      "{fileID: 2800000, guid: %s, type: 3}" % cinder_guid)
    leftover = re.findall(r"guid: (\w+)", src)
    for g in leftover:
        if g not in (GLOW_MOCK_GUID, cinder_guid, "8397e7f42fd80a44ab95e11b67689489"):
            print("  %s: dropped a reference to rip asset %s" % (name, g))
            src = re.sub(r"\{fileID: \d+, guid: %s, type: \d+\}" % g, "{fileID: 0}", src)

    # Recolour the emission. The rest of the material's colours are greys.
    def sub(m):
        r, g, b, a = (float(m.group(i)) for i in range(1, 5))
        if emission is not None:
            r, g, b = emission
        else:
            r, g, b = blue(r, g, b)
        return "_EmissionColor: {r: %s, g: %s, b: %s, a: %s}" % (fmt(r), fmt(g), fmt(b), m.group(4))
    src = re.sub(r"_EmissionColor: \{r: ([-\d.eE]+), g: ([-\d.eE]+), b: ([-\d.eE]+), a: ([-\d.eE]+)\}", sub, src)

    path = os.path.join(MAT_DIR, name + ".mat")
    meta_path = path + ".meta"
    guid = existing_guid(meta_path)
    write(path, src)
    write(meta_path, material_meta(guid))
    return guid


# ---- the orb -------------------------------------------------------------------------------------

def make_orb(materials):
    header, docs, _ = parse(os.path.join(RIP, "GameObject", "piece_magetable.prefab"))
    effects = find_go(docs, "Effects")
    keep = {"Point Light", "Blackhole", "Blackhole edge", "Blackhole edge (1)", "drips", "connection"}
    kept_children = []
    for child_tr in children(docs, transform_of(docs, effects)):
        go = go_of_transform(docs, child_tr)
        if go_name(docs[go]) in keep:
            kept_children.append(go)

    # The orb's centre is the root: everything shifts by the Blackhole's position.
    blackhole_tr = transform_of(docs, find_go(docs, "Blackhole"))
    cy = float(re.search(r"m_LocalPosition: \{x: [-\d.e]+, y: ([-\d.e]+)", docs[blackhole_tr]["body"]).group(1))

    ids = [effects, transform_of(docs, effects)]
    root_tr = transform_of(docs, effects)
    docs[effects]["body"] = set_field(docs[effects]["body"], "m_Name", "DA_SeekingOrb")
    docs[effects]["body"] = re.sub(r"component: \{fileID: (-?\d+)\}\n(?=  - component)", lambda m: m.group(0), docs[effects]["body"])
    docs[root_tr]["body"] = set_field(docs[root_tr]["body"], "m_Father", "{fileID: 0}")
    docs[root_tr]["body"] = set_field(docs[root_tr]["body"], "m_LocalPosition", "{x: 0, y: 0, z: 0}")
    docs[root_tr]["body"] = set_field(docs[root_tr]["body"], "m_LocalRotation", "{x: 0, y: 0, z: 0, w: 1}")
    # The root of a prefab carries no PrefabInstance link back into the rip.
    child_trs = []

    stream_go = None
    for go in kept_children:
        name = go_name(docs[go])
        tr = transform_of(docs, go)
        y = float(re.search(r"m_LocalPosition: \{x: [-\d.e]+, y: ([-\d.e]+)", docs[tr]["body"]).group(1))
        docs[tr]["body"] = set_field(docs[tr]["body"], "m_LocalPosition", "{x: 0, y: %s, z: 0}" % fmt(round(y - cy, 4)))
        if name == "connection":
            stream_go = go
            continue
        child_trs.append(tr)
        ids += subtree_docs(docs, go)

    # Four streams, one to each pillar, from the vanilla one. Pointed down the diagonals; the builder
    # pairs each with the socket it actually faces.
    for n, deg in enumerate((45, 135, 225, 315), start=1):
        mapping = {}
        for fid in subtree_docs(docs, stream_go):
            mapping[fid] = str(random.randint(10 ** 15, 9 * 10 ** 17))
        for old, new in mapping.items():
            d = dict(docs[old])
            body = d["body"]
            for o, nw in mapping.items():
                body = body.replace("{fileID: %s}" % o, "{fileID: %s}" % nw)
            d["body"] = body
            docs[new] = d
        go = mapping[stream_go]
        tr = transform_of(docs, go)
        docs[go]["body"] = set_field(docs[go]["body"], "m_Name", "Stream%d" % n)
        docs[tr]["body"] = set_field(docs[tr]["body"], "m_LocalRotation", quat_y(deg))
        docs[tr]["body"] = set_field(docs[tr]["body"], "m_LocalEulerAnglesHint", "{x: 0, y: %d, z: 0}" % deg)
        child_trs.append(tr)
        ids += [mapping[f] for f in subtree_docs(docs, stream_go)]

    docs[root_tr]["body"] = set_children(docs[root_tr]["body"], child_trs)
    for tr in child_trs:
        docs[tr]["body"] = set_field(docs[tr]["body"], "m_Father", "{fileID: %s}" % root_tr)

    for fid in ids:
        d = docs[fid]
        if d["cls"] in ("198",):
            d["body"] = recolor_block(d["body"])
        if d["cls"] == "199":
            for rip_mat, ours in materials.items():
                d["body"] = d["body"].replace("guid: %s, type: 2}" % rip_mat, "guid: %s, type: 2}" % ours)
        if d["cls"] == "108":
            d["body"] = set_field(d["body"], "m_Color", "{r: 0.2, g: 0.55, b: 1, a: 1}")
            d["body"] = set_field(d["body"], "m_Range", "3.5")
        if d["cls"] == "114":
            d["body"] = d["body"].replace(RIP_VALHEIM_GUID, PROJECT_VALHEIM_GUID)

    check_refs("DA_SeekingOrb", docs, ids, set(materials.values()) | {PROJECT_VALHEIM_GUID})
    path = os.path.join(FX_DIR, "DA_SeekingOrb.prefab")
    guid = existing_guid(path + ".meta")
    emit(path, header, docs, ids)
    write(path + ".meta", prefab_meta(guid, ""))
    print("wrote", path)


def make_beam(materials):
    header, docs, _ = parse(os.path.join(RIP, "GameObject", "vfx_ExtensionConnection.prefab"))
    root = find_go(docs, "vfx_ExtensionConnection")
    ids = subtree_docs(docs, root)
    docs[root]["body"] = set_field(docs[root]["body"], "m_Name", "DA_RetrievalBeam")
    root_tr = transform_of(docs, root)
    docs[root_tr]["body"] = set_field(docs[root_tr]["body"], "m_LocalPosition", "{x: 0, y: 0, z: 0}")
    for fid in ids:
        d = docs[fid]
        if d["cls"] == "198":
            d["body"] = recolor_block(d["body"])
        if d["cls"] == "199":
            for rip_mat, ours in materials.items():
                d["body"] = d["body"].replace("guid: %s, type: 2}" % rip_mat, "guid: %s, type: 2}" % ours)
    check_refs("DA_RetrievalBeam", docs, ids, set(materials.values()))
    path = os.path.join(FX_DIR, "DA_RetrievalBeam.prefab")
    guid = existing_guid(path + ".meta")
    emit(path, header, docs, ids)
    # Loaded from the bundle by name at startup, so it is in the bundle itself.
    write(path + ".meta", prefab_meta(guid, "automation"))
    print("wrote", path)


def rip_material_guid(name):
    meta = open(os.path.join(RIP, "Material", name + ".mat.meta"), encoding="utf-8").read()
    return re.search(r"^guid: (\w+)", meta, re.M).group(1)


def main():
    cinder = make_cinder_mock()
    # The table's blue, as an HDR emission: hue 207, about as bright as the vanilla magenta.
    r, g, b = colorsys.hsv_to_rgb(207 / 360.0, 1.0, 1.0)
    orb_glow = (r * 1.31, g * 1.31, b * 1.31)
    beam_glow = (r * 4.0, g * 4.0, b * 4.0)
    mats = {
        rip_material_guid("magetable_smoke"): make_material("magetable_smoke", "DA_seek_core", cinder),
        rip_material_guid("magetable_smoke_edge"): make_material("magetable_smoke_edge", "DA_seek_edge", cinder, orb_glow),
        rip_material_guid("magetable_smoke_cinder"): make_material("magetable_smoke_cinder", "DA_seek_cinder", cinder, (r, g, b)),
    }
    beam_mat = make_material("extensionconnection", "DA_seek_beam", cinder, beam_glow)
    beam_mats = {
        rip_material_guid("extensionconnection"): beam_mat,
        # The trail material: the beam's own colour serves for that too.
        rip_material_guid("waystone_Trail"): beam_mat,
    }
    make_orb(mats)
    make_beam(beam_mats)


main()
