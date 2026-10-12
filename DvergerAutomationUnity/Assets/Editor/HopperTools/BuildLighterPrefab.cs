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
/// The model carries no empties. The fuel tank's hatch is found from the wooden stand under it and the
/// smoke from the top of the centre column, unless the model gains a "StoreDoor" or "SmokePoint" empty.
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

    // The smelter's chimney smoke, made small: a thin wisp from the column while there is fuel to give.
    private const string SmokePath = "Assets/Custom/Effects/DA_SmelterSmoke.prefab";
    private const float SmokeScale = 0.35f;

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
    private static readonly Vector3 SmokeFallback = new Vector3(0f, 1.73f, 0.03f);

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

        // An identity wrapper, so everything added here is in real metres. LighterStore.ConfigurePrefab
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
        GameObject active = new GameObject("_active");
        active.transform.SetParent(body, worldPositionStays: false);
        active.layer = PieceLayer;
        GameObject smokeAsset = AssetDatabase.LoadAssetAtPath<GameObject>(SmokePath);
        if (smokeAsset != null) {
            // Copied inline, not nested, like the Hopper's chimney smoke.
            GameObject smoke = Object.Instantiate(smokeAsset, active.transform, false);
            smoke.name = "Smoke";
            smoke.transform.position = SmokePoint(root.transform, modelInstance, notes);
            smoke.transform.rotation = Quaternion.identity;
            smoke.transform.localScale = Vector3.one * SmokeScale;
        } else {
            notes.Add("Missing " + SmokePath + " - the Lighter works without smoke.");
        }
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

    // An empty named SmokePoint if the model has one; otherwise the middle of the highest vertices of the
    // centre column, which is the DepositAndCenter mesh's top.
    private static Vector3 SmokePoint(Transform root, GameObject model, List<string> notes) {
        Transform point = BuildHopperPrefab.FindDeep(root, "SmokePoint");
        if (point != null) { return point.position; }
        Renderer column = RendererFor(model, "DepositAndCenter");
        MeshFilter filter = column != null ? column.GetComponent<MeshFilter>() : null;
        if (filter != null && filter.sharedMesh != null) {
            try {
                Vector3[] verts = filter.sharedMesh.vertices;
                float top = float.MinValue;
                Vector3[] world = new Vector3[verts.Length];
                for (int i = 0; i < verts.Length; ++i) {
                    world[i] = filter.transform.TransformPoint(verts[i]);
                    top = Mathf.Max(top, world[i].y);
                }
                Vector3 sum = Vector3.zero;
                int count = 0;
                foreach (Vector3 v in world) {
                    if (v.y < top - 0.04f) { continue; }
                    sum += v;
                    ++count;
                }
                if (count > 0) { return sum / count + Vector3.up * 0.02f; }
            } catch (Exception ex) {
                notes.Add("Could not read the column's vertices (" + ex.Message + ").");
            }
        }
        notes.Add("No 'SmokePoint' empty or readable column - the smoke was placed at the measured position.");
        return root.position + SmokeFallback;
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
