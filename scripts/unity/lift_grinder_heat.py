"""Lifts the black forge's lava heat - its rising smoke and its glow - out of the game rip into the
DvergerAutomation Unity project, for the Grinder's lava tank.

Usage: python -I scripts/unity/lift_grinder_heat.py <rip Assets dir> DvergerAutomationUnity/Assets

Writes Assets/Custom/Effects/DA_LavaHeat.prefab: a root holding "RisingSmoke" (the forge's
"Rising Smoke (Circle)", a slow plume off a 0.27m circle, which is the size of the tank's mouth) and "Glow"
(the forge's "flare" over its lava). Their GameObject, Transform, ParticleSystem and renderer documents are
copied verbatim; only the hierarchy is rebuilt and each renderer is pointed at a JVLmock_ material named
after the vanilla one, which Jotunn swaps for the real material when the piece registers. The mock
materials (JVLmock_risingsmoke, JVLmock_light_glow) are written too, with no textures, so nothing of the
game's ships in the bundle. Re-runnable: every asset keeps its guid. Run the Grinder builder after.
"""
import os
import random
import re
import sys

RIP, UNITY = sys.argv[1], sys.argv[2]
random.seed(20261012)

SOURCE = os.path.join(RIP, "GameObject", "blackforge.prefab")
FX_PATH = os.path.join(UNITY, "Custom", "Effects", "DA_LavaHeat.prefab")
MOCK_DIR = os.path.join(UNITY, "Custom", "Mocks", "Materials")
SHELL = os.path.join(MOCK_DIR, "JVLmock_fog.mat")

# Vanilla node name -> our node name, vanilla material name, local position.
PICK = [
    ("Rising Smoke (Circle)", "RisingSmoke", "risingsmoke", (0.0, 0.0, 0.0)),
    ("flare", "Glow", "light_glow", (0.0, 0.08, 0.0)),
]


def new_guid():
    return "%032x" % random.getrandbits(128)


def existing_guid(meta_path):
    if os.path.exists(meta_path):
        m = re.search(r"^guid: (\w+)", open(meta_path, encoding="utf-8").read(), re.M)
        if m:
            return m.group(1)
    return new_guid()


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def mock_material(vanilla_name):
    """A textureless stand-in named after the vanilla material, tagged into the bundle."""
    path = os.path.join(MOCK_DIR, f"JVLmock_{vanilla_name}.mat")
    meta = path + ".meta"
    guid = existing_guid(meta)
    shell = open(SHELL, encoding="utf-8").read()
    write(path, re.sub(r"m_Name: .*", f"m_Name: JVLmock_{vanilla_name}", shell, count=1))
    write(meta, "fileFormatVersion: 2\nguid: %s\nNativeFormatImporter:\n  externalObjects: {}\n"
                "  mainObjectFileID: 2100000\n  userData: \n  assetBundleName: automation\n"
                "  assetBundleVariant: \n" % guid)
    return guid


text = open(SOURCE, encoding="utf-8").read()
parts = re.split(r"^(--- !u!(\d+) &(\d+).*)$", text, flags=re.M)
docs = {}
for i in range(1, len(parts), 4):
    header, cls, fid, body = parts[i], parts[i + 1], parts[i + 2], parts[i + 3]
    docs[fid] = (cls, header, body)

names = {fid: re.search(r"m_Name: (.*)", body).group(1).strip()
         for fid, (cls, _, body) in docs.items() if cls == "1"}

root_go, root_tr = str(random.getrandbits(62)), str(random.getrandbits(62))
out_docs = []
child_trs = []
for vanilla, ours, material, pos in PICK:
    go = next(fid for fid, n in names.items() if n == vanilla)
    comps = re.findall(r"component: \{fileID: (\d+)\}", docs[go][2])
    mat_guid = mock_material(material)
    cls, header, body = docs[go]
    out_docs.append(header + re.sub(r"m_Name: .*", f"m_Name: {ours}", body, count=1))
    for c in comps:
        ccls, cheader, cbody = docs[c]
        if ccls == "4":
            cbody = re.sub(r"m_LocalPosition: \{[^}]*\}", "m_LocalPosition: {x: %s, y: %s, z: %s}" % pos, cbody)
            cbody = re.sub(r"m_Father: \{fileID: \d+\}", "m_Father: {fileID: %s}" % root_tr, cbody)
            child_trs.append(c)
        if ccls == "199":
            cbody = re.sub(r"\{fileID: 2100000, guid: [0-9a-f]+, type: 2\}",
                           "{fileID: 2100000, guid: %s, type: 2}" % mat_guid, cbody)
        out_docs.append(cheader + cbody)

root = f"""--- !u!1 &{root_go}
GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  serializedVersion: 6
  m_Component:
  - component: {{fileID: {root_tr}}}
  m_Layer: 0
  m_Name: DA_LavaHeat
  m_TagString: Untagged
  m_Icon: {{fileID: 0}}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
--- !u!4 &{root_tr}
Transform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {root_go}}}
  serializedVersion: 2
  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}
  m_LocalPosition: {{x: 0, y: 0, z: 0}}
  m_LocalScale: {{x: 1, y: 1, z: 1}}
  m_ConstrainProportionsScale: 0
  m_Children:
""" + "".join(f"  - {{fileID: {t}}}\n" for t in child_trs) + """  m_Father: {fileID: 0}
  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}
"""

write(FX_PATH, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n" + root + "".join(out_docs).rstrip("\n") + "\n")
meta = FX_PATH + ".meta"
write(meta, "fileFormatVersion: 2\nguid: %s\nPrefabImporter:\n  externalObjects: {}\n  userData: \n"
            "  assetBundleName: \n  assetBundleVariant: \n" % existing_guid(meta))
print("wrote", FX_PATH, "with", len(PICK), "effects")
