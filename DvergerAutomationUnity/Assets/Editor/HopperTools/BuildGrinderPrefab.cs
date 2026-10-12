using System.Collections.Generic;
using System.IO;
using DvergerAutomation;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the Dverger Grinder: Assets/Custom/Prefabs/DA_Grinder.prefab from DvergerGrinder.fbx, and its
/// materials.
///
/// The body is painted in Substance (Materials/Grinder/Grinder.spp): one texture set per FBX material,
/// exported with Unity's Standard (Metallic) preset as AlbedoTransparency, MetallicSmoothness and an OpenGL
/// Normal, which is the layout Custom/Piece reads (_MainTex, _MetallicTex with smoothness in alpha, _BumpMap).
///
/// The rollers are not painted. They keep the UVs the model script gave them, laid into regions of vanilla's
/// IronGrill_d texture, and get their own materials on that texture through a Jotunn mock, set on their
/// renderers after import because they share the IronDark and Rust slot names with the body. The lava, the
/// Molten Cores and the stone base slab are vanilla's own materials (the black forge's lava, the molten core
/// item's, the Grausten building pieces'), swapped in whole by name, so nothing of the game's ships in the
/// bundle. The slab's UVs come to about one Grausten tile per two metres, the density of vanilla's own
/// Grausten floors, so the material needs no retiling.
///
/// Re-runnable: re-export the model or the textures and run it again rather than hand-editing the prefab.
/// Shares the Hopper builder's helpers and its assembly definition.
/// </summary>
public static class BuildGrinderPrefab {
    private const string ModelPath = "Assets/Custom/Models/DvergerGrinder.fbx";
    private const string PrefabDir = "Assets/Custom/Prefabs";
    private const string PieceName = "DA_Grinder";
    private const string PrefabPath = PrefabDir + "/" + PieceName + ".prefab";
    private const string BundleName = "automation";

    private const string MatDir = "Assets/Custom/Materials/Grinder";
    private const string MockMatDir = "Assets/Custom/Mocks/Materials";
    private const string MockTexDir = "Assets/Custom/Mocks/Images";
    private const string PieceShader = "JVLmock_Custom/Piece";
    // A mock material to copy the empty shell of; Jotunn swaps the whole material, so only the name counts.
    private const string MockShellPath = MockMatDir + "/JVLmock_surtlingcore.mat";
    // The smelter's chimney smoke, toned down, stands in for the dust a crusher throws up.
    private const string DustPath = "Assets/Custom/Effects/DA_SmelterSmoke.prefab";

    private const int PieceLayer = 10;
    private const int DefaultLayer = 0;
    private const int CoreSlots = 10;

    // Substance texture sets, named after the FBX materials they were painted onto.
    private const string TexPrefix = MatDir + "/DvergerGrinder_";
    private static readonly string[] PaintedSlots = { "Iron", "IronDark", "Rust", "Rust2", "Flametal", "Flametal2" };

    // The rollers' own materials on vanilla's IronGrill_d: the slot they replace on the roller meshes, the
    // material's name, and its tint. The model's UVs pick plain iron or the rust streak.
    private static readonly string[] RollerSlots = { "IronDark", "Rust" };
    private static readonly string[] RollerMaterials = { "Grinder_RollerIron", "Grinder_RollerRust" };
    private static readonly Color[] RollerTints = { new Color(0.28f, 0.26f, 0.25f), new Color(0.60f, 0.44f, 0.38f) };
    private static readonly float[] RollerMetallic = { 0.7f, 0.5f };
    private static readonly float[] RollerSmoothness = { 0.3f, 0.2f };
    private static readonly string[] RollerNames = { "RollerA", "RollerB" };

    // Vanilla materials used as they are: FBX slot -> vanilla material name.
    private static readonly string[] VanillaSlots = { "Lava", "MoltenCore", "Stone" };
    private static readonly string[] VanillaMaterials = { "BlackForgeLava_mat", "MoltenCore_mat", "Grausten_mat" };

