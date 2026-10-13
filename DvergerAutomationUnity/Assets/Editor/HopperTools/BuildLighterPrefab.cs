using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DvergerAutomation;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Builds the Dverger Lighter: its materials from the Substance Painter export, and
/// Assets/Custom/Prefabs/DA_Lighter.prefab from DvergerLighter.fbx.
///
/// Re-runnable, like the other builders: re-export the model or the textures and run it again rather than
/// hand-editing the prefab. Until a texture set is painted its material is a flat colour, so the piece can
/// be placed and tested before the art is done. The material helpers are copied from the Seeking Table's
/// builder, and the prefab helpers shared with the Hopper's, as the Grinder's builder does.
///
/// The model carries no empties. The fuel tank's hatch is found from the wooden stand under it, and the
/// smoke from the tops of the centre column and the two thin vents beside it, unless the model gains
/// "StoreDoor", "SmokePoint" or "VentPoint1"/"VentPoint2" empties. All three pipes show wisps of smoke;
/// only the column puffs out physical smoke.
/// </summary>
public static class BuildLighterPrefab {
    private const string ModelPath = "Assets/Custom/Models/DvergerLighter.fbx";
    private const string PrefabDir = "Assets/Custom/Prefabs";
    private const string PieceName = "DA_Lighter";
    private const string PrefabPath = PrefabDir + "/" + PieceName + ".prefab";
    private const string IconPath = "Assets/PrefabIcons/" + PieceName + ".png";
    private const string BundleName = "automation";

    private const string TexDir = "Assets/Custom/Materials/Lighter";
    private const string PieceShader = "JVLmock_Custom/Piece";

    // The smelter's chimney smoke, made small: a wisp from each upward pipe while there is fuel to give.
    // Its cone has a 0.2m radius at full size, so each is scaled to its pipe's mouth.
    private const string SmokePath = "Assets/Custom/Effects/DA_SmelterSmoke.prefab";
    private const float ColumnSmokeScale = 0.45f;
    private const float VentSmokeScale = 0.25f;

    // Physical smoke from the centre column only, set up as the Hopper's vent: vanilla's own SmokeBall
    // through a mock, at the smelter's pace. Layer 31 is "smoke", which the spawner tests so it stops
    // when its smoke piles up against a ceiling.
    private const string SmokeBallMock = "JVLmock_SmokeBall";
    private const int SmokeLayer = 31;
    // Above the mouth, so the smoke balls are not born inside the column's collider.
    private const float PhysicalSmokeLift = 0.1f;

    // The two thin vents beside the column are told apart by how far apart they stand. Metres.
    private const float VentSeparation = 0.15f;

    // Substance Painter texture sets, named after the FBX materials they will be painted onto, with the
    // flat colour each falls back to until it is painted, and whether it is metal.
    private static readonly string[] TextureSets = {
        "DepositAndCenter", "Hoses", "Mounts", "Rungs", "SecondaryVents", "Tanks", "WoodFeet",
    };
    private static readonly Dictionary<string, Color> Fallback = new Dictionary<string, Color> {
        { "DepositAndCenter", new Color(0.55f, 0.42f, 0.20f) },
        { "Hoses", new Color(0.13f, 0.12f, 0.11f) },
        { "Mounts", new Color(0.36f, 0.35f, 0.34f) },
        { "Rungs", new Color(0.60f, 0.46f, 0.22f) },
        { "SecondaryVents", new Color(0.28f, 0.29f, 0.31f) },
        { "Tanks", new Color(0.58f, 0.32f, 0.20f) },
        { "WoodFeet", new Color(0.36f, 0.24f, 0.13f) },
    };
    private static readonly HashSet<string> NotMetal = new HashSet<string> { "WoodFeet", "Hoses" };

    private static readonly string[] PlaceEffectMocks = { "JVLmock_vfx_Place_forge", "JVLmock_sfx_build_hammer_metal" };
    private static readonly string[] HitEffectMocks = { "JVLmock_vfx_HitSparks", "JVLmock_sfx_metal_blocked" };

