using System.Collections.Generic;
using System.IO;
using DvergerAutomation;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the Dverger Seeking Table: its materials, and Assets/Custom/Prefabs/DA_SeekingTable.prefab from
/// DvergerSeekingTable.fbx.
///
/// The body is painted in Substance (Materials/SeekingTable/SeekingTable.spp): one texture set per FBX
/// material, exported with Unity's Standard (Metallic) preset as AlbedoTransparency, MetallicSmoothness and
/// an OpenGL Normal - the layout Custom/Piece reads (_MainTex, _MetallicTex with smoothness in alpha,
/// _BumpMap). The runes glow with their own painted colour, tinted to the orb's blue.
///
/// The Black Cores in the sockets are vanilla's own, mesh and material: the model's core spheres only mark
/// where each one sits. Both are Jotunn mocks, resolved when the piece is registered, so nothing of the
/// game's ships in the bundle. A core painted in Substance is not used.
///
/// Re-runnable: re-export the model or the textures and run it again rather than hand-editing the prefab.
/// Shares the Hopper builder's helpers and its assembly definition. Until a texture set is painted, its
/// material is a flat colour, so the piece can still be placed and tested.
/// </summary>
public static class BuildSeekingTablePrefab {
    private const string ModelPath = "Assets/Custom/Models/DvergerSeekingTable.fbx";
    private const string PrefabDir = "Assets/Custom/Prefabs";
    private const string PieceName = "DA_SeekingTable";
    private const string PrefabPath = PrefabDir + "/" + PieceName + ".prefab";
    private const string BundleName = "automation";

    private const string MatDir = "Assets/Custom/Materials/SeekingTable";
    private const string TexPrefix = MatDir + "/DvergerSeekingTable_";
    private const string PieceShader = "JVLmock_Custom/Piece";

    // The blue orb and its four streams, lifted from the Galdr table (scripts/unity/lift_seeking_effects.py).
    private const string OrbPath = "Assets/Custom/Effects/DA_SeekingOrb.prefab";

    // Substance texture sets, named after the FBX materials they were painted onto, with the flat colour each
    // falls back to until it is painted.
    private static readonly string[] TextureSets = { "Stone", "Metal", "Runes" };
    private static readonly Dictionary<string, Color> Fallback = new Dictionary<string, Color> {
        { "Stone", new Color(0.24f, 0.26f, 0.30f) },
        { "Metal", new Color(0.62f, 0.38f, 0.22f) },
        { "Runes", new Color(0.10f, 0.12f, 0.16f) },
    };
    // Multiplies the runes' painted colour into their glow: the orb's blue, bright enough to read in daylight.
    private static readonly Color RuneGlow = new Color(0.35f, 0.75f, 1.6f);

    // ---- the Black Cores --------------------------------------------------------------------
    // Vanilla's BlackCore item draws its core from the child "attach/default": a mesh standing on its
    // base, 0.18m across and 0.315m tall, under the material BlackCore_mat. "JVLmock_<prefab>__<child>..."
    // makes Jotunn look the mesh up on that child of the BlackCore prefab - the mesh itself is called
    // "default", like hundreds of others, so it cannot be found by name alone. The material's name is
    // its own.
    private const string MockMeshDir = "Assets/Custom/Mocks/Meshes";
    private const string CoreMeshMock = "JVLmock_BlackCore__attach__default";
    private const string CoreMaterialMockPath = "Assets/Custom/Mocks/Materials/JVLmock_BlackCore_mat.mat";
    private const float CoreHeight = 0.315f;
    private const float CoreRadius = 0.091f;

    private static readonly string[] PlaceEffectMocks = { "JVLmock_vfx_Place_forge", "JVLmock_sfx_build_hammer_metal" };
    private static readonly string[] HitEffectMocks = { "JVLmock_vfx_HitSparks", "JVLmock_sfx_metal_blocked" };

    private const int PieceLayer = 10;
    private const int DefaultLayer = 0;
    private const int CoreSlots = 4;
    private const float OrbFallbackHeight = 1.9f;

    // The box the table is set down by (see BuildPrefab): the lower tier of the base, its bottom on the
    // model's own, so the table stands exactly on the point aimed at. The size of the one first added to the
    // prefab by hand.
    private static readonly Vector3 FootingSize = new Vector3(2.59f, 0.21f, 2.5f);
    private static readonly Vector3 FootingCenter = new Vector3(0f, 0.105f, 0f);