    private static readonly string[] PlaceEffectMocks = { "JVLmock_vfx_Place_forge", "JVLmock_sfx_build_hammer_metal" };
    private static readonly string[] HitEffectMocks = { "JVLmock_vfx_HitSparks", "JVLmock_sfx_metal_blocked" };
    // The eitr refinery's crunch as its grinder takes soft tissue, and the windmill's grinding with a
    // spray of rock chips as each item is broken down.
    private static readonly string[] FeedEffectMocks = { "JVLmock_fx_refinery_addtissue" };
    private static readonly string[] ProduceEffectMocks = { "JVLmock_sfx_mill_produce", "JVLmock_vfx_RockHit" };

    // Degrees per second. Heavy spiked drums: slower than the refinery's near-700 reads as more weight.
    private const float RollerSpeed = 240f;

    // The feed collider: a box round the whole hopper, so it is hit before the hopper's own mesh from any
    // side. Metres, centred on the model's Funnel empty.
    private static readonly Vector3 FunnelBox = new Vector3(1.06f, 0.34f, 0.94f);

    [MenuItem("DvergerAutomation/Build Dverger Grinder (materials + prefab)")]
    public static void BuildAll() {
        List<string> notes = new List<string>();
        if (BuildMaterials(notes)) { BuildPrefab(notes); }
        Report(notes);
    }

    // ==================================================================== materials

    private static bool BuildMaterials(List<string> notes) {
        Directory.CreateDirectory(MatDir);
        ModelImporter importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer == null) {
            notes.Add("ERROR: no ModelImporter for " + ModelPath + " - has Unity imported it?");
            return false;
        }

        Shader shader = Shader.Find(PieceShader);
        if (shader == null) {
            notes.Add("ERROR: shader '" + PieceShader + "' not found; no materials built.");
            return false;
        }

