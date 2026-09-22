using System.Collections.Generic;
using System.IO;
using DvergerAutomation;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the Forge Hopper: its materials from the Substance Painter export, and
/// Assets/Custom/Prefabs/DA_ForgeHopper.prefab from DvergerHopperPaintable.fbx.
///
/// Re-runnable. Re-export textures or the model and run it again rather than hand-editing the
/// prefab or materials - anything worth keeping by hand belongs in this script instead.
///
/// This lives under its own assembly definition because the Valheim DLLs in Assets/Assemblies have
/// Auto Reference turned OFF - they dump hundreds of types into the global namespace and shadow
/// System.* types in package assemblies when auto-referenced. The asmdef next to this file pulls
/// them in explicitly, for this folder only.
/// </summary>
public static class BuildHopperPrefab {
    private const string ModelPath = "Assets/Custom/Models/DvergerHopperPaintable.fbx";
    private const string PrefabDir = "Assets/Custom/Prefabs";
    private const string PieceName = "DA_ForgeHopper";
    private const string PrefabPath = PrefabDir + "/" + PieceName + ".prefab";
    private const string BundleName = "automation";

    private const string TexDir = "Assets/Custom/Materials/ForgeHopper";
    private const string PieceShader = "JVLmock_Custom/Piece";
    private const string CoreMockPath = "Assets/Custom/Mocks/Materials/JVLmock_surtlingcore.mat";

    // Substance Painter texture sets, named after the FBX materials they were painted onto.
    private static readonly string[] TextureSets = {
        "Bars", "CoreHolder", "EngineMaterial", "GearsLarge", "GearsSmall", "Hopper",
        "Mainbody", "Slide", "Stand", "Support_Hopper", "Wood Legs",
    };

    // Jotunn swaps any asset named "JVLmock_<something>" for the real vanilla asset of that name at
    // registration time. SmokeBall is what vanilla's smelter and blast furnace both feed their
    // SmokeSpawner.
    private const string MockDir = "Assets/Custom/Mocks/Prefabs";
    private const string SmokeMockName = "JVLmock_SmokeBall";
    private const string SmokeMockPath = MockDir + "/" + SmokeMockName + ".prefab";

    // The continuous chimney smoke vanilla's smelter shows while running ("_enabled/smoke (1)" on the
    // smelter): its ParticleSystem and renderer copied verbatim from the game rip, with the renderer
    // pointed at JVLmock_fog so Jotunn swaps in vanilla's fog material. An inline child of the smelter
    // prefab rather than a standalone asset, so unlike SmokeBall it cannot itself be a mock.
    private const string ChimneySmokePath = "Assets/Custom/Effects/DA_SmelterSmoke.prefab";

    // "smoke" in this project's TagManager, and the same mask vanilla's furnaces use - SmokeSpawner
    // tests it to notice when its own output is piling up against a ceiling.
    private const int SmokeLayer = 31;

    // "piece" in this project's TagManager. The DA_Autosorter root sits on Default and everything
    // under it on piece; mirrored here.
    private const int PieceLayer = 10;
    private const int DefaultLayer = 0;

    private const int CoreSlots = 6;

    [MenuItem("DvergerAutomation/Build Forge Hopper (materials + prefab)")]
    public static void BuildAll() {
        List<string> notes = new List<string>();
        if (!BuildMaterials(notes)) { Report(notes); return; }
        BuildPrefab(notes);
        Report(notes);
    }

    [MenuItem("DvergerAutomation/Build Forge Hopper Materials Only")]
    public static void BuildMaterialsOnly() {
        List<string> notes = new List<string>();
        BuildMaterials(notes);
        Report(notes);
    }

    // ==================================================================== materials

