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
/// It also builds DA_GrinderFeed, the hopper the Grinder spawns into its mouth (a container of its own; see
/// GrinderFeed), builds the machine 30% larger than the model, keeps the black forge's lava heat
/// (DA_LavaHeat) rising off the tank, and puts the start lever (DvergerGrinderLever.fbx) on its left flank.
///
/// Re-runnable: re-export the model or the textures and run it again rather than hand-editing the prefab.
/// Shares the Hopper builder's helpers and its assembly definition.
/// </summary>
public static class BuildGrinderPrefab {
    private const string ModelPath = "Assets/Custom/Models/DvergerGrinder.fbx";
    private const string PrefabDir = "Assets/Custom/Prefabs";
    private const string PieceName = "DA_Grinder";
    private const string PrefabPath = PrefabDir + "/" + PieceName + ".prefab";
    // The hopper on top: a networked object of its own that the Grinder spawns (see GrinderFeed).
    private const string FeedName = "DA_GrinderFeed";
    private const string FeedPath = PrefabDir + "/" + FeedName + ".prefab";
    private const string BundleName = "automation";

    // The whole machine is built this much larger than the model. Applied to the Grinder_Body wrapper last,
    // so everything under it - model, colliders, effects - scales together.
    private const float Scale = 1.3f;

    // The black forge's heat off its lava, lifted by scripts/unity/lift_grinder_heat.py. Always on, over the
    // lava tank. Its particle systems scale by their own transforms only (Local scaling), not the machine's.
    private const string LavaHeatPath = "Assets/Custom/Effects/DA_LavaHeat.prefab";
    // At the forge's own size the plume and glow swamped the tank (Carl, 2026-10-12): a tenth of it, then a
    // fifth of that again.
    private const float LavaHeatScale = 0.02f;
    // Ashlands' lava bubbling (fx_lavabubbling), lifted by scripts/unity/lift_grinder_bubble.py and built in
    // over the tank, where GrinderHub replays it every few seconds. Silent, two seconds long. Spawned whole it
    // splashed three times the machine's size (Carl, 2026-10-12), so each of its systems is shrunk to this
    // much of its own scale - again the only scale they read.
    private const string LavaBubblePath = "Assets/Custom/Effects/DA_LavaBubble.prefab";
    private const float LavaBubbleScale = 0.15f;

    private const string MatDir = "Assets/Custom/Materials/Grinder";
    private const string MockMatDir = "Assets/Custom/Mocks/Materials";
    private const string MockTexDir = "Assets/Custom/Mocks/Images";
    private const string PieceShader = "JVLmock_Custom/Piece";
    // A mock material to copy the empty shell of; Jotunn swaps the whole material, so only the name counts.
    private const string MockShellPath = MockMatDir + "/JVLmock_surtlingcore.mat";
    // The smelter's chimney smoke, toned down, stands in for the dust a crusher throws up.
    private const string DustPath = "Assets/Custom/Effects/DA_SmelterSmoke.prefab";

    private const int PieceLayer = 10;
    // "piece_nonsolid": still in the player's interact mask, but nothing collides with it.
    private const int PieceNonSolidLayer = 16;
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

    // The start lever on the left flank (scripts/blender/build_grinder_lever.py), modelled in the Grinder
    // model's own space so it lines up when dropped in beside it. Its two slots go on IronGrill_d as the
    // rollers do, but the regions - plain iron, and the rust streak for the grip - are picked out by the
    // materials' tiling: its own UVs are a proper unwrap, kept for painting it later.
    private const string LeverModelPath = "Assets/Custom/Models/DvergerGrinderLever.fbx";
    private static readonly string[] LeverSlots = { "Lever", "LeverGrip" };
    private static readonly string[] LeverMaterials = { "Grinder_Lever", "Grinder_LeverGrip" };
    // Scale x, y and offset x, y of _MainTex.
    private static readonly Vector4[] LeverTiling = { new Vector4(0.63f, 0.94f, 0.03f, 0.03f), new Vector4(0.09f, 0.61f, 0.86f, 0.36f) };
    // Metres added round the arm's grab box: the shaft alone is a thin thing to aim at.
    private const float LeverGrabPadding = 0.06f;
    // A heavy clunk as it is thrown: the Ashlands catapult dropping its legs.
    private static readonly string[] LeverEffectMocks = { "JVLmock_sfx_catapult_legs_down" };