        foreach (string set in PaintedSlots) {
            string prefix = TexPrefix + set;
            Texture2D albedo = ConfigureTexture(prefix + "_AlbedoTransparency.png", TextureImporterType.Default, true, notes);
            Texture2D metalGloss = ConfigureTexture(prefix + "_MetallicSmoothness.png", TextureImporterType.Default, false, notes);
            Texture2D normal = ConfigureTexture(prefix + "_Normal.png", TextureImporterType.NormalMap, false, notes);
            if (albedo == null) {
                notes.Add("ERROR: no albedo for texture set " + set + " - export it from Grinder.spp.");
                return false;
            }

            Material mat = LoadOrCreate(MatDir + "/Grinder_" + set + ".mat", shader);
            mat.SetTexture("_MainTex", albedo);
            mat.SetTexture("_MetallicTex", metalGloss);
            mat.SetTexture("_BumpMap", normal);
            mat.SetColor("_Color", Color.white);
            // The values the Forge Hopper takes from vanilla's eitr refinery: the metallic map decides what
            // is metal, its alpha how smooth, scaled by _MetallicAlphaGloss.
            mat.SetFloat("_Metallic", 1f);
            mat.SetFloat("_MetallicAlphaGloss", 0.75f);
            mat.SetFloat("_Glossiness", 0.15f);
            mat.SetColor("_MetalColor", Color.white);
            mat.SetFloat("_BumpScale", 1f);
            SetPieceFlags(mat);
            if (normal != null) { mat.EnableKeyword("_NORMALMAP"); } else { mat.DisableKeyword("_NORMALMAP"); }
            if (metalGloss != null) { mat.EnableKeyword("_METALLICGLOSSMAP"); } else { mat.DisableKeyword("_METALLICGLOSSMAP"); }
            EditorUtility.SetDirty(mat);
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), set), mat);
        }

        // The rollers' materials. Not remapped on the model - their slot names belong to the body - but put
        // on the roller renderers by BuildPrefab.
        Texture2D ironGrill = EnsureMockTexture("IronGrill_d", notes);
        for (int i = 0; i < RollerMaterials.Length; ++i) {
            Material mat = LoadOrCreate(MatDir + "/" + RollerMaterials[i] + ".mat", shader);
            mat.SetTexture("_MainTex", ironGrill);
            mat.SetTexture("_MetallicTex", null);
            mat.SetTexture("_BumpMap", null);
            mat.SetColor("_Color", RollerTints[i]);
            mat.SetFloat("_Metallic", RollerMetallic[i]);
            mat.SetFloat("_Glossiness", RollerSmoothness[i]);
            mat.SetFloat("_MetallicAlphaGloss", 0f);
            mat.SetColor("_MetalColor", Color.white);
            SetPieceFlags(mat);
            mat.DisableKeyword("_NORMALMAP");
            mat.DisableKeyword("_METALLICGLOSSMAP");
            EditorUtility.SetDirty(mat);
        }

        Material shell = AssetDatabase.LoadAssetAtPath<Material>(MockShellPath);
        for (int i = 0; i < VanillaSlots.Length; ++i) {
            string path = MockMatDir + "/JVLmock_" + VanillaMaterials[i] + ".mat";
            Material mock = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mock == null) {
                mock = shell != null ? new Material(shell) : new Material(Shader.Find("Standard"));
                mock.name = "JVLmock_" + VanillaMaterials[i];
                AssetDatabase.CreateAsset(mock, path);
                notes.Add("Created " + path + ".");
            }
            TagForBundle(path);
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), VanillaSlots[i]), mock);
        }

        AssetDatabase.SaveAssets();
        importer.SaveAndReimport();
        notes.Add("Built " + PaintedSlots.Length + " painted and " + RollerMaterials.Length + " roller materials in " + MatDir
                  + ", and mapped them and " + VanillaSlots.Length + " vanilla mocks onto the model.");
        return true;
    }

    private static Material LoadOrCreate(string path, Shader shader) {
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) {
            mat = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        return mat;
    }

    private static void SetPieceFlags(Material mat) {
        mat.SetFloat("_AddRain", 1f);
        mat.SetFloat("_Cull", 2f);
        mat.EnableKeyword("_ADDRAIN_ON");
    }

    /// <summary>
    /// Import settings for a Substance export: colour in sRGB, the metallic map as linear data with its
    /// smoothness alpha kept, and the normal map as a normal map. These exports are OpenGL-convention, which is
    /// Unity's own, so the green channel is not flipped.
    /// </summary>
    private static Texture2D ConfigureTexture(string path, TextureImporterType type, bool sRGB, List<string> notes) {
        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) {
            notes.Add("Missing " + path + ".");
            return null;
        }
        bool alpha = path.EndsWith("_MetallicSmoothness.png");
        if (ti.textureType != type || ti.sRGBTexture != sRGB || ti.flipGreenChannel
            || (alpha && ti.alphaSource != TextureImporterAlphaSource.FromInput)) {
            ti.textureType = type;
            ti.sRGBTexture = sRGB;
            ti.flipGreenChannel = false;
            if (alpha) {
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = false;
            }
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>
    /// A small grey stand-in named after a vanilla texture. Jotunn swaps it for the real one when the piece
    /// is registered, so the game's texture never travels in the bundle; in the editor it looks flat.
    /// </summary>
    private static Texture2D EnsureMockTexture(string vanillaName, List<string> notes) {
        string path = MockTexDir + "/JVLmock_" + vanillaName + ".png";
        if (!File.Exists(path)) {
            Directory.CreateDirectory(MockTexDir);
            Texture2D tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[64];
            for (int i = 0; i < px.Length; ++i) { px[i] = new Color32(128, 128, 128, 255); }
            tex.SetPixels32(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            notes.Add("Created " + path + ".");
        }
        // Repeat, as vanilla has it.
        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti != null && ti.wrapMode != TextureWrapMode.Repeat) {
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.SaveAndReimport();
        }
        TagForBundle(path);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static void TagForBundle(string path) {
        AssetImporter importer = AssetImporter.GetAtPath(path);
        if (importer != null && importer.assetBundleName != BundleName) {
            importer.assetBundleName = BundleName;
            importer.SaveAndReimport();
        }
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

        // An identity wrapper, so everything added here is in real metres. GrinderStore.ConfigurePrefab
        // looks the chest up at "Grinder_Body/GrinderStore".
        GameObject bodyGo = new GameObject("Grinder_Body");
        bodyGo.layer = PieceLayer;
        Transform body = bodyGo.transform;
        body.SetParent(root.transform, worldPositionStays: false);

        // As for the Hopper: the FBX is in centimetres and its root carries the axis conversion and the
        // x100. Never reset it.
        GameObject modelInstance = Object.Instantiate(model);
        modelInstance.name = "Model";
        modelInstance.transform.SetParent(body, worldPositionStays: false);
        BuildHopperPrefab.SetLayerRecursive(modelInstance, PieceLayer);
        ApplyRollerMaterials(root.transform, notes);

        Transform bodyMesh = BuildHopperPrefab.FindDeep(root.transform, "Body");
        if (bodyMesh != null) {
            BuildHopperPrefab.AddMeshCollider(bodyMesh.gameObject, notes);
        } else {
            notes.Add("ERROR: no 'Body' mesh in the model - the piece has no collision.");
        }

        // ---- the funnel ---------------------------------------------------------
        // The hopper is the feed: the box (FunnelBox) is hit before the hopper's own mesh collider from any
        // side. The radial looks for the Switch on the very object the player is looking at.
        Transform funnelPoint = BuildHopperPrefab.FindDeep(root.transform, "Funnel");
        GameObject funnel = new GameObject("FeedFunnel");
        funnel.layer = PieceLayer;
        funnel.transform.SetParent(body, worldPositionStays: false);
        if (funnelPoint != null) {
            funnel.transform.position = funnelPoint.position;
        } else {
            funnel.transform.localPosition = new Vector3(0f, 1.25f, 0f);
            notes.Add("Missing empty 'Funnel' - the feed collider was placed at a guessed position.");
        }
        BoxCollider funnelBox = funnel.AddComponent<BoxCollider>();
        funnelBox.size = FunnelBox;
        Switch feed = funnel.AddComponent<Switch>();
        feed.m_name = "$DA_grinder_name";
        feed.m_hoverText = "";
        feed.m_holdRepeatInterval = -1f;

        // ---- core sockets -------------------------------------------------------
        List<Renderer> crystals = new List<Renderer>();
        for (int n = 1; n <= CoreSlots; ++n) {
            Transform c = BuildHopperPrefab.FindDeep(root.transform, "core" + n);
            Renderer rend = c != null ? c.GetComponent<Renderer>() : null;
            if (rend != null) { crystals.Add(rend); } else { notes.Add("Missing crystal mesh core" + n + "."); }
        }
        Switch[] switches = new Switch[CoreSlots];
        GameObject[] coreVisuals = new GameObject[CoreSlots];
        for (int i = 0; i < CoreSlots; ++i) {
            int n = i + 1;
            Transform socket = BuildHopperPrefab.FindDeep(root.transform, "AddCore" + n);
            if (socket == null) {
                notes.Add("Missing empty 'AddCore" + n + "' - slot " + n + " has no switch.");
                continue;
            }
            GameObject go = socket.gameObject;
            go.layer = PieceLayer;
            // Inside the model's x100, so the metre radius is divided by the world scale. Small, since
            // they sit 14cm apart in two rows of five on the power panel.
            SphereCollider col = BuildHopperPrefab.GetOrAdd<SphereCollider>(go);
            col.radius = 0.06f / Mathf.Max(0.0001f, socket.lossyScale.x);
            col.isTrigger = false;
            Switch sw = BuildHopperPrefab.GetOrAdd<Switch>(go);
            sw.m_hoverText = "$DA_Add_MoltenCore";
            sw.m_name = "$DA_grinder_name";
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
            if (best > 0.1f) {
                notes.Add("AddCore" + n + " paired with " + nearest.name + " but they are " + best.ToString("0.00") + "m apart.");
            }
            nearest.gameObject.layer = DefaultLayer;
            nearest.gameObject.SetActive(false);
            coreVisuals[i] = nearest.gameObject;
        }

        // ---- the results chest ----------------------------------------------------
        GameObject store = new GameObject("GrinderStore");
        store.transform.SetParent(body, worldPositionStays: false);
        store.layer = PieceLayer;
        Transform door = BuildHopperPrefab.FindDeep(root.transform, "StoreDoor");
        if (door != null) {
            store.transform.position = door.position;
        } else {
            store.transform.localPosition = new Vector3(0f, 0.45f, 0.52f);
            notes.Add("Missing empty 'StoreDoor' - the chest collider was placed at a guessed position.");
        }
        SphereCollider storeCol = store.AddComponent<SphereCollider>();
        storeCol.radius = 0.2f;
        storeCol.isTrigger = false;
        Container container = store.AddComponent<Container>();
        container.m_name = "$DA_grinder_store_name";
        container.m_width = 8;
        container.m_height = 2;
        container.m_privacy = Container.PrivacySetting.Public;
        container.m_checkGuardStone = true;
        container.m_autoDestroyEmpty = false;
        container.m_discoverStat = PlayerStatType.None;

        // ---- working visuals -------------------------------------------------------
        GameObject working = new GameObject("_working");
        working.transform.SetParent(body, worldPositionStays: false);
        working.layer = PieceLayer;
        Transform dustPoint = BuildHopperPrefab.FindDeep(root.transform, "DustPoint");
        GameObject dustAsset = AssetDatabase.LoadAssetAtPath<GameObject>(DustPath);
        if (dustAsset != null) {
            GameObject dust = Object.Instantiate(dustAsset, working.transform, false);
            dust.name = "Dust";
            dust.transform.position = dustPoint != null ? dustPoint.position : body.position + Vector3.up * 1.33f;
            dust.transform.localScale = Vector3.one * 0.5f;
        } else {
            notes.Add("Missing " + DustPath + " - the Grinder works without dust.");
        }
        working.SetActive(false);

        // ---- spinning parts --------------------------------------------------------
        // Each roller turns about its length, which is its longest extent; the axis is stored in the
        // roller's own space, since the model's import transform makes that anything but obvious.
        List<Transform> spinners = new List<Transform>();
        List<Vector3> axes = new List<Vector3>();
        List<float> speeds = new List<float>();
        string[] rollerNames = { "RollerA", "RollerB" };
        for (int i = 0; i < rollerNames.Length; ++i) {
            Transform roller = BuildHopperPrefab.FindDeep(root.transform, rollerNames[i]);
            Renderer rend = roller != null ? roller.GetComponent<Renderer>() : null;
            if (rend == null) {
                notes.Add("Missing roller '" + rollerNames[i] + "' - it will not turn.");
                continue;
            }
            Vector3 size = rend.bounds.size;
            Vector3 world = size.x >= size.y && size.x >= size.z ? Vector3.right
                          : size.y >= size.z ? Vector3.up : Vector3.forward;
            spinners.Add(roller);
            axes.Add(roller.InverseTransformDirection(world).normalized);
            // Counter-rotating, so the two pull what is fed down between them.
            speeds.Add(i == 0 ? RollerSpeed : -RollerSpeed);
        }

        // ---- piece components --------------------------------------------------------
        ZNetView nview = root.AddComponent<ZNetView>();
        nview.m_persistent = true;
        nview.m_distant = false;
        nview.m_type = ZDO.ObjectType.Default;
        nview.m_syncInitialScale = false;

        Piece piece = root.AddComponent<Piece>();
        piece.m_name = "$DA_grinder_name";
        piece.m_description = "$DA_grinder_desc";
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
        piece.m_placeEffect = BuildHopperPrefab.MockEffects(PlaceEffectMocks, notes);

        WearNTear wnt = root.AddComponent<WearNTear>();
        wnt.m_health = 1000f;
        wnt.m_noRoofWear = false;
        wnt.m_noSupportWear = true;
        wnt.m_supports = true;
        wnt.m_burnable = true;
        wnt.m_staticPosition = true;
        wnt.m_triggerPrivateArea = true;
        // Iron, for the same reason as the Hopper: Stone could never stand on a wooden floor.
        wnt.m_materialType = WearNTear.MaterialType.Iron;
        wnt.m_hitEffect = BuildHopperPrefab.MockEffects(HitEffectMocks, notes);
        wnt.m_destroyedEffect = BuildHopperPrefab.MockEffects(HitEffectMocks, notes);

        CoreSockets sockets = root.AddComponent<CoreSockets>();
        sockets.Switches = switches;
        sockets.SlotVisuals = coreVisuals;
        sockets.SlotEffects = new GameObject[0];
        sockets.ActiveVisuals = null;
        sockets.ItemPrefab = "MoltenCore";
        sockets.MaskKey = "DA_grinder_cores";
        sockets.HoverName = "$DA_grinder_name";
        sockets.AddMessage = "$DA_Add_MoltenCore";
        sockets.RemoveMessage = "$DA_Remove_MoltenCore";
        sockets.NeedMessage = "$DA_Need_MoltenCore";

        GrinderHub hub = root.AddComponent<GrinderHub>();
        hub.Funnel = feed;
        hub.Spinners = spinners.ToArray();
        hub.SpinAxes = axes.ToArray();
        hub.SpinSpeeds = speeds.ToArray();
        hub.WorkingVisuals = working;
        hub.FeedEffects = BuildHopperPrefab.MockEffects(FeedEffectMocks, notes);
        hub.ProduceEffects = BuildHopperPrefab.MockEffects(ProduceEffectMocks, notes);

        // ---- save -------------------------------------------------------------------
        Bounds all = new Bounds(root.transform.position, Vector3.zero);
        foreach (Renderer rend in modelInstance.GetComponentsInChildren<Renderer>(true)) { all.Encapsulate(rend.bounds); }
        notes.Add("Model bounds: " + all.size.x.ToString("0.00") + " x " + all.size.y.ToString("0.00") + " x " + all.size.z.ToString("0.00") + " m (w x h x d).");

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
    /// Puts the rollers' own materials on the roller renderers in place of the painted body materials their
    /// shared slot names were remapped to. The model in the prefab is a plain copy, so this sticks.
    /// </summary>
    private static void ApplyRollerMaterials(Transform root, List<string> notes) {
        Dictionary<string, Material> swap = new Dictionary<string, Material>();
        for (int i = 0; i < RollerSlots.Length; ++i) {
            Material painted = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/Grinder_" + RollerSlots[i] + ".mat");
            Material roller = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/" + RollerMaterials[i] + ".mat");
            if (painted != null && roller != null) { swap[painted.name] = roller; }
        }
        foreach (string name in RollerNames) {
            Transform t = BuildHopperPrefab.FindDeep(root, name);
            Renderer rend = t != null ? t.GetComponent<Renderer>() : null;
            if (rend == null) {
                notes.Add("Missing roller '" + name + "' - its materials were not swapped.");
                continue;
            }
            Material[] mats = rend.sharedMaterials;
            int swapped = 0;
            for (int k = 0; k < mats.Length; ++k) {
                if (mats[k] != null && swap.TryGetValue(mats[k].name, out Material m)) {
                    mats[k] = m;
                    ++swapped;
                }
            }
            rend.sharedMaterials = mats;
            if (swapped != mats.Length) {
                notes.Add(name + ": " + swapped + " of " + mats.Length + " materials swapped to the roller ones.");
            }
        }
    }

    private static void Report(List<string> notes) {
        foreach (string note in notes) {
            if (note.StartsWith("ERROR")) { Debug.LogError("[Grinder] " + note); }
            else { Debug.Log("[Grinder] " + note); }
        }
    }
}