    /// <summary>
    /// One material per Substance texture set on Valheim's piece shader, then remapped onto the FBX so
    /// the model itself carries them (and any prefab built from it inherits them).
    ///
    /// Valheim's Custom/Piece has no roughness, AO or height slot - only albedo, a metallic map and a
    /// normal map. So roughness is packed into the metallic map's alpha as smoothness, which is where
    /// the shader reads it from (its _MetallicAlphaGloss "metal smoothness" scales that alpha).
    /// </summary>
    private static bool BuildMaterials(List<string> notes) {
        Shader shader = Shader.Find(PieceShader);
        if (shader == null) {
            notes.Add("ERROR: shader '" + PieceShader + "' not found; no materials built.");
            return false;
        }

        Dictionary<string, Material> built = new Dictionary<string, Material>();
        foreach (string set in TextureSets) {
            string prefix = TexDir + "/" + set;
            Texture2D albedo = ConfigureAlbedo(prefix + "_Base_color.png", notes);
            Texture2D normal = ConfigureNormal(prefix + "_Normal_DirectX.png", notes);
            Texture2D metalGloss = PackMetallicGloss(prefix + "_Metallic.png", prefix + "_Roughness.png",
                                                    prefix + "_MetallicGloss.png", notes);

            string matPath = TexDir + "/ForgeHopper_" + set.Replace(" ", "") + ".mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            bool isNew = mat == null;
            if (isNew) { mat = new Material(shader); }
            mat.shader = shader;
            mat.name = Path.GetFileNameWithoutExtension(matPath);

            mat.SetTexture("_MainTex", albedo);
            mat.SetTexture("_MetallicTex", metalGloss);
            mat.SetTexture("_BumpMap", normal);
            mat.SetColor("_Color", Color.white);

            // Lifted from vanilla's destilereitr (the eitr refinery) - the dverger machine this piece is
            // modelled on. Deliberately NOT BlastFurnace_mat: that one carries _MetalColor 11.98, which is
            // an outlier among Custom/Piece materials (the refinery, artisan table, smelter and spinning
            // wheel all sit at 1). It only works there because that material's metallic mask is nearly
            // black; against a fully-white metallic mask a ~12x tint blows the metal out to flat white and
            // the albedo colour disappears.
            mat.SetFloat("_Metallic", 1f);
            mat.SetFloat("_MetallicAlphaGloss", 0.75f);
            mat.SetFloat("_Glossiness", 0.15f);
            mat.SetColor("_MetalColor", Color.white);
            mat.SetFloat("_BumpScale", 1f);
            mat.SetFloat("_AddRain", 1f);
            mat.SetFloat("_Cull", 2f);
            mat.EnableKeyword("_NORMALMAP");
            mat.EnableKeyword("_METALLICGLOSSMAP");
            mat.EnableKeyword("_ADDRAIN_ON");

            if (isNew) { AssetDatabase.CreateAsset(mat, matPath); } else { EditorUtility.SetDirty(mat); }
            built[set] = mat;
        }
        AssetDatabase.SaveAssets();

        // Remap the FBX's embedded materials onto the ones above. The six surtlingcore slots all go to
        // the existing mock, which Jotunn resolves to vanilla's glowing surtling core material.
        ModelImporter importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer == null) {
            notes.Add("ERROR: no ModelImporter for " + ModelPath + " - has Unity imported it?");
            return false;
        }
        foreach (KeyValuePair<string, Material> entry in built) {
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), entry.Key), entry.Value);
        }
        Material coreMock = AssetDatabase.LoadAssetAtPath<Material>(CoreMockPath);
        if (coreMock == null) {
            notes.Add("Missing " + CoreMockPath + " - the core crystals keep their embedded material.");
        } else {
            string[] coreSlots = { "surtlingcore", "surtlingcore.001", "surtlingcore.002",
                                   "surtlingcore.003", "surtlingcore.004", "surtlingcore.005" };
            foreach (string slot in coreSlots) {
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), slot), coreMock);
            }
        }
        importer.SaveAndReimport();

        notes.Add("Built " + built.Count + " materials in " + TexDir + " and remapped them onto the model.");
        return true;
    }

    private static Texture2D ConfigureAlbedo(string path, List<string> notes) {
        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) { notes.Add("Missing albedo " + path); return null; }
        if (ti.textureType != TextureImporterType.Default || !ti.sRGBTexture) {
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>
    /// Uses the DirectX normal, green-flipped into Unity's OpenGL convention. The OpenGL "_Normal"
    /// exports from this Substance project are flat - a single colour - so they carry none of the
    /// painted detail; the DirectX ones do.
    /// </summary>
    private static Texture2D ConfigureNormal(string path, List<string> notes) {
        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) { notes.Add("Missing normal " + path); return null; }
        if (ti.textureType != TextureImporterType.NormalMap || !ti.flipGreenChannel) {
            ti.textureType = TextureImporterType.NormalMap;
            ti.flipGreenChannel = true;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>
    /// RGB = metallic, A = smoothness (1 - roughness). Decoded straight from the PNG bytes so the
    /// source textures' import settings are never touched.
    /// </summary>
    private static Texture2D PackMetallicGloss(string metalPath, string roughPath, string outPath, List<string> notes) {
        if (!File.Exists(metalPath)) { notes.Add("Missing metallic " + metalPath); return null; }
        Texture2D metal = Decode(metalPath);
        Texture2D rough = File.Exists(roughPath) ? Decode(roughPath) : null;
        if (rough != null && (rough.width != metal.width || rough.height != metal.height)) {
            notes.Add("Roughness size differs from metallic for " + Path.GetFileName(metalPath)
                      + " - packed with flat smoothness instead.");
            Object.DestroyImmediate(rough);
            rough = null;
        }

        Color32[] m = metal.GetPixels32();
        Color32[] r = rough != null ? rough.GetPixels32() : null;
        Color32[] packed = new Color32[m.Length];
        for (int i = 0; i < m.Length; ++i) {
            byte metallic = m[i].r;
            byte smooth = r != null ? (byte)(255 - r[i].r) : (byte)128;
            packed[i] = new Color32(metallic, metallic, metallic, smooth);
        }
        Texture2D outTex = new Texture2D(metal.width, metal.height, TextureFormat.RGBA32, false);
        outTex.SetPixels32(packed);
        File.WriteAllBytes(outPath, outTex.EncodeToPNG());
        Object.DestroyImmediate(outTex);
        Object.DestroyImmediate(metal);
        if (rough != null) { Object.DestroyImmediate(rough); }

        AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceUpdate);
        TextureImporter ti = AssetImporter.GetAtPath(outPath) as TextureImporter;
        if (ti != null) {
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = false;   // linear data, not colour
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = false;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
    }

    private static Texture2D Decode(string path) {
        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.LoadImage(File.ReadAllBytes(path));
        return tex;
    }

    // ==================================================================== prefab

    private static void BuildPrefab(List<string> notes) {
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null) {
            notes.Add("ERROR: could not load " + ModelPath + ".");
            return;
        }

        GameObject root = new GameObject(PieceName);
        root.layer = DefaultLayer;

        // Hopper_Body is a clean identity container, so everything this script adds (store, smoke)
        // lives in real-world metres. HopperStore.ConfigurePrefab looks the store up at
        // "Hopper_Body/HopperStore".
        GameObject bodyGo = new GameObject("Hopper_Body");
        bodyGo.layer = PieceLayer;
        Transform body = bodyGo.transform;
        body.SetParent(root.transform, worldPositionStays: false);

        // The model goes in underneath with its imported transform left exactly as it is. This FBX is
        // in centimetres (fileScale 0.01), and Unity carries the axis conversion (270 X) and the unit
        // scale (x100) on its root. Resetting that root shrinks the machine 100x and tips it on its side;
        // baking the axis conversion at import instead flips the front to face backwards.
        // A plain Instantiate keeps mesh/material references pointing at the FBX without a nested
        // prefab instance that would need unpacking.
        GameObject modelInstance = Object.Instantiate(model);
        modelInstance.name = "Model";
        modelInstance.transform.SetParent(body, worldPositionStays: false);
        SetLayerRecursive(modelInstance, PieceLayer);

        // ---- collision -------------------------------------------------------
        // MeshColliders are fine under the x100: they are built from the small local-space mesh, and
        // the transform scales them back up to the real size.
        AddMeshCollider(modelInstance, notes);
        Transform head = FindDeep(root.transform, "Hopper_Head");
        if (head != null) { AddMeshCollider(head.gameObject, notes); }

        // ---- core sockets ----------------------------------------------------
        // Each switch is paired with the crystal physically nearest it, not the one sharing its number:
        // in this model AddCore4/AddCore6 and core4/core6 are cross-wired (0.96m apart), so pairing by
        // name would light the top socket when you click the bottom one.
        List<Renderer> crystals = new List<Renderer>();
        for (int n = 1; n <= CoreSlots; ++n) {
            Transform c = FindDeep(root.transform, "core" + n) ?? FindDeep(root.transform, "Core" + n);
            Renderer rend = c != null ? c.GetComponent<Renderer>() : null;
            if (rend != null) { crystals.Add(rend); } else { notes.Add("Missing crystal mesh core" + n + "."); }
        }

        Switch[] switches = new Switch[CoreSlots];
        GameObject[] coreVisuals = new GameObject[CoreSlots];
        for (int i = 0; i < CoreSlots; ++i) {
            int n = i + 1;
            Transform socket = FindDeep(root.transform, "AddCore" + n);
            if (socket == null) {
                notes.Add("Missing empty 'AddCore" + n + "' - slot " + n + " has no switch.");
                continue;
            }
            GameObject go = socket.gameObject;
            go.layer = PieceLayer;
            // The AddCore empties sit inside the model's x100, and SphereCollider.radius is local - so a
            // metre-based radius has to be divided by the socket's world scale, or it comes out at 18m.
            SphereCollider col = GetOrAdd<SphereCollider>(go);
            col.radius = 0.18f / Mathf.Max(0.0001f, socket.lossyScale.x);
            col.isTrigger = false;
            Switch sw = GetOrAdd<Switch>(go);
            sw.m_hoverText = "$DA_Add_Core";
            sw.m_name = "Core" + n;
            sw.m_holdRepeatInterval = -1f;
            switches[i] = sw;

            Renderer nearest = null;
            float best = float.MaxValue;
            foreach (Renderer rend in crystals) {
                float d = Vector3.Distance(socket.position, rend.bounds.center);
                if (d < best) { best = d; nearest = rend; }
            }
            if (nearest == null) { continue; }
            crystals.Remove(nearest);
            if (best > 0.25f) {
                notes.Add("AddCore" + n + " paired with " + nearest.name + " but they are " + best.ToString("0.00")
                          + "m apart - check the socket placement.");
            }
            if (nearest.name != "core" + n) {
                notes.Add("AddCore" + n + " paired with " + nearest.name + " by position (names disagree).");
            }
            // The crystals ship hidden and on Default, exactly like DA_Autosorter's Core1-4;
            // HopperHub toggles them from the ZDO core mask.
            nearest.gameObject.layer = DefaultLayer;
            nearest.gameObject.SetActive(false);
            coreVisuals[i] = nearest.gameObject;
        }

        // ---- the shared store ------------------------------------------------
        GameObject store = new GameObject("HopperStore");
        store.transform.SetParent(body, worldPositionStays: false);
        store.layer = PieceLayer;
        Transform hatch = FindDeep(root.transform, "HatchInteract");
        if (hatch != null) {
            store.transform.position = hatch.position;
        } else {
            store.transform.localPosition = new Vector3(0f, 1.03f, 0.97f);
            notes.Add("Missing empty 'HatchInteract' - the store collider was placed at a guessed position.");
        }
        SphereCollider storeCol = store.AddComponent<SphereCollider>();
        storeCol.radius = 0.45f;
        storeCol.isTrigger = false;

        Container container = store.AddComponent<Container>();
        container.m_name = "$DA_hopper_store_name";
        container.m_width = 8;
        container.m_height = 4;
        container.m_privacy = Container.PrivacySetting.Public;
        container.m_checkGuardStone = true;
        container.m_autoDestroyEmpty = false;
        container.m_discoverStat = PlayerStatType.None;
        // m_rootObjectOverride is bound at runtime by HopperStore.ConfigurePrefab: the child has no
        // ZNetView of its own and Container.Awake would NRE on the very next line without it.

        // Vanilla chests open by swapping two GameObjects. This model has no hatch meshes, so the
        // store works but does not visibly open until Hatch_Closed / Hatch_Open are modelled.
        Transform closed = FindDeep(root.transform, "Hatch_Closed");
        Transform open = FindDeep(root.transform, "Hatch_Open");
        if (closed != null) { container.m_closed = closed.gameObject; closed.gameObject.SetActive(true); }
        if (open != null) { container.m_open = open.gameObject; open.gameObject.SetActive(false); }
        if (closed == null || open == null) {
            notes.Add("No Hatch_Closed / Hatch_Open in the model - the store opens but the hatch will not visibly move.");
        }

        // ---- "powered" visuals, including the vent smoke ---------------------
        // HopperHub toggles this whole holder from IsActive, so everything under it is off until the
        // hopper is actually running - which is exactly when the vents should start smoking.
        GameObject enabledVisuals = new GameObject("_enabled");
        enabledVisuals.transform.SetParent(body, worldPositionStays: false);
        enabledVisuals.layer = PieceLayer;

        GameObject smokeVent = new GameObject("SmokeVent");
        smokeVent.transform.SetParent(enabledVisuals.transform, worldPositionStays: false);
        smokeVent.layer = PieceLayer;
        smokeVent.transform.position = SmokeEmitPoint(root.transform, body, notes);

        SmokeSpawner spawner = smokeVent.AddComponent<SmokeSpawner>();
        spawner.m_smokePrefab = EnsureSmokeMock(notes);
        // Identical to vanilla's smelter and blast furnace, so the plume reads the same.
        spawner.m_interval = 0.5f;
        spawner.m_testMask = 1 << SmokeLayer;
        spawner.m_testRadius = 0.75f;
        spawner.m_spawnRadius = 0f;
        spawner.m_stopFireOnStart = false;

        // Alongside the puffs, the smelter's own continuous chimney smoke. It loops with playOnAwake, so
        // it starts and stops with _enabled exactly as it does on the smelter. A plain Instantiate
        // copies it into this prefab inline rather than nesting DA_SmelterSmoke, and keeps its local
        // -90 X rotation, which is what points the emitter up.
        GameObject chimneySmoke = AssetDatabase.LoadAssetAtPath<GameObject>(ChimneySmokePath);
        if (chimneySmoke == null) {
            notes.Add("Missing " + ChimneySmokePath + " - the vents will puff, without the chimney smoke.");
        } else {
            GameObject smoke = Object.Instantiate(chimneySmoke, smokeVent.transform, false);
            smoke.name = "SmelterSmoke";
            smoke.transform.localPosition = Vector3.zero;
        }

        enabledVisuals.SetActive(false);

        // ---- piece components -----------------------------------------------
        ZNetView nview = root.AddComponent<ZNetView>();
        nview.m_persistent = true;
        nview.m_distant = false;
        nview.m_type = ZDO.ObjectType.Default;
        nview.m_syncInitialScale = false;

        Piece piece = root.AddComponent<Piece>();
        piece.m_name = "$DA_hopper_name";
        piece.m_description = "";
        piece.m_enabled = true;
        piece.m_category = Piece.PieceCategory.Crafting;
        piece.m_randomTarget = true;
        piece.m_primaryTarget = false;
        piece.m_targetNonPlayerBuilt = true;
        piece.m_groundPiece = false;
        piece.m_groundOnly = false;
        piece.m_cultivatedGroundOnly = false;
        piece.m_clipGround = false;
        piece.m_noClipping = false;
        piece.m_allowedInDungeons = false;
        // Icon and build requirements are applied by Jotunn at runtime from the piece config.

        WearNTear wnt = root.AddComponent<WearNTear>();
        wnt.m_health = 1000f;
        wnt.m_noRoofWear = true;
        wnt.m_noSupportWear = true;
        wnt.m_supports = true;
        wnt.m_burnable = true;
        wnt.m_staticPosition = true;
        wnt.m_triggerPrivateArea = true;
        // Iron, not Stone, and deliberately so: GetMaterialProperties gives Stone a minimum support of
        // 100 while wood caps at 100, so a Stone piece can never be held by a wooden floor - it fails
        // HaveSupport() and takes 100 damage per wear tick until it breaks. Iron needs only 20.
        wnt.m_materialType = WearNTear.MaterialType.Iron;

        HopperHub hub = root.AddComponent<HopperHub>();
        hub.CoreSwitches = switches;
        hub.CoreVisuals = coreVisuals;
        hub.EnabledVisuals = enabledVisuals;
        hub.SpinningGears = new Transform[0];

        // ---- save -------------------------------------------------------------
        Directory.CreateDirectory(PrefabDir);
        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        if (saved == null) {
            notes.Add("ERROR: failed to save " + PrefabPath);
            return;
        }

        AssetImporter prefabImporter = AssetImporter.GetAtPath(PrefabPath);
        if (prefabImporter != null && prefabImporter.assetBundleName != BundleName) {
            prefabImporter.assetBundleName = BundleName;
            prefabImporter.SaveAndReimport();
        }
        AssetDatabase.SaveAssets();
        notes.Add("Built " + PrefabPath + " (bundle '" + BundleName + "').");

        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        EditorGUIUtility.PingObject(Selection.activeObject);
    }

    /// <summary>
    /// The empty stand-in Jotunn resolves to vanilla's real SmokeBall. It only has to carry the right
    /// name - Jotunn strips the "JVLmock_" prefix and looks the remainder up in the game's prefab cache.
    /// Created on demand so a fresh clone of the repo does not need the asset committed.
    /// </summary>
    private static GameObject EnsureSmokeMock(List<string> notes) {
        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(SmokeMockPath);
        if (existing != null) { return existing; }

        Directory.CreateDirectory(MockDir);
        GameObject temp = new GameObject(SmokeMockName);
        GameObject asset = PrefabUtility.SaveAsPrefabAsset(temp, SmokeMockPath);
        Object.DestroyImmediate(temp);
        if (asset == null) {
            notes.Add("Could not create " + SmokeMockPath + "; the vents will not smoke.");
            return null;
        }

        // Mocks have to travel in the bundle with the piece that references them, the same way
        // JVLmock_surtlingcore.mat does.
        AssetImporter importer = AssetImporter.GetAtPath(SmokeMockPath);
        if (importer != null) {
            importer.assetBundleName = BundleName;
            importer.SaveAndReimport();
        }
        notes.Add("Created " + SmokeMockPath + " (bundle '" + BundleName + "').");
        return asset;
    }

    /// <summary>
    /// Where the plume comes from: an empty named "SmokePoint" if the model has one, otherwise just
    /// above the top of the Vents grate, otherwise just above the top of the tower.
    /// </summary>
    private static Vector3 SmokeEmitPoint(Transform root, Transform body, List<string> notes) {
        Transform explicitPoint = FindDeep(root, "SmokePoint");
        if (explicitPoint != null) { return explicitPoint.position; }

        Transform vents = FindDeep(root, "Vents");
        Renderer ventRenderer = vents != null ? vents.GetComponent<Renderer>() : null;
        if (ventRenderer != null) {
            Bounds b = ventRenderer.bounds;
            return new Vector3(b.center.x, b.max.y + 0.08f, b.center.z);
        }

        // Hopper_Body is now an empty wrapper, so fall back to the combined bounds of the whole model.
        Renderer[] all = body.GetComponentsInChildren<Renderer>();
        if (all.Length > 0) {
            notes.Add("No 'Vents' mesh; smoke placed at the top of the model.");
            Bounds b = all[0].bounds;
            foreach (Renderer rend in all) { b.Encapsulate(rend.bounds); }
            return new Vector3(b.center.x, b.max.y + 0.10f, b.center.z);
        }

        notes.Add("No SmokePoint, Vents or renderers; smoke placed at the origin.");
        return body.position;
    }

    // ==================================================================== helpers

    private static void Report(List<string> notes) {
        foreach (string note in notes) {
            if (note.StartsWith("ERROR")) { Debug.LogError("[ForgeHopper] " + note); }
            else { Debug.Log("[ForgeHopper] " + note); }
        }
    }

    private static T GetOrAdd<T>(GameObject go) where T : Component {
        T existing = go.GetComponent<T>();
        return existing != null ? existing : go.AddComponent<T>();
    }

    private static void AddMeshCollider(GameObject go, List<string> notes) {
        MeshFilter filter = go.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) {
            notes.Add("No mesh on '" + go.name + "'; skipped its collider.");
            return;
        }
        if (go.GetComponent<Collider>() != null) { return; }
        MeshCollider col = go.AddComponent<MeshCollider>();
        col.sharedMesh = filter.sharedMesh;
        col.convex = false;
    }

    private static Transform FindDeep(Transform parent, string name) {
        if (parent.name == name) { return parent; }
        for (int i = 0; i < parent.childCount; ++i) {
            Transform found = FindDeep(parent.GetChild(i), name);
            if (found != null) { return found; }
        }
        return null;
    }

    private static void SetLayerRecursive(GameObject go, int layer) {
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; ++i) {
            SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
        }
    }
}