    // Vanilla materials used as they are: FBX slot -> vanilla material name.
    private static readonly string[] VanillaSlots = { "Lava", "MoltenCore", "Stone" };
    private static readonly string[] VanillaMaterials = { "BlackForgeLava_mat", "MoltenCore_mat", "Grausten_mat" };

    private static readonly string[] PlaceEffectMocks = { "JVLmock_vfx_Place_forge", "JVLmock_sfx_build_hammer_metal" };
    private static readonly string[] HitEffectMocks = { "JVLmock_vfx_HitSparks", "JVLmock_sfx_metal_blocked" };
    // As each item is broken down: the windmill's grinding and a spray of rock chips. Not the eitr refinery's
    // fx_refinery_addtissue: its particles sit on a child 4m up and 1.6m aside, where the refinery's intake
    // is, so they rained blood and skin flakes from high above the Grinder (Carl, 2026-10-12).
    private static readonly string[] ProduceEffectMocks = { "JVLmock_sfx_mill_produce", "JVLmock_vfx_RockHit" };

    // Degrees per second. Heavy spiked drums: slower than the refinery's near-700 reads as more weight.
    private const float RollerSpeed = 240f;

    // The hopper's collider: a box round the whole hopper, so it is hit before the hopper's own mesh from
    // any side. Metres at model size, centred on the model's Funnel empty; scaled with the machine.
    private static readonly Vector3 FunnelBox = new Vector3(1.06f, 0.34f, 0.94f);

    [MenuItem("DvergerAutomation/Build Dverger Grinder (materials + prefab)")]
    public static void BuildAll() {
        List<string> notes = new List<string>();
        if (BuildMaterials(notes)) {
            BuildPrefab(notes);
            BuildFeedPrefab(notes);
        }
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
            IronGrillMaterial(RollerMaterials[i], shader, ironGrill, i, new Vector4(1f, 1f, 0f, 0f));
        }