    private const int PieceLayer = 10;
    private const int DefaultLayer = 0;

    // The hatch box: wide and deep enough to cover the tilted plate and its frame, centred this far above
    // the top of the wooden stand it sits on. Metres.
    private static readonly Vector3 HatchBox = new Vector3(0.42f, 0.36f, 0.46f);
    private const float HatchAboveStand = 0.12f;
    // Measured from the model in Blender, for when neither the empty nor the stand can be found.
    private static readonly Vector3 HatchFallback = new Vector3(0.007f, 0.49f, -0.336f);
    private static readonly Vector3 SmokeFallback = new Vector3(0f, 1.713f, 0.03f);
    private static readonly Vector3[] VentFallback = { new Vector3(-0.201f, 1.65f, 0.243f), new Vector3(0.319f, 1.719f, 0.128f) };

    private const float IconDistance = 0.7f;

    [MenuItem("DvergerAutomation/Build Dverger Lighter (materials + prefab)")]
    public static void BuildAll() {
        List<string> notes = new List<string>();
        if (BuildMaterials(notes)) { BuildPrefab(notes); }
        Report(notes);
    }

    [MenuItem("DvergerAutomation/Build Dverger Lighter Materials Only")]
    public static void BuildMaterialsOnly() {
        List<string> notes = new List<string>();
        BuildMaterials(notes);
        Report(notes);
    }

    // ==================================================================== materials

    private static bool BuildMaterials(List<string> notes) {
        Shader shader = Shader.Find(PieceShader);
        if (shader == null) {
            notes.Add("ERROR: shader '" + PieceShader + "' not found; no materials built.");
            return false;
        }
        ModelImporter importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer == null) {
            notes.Add("ERROR: no ModelImporter for " + ModelPath + " - has Unity imported it?");
            return false;
        }
        Directory.CreateDirectory(TexDir);

