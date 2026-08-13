#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds a 3-level haunted house (basement / ground / upper) with stairs and room traversal.
/// Menu: FYP → Build Haunted House
/// </summary>
public static class HauntedHouseBuilder
{
    const float FloorH = 3.2f;
    const float WallT = 0.35f;
    const float HouseW = 22f;
    const float HouseD = 16f;
    const int StairSteps = 16;
    // XZ wells: stairs run +Z from zMin and land on solid floor just past zMax.
    static readonly Rect WellWest = new Rect(-10.15f, 2.15f, 3.5f, 4.4f);
    static readonly Rect WellEast = new Rect(6.65f, 2.15f, 3.5f, 4.4f);

    static readonly Color WallColor = new Color(0.22f, 0.18f, 0.16f);
    static readonly Color FloorColor = new Color(0.16f, 0.12f, 0.10f);
    static readonly Color CeilingColor = new Color(0.12f, 0.11f, 0.10f);
    static readonly Color TrimColor = new Color(0.08f, 0.07f, 0.06f);
    static readonly Color StairColor = new Color(0.28f, 0.22f, 0.18f);

    [MenuItem("FYP/Build Haunted House")]
    public static void Build()
    {
        // Remove previous build
        var existing = GameObject.Find("HauntedHouse");
        if (existing != null)
            Object.DestroyImmediate(existing);

        // Hide old single-room shell and keep entities out of this house
        var oldRoom = GameObject.Find("HorrorRoom");
        if (oldRoom != null)
            oldRoom.SetActive(false);

        foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (go.name == "Entities" || go.name == "Ground")
                go.SetActive(false);
        }

        var evc = Object.FindFirstObjectByType<EntityVisibilityController>(FindObjectsInactive.Include);
        if (evc != null)
            evc.enabled = false;

        var existingYard = GameObject.Find("HauntedYard");
        if (existingYard != null)
            Object.DestroyImmediate(existingYard);

        var root = new GameObject("HauntedHouse");
        Undo.RegisterCreatedObjectUndo(root, "Build Haunted House");

        var mats = CreateHauntedMaterials();
        var wallMat = mats.wall;
        var ceilMat = mats.ceiling;
        var trimMat = mats.trim;
        var stairMat = mats.stair;

        // --- Floors ---
        var basement = MakeEmpty(root.transform, "Basement", new Vector3(0f, -FloorH, 0f)).transform;
        var groundFl = MakeEmpty(root.transform, "GroundFloor", Vector3.zero).transform;
        var upper = MakeEmpty(root.transform, "UpperFloor", new Vector3(0f, FloorH, 0f)).transform;

        BuildFloorShell(basement, "B", wallMat, mats.floorBasement, ceilMat, includeCeiling: true,
            floorHole: null,
            ceilingHole: WellWest);
        BuildFloorShell(groundFl, "G", wallMat, mats.floor, ceilMat, includeCeiling: true,
            floorHole: WellWest,
            ceilingHole: WellEast);
        BuildFloorShell(upper, "U", wallMat, mats.floorUpper, ceilMat, includeCeiling: true,
            floorHole: WellEast,
            ceilingHole: null);

        // Interior partitions + doorways
        BuildBasementRooms(basement, wallMat, trimMat);
        BuildGroundRooms(groundFl, wallMat, trimMat);
        BuildUpperRooms(upper, wallMat, trimMat);

        // Stair wells (cut floors + stair meshes)
        BuildStairGroundToUpper(groundFl, upper, stairMat, wallMat);
        BuildStairGroundToBasement(groundFl, basement, stairMat, wallMat);
        PlaceStairWellGuards(groundFl, upper, wallMat);

        // Atmosphere
        PlaceLights(basement, groundFl, upper);
        PlaceProps(basement, groundFl, upper, trimMat, mats.porch);
        PlaceInteriorDetails(root.transform, basement, groundFl, upper, mats);

        // Entry porch / front door opening on south wall (already open via doorway)
        Box(root.transform, "Porch", new Vector3(0f, 0.05f, -HouseD * 0.5f - 1.2f),
            new Vector3(4f, 0.1f, 2.2f), mats.porch);

        // Player spawn: front foyer + stair-friendly controller
        var player = GameObject.Find("Player");
        if (player != null)
        {
            Undo.RecordObject(player.transform, "Move Player");
            player.transform.position = new Vector3(0f, 1.05f, -HouseD * 0.5f + 2.2f);
            player.transform.rotation = Quaternion.identity;
            var cc = player.GetComponent<CharacterController>();
            if (cc != null)
            {
                Undo.RecordObject(cc, "Tune CC for stairs");
                cc.stepOffset = 0.45f;
                cc.slopeLimit = 50f;
            }
        }

