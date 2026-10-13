"""Lifts Ashlands' lava bubbling (fx_lavabubbling) out of the game rip into the DvergerAutomation Unity
project, for the Grinder's lava tank.

Usage: python -I scripts/unity/lift_grinder_bubble.py <rip Assets dir> DvergerAutomationUnity/Assets

Writes Assets/Custom/Effects/DA_LavaBubble.prefab: the whole effect - the splash on its root and its chunks,
lava bits, smoke, large splash and sparks - with every GameObject, Transform, ParticleSystem and renderer
document copied verbatim. Only these change:
- the root is renamed and moved to the origin;
- its TimedDestruction is dropped, since this one stays on the Grinder and is replayed rather than spawned;
- no system plays on awake, so a Grinder does not bubble the moment it loads;
- each renderer points at a JVLmock_ material named after the vanilla one, which Jotunn swaps for the real
  material when the piece registers. The mock materials are written too, with no textures, so nothing of
  the game's ships in the bundle.

Vanilla's effect is sized for a lava field, and its systems scale by their own transforms only (Local
scaling), so the Grinder builder shrinks each one where it places it. Re-runnable: every asset keeps its
guid. Run the Grinder builder after.
"""
import os
import random
import re
import sys

RIP, UNITY = sys.argv[1], sys.argv[2]
random.seed(20261013)

SOURCE = os.path.join(RIP, "PrefabInstance", "fx_lavabubbling.prefab")
FX_PATH = os.path.join(UNITY, "Custom", "Effects", "DA_LavaBubble.prefab")
MOCK_DIR = os.path.join(UNITY, "Custom", "Mocks", "Materials")
SHELL = os.path.join(MOCK_DIR, "JVLmock_fog.mat")
NAME = "DA_LavaBubble"


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


# guid -> vanilla material name, from the rip's material metas.
material_names = {}
material_dir = os.path.join(RIP, "Material")
for meta in os.listdir(material_dir):
    if not meta.endswith(".mat.meta"):
        continue
    m = re.search(r"^guid: (\w+)", open(os.path.join(material_dir, meta), encoding="utf-8").read(), re.M)
    if m:
        material_names[m.group(1)] = meta[:-len(".mat.meta")]

text = open(SOURCE, encoding="utf-8").read()
parts = re.split(r"^(--- !u!(\d+) &(\d+).*)$", text, flags=re.M)
docs = []
for i in range(1, len(parts), 4):
    docs.append((parts[i + 1], parts[i + 2], parts[i], parts[i + 3]))

# The root GameObject: the one whose Transform has no parent.
fathers = {fid: re.search(r"m_Father: \{fileID: (\d+)\}", body).group(1)
           for cls, fid, _, body in docs if cls == "4"}
root_tr = next(fid for fid, father in fathers.items() if father == "0")
dropped = {fid for cls, fid, _, _ in docs if cls == "114"}

mock_guids = {}
out = []
for cls, fid, header, body in docs:
    if fid in dropped:
        continue
    if cls == "1":
        for d in dropped:
            body = re.sub(r"  - component: \{fileID: %s\}\n" % d, "", body)
        if re.search(r"component: \{fileID: %s\}" % root_tr, body):
            body = re.sub(r"m_Name: .*", f"m_Name: {NAME}", body, count=1)
    if cls == "4" and fid == root_tr:
        body = re.sub(r"m_LocalPosition: \{[^}]*\}", "m_LocalPosition: {x: 0, y: 0, z: 0}", body)
    if cls == "198":
        body = re.sub(r"^  playOnAwake: 1$", "  playOnAwake: 0", body, flags=re.M)
    if cls == "199":
        def to_mock(m):
            vanilla = material_names.get(m.group(1))
            if vanilla is None:
                raise SystemExit(f"no material in the rip for guid {m.group(1)}")
            if vanilla not in mock_guids:
                mock_guids[vanilla] = mock_material(vanilla)
            return "{fileID: 2100000, guid: %s, type: 2}" % mock_guids[vanilla]
        body = re.sub(r"\{fileID: 2100000, guid: (\w+), type: 2\}", to_mock, body)
    # Anything else pointing outside this file would come in as a missing reference.
    for g in re.findall(r"guid: (\w+), type: [23]", body):
        if g not in mock_guids.values():
            raise SystemExit(f"{cls} &{fid} references asset {g}, which is not lifted")
    out.append(header + body)

write(FX_PATH, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n" + "".join(out).rstrip("\n") + "\n")
meta = FX_PATH + ".meta"
write(meta, "fileFormatVersion: 2\nguid: %s\nPrefabImporter:\n  externalObjects: {}\n  userData: \n"
            "  assetBundleName: \n  assetBundleVariant: \n" % existing_guid(meta))
print("wrote", FX_PATH, "with mocks for", ", ".join(sorted(mock_guids)))