        int painted = 0;
        foreach (string set in TextureSets) {
            string prefix = TexDir + "/" + set;
            bool isPainted = File.Exists(prefix + "_Base_color.png");
            Texture2D albedo = isPainted ? ConfigureAlbedo(prefix + "_Base_color.png", notes) : null;
            Texture2D normal = isPainted ? ConfigureNormal(prefix + "_Normal_DirectX.png", notes) : null;
            Texture2D metalGloss = isPainted
                ? PackMetallicGloss(prefix + "_Metallic.png", prefix + "_Roughness.png", prefix + "_MetallicGloss.png", notes)
                : null;
            if (isPainted) { ++painted; }

            string matPath = TexDir + "/Lighter_" + set + ".mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            bool isNew = mat == null;
            if (isNew) { mat = new Material(shader); }
            mat.shader = shader;
            mat.name = Path.GetFileNameWithoutExtension(matPath);

            mat.SetTexture("_MainTex", albedo);
            mat.SetTexture("_MetallicTex", metalGloss);
            mat.SetTexture("_BumpMap", normal);
            mat.SetColor("_Color", isPainted ? Color.white : Fallback[set]);

            // The eitr refinery's scalars, as the other machines use (see BuildHopperPrefab for why not the
            // blast furnace's). An unpainted set has no metallic map, so its metal is the scalar alone.
            bool metal = !NotMetal.Contains(set);
            mat.SetFloat("_Metallic", isPainted ? 1f : (metal ? 0.8f : 0f));
            mat.SetFloat("_MetallicAlphaGloss", 0.75f);
            mat.SetFloat("_Glossiness", isPainted ? 0.15f : (metal ? 0.45f : 0.1f));
            mat.SetColor("_MetalColor", Color.white);
            mat.SetFloat("_BumpScale", 1f);
            mat.SetFloat("_AddRain", 1f);
            mat.SetFloat("_Cull", 2f);
            SetKeyword(mat, "_NORMALMAP", normal != null);
            SetKeyword(mat, "_METALLICGLOSSMAP", metalGloss != null);
            mat.EnableKeyword("_ADDRAIN_ON");

            if (isNew) { AssetDatabase.CreateAsset(mat, matPath); } else { EditorUtility.SetDirty(mat); }
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), set), mat);
        }
        AssetDatabase.SaveAssets();
        importer.SaveAndReimport();

        notes.Add("Built " + TextureSets.Length + " materials in " + TexDir + " (" + painted + " painted, "
                  + (TextureSets.Length - painted) + " flat until painted) and remapped them onto the model.");
        return true;
    }

    private static void SetKeyword(Material mat, string keyword, bool on) {
        if (on) { mat.EnableKeyword(keyword); } else { mat.DisableKeyword(keyword); }
    }

    private static Texture2D ConfigureAlbedo(string path, List<string> notes) {
        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) { notes.Add("Missing texture " + path); return null; }
        if (ti.textureType != TextureImporterType.Default || !ti.sRGBTexture) {
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // The DirectX normal, green-flipped into Unity's convention: this Substance setup's OpenGL normal
    // exports come out flat (see BuildHopperPrefab).
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

    // RGB = metallic, A = smoothness (1 - roughness): Custom/Piece has no roughness slot of its own.
    private static Texture2D PackMetallicGloss(string metalPath, string roughPath, string outPath, List<string> notes) {
        if (!File.Exists(metalPath)) { notes.Add("Missing metallic " + metalPath); return null; }
        Texture2D metal = Decode(metalPath);
        Texture2D rough = File.Exists(roughPath) ? Decode(roughPath) : null;
        if (rough != null && (rough.width != metal.width || rough.height != metal.height)) {
            notes.Add("Roughness size differs from metallic for " + Path.GetFileName(metalPath) + " - packed with flat smoothness instead.");
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
            ti.sRGBTexture = false;
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

        // An identity wrapper, so everything added here is in real metres. MachineStore.Setup
        // looks the tank up at "Lighter_Body/LighterStore".
        GameObject bodyGo = new GameObject("Lighter_Body");
        bodyGo.layer = PieceLayer;
        Transform body = bodyGo.transform;
        body.SetParent(root.transform, worldPositionStays: false);

        // The model's own root keeps whatever axis conversion and scale the import gave it (see
        // BuildHopperPrefab); it is only moved. Its lowest point sits 26cm above its origin in Blender,
        // so it is dropped until it stands on the piece's origin, which is the ground it is placed on.
        GameObject modelInstance = Object.Instantiate(model);
        modelInstance.name = "Model";
        modelInstance.transform.SetParent(body, worldPositionStays: false);
        BuildHopperPrefab.SetLayerRecursive(modelInstance, PieceLayer);
        Bounds all = ModelBounds(modelInstance);
        modelInstance.transform.position += Vector3.down * all.min.y;
        all = ModelBounds(modelInstance);
        notes.Add("Model bounds: " + all.size.x.ToString("0.00") + " x " + all.size.y.ToString("0.00") + " x "
                  + all.size.z.ToString("0.00") + " m (w x h x d), standing at y " + all.min.y.ToString("0.00") + ".");

        // ---- collision -------------------------------------------------------
        // Every mesh: the tanks have to stop the player, and the hover has to find the machine from any side.
        foreach (MeshFilter filter in modelInstance.GetComponentsInChildren<MeshFilter>(includeInactive: true)) {
            BuildHopperPrefab.AddMeshCollider(filter.gameObject, notes);
        }

        // ---- the fuel tank ----------------------------------------------------------
        // A box round the hatch on the wooden stand, hit before the hatch's own mesh from the front.
        GameObject store = new GameObject("LighterStore");
        store.transform.SetParent(body, worldPositionStays: false);
        store.layer = PieceLayer;
        store.transform.position = HatchPoint(root.transform, modelInstance, notes);
        BoxCollider storeCol = store.AddComponent<BoxCollider>();
        storeCol.size = HatchBox;
        storeCol.isTrigger = false;
        Container container = store.AddComponent<Container>();
        container.m_name = "$DA_lighter_store_name";
        container.m_width = 8;
        container.m_height = 2;
        container.m_privacy = Container.PrivacySetting.Public;
        container.m_checkGuardStone = true;
        container.m_autoDestroyEmpty = false;
        container.m_discoverStat = PlayerStatType.None;

        // ---- smoke while it has fuel to give ---------------------------------------
        // LighterHub shows this holder while the Lighter is working, and the physical smoke under it only
        // while the server allows it.
        GameObject active = new GameObject("_active");
        active.transform.SetParent(body, worldPositionStays: false);
        active.layer = PieceLayer;

        Vector3 column = SmokePoint(root.transform, modelInstance, notes);
        List<Vector3> vents = VentPoints(root.transform, modelInstance, notes);
        GameObject smokeAsset = AssetDatabase.LoadAssetAtPath<GameObject>(SmokePath);
        if (smokeAsset != null) {
            AddWisp(smokeAsset, active.transform, "SmokeColumn", column, ColumnSmokeScale);
            for (int i = 0; i < vents.Count; ++i) {
                AddWisp(smokeAsset, active.transform, "SmokeVent" + (i + 1), vents[i], VentSmokeScale);
            }
        } else {
            notes.Add("Missing " + SmokePath + " - the Lighter has no visible smoke.");
        }

        GameObject physical = new GameObject("PhysicalSmoke");
        physical.transform.SetParent(active.transform, worldPositionStays: false);
        physical.layer = PieceLayer;
        physical.transform.position = column + Vector3.up * PhysicalSmokeLift;
        SmokeSpawner spawner = physical.AddComponent<SmokeSpawner>();
        spawner.m_smokePrefab = BuildHopperPrefab.EnsureMock(SmokeBallMock, notes);
        spawner.m_interval = 0.5f;
        spawner.m_testMask = 1 << SmokeLayer;
        spawner.m_testRadius = 0.75f;
        spawner.m_spawnRadius = 0f;
        spawner.m_stopFireOnStart = false;
        notes.Add("Smoke: column at " + column.ToString("F2") + ", " + vents.Count + " vents, physical smoke from the column.");

        active.SetActive(false);

        // ---- piece components --------------------------------------------------------
        ZNetView nview = root.AddComponent<ZNetView>();
        nview.m_persistent = true;
        nview.m_distant = false;
        nview.m_type = ZDO.ObjectType.Default;
        nview.m_syncInitialScale = false;

        Piece piece = root.AddComponent<Piece>();
        piece.m_name = "$DA_lighter_name";
        piece.m_description = "$DA_lighter_desc";
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
        wnt.m_burnable = false;
        wnt.m_staticPosition = true;
        wnt.m_triggerPrivateArea = true;
        // Iron, as the other machines: Stone could never stand on a wooden floor.
        wnt.m_materialType = WearNTear.MaterialType.Iron;
        wnt.m_hitEffect = BuildHopperPrefab.MockEffects(HitEffectMocks, notes);
        wnt.m_destroyedEffect = BuildHopperPrefab.MockEffects(HitEffectMocks, notes);

        LighterHub hub = root.AddComponent<LighterHub>();
        hub.ActiveVisuals = active;
        hub.PhysicalSmoke = physical;

        // ---- save -------------------------------------------------------------------
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

    // From the vertices, not the renderers: several parts are rotated in the model, and a renderer's
    // bounds are its own box turned into world space, which reaches well past the mesh - the tanks' box
    // reads as touching the ground while the tanks stop a quarter of a metre above it.
    private static Bounds ModelBounds(GameObject model) {
        bool any = false;
        Bounds b = new Bounds(model.transform.position, Vector3.zero);
        foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true)) {
            if (filter.sharedMesh == null) { continue; }
            foreach (Vector3 v in filter.sharedMesh.vertices) {
                Vector3 world = filter.transform.TransformPoint(v);
                if (!any) { b = new Bounds(world, Vector3.zero); any = true; } else { b.Encapsulate(world); }
            }
        }
        return b;
    }

    /// <summary>The renderer whose (remapped) material was painted as the named FBX slot.</summary>
    private static Renderer RendererFor(GameObject model, string slot) {
        foreach (Renderer rend in model.GetComponentsInChildren<Renderer>(true)) {
            foreach (Material mat in rend.sharedMaterials) {
                if (mat != null && mat.name.EndsWith(slot)) { return rend; }
            }
        }
        return null;
    }

    // An empty named StoreDoor if the model has one; otherwise just above the wooden stand, which stands
    // centred under the hatch.
    private static Vector3 HatchPoint(Transform root, GameObject model, List<string> notes) {
        Transform door = BuildHopperPrefab.FindDeep(root, "StoreDoor");
        if (door != null) { return door.position; }
        Renderer stand = RendererFor(model, "WoodFeet");
        if (stand != null) {
            Bounds b = stand.bounds;
            return new Vector3(b.center.x, b.max.y + HatchAboveStand, b.center.z);
        }
        notes.Add("No 'StoreDoor' empty or WoodFeet stand - the fuel tank's collider was placed at the measured position.");
        return root.position + HatchFallback;
    }

    // The wisp keeps the effect's own -90 X rotation, which is what points its cone up; it is only moved
    // and scaled. Its particles scale with its own transform (Local scaling mode).
    private static void AddWisp(GameObject asset, Transform parent, string name, Vector3 at, float scale) {
        GameObject wisp = Object.Instantiate(asset, parent, false);
        wisp.name = name;
        wisp.transform.position = at;
        wisp.transform.localScale = Vector3.one * scale;
    }

    /// <summary>Every vertex of the meshes painted as the named FBX slot, in world space.</summary>
    private static List<Vector3> SlotVertices(GameObject model, string slot, List<string> notes) {
        List<Vector3> world = new List<Vector3>();
        Renderer rend = RendererFor(model, slot);
        MeshFilter filter = rend != null ? rend.GetComponent<MeshFilter>() : null;
        if (filter == null || filter.sharedMesh == null) { return world; }
        try {
            foreach (Vector3 v in filter.sharedMesh.vertices) { world.Add(filter.transform.TransformPoint(v)); }
        } catch (Exception ex) {
            notes.Add("Could not read the " + slot + " mesh's vertices (" + ex.Message + ").");
        }
        return world;
    }

    // An empty named SmokePoint if the model has one; otherwise the mouth of the centre column - the top
    // of the DepositAndCenter mesh. The mouth is cut at a slant, so its middle is taken from the column's
    // round body just below it (the band within a quarter metre of the top) rather than from the highest
    // vertices, which all sit on the high lip.
    private static Vector3 SmokePoint(Transform root, GameObject model, List<string> notes) {
        Transform point = BuildHopperPrefab.FindDeep(root, "SmokePoint");
        if (point != null) { return point.position; }
        List<Vector3> verts = SlotVertices(model, "DepositAndCenter", notes);
        if (verts.Count > 0) {
            float top = float.MinValue;
            foreach (Vector3 v in verts) { top = Mathf.Max(top, v.y); }
            Bounds band = new Bounds();
            bool any = false;
            foreach (Vector3 v in verts) {
                if (v.y < top - 0.25f) { continue; }
                if (!any) { band = new Bounds(v, Vector3.zero); any = true; } else { band.Encapsulate(v); }
            }
            return new Vector3(band.center.x, top, band.center.z);
        }
        notes.Add("No 'SmokePoint' empty or readable column - the column's smoke was placed at the measured position.");
        return root.position + SmokeFallback;
    }

    // Empties named VentPoint1 and VentPoint2 if the model has them; otherwise the top of each upright
    // pipe in the SecondaryVents mesh, told apart by where they stand.
    private static List<Vector3> VentPoints(Transform root, GameObject model, List<string> notes) {
        List<Vector3> points = new List<Vector3>();
        for (int n = 1; n <= 2; ++n) {
            Transform empty = BuildHopperPrefab.FindDeep(root, "VentPoint" + n);
            if (empty != null) { points.Add(empty.position); }
        }
        if (points.Count > 0) { return points; }

        // Each vertex joins the first pipe standing within VentSeparation of it, seen from above.
        List<Bounds> pipes = new List<Bounds>();
        foreach (Vector3 v in SlotVertices(model, "SecondaryVents", notes)) {
            int found = -1;
            for (int i = 0; i < pipes.Count && found < 0; ++i) {
                Vector2 offset = new Vector2(v.x - pipes[i].center.x, v.z - pipes[i].center.z);
                if (offset.magnitude < VentSeparation) { found = i; }
            }
            if (found < 0) {
                pipes.Add(new Bounds(v, Vector3.zero));
            } else {
                Bounds b = pipes[found];
                b.Encapsulate(v);
                pipes[found] = b;
            }
        }
        foreach (Bounds pipe in pipes) { points.Add(new Vector3(pipe.center.x, pipe.max.y, pipe.center.z)); }
        if (points.Count == 2) { return points; }

        notes.Add("Expected 2 vent pipes in the SecondaryVents mesh, found " + points.Count + " - their smoke was placed at the measured positions.");
        points.Clear();
        foreach (Vector3 fallback in VentFallback) { points.Add(root.position + fallback); }
        return points;
    }

    // ==================================================================== icon

    /// <summary>
    /// Renders Assets/PrefabIcons/DA_Lighter.png through the Prefab Icon Exporter window's own renderer,
    /// reached by reflection because that window lives in the default editor assembly, which this
    /// folder's assembly definition cannot reference. Its camera sits on -Z, and the hatch faces -Z, so a
    /// small yaw shows the hatch and both side tanks.
    /// </summary>
    [MenuItem("DvergerAutomation/Export Dverger Lighter Icon")]
    public static void ExportIcon() {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        Type exporterType = Type.GetType("PrefabIconExporter, Assembly-CSharp-Editor");
        if (prefab == null || exporterType == null) {
            Debug.LogError("[Lighter] icon: " + (prefab == null ? "build the prefab first." : "PrefabIconExporter not found."));
            return;
        }

        ScriptableObject exporter = ScriptableObject.CreateInstance(exporterType);
        try {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            // Euler applies the tilt before the yaw, so the pose is composed and handed over as angles.
            Vector3 pose = (Quaternion.AngleAxis(-18f, Vector3.right) * Quaternion.AngleAxis(35f, Vector3.up)).eulerAngles;
            exporterType.GetProperty("prefab", flags).SetValue(exporter, prefab);
            exporterType.GetField("objectRotationX", flags).SetValue(exporter, pose.x);
            exporterType.GetField("objectRotationY", flags).SetValue(exporter, pose.y);
            exporterType.GetField("objectRotationZ", flags).SetValue(exporter, pose.z);
            // The exporter frames on renderer bounds, which the model's rotated parts inflate, so it
            // would leave the machine small in the middle of the icon.
            exporterType.GetField("cameraDistance", flags).SetValue(exporter, IconDistance);
            exporterType.GetField("openAfterExport", flags).SetValue(exporter, false);
            exporterType.GetMethod("ExportIcon", flags).Invoke(exporter, null);
        } finally {
            Object.DestroyImmediate(exporter);
        }

        AssetDatabase.ImportAsset(IconPath, ImportAssetOptions.ForceUpdate);
        TextureImporter ti = AssetImporter.GetAtPath(IconPath) as TextureImporter;
        if (ti == null) {
            Debug.LogError("[Lighter] icon: nothing was written to " + IconPath + ".");
            return;
        }
        if (ti.textureType != TextureImporterType.Sprite || ti.assetBundleName != BundleName) {
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.assetBundleName = BundleName;
            ti.SaveAndReimport();
        }
        Debug.Log("[Lighter] exported " + IconPath + " (bundle '" + BundleName + "').");
    }

    private static void Report(List<string> notes) {
        foreach (string note in notes) {
            if (note.StartsWith("ERROR")) { Debug.LogError("[Lighter] " + note); }
            else { Debug.Log("[Lighter] " + note); }
        }
    }
}