    [MenuItem("DvergerAutomation/Build Seeking Table (materials + prefab)")]
    public static void BuildAll() {
        List<string> notes = new List<string>();
        if (BuildMaterials(notes)) { BuildPrefab(notes); }
        Report(notes);
    }

    [MenuItem("DvergerAutomation/Build Seeking Table Materials Only")]
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
        Directory.CreateDirectory(MatDir);

        foreach (string set in TextureSets) {
            string prefix = TexPrefix + set;
            Texture2D albedo = ConfigureTexture(prefix + "_AlbedoTransparency.png", TextureImporterType.Default, true, notes);
            Texture2D metalGloss = ConfigureTexture(prefix + "_MetallicSmoothness.png", TextureImporterType.Default, false, notes);
            Texture2D normal = ConfigureTexture(prefix + "_Normal.png", TextureImporterType.NormalMap, false, notes);
            bool painted = albedo != null;
            if (!painted) { notes.Add("'" + set + "' is not painted yet - a flat colour stands in."); }

            Material mat = LoadOrCreate(MatDir + "/SeekingTable_" + set + ".mat", shader);
            mat.SetTexture("_MainTex", albedo);
            mat.SetTexture("_MetallicTex", metalGloss);
            mat.SetTexture("_BumpMap", normal);
            mat.SetColor("_Color", painted ? Color.white : Fallback[set]);
            // The eitr refinery's values, as the Forge Hopper and the Grinder use them: the metallic map
            // decides what is metal, its alpha how smooth, scaled by _MetallicAlphaGloss. Without a map the
            // scalar alone decides.
            bool metal = set == "Metal";
            mat.SetFloat("_Metallic", metalGloss != null ? 1f : (metal ? 0.8f : 0f));
            mat.SetFloat("_MetallicAlphaGloss", 0.75f);
            mat.SetFloat("_Glossiness", metalGloss != null ? 0.15f : (metal ? 0.45f : 0.1f));
            mat.SetColor("_MetalColor", Color.white);
            mat.SetFloat("_BumpScale", 1f);
            mat.SetFloat("_AddRain", 1f);
            mat.SetFloat("_Cull", 2f);
            mat.EnableKeyword("_ADDRAIN_ON");
            SetKeyword(mat, "_NORMALMAP", normal != null);
            SetKeyword(mat, "_METALLICGLOSSMAP", metalGloss != null);

            // Custom/Piece adds _EmissionMap x _EmissionColor whatever its keywords say (the Galdr table's
            // own material lights its runes the same way), so black means no glow. The runes glow in their
            // painted colour - or, unpainted, all over.
            if (set == "Runes") {
                mat.SetTexture("_EmissionMap", albedo);
                mat.SetColor("_EmissionColor", RuneGlow);
                mat.EnableKeyword("_EMISSION");
            } else {
                mat.SetTexture("_EmissionMap", null);
                mat.SetColor("_EmissionColor", Color.black);
                mat.DisableKeyword("_EMISSION");
            }
            EditorUtility.SetDirty(mat);
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), set), mat);
        }

        // The model's own core spheres are replaced by vanilla Black Cores in BuildPrefab; this only keeps
        // the model asset itself showing a core material rather than an embedded default.
        Material coreMock = AssetDatabase.LoadAssetAtPath<Material>(CoreMaterialMockPath);
        if (coreMock != null) {
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "BlackCore"), coreMock);
        }
        // The game builds its creatures' navigation mesh from the pieces' own meshes at runtime, and skips -
        // with an error logged for every pass - any mesh it is not allowed to read. Unreadable, the table is
        // not in the navmesh at all. The AutoSorter's and Hopper's models are readable too.
        importer.isReadable = true;

        AssetDatabase.SaveAssets();
        importer.SaveAndReimport();
        notes.Add("Built " + TextureSets.Length + " materials in " + MatDir + " and remapped them onto the model.");
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

    private static void SetKeyword(Material mat, string keyword, bool on) {
        if (on) { mat.EnableKeyword(keyword); } else { mat.DisableKeyword(keyword); }
    }

    /// <summary>
    /// Import settings for a Substance export: colour in sRGB, the metallic map as linear data with its
    /// smoothness alpha kept, the normal map as a normal map. These exports are OpenGL-convention, which is
    /// Unity's own, so the green channel is not flipped. Null, quietly, for a texture not exported yet.
    /// </summary>
    private static Texture2D ConfigureTexture(string path, TextureImporterType type, bool sRGB, List<string> notes) {
        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) { return null; }
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

    // ==================================================================== the core mocks

    /// <summary>
    /// The stand-in for vanilla's Black Core mesh: the right size and the same standing pivot, so the
    /// socket looks right in the editor too. Only its name matters in game. Created once and left alone.
    /// </summary>
    private static Mesh EnsureCoreMeshMock(List<string> notes) {
        string path = MockMeshDir + "/" + CoreMeshMock + ".asset";
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null) {
            TagForBundle(path);
            return existing;
        }

        Directory.CreateDirectory(MockMeshDir);
        Mesh mesh = CrystalMesh(CoreRadius, CoreHeight, 8);
        mesh.name = CoreMeshMock;
        AssetDatabase.CreateAsset(mesh, path);
        TagForBundle(path);
        notes.Add("Created " + path + " (bundle '" + BundleName + "').");
        return mesh;
    }

    // A long eight-sided crystal standing on its point, widest a third of the way up.
    private static Mesh CrystalMesh(float radius, float height, int sides) {
        List<Vector3> verts = new List<Vector3>();
        List<int> tris = new List<int>();
        float waist = height * 0.4f;
        for (int i = 0; i < sides; ++i) {
            float a0 = Mathf.PI * 2f * i / sides;
            float a1 = Mathf.PI * 2f * (i + 1) / sides;
            Vector3 r0 = new Vector3(Mathf.Cos(a0) * radius, waist, Mathf.Sin(a0) * radius);
            Vector3 r1 = new Vector3(Mathf.Cos(a1) * radius, waist, Mathf.Sin(a1) * radius);
            AddTriangle(verts, tris, Vector3.zero, r1, r0);
            AddTriangle(verts, tris, new Vector3(0f, height, 0f), r0, r1);
        }
        Mesh mesh = new Mesh();
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AddTriangle(List<Vector3> verts, List<int> tris, Vector3 a, Vector3 b, Vector3 c) {
        tris.Add(verts.Count); verts.Add(a);
        tris.Add(verts.Count); verts.Add(b);
        tris.Add(verts.Count); verts.Add(c);
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
        Mesh coreMesh = EnsureCoreMeshMock(notes);
        Material coreMaterial = AssetDatabase.LoadAssetAtPath<Material>(CoreMaterialMockPath);
        if (coreMaterial == null) {
            notes.Add("ERROR: missing " + CoreMaterialMockPath + " - the sockets would show no core.");
            return;
        }
        TagForBundle(CoreMaterialMockPath);

        GameObject root = new GameObject(PieceName);
        root.layer = DefaultLayer;

        // An identity wrapper, so everything added here is in real metres whatever transform the FBX
        // import put on the model's own root - which is left untouched (see BuildHopperPrefab).
        GameObject bodyGo = new GameObject("Table_Body");
        bodyGo.layer = PieceLayer;
        Transform body = bodyGo.transform;
        body.SetParent(root.transform, worldPositionStays: false);

        GameObject modelInstance = Object.Instantiate(model);
        modelInstance.name = "Model";
        modelInstance.transform.SetParent(body, worldPositionStays: false);
        BuildHopperPrefab.SetLayerRecursive(modelInstance, PieceLayer);

        // ---- core markers, found before the colliders so they get none ----------
        List<Renderer> markers = new List<Renderer>();
        for (int n = 1; n <= CoreSlots; ++n) {
            Transform c = BuildHopperPrefab.FindDeep(root.transform, "core" + n) ?? BuildHopperPrefab.FindDeep(root.transform, "Core" + n);
            Renderer rend = c != null ? c.GetComponent<Renderer>() : null;
            if (rend != null) { markers.Add(rend); } else { notes.Add("Missing core marker mesh core" + n + "."); }
        }

        // ---- collision -------------------------------------------------------
        // Every visible mesh but the core markers: the pillars have to stop the player, and the hover has to
        // find the table from any side.
        foreach (MeshFilter filter in modelInstance.GetComponentsInChildren<MeshFilter>(includeInactive: true)) {
            if (markers.Exists(c => c.gameObject == filter.gameObject)) { continue; }
            BuildHopperPrefab.AddMeshCollider(filter.gameObject, notes);
        }

        // The footing. Vanilla sets a placement ghost down by whichever of its colliders comes closest to the
        // point aimed at, and only counts boxes, spheres and convex meshes - the model's mesh collider is not
        // convex, so it is skipped (Player.UpdatePlacementGhost). Without this box the nearest left would be
        // a core socket's sphere, 1.9m up a pillar: the table went into the ground to put that sphere on the
        // aimed point, buried up to its cores and pushed sideways. A box under the base is always nearest the
        // ground, so the base sits on it, as the AutoSorter's and the Hopper's own boxes do for them.
        BoxCollider footing = bodyGo.AddComponent<BoxCollider>();
        footing.size = FootingSize;
        footing.center = FootingCenter;
        footing.isTrigger = false;

        // ---- the orb -----------------------------------------------------------
        Transform orbPoint = BuildHopperPrefab.FindDeep(root.transform, "OrbPoint");
        if (orbPoint == null) {
            GameObject point = new GameObject("OrbPoint");
            point.transform.SetParent(body, worldPositionStays: false);
            point.transform.localPosition = new Vector3(0f, OrbFallbackHeight, 0f);
            orbPoint = point.transform;
            notes.Add("Missing empty 'OrbPoint' - the orb was placed at " + OrbFallbackHeight + "m.");
        }

        // SeekingTable shows this holder while the table is awake.
        GameObject enabledVisuals = new GameObject("_enabled");
        enabledVisuals.transform.SetParent(body, worldPositionStays: false);
        enabledVisuals.layer = PieceLayer;

        List<Transform> streams = new List<Transform>();
        GameObject orbPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbPath);
        if (orbPrefab == null) {
            notes.Add("Missing " + OrbPath + " - the table has no orb.");
        } else {
            // Copied inline, not nested, like the hopper's chimney smoke. Unrotated and unscaled whatever
            // the model's root carries, so the orb is its own size and the streams point where they say.
            GameObject orb = Object.Instantiate(orbPrefab, enabledVisuals.transform, false);
            orb.name = "Orb";
            orb.transform.position = orbPoint.position;
            orb.transform.rotation = Quaternion.identity;
            for (int n = 1; n <= CoreSlots; ++n) {
                Transform stream = orb.transform.Find("Stream" + n);
                if (stream != null) { streams.Add(stream); } else { notes.Add("The orb has no Stream" + n + "."); }
            }
        }

        // ---- core sockets ------------------------------------------------------
        // Each switch is paired with the nearest core marker, and the marker swapped for a vanilla Black
        // Core standing where it was; each stream with whichever points most nearly at the socket, seen from
        // above.
        Switch[] switches = new Switch[CoreSlots];
        GameObject[] slotVisuals = new GameObject[CoreSlots];
        GameObject[] slotEffects = new GameObject[CoreSlots];
        for (int i = 0; i < CoreSlots; ++i) {
            int n = i + 1;
            Transform socket = BuildHopperPrefab.FindDeep(root.transform, "AddCore" + n);
            if (socket == null) {
                notes.Add("Missing empty 'AddCore" + n + "' - slot " + n + " has no switch.");
                continue;
            }
            GameObject go = socket.gameObject;
            go.layer = PieceLayer;
            // A local radius under whatever scale the model root carries.
            SphereCollider col = BuildHopperPrefab.GetOrAdd<SphereCollider>(go);
            col.radius = 0.2f / Mathf.Max(0.0001f, socket.lossyScale.x);
            col.isTrigger = false;
            Switch sw = BuildHopperPrefab.GetOrAdd<Switch>(go);
            sw.m_hoverText = "$DA_Add_BlackCore";
            sw.m_name = "$DA_seek_name";
            sw.m_holdRepeatInterval = -1f;
            switches[i] = sw;

            Renderer nearest = null;
            float best = float.MaxValue;
            foreach (Renderer rend in markers) {
                float d = Vector3.Distance(socket.position, rend.bounds.center);
                if (d < best) { best = d; nearest = rend; }
            }
            Vector3 coreCentre = socket.position;
            if (nearest != null) {
                markers.Remove(nearest);
                if (best > 0.3f) {
                    notes.Add("AddCore" + n + " paired with " + nearest.name + " but they are " + best.ToString("0.00") + "m apart - check the socket placement.");
                }
                coreCentre = nearest.bounds.center;
                Object.DestroyImmediate(nearest.gameObject);
            }
            slotVisuals[i] = BlackCore(body, n, coreCentre, coreMesh, coreMaterial);

            Vector3 toSocket = socket.position - orbPoint.position;
            toSocket.y = 0f;
            Transform aimed = null;
            float bestDot = -2f;
            foreach (Transform stream in streams) {
                Vector3 forward = stream.forward;
                forward.y = 0f;
                float dot = Vector3.Dot(forward.normalized, toSocket.normalized);
                if (dot > bestDot) { bestDot = dot; aimed = stream; }
            }
            if (aimed != null) {
                streams.Remove(aimed);
                if (bestDot < 0.9f) {
                    notes.Add("Stream for AddCore" + n + " is " + (Mathf.Acos(Mathf.Clamp(bestDot, -1f, 1f)) * Mathf.Rad2Deg).ToString("0") + " degrees off the socket - check the pillars sit on the diagonals.");
                }
                aimed.gameObject.SetActive(false);
                slotEffects[i] = aimed.gameObject;
            }
        }
        // A marker no socket claimed would otherwise sit there as a grey ball.
        foreach (Renderer leftover in markers) {
            notes.Add("Core marker " + leftover.name + " has no socket; removed.");
            Object.DestroyImmediate(leftover.gameObject);
        }

        // ---- piece components ----------------------------------------------------
        ZNetView nview = root.AddComponent<ZNetView>();
        nview.m_persistent = true;
        nview.m_distant = false;
        nview.m_type = ZDO.ObjectType.Default;
        nview.m_syncInitialScale = false;

        Piece piece = root.AddComponent<Piece>();
        piece.m_name = "$DA_seek_name";
        piece.m_description = "$DA_seek_desc";
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
        // Iron rather than Marble or Stone, so a wooden floor can hold it (see BuildHopperPrefab).
        wnt.m_materialType = WearNTear.MaterialType.Iron;
        wnt.m_hitEffect = BuildHopperPrefab.MockEffects(HitEffectMocks, notes);
        wnt.m_destroyedEffect = BuildHopperPrefab.MockEffects(HitEffectMocks, notes);

        CoreSockets sockets = root.AddComponent<CoreSockets>();
        sockets.Switches = switches;
        sockets.SlotVisuals = slotVisuals;
        sockets.SlotEffects = slotEffects;
        sockets.ItemPrefab = "BlackCore";
        sockets.MaskKey = "DA_seek_cores";
        sockets.HoverName = "$DA_seek_name";
        sockets.AddMessage = "$DA_Add_BlackCore";
        sockets.RemoveMessage = "$DA_Remove_BlackCore";
        sockets.NeedMessage = "$DA_Need_BlackCore";

        SeekingTable table = root.AddComponent<SeekingTable>();
        table.OrbPoint = orbPoint;
        table.EnabledVisuals = enabledVisuals;

        // ---- save --------------------------------------------------------------------
        Directory.CreateDirectory(PrefabDir);
        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        if (saved == null) {
            notes.Add("ERROR: failed to save " + PrefabPath);
            return;
        }
        TagForBundle(PrefabPath);
        AssetDatabase.SaveAssets();
        notes.Add("Built " + PrefabPath + " (bundle '" + BundleName + "').");

        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        EditorGUIUtility.PingObject(Selection.activeObject);
    }

    /// <summary>
    /// A vanilla Black Core in socket <paramref name="n"/>, its middle where the model's marker had its
    /// centre. Upright and at its own size whatever the model root carries, since the mesh is vanilla's and
    /// stands on its base. Ships hidden and on Default, like the other machines' cores; CoreSockets shows it
    /// while the socket is filled.
    /// </summary>
    private static GameObject BlackCore(Transform body, int n, Vector3 centre, Mesh mesh, Material material) {
        GameObject core = new GameObject("BlackCore" + n);
        core.layer = DefaultLayer;
        core.transform.SetParent(body, worldPositionStays: false);
        core.transform.position = centre - new Vector3(0f, CoreHeight / 2f, 0f);
        core.transform.rotation = Quaternion.identity;
        core.AddComponent<MeshFilter>().sharedMesh = mesh;
        core.AddComponent<MeshRenderer>().sharedMaterial = material;
        core.SetActive(false);
        return core;
    }

    // ==================================================================== helpers

    private static void Report(List<string> notes) {
        foreach (string note in notes) {
            if (note.StartsWith("ERROR")) { Debug.LogError("[SeekingTable] " + note); }
            else { Debug.Log("[SeekingTable] " + note); }
        }
    }
}