        // Mood: dim the sun a bit for haunted look
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (l.type == LightType.Directional)
            {
                Undo.RecordObject(l, "Dim sun");
                l.intensity = 0.35f;
                l.color = new Color(0.55f, 0.6f, 0.75f);
                break;
            }
        }

        // Wire realtime texture generation (ONNX + procedural) onto house materials
        WireTextureCorruption(mats);
        PlaceNatureYard(root.transform, mats.porch);
        EnsureGameplayHud();

        Selection.activeGameObject = root;
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[HauntedHouse] Built 3-floor haunted house with realtime textured surfaces. Player at foyer.");
    }

    struct HouseMaterials
    {
        public Material wall, floor, floorUpper, floorBasement, ceiling, trim, stair, porch, tile;
        public Texture2D wallTex, floorTex, floorUpperTex, floorBasementTex, ceilingTex, woodTex, stairTex, porchTex, tileTex;
    }

    static HouseMaterials CreateHauntedMaterials()
    {
        const string folder = "Assets/FYP/Materials/HauntedHouse";
        if (!AssetDatabase.IsValidFolder("Assets/FYP"))
            AssetDatabase.CreateFolder("Assets", "FYP");
        if (!AssetDatabase.IsValidFolder("Assets/FYP/Materials"))
            AssetDatabase.CreateFolder("Assets/FYP", "Materials");
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder("Assets/FYP/Materials", "HauntedHouse");

        var wallTex = HauntedHouseTextures.CreateWallpaper(256, 42);
        var floorTexProc = HauntedHouseTextures.CreateFloorboards(256, 77);
        var ceilTex = HauntedHouseTextures.CreatePlasterCeiling(256, 91);
        var woodTexProc = HauntedHouseTextures.CreateDarkWood(256, 55);

        SaveTex(folder + "/HH_Wallpaper.asset", wallTex);
        SaveTex(folder + "/HH_Floorboards.asset", floorTexProc);
        SaveTex(folder + "/HH_Plaster.asset", ceilTex);
        SaveTex(folder + "/HH_DarkWood.asset", woodTexProc);

        wallTex = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/HH_Wallpaper.asset");
        ceilTex = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/HH_Plaster.asset");
        woodTexProc = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/HH_DarkWood.asset");
        floorTexProc = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/HH_Floorboards.asset");

        // Yughues flooring + cobble (fall back to procedural if the pack is missing)
        var woodGround = TexOr("Assets/YughuesFreeFlooringMaterials/Textures/T_YFFlM_08_d.tga", floorTexProc);
        var woodUpper = TexOr("Assets/YughuesFreeFlooringMaterials/Textures/T_YFFlM_02_d.tga", floorTexProc);
        var woodStair = TexOr("Assets/YughuesFreeFlooringMaterials/Textures/T_YFFlM_10_d.tga", woodTexProc);
        var woodTrim = TexOr("Assets/YughuesFreeFlooringMaterials/Textures/T_YFFlM_12_d.tga", woodTexProc);
        var tileTex = TexOr("Assets/YughuesFreeFlooringMaterials/Textures/T_YFFlM_03_d.tga", floorTexProc);
        var cobbleBase = TexOr("Assets/YughuesFreeCobbleMaterials/Textures/T_YFCM_08_d.tga", floorTexProc);
        var cobblePorch = TexOr("Assets/YughuesFreeCobbleMaterials/Textures/T_YFCM_04_d.tga", cobbleBase);

        var nGround = TexOr("Assets/YughuesFreeFlooringMaterials/Textures/T_YFFlM_08_n.tga", null);
        var nUpper = TexOr("Assets/YughuesFreeFlooringMaterials/Textures/T_YFFlM_02_n.tga", null);
        var nStair = TexOr("Assets/YughuesFreeFlooringMaterials/Textures/T_YFFlM_10_n.tga", null);
        var nTrim = TexOr("Assets/YughuesFreeFlooringMaterials/Textures/T_YFFlM_12_n.tga", null);
        var nTile = TexOr("Assets/YughuesFreeFlooringMaterials/Textures/T_YFFlM_03_n.tga", null);
        var nCobbleB = TexOr("Assets/YughuesFreeCobbleMaterials/Textures/T_YFCM_08_n.tga", null);
        var nCobbleP = TexOr("Assets/YughuesFreeCobbleMaterials/Textures/T_YFCM_04_n.tga", null);

        var wall = MakeTexturedMat(folder + "/HH_Wall.mat", "HH_Wall", wallTex, new Vector2(3.5f, 2.2f), new Color(0.92f, 0.88f, 0.8f));
        var floor = MakeTexturedMat(folder + "/HH_Floor.mat", "HH_Floor", woodGround, new Vector2(3.2f, 3.2f), Color.white, nGround, 0.22f);
        var floorUpper = MakeTexturedMat(folder + "/HH_FloorUpper.mat", "HH_FloorUpper", woodUpper, new Vector2(3.4f, 3.4f), Color.white, nUpper, 0.24f);
        var floorBasement = MakeTexturedMat(folder + "/HH_FloorBasement.mat", "HH_FloorBasement", cobbleBase, new Vector2(2.8f, 2.8f), Color.white, nCobbleB, 0.12f);
        var ceiling = MakeTexturedMat(folder + "/HH_Ceiling.mat", "HH_Ceiling", ceilTex, new Vector2(2.5f, 2.5f), new Color(0.95f, 0.93f, 0.9f));
        var trim = MakeTexturedMat(folder + "/HH_Trim.mat", "HH_Trim", woodTrim, new Vector2(2.2f, 2.2f), Color.white, nTrim, 0.2f);
        var stair = MakeTexturedMat(folder + "/HH_Stair.mat", "HH_Stair", woodStair, new Vector2(1.8f, 1.8f), new Color(0.95f, 0.9f, 0.84f), nStair, 0.2f);
        var porch = MakeTexturedMat(folder + "/HH_Porch.mat", "HH_Porch", cobblePorch, new Vector2(2.4f, 2.4f), Color.white, nCobbleP, 0.14f);
        var tile = MakeTexturedMat(folder + "/HH_Tile.mat", "HH_Tile", tileTex, new Vector2(2.6f, 2.6f), Color.white, nTile, 0.28f);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        return new HouseMaterials
        {
            wall = wall,
            floor = floor,
            floorUpper = floorUpper,
            floorBasement = floorBasement,
            ceiling = ceiling,
            trim = trim,
            stair = stair,
            porch = porch,
            tile = tile,
            wallTex = wallTex,
            floorTex = woodGround,
            floorUpperTex = woodUpper,
            floorBasementTex = cobbleBase,
            ceilingTex = ceilTex,
            woodTex = woodTrim,
            stairTex = woodStair,
            porchTex = cobblePorch,
            tileTex = tileTex
        };
    }

    static Texture2D TexOr(string path, Texture2D fallback) =>
        AssetDatabase.LoadAssetAtPath<Texture2D>(path) ?? fallback;

    static void SaveTex(string path, Texture2D tex)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (existing != null)
        {
            // Replace pixels on existing asset
            if (existing.width == tex.width && existing.height == tex.height)
            {
                existing.SetPixels(tex.GetPixels());
                existing.Apply(true);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(tex);
                return;
            }
            AssetDatabase.DeleteAsset(path);
        }
        AssetDatabase.CreateAsset(tex, path);
    }

    static Material MakeTexturedMat(string path, string name, Texture2D albedo, Vector2 tiling, Color tint,
        Texture2D bump = null, float smoothness = 0.18f)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");

        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = shader;
            mat.name = name;
        }

        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", albedo);
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", albedo);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        if (bump != null && mat.HasProperty("_BumpMap"))
        {
            mat.SetTexture("_BumpMap", bump);
            mat.EnableKeyword("_NORMALMAP");
            if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
        }
        mat.SetTextureScale("_BaseMap", tiling);
        if (mat.HasProperty("_MainTex")) mat.SetTextureScale("_MainTex", tiling);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static void WireTextureCorruption(HouseMaterials mats)
    {
        var runner = Object.FindFirstObjectByType<TextureCorruptionRunner>();
        if (runner == null)
        {
            var hs = GameObject.Find("HorrorSystems");
            if (hs != null)
                runner = hs.GetComponent<TextureCorruptionRunner>() ?? hs.AddComponent<TextureCorruptionRunner>();
        }
        if (runner == null)
        {
            Debug.LogWarning("[HauntedHouse] No TextureCorruptionRunner found — textures won't update in realtime.");
            return;
        }

        runner.BindSurfaces(new[]
        {
            new TextureCorruptionRunner.SurfaceTarget
            {
                name = "Wall",
                material = mats.wall,
                cleanAlbedo = mats.wallTex
            },
            new TextureCorruptionRunner.SurfaceTarget
            {
                name = "Floor",
                material = mats.floor,
                cleanAlbedo = mats.floorTex
            },
            new TextureCorruptionRunner.SurfaceTarget
            {
                name = "FloorUpper",
                material = mats.floorUpper,
                cleanAlbedo = mats.floorUpperTex
            },
            new TextureCorruptionRunner.SurfaceTarget
            {
                name = "FloorBasement",
                material = mats.floorBasement,
                cleanAlbedo = mats.floorBasementTex
            },
            new TextureCorruptionRunner.SurfaceTarget
            {
                name = "Ceiling",
                material = mats.ceiling,
                cleanAlbedo = mats.ceilingTex
            },
            new TextureCorruptionRunner.SurfaceTarget
            {
                name = "Trim",
                material = mats.trim,
                cleanAlbedo = mats.woodTex
            },
            new TextureCorruptionRunner.SurfaceTarget
            {
                name = "Stair",
                material = mats.stair,
                cleanAlbedo = mats.stairTex
            },
            new TextureCorruptionRunner.SurfaceTarget
            {
                name = "Porch",
                material = mats.porch,
                cleanAlbedo = mats.porchTex
            },
            new TextureCorruptionRunner.SurfaceTarget
            {
                name = "Tile",
                material = mats.tile,
                cleanAlbedo = mats.tileTex
            },
        });

        EditorUtility.SetDirty(runner);
        Debug.Log("[HauntedHouse] TextureCorruptionRunner bound to wall/floor/ceiling/trim/stair.");
    }

    static void EnsureGameplayHud()
    {
        var hs = GameObject.Find("HorrorSystems");
        if (hs == null) return;
        if (hs.GetComponent<GameplayHud>() == null)
            hs.AddComponent<GameplayHud>();
        var stress = hs.GetComponent<StressController>();
        if (stress != null)
        {
            var so = new SerializedObject(stress);
            var hud = so.FindProperty("showHud");
            if (hud != null) hud.boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        var audio = hs.GetComponent<AudioGenerationRunner>();
        if (audio != null)
        {
            var so = new SerializedObject(audio);
            var hud = so.FindProperty("showStatusHud");
            if (hud != null) hud.boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        EditorUtility.SetDirty(hs);
    }

    static void PlaceNatureYard(Transform house, Material cobble)
    {
        const string prefabRoot = "Assets/SimpleNaturePack/Prefabs/";
        GameObject Load(string n) => AssetDatabase.LoadAssetAtPath<GameObject>(prefabRoot + n + ".prefab");

        ConvertNaturePackToUrp();

        var tree1 = Load("Tree_01");
        var tree2 = Load("Tree_02");
        var tree3 = Load("Tree_03");
        var tree4 = Load("Tree_04");
        var tree5 = Load("Tree_05");
        var bush1 = Load("Bush_01");
        var bush2 = Load("Bush_02");
        var bush3 = Load("Bush_03");
        var rock1 = Load("Rock_01");
        var rock2 = Load("Rock_02");
        var rock3 = Load("Rock_03");
        var rock4 = Load("Rock_04");
        var stump = Load("Stump_01");
        var mush1 = Load("Mushroom_01");
        var mush2 = Load("Mushroom_02");
        var grass1 = Load("Grass_01");
        var grass2 = Load("Grass_02");
        var flower1 = Load("Flowers_01");
        var flower2 = Load("Flowers_02");
        var branch = Load("Branch_01");
        var ground1 = Load("Ground_01");
        var ground2 = Load("Ground_02");
        var ground3 = Load("Ground_03");

        if (tree1 == null)
        {
            Debug.LogWarning("[HauntedHouse] SimpleNaturePack prefabs not found — skip yard.");
            return;
        }

        var nested = house.Find("Yard");
        if (nested != null)
            Object.DestroyImmediate(nested.gameObject);
        var oldYard = GameObject.Find("HauntedYard");
        if (oldYard != null)
            Object.DestroyImmediate(oldYard);

        // Sibling of the house so house texture/UV systems never walk these renderers.
        var yard = new GameObject("HauntedYard");
        Undo.RegisterCreatedObjectUndo(yard, "Haunted Yard");
        yard.transform.position = house.position;
        yard.AddComponent<EnvironmentTextureLock>();

        // Flat grass pads OUTSIDE the house — nature-pack Ground meshes are hills
        // and at full scale they punch through floors/walls.
        SpawnFlatGroundRing(yard.transform, new[] { ground1, ground2, ground3 }, count: 16, radius: 18.5f, scale: 0.95f);
        SpawnFlatGroundRing(yard.transform, new[] { ground2, ground3 }, count: 10, radius: 22.5f, scale: 1.1f);

        // Tree line wrapping the house
        var trees = new[] { tree1, tree2, tree3, tree4, tree5 };
        SpawnRing(yard.transform, trees, count: 16, radius: 17.5f, y: 0f, scale: 1.25f, yawJitter: 360f, skipSouthGap: true);
        SpawnRing(yard.transform, trees, count: 12, radius: 22f, y: 0f, scale: 1.55f, yawJitter: 360f, skipSouthGap: false);

        var bushes = new[] { bush1, bush2, bush3 };
        SpawnRing(yard.transform, bushes, count: 14, radius: 14.2f, y: 0f, scale: 1.1f, yawJitter: 360f, skipSouthGap: true);

        var rocks = new[] { rock1, rock2, rock3, rock4 };
        SpawnScatter(yard.transform, rocks, 8, 14.5f, 21f, 0f, 1.1f);
        SpawnScatter(yard.transform, new[] { stump, branch }, 6, 14f, 19f, 0f, 1.0f);
        SpawnScatter(yard.transform, new[] { mush1, mush2 }, 10, 13.5f, 17f, 0f, 1.2f);
        SpawnScatter(yard.transform, new[] { grass1, grass2, flower1, flower2 }, 22, 13.8f, 20f, 0f, 1.0f);

        // Porch-side dead garden (left/right of door, not blocking entry)
        SpawnAt(yard.transform, bush2, new Vector3(-3.2f, 0f, -HouseD * 0.5f - 1.1f), 1.1f);
        SpawnAt(yard.transform, bush3, new Vector3(3.4f, 0f, -HouseD * 0.5f - 1.0f), 1.05f);
        SpawnAt(yard.transform, rock2, new Vector3(-4.5f, 0f, -HouseD * 0.5f - 0.4f), 0.9f);
        SpawnAt(yard.transform, flower2, new Vector3(2.4f, 0f, -HouseD * 0.5f - 1.6f), 1f);
        SpawnAt(yard.transform, tree4, new Vector3(-9f, 0f, -HouseD * 0.5f - 3.5f), 1.5f);
        SpawnAt(yard.transform, tree2, new Vector3(9.5f, 0f, -HouseD * 0.5f - 3.2f), 1.4f);

        if (cobble != null)
        {
            for (int i = 0; i < 7; i++)
            {
                float z = -HouseD * 0.5f - 2.5f - i * 1.05f;
                float x = (i % 2 == 0) ? -0.15f : 0.2f;
                Box(yard.transform, "Path_" + i, new Vector3(x, 0.03f, z), new Vector3(1.7f, 0.06f, 0.95f), cobble);
            }
        }

        IsolateYardMaterials(yard);
        CullYardOverlappingHouse(yard);
        Debug.Log("[HauntedHouse] SimpleNaturePack yard placed as a frozen exterior (HauntedYard).");
    }

    static void IsolateYardMaterials(GameObject yard)
    {
        var cache = new Dictionary<Material, Material>();
        var renderers = yard.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            if (r == null) continue;
            var shared = r.sharedMaterials;
            if (shared == null || shared.Length == 0) continue;
            var copies = new Material[shared.Length];
            for (int i = 0; i < shared.Length; i++)
            {
                var src = shared[i];
                if (src == null) continue;
                if (!cache.TryGetValue(src, out var copy))
                {
                    copy = MakeUrpYardMaterial(src);
                    cache[src] = copy;
                }
                copies[i] = copy;
            }
            r.sharedMaterials = copies;
            r.SetPropertyBlock(null);
        }
        var lockCmp = yard.GetComponent<EnvironmentTextureLock>();
        if (lockCmp != null)
            lockCmp.Capture();
    }

    static void SpawnRing(Transform parent, GameObject[] prefabs, int count, float radius, float y, float scale, float yawJitter, bool skipSouthGap = false)
    {
        if (prefabs == null || prefabs.Length == 0) return;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)count;
            float ang = t * Mathf.PI * 2f;
            // South is -Z (front door). Skip a wedge so the approach stays open.
            if (skipSouthGap)
            {
                float deg = ang * Mathf.Rad2Deg;
                if (deg > 240f && deg < 300f) continue;
            }
            var prefab = prefabs[i % prefabs.Length];
            if (prefab == null) continue;
            var pos = new Vector3(Mathf.Sin(ang) * radius, y, Mathf.Cos(ang) * radius);
            if (InsideHouseFootprint(pos, 1.4f)) continue;
            float yaw = t * yawJitter + i * 17f;
            SpawnAt(parent, prefab, pos, scale * (0.85f + (i % 5) * 0.06f), yaw);
        }
    }

    static void SpawnScatter(Transform parent, GameObject[] prefabs, int count, float rMin, float rMax, float y, float scale)
    {
        if (prefabs == null) return;
        var rng = new System.Random(2026 + count * 13);
        for (int i = 0; i < count; i++)
        {
            var prefab = prefabs[i % prefabs.Length];
            if (prefab == null) continue;
            float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
            float r = Mathf.Lerp(rMin, rMax, (float)rng.NextDouble());
            var pos = new Vector3(Mathf.Sin(ang) * r, y, Mathf.Cos(ang) * r);
            if (InsideHouseFootprint(pos, 1.4f)) continue;
            // Keep porch lane clear
            if (pos.z < -HouseD * 0.5f - 0.4f && Mathf.Abs(pos.x) < 2.2f)
                continue;
            SpawnAt(parent, prefab, pos, scale * (0.8f + (float)rng.NextDouble() * 0.5f), (float)rng.NextDouble() * 360f);
        }
    }

    static void SpawnAt(Transform parent, GameObject prefab, Vector3 localPos, float scale, float yaw = 0f)
    {
        if (prefab == null) return;
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        go.transform.localScale = Vector3.one * scale;
    }

    static bool InsideHouseFootprint(Vector3 pos, float margin)
    {
        return Mathf.Abs(pos.x) < HouseW * 0.5f + margin
               && Mathf.Abs(pos.z) < HouseD * 0.5f + margin;
    }

    static void SpawnFlatGroundRing(Transform parent, GameObject[] prefabs, int count, float radius, float scale)
    {
        if (prefabs == null || prefabs.Length == 0) return;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)count;
            float ang = t * Mathf.PI * 2f;
            var prefab = prefabs[i % prefabs.Length];
            if (prefab == null) continue;
            var pos = new Vector3(Mathf.Sin(ang) * radius, -0.04f, Mathf.Cos(ang) * radius);
            if (InsideHouseFootprint(pos, 2.2f)) continue;
            float s = scale * (0.85f + (i % 5) * 0.05f);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, t * 180f + i * 13f, 0f);
            // Squash the nature-pack mounds so they stay as a carpet, not hills.
            go.transform.localScale = new Vector3(s, 0.045f, s);
        }
    }

    static void CullYardOverlappingHouse(GameObject yard)
    {
        float x0 = -HouseW * 0.5f - 0.2f;
        float x1 = HouseW * 0.5f + 0.2f;
        float z0 = -HouseD * 0.5f - 0.2f;
        float z1 = HouseD * 0.5f + 0.2f;
        float y0 = -FloorH - 0.6f;
        float y1 = FloorH * 2f + 1.2f;

        var kill = new List<GameObject>();
        for (int i = 0; i < yard.transform.childCount; i++)
        {
            var child = yard.transform.GetChild(i);
            if (child.name.StartsWith("Path_")) continue;
            // Porch plantings sit south of the front wall on purpose.
            if (child.localPosition.z < -HouseD * 0.5f - 0.35f)
                continue;

            var rs = child.GetComponentsInChildren<Renderer>(true);
            if (rs == null || rs.Length == 0) continue;
            var b = rs[0].bounds;
            for (int r = 1; r < rs.Length; r++)
                b.Encapsulate(rs[r].bounds);

            bool overlap = b.min.x < x1 && b.max.x > x0
                           && b.min.z < z1 && b.max.z > z0
                           && b.min.y < y1 && b.max.y > y0;
            if (overlap)
                kill.Add(child.gameObject);
        }
        foreach (var go in kill)
            Object.DestroyImmediate(go);
    }

    static void ConvertNaturePackToUrp()
    {
        ConvertMatToUrp("Assets/SimpleNaturePack/Materials/SimpleNaturePack_Texture_01.mat");
        ConvertMatToUrp("Assets/SimpleNaturePack/Materials/SimpleNaturePack_BG.mat");
    }

    static void ConvertMatToUrp(string path)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) return;
        var urp = Shader.Find("Universal Render Pipeline/Lit");
        if (urp == null || mat.shader == urp) return;
        Texture albedo = null;
        if (mat.HasProperty("_MainTex")) albedo = mat.GetTexture("_MainTex");
        if (albedo == null && mat.HasProperty("_BaseMap")) albedo = mat.GetTexture("_BaseMap");
        Texture bump = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
        Color tint = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
        mat.shader = urp;
        if (mat.HasProperty("_BaseMap") && albedo != null) mat.SetTexture("_BaseMap", albedo);
        if (mat.HasProperty("_MainTex") && albedo != null) mat.SetTexture("_MainTex", albedo);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
        if (bump != null && mat.HasProperty("_BumpMap"))
        {
            mat.SetTexture("_BumpMap", bump);
            mat.EnableKeyword("_NORMALMAP");
        }
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.14f);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(mat);
    }

    static Material MakeUrpYardMaterial(Material src)
    {
        var urp = Shader.Find("Universal Render Pipeline/Lit");
        var copy = new Material(urp != null ? urp : src.shader) { name = src.name + " (yard)" };
        Texture albedo = null;
        if (src.HasProperty("_BaseMap")) albedo = src.GetTexture("_BaseMap");
        if (albedo == null && src.HasProperty("_MainTex")) albedo = src.GetTexture("_MainTex");
        if (copy.HasProperty("_BaseMap") && albedo != null) copy.SetTexture("_BaseMap", albedo);
        if (copy.HasProperty("_MainTex") && albedo != null) copy.SetTexture("_MainTex", albedo);
        Color tint = Color.white;
        if (src.HasProperty("_BaseColor")) tint = src.GetColor("_BaseColor");
        else if (src.HasProperty("_Color")) tint = src.GetColor("_Color");
        if (copy.HasProperty("_BaseColor")) copy.SetColor("_BaseColor", tint);
        if (copy.HasProperty("_Color")) copy.SetColor("_Color", tint);
        if (src.HasProperty("_BumpMap") && copy.HasProperty("_BumpMap"))
        {
            var n = src.GetTexture("_BumpMap");
            if (n != null)
            {
                copy.SetTexture("_BumpMap", n);
                copy.EnableKeyword("_NORMALMAP");
            }
        }
        if (copy.HasProperty("_Smoothness")) copy.SetFloat("_Smoothness", 0.14f);
        if (copy.HasProperty("_Metallic")) copy.SetFloat("_Metallic", 0f);
        return copy;
    }

    /// <param name="floorHole">XZ rect of floor opening; null = solid.</param>
    /// <param name="ceilingHole">XZ rect of ceiling opening for stair pass-through.</param>
    static void BuildFloorShell(Transform parent, string tag, Material wall, Material floor, Material ceil,
        bool includeCeiling, Rect? floorHole, Rect? ceilingHole)
    {
        if (floorHole == null)
            Box(parent, tag + "_Floor", new Vector3(0f, -0.1f, 0f), new Vector3(HouseW, 0.2f, HouseD), floor);
        else
            BuildFloorWithHole(parent, tag + "_Floor", floorHole.Value, floor);

        if (includeCeiling)
        {
            if (ceilingHole == null)
                Box(parent, tag + "_Ceiling", new Vector3(0f, FloorH - 0.1f, 0f), new Vector3(HouseW, 0.2f, HouseD), ceil);
            else
                BuildSlabWithHole(parent, tag + "_Ceiling", FloorH - 0.1f, ceilingHole.Value, ceil);
        }

        float hw = HouseW * 0.5f;
        float hd = HouseD * 0.5f;
        float midY = FloorH * 0.5f - 0.1f;
        float wallH = FloorH - 0.2f;

        Box(parent, tag + "_Wall_N", new Vector3(0f, midY, hd), new Vector3(HouseW, wallH, WallT), wall);
        Box(parent, tag + "_Wall_E", new Vector3(hw, midY, 0f), new Vector3(WallT, wallH, HouseD), wall);
        Box(parent, tag + "_Wall_W", new Vector3(-hw, midY, 0f), new Vector3(WallT, wallH, HouseD), wall);

        float doorW = 2.2f;
        float side = (HouseW - doorW) * 0.5f;
        Box(parent, tag + "_Wall_S_L", new Vector3(-hw + side * 0.5f, midY, -hd),
            new Vector3(side, wallH, WallT), wall);
        Box(parent, tag + "_Wall_S_R", new Vector3(hw - side * 0.5f, midY, -hd),
            new Vector3(side, wallH, WallT), wall);
        Box(parent, tag + "_DoorHeader_S", new Vector3(0f, FloorH - 0.45f, -hd),
            new Vector3(doorW, 0.5f, WallT), wall);
    }

    static void BuildFloorWithHole(Transform parent, string name, Rect hole, Material mat) =>
        BuildSlabWithHole(parent, name, -0.1f, hole, mat);

    /// <summary>Four pads around an axis-aligned hole.</summary>
    static void BuildSlabWithHole(Transform parent, string name, float y, Rect hole, Material mat)
    {
        float hw = HouseW * 0.5f;
        float hd = HouseD * 0.5f;
        float x0 = -hw, x1 = hw, z0 = -hd, z1 = hd;
        float hx0 = hole.xMin, hx1 = hole.xMin + hole.width;
        float hz0 = hole.yMin, hz1 = hole.yMin + hole.height;

        float southD = hz0 - z0;
        if (southD > 0.15f)
            Box(parent, name + "_S", new Vector3(0f, y, (z0 + hz0) * 0.5f),
                new Vector3(HouseW, 0.2f, southD), mat);

        float northD = z1 - hz1;
        if (northD > 0.15f)
            Box(parent, name + "_N", new Vector3(0f, y, (hz1 + z1) * 0.5f),
                new Vector3(HouseW, 0.2f, northD), mat);

        float westW = hx0 - x0;
        float bandD = hz1 - hz0;
        if (westW > 0.15f && bandD > 0.15f)
            Box(parent, name + "_W", new Vector3((x0 + hx0) * 0.5f, y, (hz0 + hz1) * 0.5f),
                new Vector3(westW, 0.2f, bandD), mat);

        float eastW = x1 - hx1;
        if (eastW > 0.15f && bandD > 0.15f)
            Box(parent, name + "_E", new Vector3((hx1 + x1) * 0.5f, y, (hz0 + hz1) * 0.5f),
                new Vector3(eastW, 0.2f, bandD), mat);
    }

    static void BuildGroundRooms(Transform parent, Material wall, Material trim)
    {
        float midY = FloorH * 0.5f - 0.1f;
        float wallH = FloorH - 0.2f;
        float hd = HouseD * 0.5f;

        // Hall spine along Z through center (two walls with openings)
        // Living room west partition (x = -3.5) with doorway
        PartitionX(parent, "G_Part_Living", -3.5f, -hd + 0.5f, 4.5f, midY, wallH, wall, doorAtZ: -2f);
        // Kitchen east partition (x = 3.5)
        PartitionX(parent, "G_Part_Kitchen", 3.5f, -hd + 0.5f, 4.5f, midY, wallH, wall, doorAtZ: -1f);
        // Back hallway cross wall (z = 2) with two doorways
        PartitionZ(parent, "G_Part_Back", 2f, -HouseW * 0.5f + 0.5f, HouseW - 1f, midY, wallH, wall,
            doorAtX1: -5f, doorAtX2: 5f);
        // West of the east stair well — enter from the back hall
        PartitionX(parent, "G_Stair_Encl", WellEast.xMin - 0.08f, WellEast.yMin, HouseD * 0.5f - 0.5f, midY, wallH, wall,
            doorAtZ: WellEast.yMin + 1.3f);

        Label(parent, "Foyer", new Vector3(0f, 0.05f, -5f));
        Label(parent, "LivingRoom", new Vector3(-7f, 0.05f, -2f));
        Label(parent, "Kitchen", new Vector3(7f, 0.05f, -2f));
        Label(parent, "BackHall", new Vector3(0f, 0.05f, 5f));
        Label(parent, "Stairwell", new Vector3(8.5f, 0.05f, 5f));

        // Trim rails
        Box(parent, "G_Baseboard_N", new Vector3(0f, 0.15f, hd - 0.05f), new Vector3(HouseW - 0.4f, 0.25f, 0.08f), trim);
    }

    static void BuildUpperRooms(Transform parent, Material wall, Material trim)
    {
        float midY = FloorH * 0.5f - 0.1f;
        float wallH = FloorH - 0.2f;
        float hd = HouseD * 0.5f;

        // Central hall
        PartitionZ(parent, "U_Hall_Front", -2f, -HouseW * 0.5f + 0.5f, HouseW - 1f, midY, wallH, wall,
            doorAtX1: 0f, doorAtX2: 7f);
        PartitionX(parent, "U_Bed_Part", -3f, -2f, hd - 0.5f, midY, wallH, wall, doorAtZ: 2f);
        PartitionX(parent, "U_Study_Part", 3f, -2f, hd - 0.5f, midY, wallH, wall, doorAtZ: 3f);
        PartitionZ(parent, "U_Bath_Part", 4f, -3f, 6f, midY, wallH, wall, doorAtX1: -1f);

        Label(parent, "MasterBedroom", new Vector3(-7f, 0.05f, 3f));
        Label(parent, "Study", new Vector3(7f, 0.05f, 3f));
        Label(parent, "Bathroom", new Vector3(0f, 0.05f, 6f));
        Label(parent, "UpperHall", new Vector3(0f, 0.05f, -4f));
    }

    static void BuildBasementRooms(Transform parent, Material wall, Material trim)
    {
        float midY = FloorH * 0.5f - 0.1f;
        float wallH = FloorH - 0.2f;

        PartitionX(parent, "B_Part_Mid", 0f, -HouseD * 0.5f + 0.5f, HouseD - 1f, midY, wallH, wall, doorAtZ: 0f);
        PartitionZ(parent, "B_Part_Ritual", 3f, WellWest.xMax + 0.25f, -0.25f - (WellWest.xMax + 0.25f), midY, wallH, wall,
            doorAtX1: -4f);

        Label(parent, "Cellar", new Vector3(-6f, 0.05f, -2f));
        Label(parent, "BoilerRoom", new Vector3(6f, 0.05f, -2f));
        Label(parent, "RitualChamber", new Vector3(-5f, 0.05f, 5f));
    }

    static void BuildStairGroundToUpper(Transform ground, Transform upper, Material stairMat, Material wallMat)
    {
        float x = WellEast.xMin + WellEast.width * 0.5f;
        BuildStairFlight(ground, "Stairs_ToUpper", new Vector3(x, 0f, WellEast.yMin), WellEast, stairMat, wallMat);
        Label(ground, "StairsUp", new Vector3(x, 0.05f, WellEast.yMin - 0.4f));
        Label(upper, "StairsFromGround", new Vector3(x, 0.05f, WellEast.yMax + 0.6f));
    }

    static void BuildStairGroundToBasement(Transform ground, Transform basement, Material stairMat, Material wallMat)
    {
        float x = WellWest.xMin + WellWest.width * 0.5f;
        BuildStairFlight(basement, "Stairs_ToGround", new Vector3(x, 0f, WellWest.yMin), WellWest, stairMat, wallMat);
        Label(basement, "StairsBasement", new Vector3(x, 0.05f, WellWest.yMin - 0.4f));
        Label(ground, "StairsFromBasement", new Vector3(x, 0.05f, WellWest.yMax + 0.6f));
    }

    static void BuildStairFlight(Transform parent, string name, Vector3 origin, Rect well, Material stairMat, Material wallMat)
    {
        int steps = StairSteps;
        float stepH = FloorH / steps;
        float stepD = well.height / steps;
        float stepW = Mathf.Max(1.6f, well.width - 0.85f);
        var stairs = MakeEmpty(parent, name, origin).transform;
        BuildStraightStairs(stairs, steps, stepW, stepD, stepH, stairMat);
        float run = well.height;
        Box(stairs, "Rail_L", new Vector3(-stepW * 0.5f - 0.1f, FloorH * 0.45f, run * 0.5f),
            new Vector3(0.1f, FloorH * 0.9f, run), wallMat);
        Box(stairs, "Rail_R", new Vector3(stepW * 0.5f + 0.1f, FloorH * 0.45f, run * 0.5f),
            new Vector3(0.1f, FloorH * 0.9f, run), wallMat);
    }

    static void BuildStraightStairs(Transform parent, int stepCount, float stepW, float stepD, float stepH, Material mat)
    {
        for (int i = 0; i < stepCount; i++)
        {
            float y = stepH * (i + 0.5f);
            float z = stepD * (i + 0.5f);
            Box(parent, $"Step_{i}", new Vector3(0f, y, z), new Vector3(stepW, stepH, stepD), mat);
        }
        // Land just past the well on the destination floor, not inside the hole.
        float topY = stepH * stepCount;
        float topZ = stepD * stepCount;
        Box(parent, "Landing", new Vector3(0f, topY + 0.05f, topZ + 0.55f),
            new Vector3(stepW, 0.1f, 1.1f), mat);
    }

    static void PlaceStairWellGuards(Transform ground, Transform upper, Material wall)
    {
        void Rails(Transform parent, string tag, Rect well, bool openNorth)
        {
            float x0 = well.xMin, x1 = well.xMax, z0 = well.yMin, z1 = well.yMax;
            float h = 0.95f, t = 0.12f, y = h * 0.5f;
            Box(parent, tag + "_S", new Vector3((x0 + x1) * 0.5f, y, z0), new Vector3(x1 - x0 + t, h, t), wall);
            Box(parent, tag + "_E", new Vector3(x1, y, (z0 + z1) * 0.5f), new Vector3(t, h, z1 - z0), wall);
            Box(parent, tag + "_W", new Vector3(x0, y, (z0 + z1) * 0.5f), new Vector3(t, h, z1 - z0), wall);
            if (!openNorth)
                Box(parent, tag + "_N", new Vector3((x0 + x1) * 0.5f, y, z1), new Vector3(x1 - x0 + t, h, t), wall);
        }
        // Basement well is a hole in the ground floor — enter from the north landing.
        Rails(ground, "WellWest", WellWest, openNorth: true);
        // Upper well is a hole in the upper floor — enter from the north landing.
        Rails(upper, "WellEast", WellEast, openNorth: true);
    }

    static void PlaceLights(Transform basement, Transform ground, Transform upper)
    {
        MakePointLight(ground, "Light_Foyer", new Vector3(0f, 2.6f, -5f), new Color(1f, 0.75f, 0.45f), 1.1f, 10f);
        MakePointLight(ground, "Light_Living", new Vector3(-7f, 2.5f, -2f), new Color(1f, 0.55f, 0.3f), 0.7f, 9f);
        MakePointLight(ground, "Light_Kitchen", new Vector3(7f, 2.5f, -2f), new Color(0.7f, 0.85f, 1f), 0.55f, 8f);
        MakePointLight(ground, "Light_Stair", new Vector3(8.5f, 2.4f, 5f), new Color(1f, 0.7f, 0.4f), 0.8f, 8f);

        MakePointLight(upper, "Light_UpperHall", new Vector3(0f, 2.5f, -4f), new Color(0.9f, 0.7f, 0.5f), 0.65f, 9f);
        MakePointLight(upper, "Light_Bedroom", new Vector3(-7f, 2.4f, 3f), new Color(0.6f, 0.4f, 0.8f), 0.45f, 7f);
        MakePointLight(upper, "Light_Study", new Vector3(7f, 2.4f, 3f), new Color(0.5f, 0.65f, 1f), 0.5f, 7f);

        MakePointLight(basement, "Light_Cellar", new Vector3(-6f, 2.2f, -2f), new Color(0.4f, 0.7f, 0.45f), 0.35f, 7f);
        MakePointLight(basement, "Light_Ritual", new Vector3(-5f, 2.0f, 5f), new Color(0.85f, 0.15f, 0.1f), 0.55f, 8f);
        MakePointLight(basement, "Light_Boiler", new Vector3(6f, 2.2f, -2f), new Color(1f, 0.45f, 0.15f), 0.4f, 7f);
    }

    static void PlaceProps(Transform basement, Transform ground, Transform upper, Material trim, Material cobble)
    {
        // Simple furniture proxies (cubes) for haunted silhouette
        Box(ground, "Prop_Table", new Vector3(-7f, 0.4f, -2f), new Vector3(1.6f, 0.8f, 0.9f), trim);
        Box(ground, "Prop_Couch", new Vector3(-7f, 0.35f, 0.5f), new Vector3(2.4f, 0.7f, 0.9f), trim);
        Box(ground, "Prop_Counter", new Vector3(7f, 0.45f, -3f), new Vector3(2.5f, 0.9f, 0.7f), trim);
        Box(ground, "Prop_Shelf", new Vector3(0.5f, 1.2f, 6.5f), new Vector3(2f, 2.2f, 0.4f), trim);

        Box(upper, "Prop_Bed", new Vector3(-7.5f, 0.35f, 4f), new Vector3(2.2f, 0.55f, 1.8f), trim);
        Box(upper, "Prop_Desk", new Vector3(7f, 0.4f, 4.5f), new Vector3(1.4f, 0.75f, 0.7f), trim);
        Box(upper, "Prop_Bookcase", new Vector3(4.2f, 1.1f, -4.5f), new Vector3(0.4f, 2.2f, 1.8f), trim);

        Box(basement, "Prop_Crates", new Vector3(-6f, 0.5f, -3f), new Vector3(1.2f, 1f, 1.2f), trim);
        Box(basement, "Prop_Boiler", new Vector3(7f, 0.9f, -3f), new Vector3(1.5f, 1.8f, 1.2f), cobble != null ? cobble : trim);
        Box(basement, "Prop_Altar", new Vector3(-5f, 0.45f, 5.5f), new Vector3(1.4f, 0.9f, 0.8f), cobble != null ? cobble : trim);
    }

    static void PlaceInteriorDetails(Transform root, Transform basement, Transform ground, Transform upper, HouseMaterials mats)
    {
        float hw = HouseW * 0.5f;
        float hd = HouseD * 0.5f;

        // Kitchen tile overlay (east rooms) + living-room hearth (west)
        Box(ground, "Kitchen_Tile", new Vector3(7.2f, 0.03f, -2.2f), new Vector3(7.2f, 0.05f, 8.4f), mats.tile);
        Box(ground, "Hearth", new Vector3(-10.4f, 0.12f, -2f), new Vector3(0.7f, 0.24f, 2.4f), mats.porch);
        Box(ground, "Chimney", new Vector3(-10.55f, 1.35f, -2f), new Vector3(0.45f, 2.5f, 1.35f), mats.porch);

        // Wood wainscot on ground-floor outer walls
        float wy = 0.42f, wh = 0.84f, wt = 0.07f;
        Box(ground, "Wainscot_N", new Vector3(0f, wy, hd - WallT * 0.5f - wt * 0.5f), new Vector3(HouseW - WallT * 2f, wh, wt), mats.trim);
        Box(ground, "Wainscot_E", new Vector3(hw - WallT * 0.5f - wt * 0.5f, wy, 0f), new Vector3(wt, wh, HouseD - WallT * 2f), mats.trim);
        Box(ground, "Wainscot_W", new Vector3(-hw + WallT * 0.5f + wt * 0.5f, wy, 0f), new Vector3(wt, wh, HouseD - WallT * 2f), mats.trim);
        float doorW = 2.2f;
        float side = (HouseW - doorW) * 0.5f - WallT;
        Box(ground, "Wainscot_S_L", new Vector3(-hw + WallT + side * 0.5f, wy, -hd + WallT * 0.5f + wt * 0.5f),
            new Vector3(side, wh, wt), mats.trim);
        Box(ground, "Wainscot_S_R", new Vector3(hw - WallT - side * 0.5f, wy, -hd + WallT * 0.5f + wt * 0.5f),
            new Vector3(side, wh, wt), mats.trim);

        // Cobble foundation ring (outside the wallpaper walls)
        float ft = 0.45f;
        Box(root, "Foundation_N", new Vector3(0f, -0.12f, hd + 0.12f), new Vector3(HouseW + 0.4f, 0.45f, ft), mats.floorBasement);
        Box(root, "Foundation_S", new Vector3(0f, -0.12f, -hd - 0.12f), new Vector3(HouseW + 0.4f, 0.45f, ft), mats.floorBasement);
        Box(root, "Foundation_E", new Vector3(hw + 0.12f, -0.12f, 0f), new Vector3(ft, 0.45f, HouseD), mats.floorBasement);
        Box(root, "Foundation_W", new Vector3(-hw - 0.12f, -0.12f, 0f), new Vector3(ft, 0.45f, HouseD), mats.floorBasement);

        // Basement cobble plinth + upper wood runner
        Box(basement, "Plinth_N", new Vector3(0f, 0.18f, hd - 0.22f), new Vector3(HouseW - 0.8f, 0.36f, 0.12f), mats.floorBasement);
        Box(upper, "Runner", new Vector3(0f, 0.03f, -3.5f), new Vector3(3.2f, 0.04f, 8.5f), mats.floor);
    }

    // --- helpers ---

    static void PartitionX(Transform parent, string name, float x, float zStart, float zEnd, float midY, float wallH,
        Material mat, float? doorAtZ = null)
    {
        float z0 = Mathf.Min(zStart, zEnd);
        float z1 = Mathf.Max(zStart, zEnd);
        float len = z1 - z0;
        if (doorAtZ == null)
        {
            Box(parent, name, new Vector3(x, midY, (z0 + z1) * 0.5f), new Vector3(WallT, wallH, len), mat);
            return;
        }
        float doorZ = doorAtZ.Value;
        float doorD = 2.0f;
        // below door
        float a0 = z0;
        float a1 = doorZ - doorD * 0.5f;
        if (a1 > a0 + 0.1f)
            Box(parent, name + "_A", new Vector3(x, midY, (a0 + a1) * 0.5f), new Vector3(WallT, wallH, a1 - a0), mat);
        // above door
        float b0 = doorZ + doorD * 0.5f;
        float b1 = z1;
        if (b1 > b0 + 0.1f)
            Box(parent, name + "_B", new Vector3(x, midY, (b0 + b1) * 0.5f), new Vector3(WallT, wallH, b1 - b0), mat);
        // header
        Box(parent, name + "_Hdr", new Vector3(x, FloorH - 0.45f, doorZ), new Vector3(WallT, 0.5f, doorD), mat);
    }

    static void PartitionZ(Transform parent, string name, float z, float xStart, float width, float midY, float wallH,
        Material mat, float? doorAtX1 = null, float? doorAtX2 = null)
    {
        float x0 = xStart;
        float x1 = xStart + width;
        var doors = new List<float>();
        if (doorAtX1.HasValue) doors.Add(doorAtX1.Value);
        if (doorAtX2.HasValue) doors.Add(doorAtX2.Value);
        doors.Sort();

        float cursor = x0;
        float doorW = 2.0f;
        int i = 0;
        foreach (var dx in doors)
        {
            float segEnd = dx - doorW * 0.5f;
            if (segEnd > cursor + 0.1f)
                Box(parent, name + "_S" + i, new Vector3((cursor + segEnd) * 0.5f, midY, z),
                    new Vector3(segEnd - cursor, wallH, WallT), mat);
            Box(parent, name + "_Hdr" + i, new Vector3(dx, FloorH - 0.45f, z),
                new Vector3(doorW, 0.5f, WallT), mat);
            cursor = dx + doorW * 0.5f;
            i++;
        }
        if (x1 > cursor + 0.1f)
            Box(parent, name + "_S" + i, new Vector3((cursor + x1) * 0.5f, midY, z),
                new Vector3(x1 - cursor, wallH, WallT), mat);
    }

    static GameObject MakeEmpty(Transform parent, string name, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        return go;
    }

    static GameObject Box(Transform parent, string name, Vector3 localPos, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = size;
        var r = go.GetComponent<Renderer>();
        if (r != null && mat != null)
            r.sharedMaterial = mat;
        return go;
    }

    static void Label(Transform parent, string name, Vector3 localPos)
    {
        var go = new GameObject("Zone_" + name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
    }

    static void MakePointLight(Transform parent, string name, Vector3 localPos, Color color, float intensity, float range)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.intensity = intensity;
        light.range = range;
        light.shadows = LightShadows.Soft;
    }

    static Material MakeMat(string name, Color color)
    {
        // Prefer URP Lit if present
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var mat = new Material(shader) { name = name };
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.15f);
        return mat;
    }
}
#endif