        // The lever's, in the same iron and rust as the rollers, remapped on its own model.
        ModelImporter leverImporter = AssetImporter.GetAtPath(LeverModelPath) as ModelImporter;
        for (int i = 0; i < LeverMaterials.Length; ++i) {
            Material mat = IronGrillMaterial(LeverMaterials[i], shader, ironGrill, i, LeverTiling[i]);
            if (leverImporter != null) {
                leverImporter.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), LeverSlots[i]), mat);
            }
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
        if (leverImporter != null) {
            leverImporter.SaveAndReimport();
        } else {
            notes.Add("ERROR: no ModelImporter for " + LeverModelPath + " - run scripts/blender/build_grinder_lever.py.");
        }
        notes.Add("Built " + PaintedSlots.Length + " painted, " + RollerMaterials.Length + " roller and " + LeverMaterials.Length
                  + " lever materials in " + MatDir + ", and mapped them and " + VanillaSlots.Length + " vanilla mocks onto the models.");
        return true;
    }

    /// <summary>
    /// Plain iron or rust on the IronGrill_d mock: tint, metallic and smoothness from the roller tables by
    /// <paramref name="look"/>, and <paramref name="tiling"/> (scale x, y, offset x, y) choosing the region.
    /// </summary>
    private static Material IronGrillMaterial(string name, Shader shader, Texture2D ironGrill, int look, Vector4 tiling) {
        Material mat = LoadOrCreate(MatDir + "/" + name + ".mat", shader);
        mat.SetTexture("_MainTex", ironGrill);
        mat.SetTextureScale("_MainTex", new Vector2(tiling.x, tiling.y));
        mat.SetTextureOffset("_MainTex", new Vector2(tiling.z, tiling.w));
        mat.SetTexture("_MetallicTex", null);
        mat.SetTexture("_BumpMap", null);
        mat.SetColor("_Color", RollerTints[look]);
        mat.SetFloat("_Metallic", RollerMetallic[look]);
        mat.SetFloat("_Glossiness", RollerSmoothness[look]);
        mat.SetFloat("_MetallicAlphaGloss", 0f);
        mat.SetColor("_MetalColor", Color.white);
        SetPieceFlags(mat);
        mat.DisableKeyword("_NORMALMAP");
        mat.DisableKeyword("_METALLICGLOSSMAP");
        EditorUtility.SetDirty(mat);
        return mat;
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

        // ---- the hopper -----------------------------------------------------------
        // Only a point here: the hopper itself is DA_GrinderFeed (BuildFeedPrefab), which the Grinder spawns
        // at this point when it is built.
        Transform funnelPoint = BuildHopperPrefab.FindDeep(root.transform, "Funnel");
        GameObject feedPoint = new GameObject("FeedPoint");
        feedPoint.layer = PieceLayer;
        feedPoint.transform.SetParent(body, worldPositionStays: false);
        if (funnelPoint != null) {
            feedPoint.transform.position = funnelPoint.position;
        } else {
            feedPoint.transform.localPosition = new Vector3(0f, 1.445f, 0f);
            notes.Add("Missing empty 'Funnel' - the hopper is placed at a guessed position.");
        }

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

        // ---- the lava tank's heat ------------------------------------------------------
        // Always on, not part of _working: the tank is hot whether or not anything is being ground. The
        // bubbling on top of it is played by GrinderHub every few seconds.
        Transform lavaPoint = BuildHopperPrefab.FindDeep(root.transform, "LavaPoint");
        GameObject heatAsset = AssetDatabase.LoadAssetAtPath<GameObject>(LavaHeatPath);
        if (heatAsset != null) {
            GameObject heat = Object.Instantiate(heatAsset, body, false);
            heat.name = "LavaHeat";
            heat.transform.position = lavaPoint != null ? lavaPoint.position : body.position + new Vector3(-0.84f, 0.98f, -0.12f);
            heat.transform.rotation = Quaternion.identity;
            BuildHopperPrefab.SetLayerRecursive(heat, DefaultLayer);
            // Each system's own transform, since that is the only scale they read. The forge's glow is sized
            // for its whole lava basin, so it starts at half before the overall shrink.
            foreach (Transform system in heat.transform) {
                float own = system.name == "Glow" ? 0.5f : 1f;
                system.localScale = Vector3.one * own * LavaHeatScale;
            }
        } else {
            notes.Add("Missing " + LavaHeatPath + " - run scripts/unity/lift_grinder_heat.py; the tank will not steam.");
        }
        if (lavaPoint == null) { notes.Add("Missing empty 'LavaPoint' - the lava effects were placed at a guessed position."); }

        ParticleSystem bubble = null;
        GameObject bubbleAsset = AssetDatabase.LoadAssetAtPath<GameObject>(LavaBubblePath);
        if (bubbleAsset != null) {
            GameObject bubbleGo = Object.Instantiate(bubbleAsset, body, false);
            bubbleGo.name = "LavaBubble";
            bubbleGo.transform.position = lavaPoint != null ? lavaPoint.position : body.position + new Vector3(-0.84f, 0.98f, -0.12f);
            bubbleGo.transform.rotation = Quaternion.identity;
            BuildHopperPrefab.SetLayerRecursive(bubbleGo, DefaultLayer);
            // The lift keeps vanilla's own scales on every system (about 2 on most), so each is shrunk from its own.
            foreach (ParticleSystem system in bubbleGo.GetComponentsInChildren<ParticleSystem>(true)) {
                ParticleSystem.MainModule main = system.main;
                main.playOnAwake = false;
                system.transform.localScale = system.transform.localScale * LavaBubbleScale;
            }
            bubble = bubbleGo.GetComponent<ParticleSystem>();
        } else {
            notes.Add("Missing " + LavaBubblePath + " - run scripts/unity/lift_grinder_bubble.py; the tank will not bubble.");
        }

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

        // ---- the start lever --------------------------------------------------------------
        // Dropped in at the model's own import transform, as the model is, so the two line up. The Switch sits
        // on its root, where the hover and use rays find it from the plate's collider and the arm's.
        Switch lever = null;
        Transform leverArm = null;
        Vector3 leverAxis = Vector3.right;
        GameObject leverModel = AssetDatabase.LoadAssetAtPath<GameObject>(LeverModelPath);
        if (leverModel != null) {
            GameObject leverGo = Object.Instantiate(leverModel);
            leverGo.name = "Lever";
            leverGo.transform.SetParent(body, worldPositionStays: false);
            BuildHopperPrefab.SetLayerRecursive(leverGo, PieceLayer);
            Transform leverBase = BuildHopperPrefab.FindDeep(leverGo.transform, "LeverBase");
            if (leverBase != null) {
                BuildHopperPrefab.AddMeshCollider(leverBase.gameObject, notes);
            } else {
                notes.Add("Missing 'LeverBase' in the lever model.");
            }
            leverArm = BuildHopperPrefab.FindDeep(leverGo.transform, "LeverArm");
            MeshFilter armMesh = leverArm != null ? leverArm.GetComponent<MeshFilter>() : null;
            if (armMesh != null && armMesh.sharedMesh != null) {
                // A box round the arm that swings with it, a little fatter than the arm itself.
                BoxCollider grab = leverArm.gameObject.AddComponent<BoxCollider>();
                Bounds b = armMesh.sharedMesh.bounds;
                grab.center = b.center;
                grab.size = b.size + Vector3.one * (LeverGrabPadding / Mathf.Max(0.0001f, leverArm.lossyScale.x));
                // It swings front to back, about the machine's own left-right axis, kept in the arm's space.
                leverAxis = leverArm.InverseTransformDirection(body.right).normalized;
            } else {
                notes.Add("ERROR: no 'LeverArm' mesh in the lever model - the lever will not swing.");
            }
            lever = leverGo.AddComponent<Switch>();
            lever.m_name = "$DA_grinder_name";
            lever.m_hoverText = "$DA_grinder_lever_use";
            lever.m_holdRepeatInterval = -1f;
        } else {
            notes.Add("ERROR: could not load " + LeverModelPath + " - the Grinder has no lever and cannot be started.");
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
        hub.FeedPoint = feedPoint.transform;
        hub.Spinners = spinners.ToArray();
        hub.SpinAxes = axes.ToArray();
        hub.SpinSpeeds = speeds.ToArray();
        hub.WorkingVisuals = working;
        hub.ProduceEffects = BuildHopperPrefab.MockEffects(ProduceEffectMocks, notes);
        hub.LavaBubble = bubble;
        hub.Lever = lever;
        hub.LeverArm = leverArm;
        hub.LeverAxis = leverAxis;
        hub.LeverEffects = BuildHopperPrefab.MockEffects(LeverEffectMocks, notes);

        // ---- size -------------------------------------------------------------------
        // Last, so every collider, point and effect above was laid out at model size and grows with it. The
        // spin axes are in each roller's own space, which a uniform scale leaves alone.
        body.localScale = Vector3.one * Scale;

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
        // The first run after the lever's material remaps change has saved the prefab without the lever, and
        // without a word: the reimport of its model was still settling.
        GrinderHub savedHub = saved.GetComponent<GrinderHub>();
        if (savedHub == null || savedHub.Lever == null || savedHub.LeverArm == null) {
            notes.Add("ERROR: the saved prefab has no lever - run the builder again.");
        }

        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        EditorGUIUtility.PingObject(Selection.activeObject);
    }

    /// <summary>
    /// The hopper: DA_GrinderFeed, a networked object of its own (see GrinderFeed for why) that the Grinder
    /// spawns into its hopper's mouth. Just a ZNetView, a Container the size of the hopper, and a box
    /// collider round the hopper on the non-solid piece layer, so Use on the hopper opens it and nothing
    /// bumps into it. GrinderStore.ConfigureFeedPrefab finishes the Container at runtime; the mod registers
    /// the prefab with the game.
    /// </summary>
    private static void BuildFeedPrefab(List<string> notes) {
        GameObject root = new GameObject(FeedName);
        root.layer = PieceNonSolidLayer;

        ZNetView nview = root.AddComponent<ZNetView>();
        nview.m_persistent = true;
        nview.m_distant = false;
        nview.m_type = ZDO.ObjectType.Default;
        nview.m_syncInitialScale = false;

        BoxCollider box = root.AddComponent<BoxCollider>();
        box.size = FunnelBox * Scale;
        box.isTrigger = false;

        Container container = root.AddComponent<Container>();
        container.m_name = "$DA_grinder_feed_name";
        container.m_width = 8;
        container.m_height = 2;
        container.m_privacy = Container.PrivacySetting.Public;
        container.m_checkGuardStone = true;
        container.m_autoDestroyEmpty = false;
        container.m_discoverStat = PlayerStatType.None;

        root.AddComponent<GrinderFeed>();

        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, FeedPath);
        Object.DestroyImmediate(root);
        if (saved == null) {
            notes.Add("ERROR: failed to save " + FeedPath);
            return;
        }
        TagForBundle(FeedPath);
        AssetDatabase.SaveAssets();
        notes.Add("Built " + FeedPath + " (bundle '" + BundleName + "').");
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
