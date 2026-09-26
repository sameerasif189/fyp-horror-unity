#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// Builds a 3-level haunted house (basement / ground / upper) with stairs and room traversal.
/// Menu: FYP → Build Haunted House
/// </summary>
public static class HauntedHouseBuilder
{
    const float FloorH = 3.2f;
    /// <summary>Taller first floor so well curbs are jumpable and rooms feel less cramped.</summary>
    const float UpperFloorH = 4.4f;
    const float WallT = 0.35f;
    /// <summary>Pad so partition ends bury into crossing walls / outer shell.</summary>
    const float CornerPad = WallT;
    /// <summary>Jumpable well curb (player jumpHeight is 1.2m).</summary>
    const float JumpCurbH = 0.55f;
    /// <summary>
    /// Ground-floor guards around the basement stairwell: as tall as can still be jumped reliably. Under the 3.0 m
    /// ceiling the 2 m capsule's feet only reach 0.92 m (PlayerController stops the rise at the ceiling). Simulated with
    /// the player's CharacterController at 30/60/144 fps, walking and sprinting: 0.90 m cleared from every take-off
    /// distance, 0.95 m about half the time, and the old 1.05 m never (26 Sep 2026).
    /// 0.85, not 0.90: at 0.89-0.95 m the NavMesh bake cuts the 1.21 m corridor north of the well (between
    /// G_WellWest_E's end and Wainscot_N), so monsters could climb from the basement but never leave the stair-top
    /// landing. 0.78-0.88 m and 1.05 m bake connected; 0.85 sits clear of the band and still needs a jump.
    /// </summary>
    const float BasementJumpCurbH = 0.85f;
    /// <summary>
    /// Visual-only size multiplier for monsters. Does NOT affect the CapsuleCollider or the
    /// NavMeshAgent, so navigation and collision are unchanged. At 1.4 the tallest (Illiakan)
    /// reads ~2.45m against 3.2m of basement headroom, so nothing clips the ceiling.
    /// </summary>
    const float MonsterVisualScale = 1.4f;
    /// <summary>
    /// Curb height at the north stair mouths. These flank the walking lane the player exits the
    /// stairs through, so they MUST be steppable - at JumpCurbH they sat exactly on the lane edge
    /// and caught the player's capsule (radius 0.4 + 0.08 skin), forcing the reported "squeeze"
    /// at the corner on both staircases. Keep this comfortably under CharacterController.stepOffset.
    /// </summary>
    const float MouthCurbH = 0.22f;
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
            ceilingHole: WellWest,
            southDoor: false);
        BuildFloorShell(groundFl, "G", wallMat, mats.floor, ceilMat, includeCeiling: true,
            floorHole: WellWest,
            ceilingHole: WellEast);
        BuildFloorShell(upper, "U", wallMat, mats.floorUpper, ceilMat, includeCeiling: true,
            floorHole: WellEast,
            ceilingHole: null,
            southDoor: true,
            storyHeight: UpperFloorH);
        // Ground ceiling sits at the same world Y as the upper floor. That double slab is
        // unique to this well (basement stairs only have one floor rim) and snags descent.
        // Keep the ceiling mesh but open the collider in the stair column like a second hole.
        DisableCeilingRimColliders(groundFl, "G_Ceiling", WellEast);
        // Front (south) boundary gap → translucent window (not a doorway).
        PlaceUpperFrontWindow(upper, mats.wall, mats.glass);

        // Interior partitions + doorways
        BuildBasementRooms(basement, wallMat, trimMat);
        BuildGroundRooms(groundFl, wallMat, trimMat);
        BuildUpperRooms(upper, wallMat, trimMat);

        // Stair wells (cut floors + stair meshes)
        BuildStairGroundToUpper(groundFl, upper, stairMat, wallMat);
        BuildStairGroundToBasement(groundFl, basement, stairMat, wallMat);
        // Continuous shafts so looking up/down the stairs shows sealed walls, not ceiling gaps.
        EncloseBasementStairBack(basement, groundFl, wallMat);
        EncloseUpperStairShaft(groundFl, upper, wallMat);
        PlaceStairWellGuards(groundFl, upper, wallMat);
        PlaceBasementChaseZones(basement);
        PlaceFallRecovery(basement);

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
            // Match original SampleScene capsule (center Y=0 so camera at local 0.6 ≈ eye height).
            var cc = player.GetComponent<CharacterController>();
            if (cc != null)
            {
                Undo.RecordObject(cc, "Restore original player capsule");
                cc.height = 2f;
                cc.radius = 0.4f;
                cc.center = Vector3.zero;
                // 0.45, not 0.3: at 0.3 the capsule caught on thresholds and stair-mouth
                // curbs. Still below JumpCurbH (0.55) so the deliberate jump curbs remain
                // jump-only.
                cc.stepOffset = 0.45f;
                cc.slopeLimit = 45f;
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
        WireMonstersAndNavMesh(root);

        Selection.activeGameObject = root;
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[HauntedHouse] Built 3-floor haunted house with monsters + NavMesh. Player at foyer.");
    }

    struct HouseMaterials
    {
        public Material wall, floor, floorUpper, floorBasement, ceiling, trim, stair, porch, tile, glass;
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
        var glass = MakeGlassMat(folder + "/HH_Glass.mat");

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
            glass = glass,
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

    static Material MakeGlassMat(string path)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader) { name = "HH_Glass" };
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = shader;
            mat.name = "HH_Glass";
        }

        var tint = new Color(0.55f, 0.72f, 0.85f, 0.28f);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.92f);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.05f);

        // Transparent surface so the stair well reads as a window.
        mat.SetFloat("_Surface", 1f); // 0 opaque, 1 transparent (URP)
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.EnableKeyword("_TRANSPARENT_ON");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
        EditorUtility.SetDirty(mat);
        return mat;
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

    /// <summary>
    /// Force the GLB's up-axis node to a rotation confirmed correct in game. Does nothing when
    /// spec.visualEuler is null, so unconfirmed models keep exactly what the importer produced.
    /// </summary>
    static void ApplyVisualEuler(Transform visual, MonsterSpawnSpec spec)
    {
        if (!spec.visualEuler.HasValue) return;
        // glTF importers park the up-axis correction on a single child: 'Sketchfab_model' for
        // Sketchfab exports, 'root' for the others.
        Transform node = visual.Find("Sketchfab_model") ?? visual.Find("root");
        if (node == null && visual.childCount > 0) node = visual.GetChild(0);
        if (node == null) return;
        node.localEulerAngles = spec.visualEuler.Value;
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

    struct MonsterSpawnSpec
    {
        public string glbPath;
        public string controllerPath; // null => EntityIdleMotion fallback
        public float scale;
        public float agentRadius;
        public float agentHeight;
        public string name;

        /// <summary>
        /// Known-good local euler for the GLB's up-axis node, CONFIRMED IN GAME. Null means leave
        /// whatever the importer produced.
        ///
        /// These are per-model on purpose and must NOT be normalised to a shared value: the
        /// importer gives Velociraptor X=270 and Illiakan X=90, and both are correct for their own
        /// model. Assuming they should match and flipping one to match the other turned a working
        /// monster upside down. Only fill this in after seeing the model standing correctly.
        /// </summary>
        public Vector3? visualEuler;
    }

    static void WireMonstersAndNavMesh(GameObject houseRoot)
    {
        var existingMonsters = GameObject.Find("HauntedMonsters");
        if (existingMonsters != null)
            Object.DestroyImmediate(existingMonsters);

        var monstersRoot = new GameObject("HauntedMonsters");
        Undo.RegisterCreatedObjectUndo(monstersRoot, "Create HauntedMonsters");

        MonsterSpawnSpec[] specs =
        {
            new MonsterSpawnSpec
            {
                name = "Illiakan",
                glbPath = "Assets/FYP/Entities/illiakan_v1.glb",
                controllerPath = "Assets/FYP/Entities/illiakan_v1.controller",
                scale = 1f,
                agentRadius = 0.4f,
                agentHeight = 1.9f,
                visualEuler = new Vector3(90f, 0f, 0f)    // confirmed upright in game 24 Sep 2026
            },
            // Velociraptor (idle only), DistortusRex and CameramanFred (no clips) were removed 25 Sep 2026 after
            // Tillagemon was verified in game - see PROJECT_MILESTONES.md.
            new MonsterSpawnSpec
            {
                // Replacement (25 Sep 2026): the only downloaded model with a rig, textures AND walk/run clips.
                // FBX (Sketchfab original) with 21 takes; tillagemon.controller blends Battle_Stand / Walk / Run on
                // MonsterAgent's Speed (0 / 2.2 patrol / 4.4 chase). A hunched crawler with a timber strapped
                // across its back - about 1.6x wider than tall - so it is kept a little smaller than the others.
                name = "Tillagemon",
                glbPath = "Assets/FYP/Entities/Tillagemon/TillagemonBoss01_Skeleton.fbx",
                controllerPath = "Assets/FYP/Entities/Tillagemon/tillagemon.controller",
                // 0.85 -> 1.36 (+60%, 26 Sep 2026): animated it stood 1.39 m tall, 1.59 m wide; now ~2.2 m x 2.5 m -
                // under every door header (2.4-2.5 m) and ceiling (3.0 m), wider only than the 2.2 / 2.4 m doors.
                scale = 1.36f,
                agentRadius = 0.55f,
                agentHeight = 1.8f,
                visualEuler = null   // upright as imported
            },
            // Added 26 Sep 2026 (Sketchfab, see PROJECT_MILESTONES.md M2 for sources and CC-BY credits). Each .prefab
            // wraps the import in a child normalised to 1.8 m of skeleton and centred on the agent, with bounds the
            // height fit can trust (the imports ranged from 1 cm to 36 m). Walk/run clips all play in place. With the
            // fit, the visual stands 2.39 m x scale tall.
            new MonsterSpawnSpec
            {
                // Pale crawler on all fours (walk/run are quadruped; its upright clips are unused). ~1.7 m crouched.
                // The eye mesh imports 6x too big and floating, so the prefab drops it; the body texture paints the sockets.
                name = "TheRake",
                glbPath = "Assets/FYP/Entities/TheRake/TheRake.prefab",
                controllerPath = "Assets/FYP/Entities/TheRake/therake.controller",
                scale = 0.56f,   // 0.7 -> 0.56 (-20%, user, 26 Sep 2026): ~1.35 m crouched
                agentRadius = 0.55f,
                agentHeight = 1.8f,
                visualEuler = null
            },
            new MonsterSpawnSpec
            {
                // Silent Hill 2 fan model. No run clip: the controller plays the walk 1.8x faster when chasing.
                name = "LyingFigure",
                glbPath = "Assets/FYP/Entities/LyingFigure/LyingFigure.prefab",
                controllerPath = "Assets/FYP/Entities/LyingFigure/lyingfigure.controller",
                scale = 0.92f,
                agentRadius = 0.4f,
                agentHeight = 1.8f,
                visualEuler = null
            },
            new MonsterSpawnSpec
            {
                // Trevor Henderson fan model; black with long clawed arms. ~2.6 m, under the ~3 m door headers.
                name = "MorningWalk",
                glbPath = "Assets/FYP/Entities/MorningWalk/MorningWalk.prefab",
                controllerPath = "Assets/FYP/Entities/MorningWalk/morningwalk.controller",
                scale = 1.09f,
                agentRadius = 0.45f,
                agentHeight = 1.8f,
                visualEuler = null
            },
            new MonsterSpawnSpec
            {
                // Backrooms "Partygoer" (entity 67). Up to 2.56 m tall animated at 1.0; 1.15 (26 Sep 2026) takes it to
                // ~2.95 m, the most that stays under the 3.0 m ground-floor and basement ceilings (+40% would be 3.6 m).
                name = "Partygoer",
                glbPath = "Assets/FYP/Entities/Partygoer/Partygoer.prefab",
                controllerPath = "Assets/FYP/Entities/Partygoer/partygoer.controller",
                scale = 1.15f,
                agentRadius = 0.45f,
                agentHeight = 1.8f,
                visualEuler = null
            },
            new MonsterSpawnSpec
            {
                // A hand that walks on its fingers, with a haloed figure rising from it. Wide (about as wide as it is
                // tall), so it is kept to ~1.8 m to clear the 2.2 m doors. Idle is its "prayer" pose.
                name = "FingerMaiden",
                glbPath = "Assets/FYP/Entities/FingerMaiden/FingerMaiden.prefab",
                controllerPath = "Assets/FYP/Entities/FingerMaiden/fingermaiden.controller",
                scale = 0.75f,
                agentRadius = 0.6f,
                agentHeight = 1.8f,
                visualEuler = null
            },
        };

        var agents = new List<MonsterAgent>();
        var westSpawn = GameObject.Find("BasementSpawn_West");
        Vector3 park = westSpawn != null
            ? westSpawn.transform.position
            : new Vector3(-8.5f, -FloorH + 0.1f, -5.7f);

        for (int i = 0; i < specs.Length; i++)
        {
            var agent = CreateMonsterAgent(monstersRoot.transform, specs[i], park + Vector3.right * (i * 0.15f));
            if (agent != null)
                agents.Add(agent);
        }

        var hs = GameObject.Find("HorrorSystems");
        if (hs == null)
        {
            hs = new GameObject("HorrorSystems");
            Undo.RegisterCreatedObjectUndo(hs, "Create HorrorSystems");
        }

        var director = hs.GetComponent<MonsterDirector>() ?? hs.AddComponent<MonsterDirector>();
        director.BindMonsters(agents.ToArray());
        EditorUtility.SetDirty(hs);
        EditorUtility.SetDirty(director);

        BakeHouseNavMesh(houseRoot);
        Debug.Log($"[HauntedHouse] Wired {agents.Count} monsters + MonsterDirector + NavMeshSurface bake.");
    }

    static MonsterAgent CreateMonsterAgent(Transform parent, MonsterSpawnSpec spec, Vector3 parkPos)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(spec.glbPath);
        if (prefab == null)
        {
            Debug.LogWarning($"[HauntedHouse] Missing monster asset: {spec.glbPath}");
            return null;
        }

        var root = new GameObject(spec.name);
        root.transform.SetParent(parent, false);
        root.transform.position = parkPos;

        var visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        visual.name = "Visual";
        visual.transform.SetParent(root.transform, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        visual.transform.localScale = Vector3.one;

        float fitted = FitMonsterScale(visual, targetHeight: Mathf.Max(1.4f, spec.agentHeight * 0.95f));
        // MonsterVisualScale applies to the VISUAL ONLY. The CapsuleCollider and NavMeshAgent
        // below keep spec.agentHeight / spec.agentRadius, so the monster reads twice as big
        // without its hitbox growing - it still fits every doorway and corridor it did before.
        float scale = Mathf.Max(0.01f, spec.scale * fitted * MonsterVisualScale);
        visual.transform.localScale = Vector3.one * scale;
        ApplyVisualEuler(visual.transform, spec);
        // Seating the model on its feet is done at runtime by MonsterAgent.SeatVisualOnGround,
        // not here: outside play mode a SkinnedMeshRenderer reports bind-pose bounds, which are
        // far enough from the animated pose that a baked offset would be wrong in game.
        // Parked monsters may therefore look sunk in the Scene view; they seat on spawn.

        // Prefer root capsule for NavMeshAgent; disable mesh colliders that fight the agent.
        foreach (var col in root.GetComponentsInChildren<Collider>(true))
            Object.DestroyImmediate(col);

        var capsule = root.AddComponent<CapsuleCollider>();
        capsule.height = spec.agentHeight;
        capsule.radius = spec.agentRadius;
        // Trigger, not solid. The NavMeshAgent moves this transform directly with no Rigidbody, so a
        // solid capsule is a teleporting static collider: on the basement stairs it was placed inside
        // the player, whose CharacterController depenetrated DOWN through the 0.18m ramp and ended up
        // under the stairs. Nothing needs it solid - MonsterAgent's LOS Linecast ignores triggers.
        capsule.isTrigger = true;
        capsule.center = new Vector3(0f, spec.agentHeight * 0.5f, 0f);

        var agent = root.AddComponent<NavMeshAgent>();
        agent.radius = spec.agentRadius;
        agent.height = spec.agentHeight;
        agent.baseOffset = 0f;
        agent.speed = 2.2f;
        agent.angularSpeed = 220f;
        agent.acceleration = 10f;
        agent.stoppingDistance = 1.35f;
        agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;

        var animator = visual.GetComponentInChildren<Animator>(true);
        if (!string.IsNullOrEmpty(spec.controllerPath))
        {
            var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(spec.controllerPath);
            if (ctrl != null)
            {
                if (animator == null)
                    animator = visual.AddComponent<Animator>();
                animator.runtimeAnimatorController = ctrl;
                animator.applyRootMotion = false;
            }
        }

        if (animator == null || animator.runtimeAnimatorController == null)
        {
            if (visual.GetComponent<EntityIdleMotion>() == null)
                visual.AddComponent<EntityIdleMotion>();
        }

        // M4 component is optional; attach without editing its source.
        if (root.GetComponent<MonsterCorruptionController>() == null)
            root.AddComponent<MonsterCorruptionController>();

        var monster = root.AddComponent<MonsterAgent>();
        if (root.activeSelf)
            root.SetActive(false);
        EditorUtility.SetDirty(root);
        return monster;
    }

    static float FitMonsterScale(GameObject go, float targetHeight)
    {
        var rends = go.GetComponentsInChildren<Renderer>(true);
        if (rends == null || rends.Length == 0) return 1f;
        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++)
            b.Encapsulate(rends[i].bounds);
        float h = b.size.y;
        if (h < 0.05f) return 1f;
        return Mathf.Clamp(targetHeight / h, 0.05f, 8f);
    }

    static void BakeHouseNavMesh(GameObject houseRoot)
    {
        if (houseRoot == null) return;

        var surface = houseRoot.GetComponent<NavMeshSurface>();
        if (surface == null)
            surface = houseRoot.AddComponent<NavMeshSurface>();

        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        surface.layerMask = ~0;
        surface.defaultArea = 0;
        surface.overrideVoxelSize = true;
        // 0.06, not 0.12. At 0.12 the voxeliser could not resolve the ~0.20m lips around the
        // basement stair mouth (B_StairHall_E / B_StairBack_NE / the mouth curbs), so the stair
        // shaft baked as an ISLAND: the monster could climb the stairs and then never leave the
        // stairwell, which read in game as "chases to the top of the stairs, then turns back".
        // Needs the G_WellWest_W/E trim above as well - either fix alone still bakes an island.
        surface.voxelSize = 0.06f;
        surface.overrideTileSize = true;
        surface.tileSize = 64;
        // 6, not the default 2. The finer 0.06 voxel size above also generates navmesh inside
        // sealed dead pockets that 0.12 was too coarse to see - notably the NE corner behind
        // B_Baffle_E_B / B_Baffle_N_S2 (3.3 m^2) and a similar one in the SW corner, ~9.8 m^2 of
        // unreachable island in total. A monster spawned into one was trapped behind a wall.
        // minRegionArea culls disconnected regions below the threshold; 6 is the smallest value
        // that clears every pocket while keeping all 10 chase routes PathComplete.
        surface.minRegionArea = 6f;
        surface.BuildNavMesh();
        EditorUtility.SetDirty(houseRoot);
        EditorUtility.SetDirty(surface);
        VerifyMonsterRoutes();
    }

    /// <summary>
    /// After every bake: each monster spawn must be able to walk to each patrol point, on all three floors. A broken
    /// route is what "monsters can't go up the stairs" looks like in game, and small geometry changes near the stair
    /// mouths have caused it more than once (see BasementJumpCurbH), so it is reported loudly here, not found in play.
    /// </summary>
    static void VerifyMonsterRoutes()
    {
        var zones = Object.FindObjectsByType<HouseZone>(FindObjectsInactive.Include);
        var spawns = new List<HouseZone>();
        var targets = new List<HouseZone>();
        foreach (var z in zones)
        {
            if (z.Kind == HouseZone.ZoneKind.MonsterSpawn) spawns.Add(z);
            else if (z.Kind == HouseZone.ZoneKind.Patrol) targets.Add(z);
        }
        int total = 0;
        var broken = new List<string>();
        var path = new NavMeshPath();
        foreach (var a in spawns)
        foreach (var b in targets)
        {
            total++;
            bool ok = NavMesh.SamplePosition(a.transform.position, out var ha, 1.5f, NavMesh.AllAreas)
                      && NavMesh.SamplePosition(b.transform.position, out var hb, 1.5f, NavMesh.AllAreas)
                      && NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, path)
                      && path.status == NavMeshPathStatus.PathComplete;
            if (!ok) broken.Add($"{a.name} -> {b.name}");
        }
        if (broken.Count > 0)
            Debug.LogError($"[HauntedHouse] NavMesh: {broken.Count} of {total} monster routes are broken (spawn -> patrol): {string.Join(", ", broken)}");
        else
            Debug.Log($"[HauntedHouse] NavMesh: all {total} monster routes complete ({spawns.Count} spawns x {targets.Count} patrol points).");
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
            // Porch plantings sit south of the front wall on purpose. The south ground pads are the front yard and are
            // kept too, but they reach far under the house: sink them below the floor instead (see SinkUnderHouseFloor).
            if (child.localPosition.z < -HouseD * 0.5f - 0.35f)
            {
                if (child.name.StartsWith("Ground_")) SinkUnderHouseFloor(child, x0, x1, z0, z1);
                continue;
            }

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

    /// <summary>
    /// A south ground pad (18-22 m out, 30-47 m across) reaches ~10 m under the house. Even squashed to 0.045, Ground_02's
    /// mounds rise 0.21 m above the 0.0 ground floor, and they showed through the two rooms beside the entrance as green
    /// patches (26 Sep 2026). If the pad overlaps the footprint, flatten it and drop its top to just under the floor. It
    /// stays as the front yard, slightly flatter.
    /// </summary>
    static void SinkUnderHouseFloor(Transform pad, float x0, float x1, float z0, float z1)
    {
        var rs = pad.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return;
        Bounds B()
        {
            var b = rs[0].bounds;
            for (int r = 1; r < rs.Length; r++) b.Encapsulate(rs[r].bounds);
            return b;
        }
        var bb = B();
        bool under = bb.min.x < x1 && bb.max.x > x0 && bb.min.z < z1 && bb.max.z > z0;
        if (!under || bb.max.y <= -0.02f) return;
        var sc = pad.localScale;
        pad.localScale = new Vector3(sc.x, Mathf.Min(sc.y, 0.01f), sc.z);
        pad.position += Vector3.up * (-0.02f - B().max.y);
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
        bool includeCeiling, Rect? floorHole, Rect? ceilingHole, bool southDoor = true, float storyHeight = -1f)
    {
        float H = storyHeight > 0f ? storyHeight : FloorH;
        if (floorHole == null)
            Box(parent, tag + "_Floor", new Vector3(0f, -0.1f, 0f), new Vector3(HouseW, 0.2f, HouseD), floor);
        else
            BuildFloorWithHole(parent, tag + "_Floor", floorHole.Value, floor);

        if (includeCeiling)
        {
            if (ceilingHole == null)
                Box(parent, tag + "_Ceiling", new Vector3(0f, H - 0.1f, 0f), new Vector3(HouseW, 0.2f, HouseD), ceil);
            else
                BuildSlabWithHole(parent, tag + "_Ceiling", H - 0.1f, ceilingHole.Value, ceil);
        }

        float hw = HouseW * 0.5f;
        float hd = HouseD * 0.5f;
        float midY = H * 0.5f - 0.1f;
        float wallH = H - 0.2f;

        // Outer shell: NS walls extend through EW thickness (and vice versa) so
        // corners are a solid WallT×WallT overlap, not a knife-edge seam.
        float nsLen = HouseW + WallT;
        float ewLen = HouseD + WallT;
        Box(parent, tag + "_Wall_N", new Vector3(0f, midY, hd), new Vector3(nsLen, wallH, WallT), wall);
        Box(parent, tag + "_Wall_E", new Vector3(hw, midY, 0f), new Vector3(WallT, wallH, ewLen), wall);
        Box(parent, tag + "_Wall_W", new Vector3(-hw, midY, 0f), new Vector3(WallT, wallH, ewLen), wall);

        if (!southDoor)
        {
            Box(parent, tag + "_Wall_S", new Vector3(0f, midY, -hd),
                new Vector3(nsLen, wallH, WallT), wall);
        }
        else
        {
            float doorW = 2.2f;
            float side = (HouseW - doorW) * 0.5f + CornerPad;
            Box(parent, tag + "_Wall_S_L", new Vector3(-hw + side * 0.5f - CornerPad * 0.5f, midY, -hd),
                new Vector3(side, wallH, WallT), wall);
            Box(parent, tag + "_Wall_S_R", new Vector3(hw - side * 0.5f + CornerPad * 0.5f, midY, -hd),
                new Vector3(side, wallH, WallT), wall);
            Box(parent, tag + "_DoorHeader_S", new Vector3(0f, H - 0.45f, -hd),
                new Vector3(doorW, 0.5f, WallT), wall);
        }
    }

    /// <summary>
    /// Fills the first-floor south (front) boundary opening with a see-through
    /// but solid glass window — the gap left by the door-style shell cut.
    /// </summary>
    static void PlaceUpperFrontWindow(Transform upper, Material wall, Material glass)
    {
        float H = UpperFloorH;
        float hd = HouseD * 0.5f;
        const float windowW = 2.2f;
        // Match BuildFloorShell south door opening: header sits at H-0.45.
        float headerBottom = H - 0.7f;
        float sillH = 0.9f;
        float glassH = Mathf.Max(0.8f, headerBottom - sillH);
        float glassMid = sillH + glassH * 0.5f;

        Box(upper, "U_FrontWindow_Sill",
            new Vector3(0f, sillH * 0.5f, -hd),
            new Vector3(windowW + WallT, sillH, WallT), wall);
        Box(upper, "U_FrontWindow_Glass",
            new Vector3(0f, glassMid, -hd),
            new Vector3(windowW, glassH, WallT * 0.45f), glass);
    }

    static void BuildFloorWithHole(Transform parent, string name, Rect hole, Material mat) =>
        BuildSlabWithHole(parent, name, -0.1f, hole, mat);

    /// <summary>
    /// Turn off colliders on ceiling pads that overlap a stair well so upper-floor
    /// descent sees one rim (the floor hole) like basement stairs — not floor+ceiling stacked.
    /// </summary>
    static void DisableCeilingRimColliders(Transform ground, string ceilingPrefix, Rect well)
    {
        foreach (Transform t in ground)
        {
            if (!t.name.StartsWith(ceilingPrefix)) continue;
            var col = t.GetComponent<Collider>();
            if (col == null) continue;
            var b = col.bounds;
            // Any ceiling piece that overlaps the well XZ band.
            bool overlapX = b.max.x > well.xMin && b.min.x < well.xMin + well.width;
            bool overlapZ = b.max.z > well.yMin && b.min.z < well.yMin + well.height;
            if (overlapX && overlapZ)
                col.enabled = false;
        }
        // Also clear the north pad strip in the stair column (same world Y as upper floor).
        var north = ground.Find(ceilingPrefix + "_N");
        if (north != null)
        {
            var col = north.GetComponent<Collider>();
            if (col != null) col.enabled = false;
        }
    }

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
        float hw = HouseW * 0.5f;
        float stairX = WellEast.xMin + WellEast.width * 0.5f;

        // Partitions run to the outer shell (±hw / ±hd); CornerPad buries into boundary walls.
        PartitionX(parent, "G_Part_Living", -3.5f, -hd, 4.5f, midY, wallH, wall, doorAtZ: -2f);
        PartitionX(parent, "G_Part_Kitchen", 3.5f, -hd, 4.5f, midY, wallH, wall, doorAtZ: -1f);
        // Back hall cross-wall: living / kitchen doors + direct stair approach under WellEast.
        // headerH 0.2 (opening 2.8m instead of 2.5m): the stairX door sits directly at the foot
        // of the upper-stair ramp, which is still ~0.4m high there. Descending, the player's head
        // reached ~2.58 and hit the 2.50 underside of the default header - they were stopped dead
        // in the doorway while walking DOWN, though walking up was fine because the climb only
        // starts after the door. Measured stuck position: (8.304, 1.581, 2.642) on TraversalRamp.
        PartitionZ(parent, "G_Part_Back", 2f, -hw, HouseW, midY, wallH, wall,
            doorAtX1: -5f, doorAtX2: stairX, doorWidth: 2.6f, headerH: 0.2f);

        // Stair west enclosure starts ABOVE the mouth so the bottom tread is open
        // from the back-hall / kitchen door (south approach).
        PartitionX(parent, "G_Stair_Encl", WellEast.xMin - 0.08f,
            WellEast.yMin + 1.35f, hd, midY, wallH, wall);

        Label(parent, "Foyer", new Vector3(0f, 0.05f, -5f));
        Label(parent, "LivingRoom", new Vector3(-7f, 0.05f, -2f));
        Label(parent, "Kitchen", new Vector3(7f, 0.05f, -2f));
        Label(parent, "BackHall", new Vector3(0f, 0.05f, 5f));
        Label(parent, "Stairwell", new Vector3(stairX, 0.05f, WellEast.yMin + 0.4f));

        Box(parent, "G_Baseboard_N", new Vector3(0f, 0.15f, hd - 0.05f), new Vector3(HouseW - 0.4f, 0.25f, 0.08f), trim);
    }

    static void BuildUpperRooms(Transform parent, Material wall, Material trim)
    {
        float H = UpperFloorH;
        float midY = H * 0.5f - 0.1f;
        float wallH = H - 0.2f;
        float hd = HouseD * 0.5f;
        float hw = HouseW * 0.5f;

        PartitionZ(parent, "U_Hall_Front", -2f, -hw, HouseW, midY, wallH, wall,
            doorAtX1: 0f, doorAtX2: WellEast.xMin + WellEast.width * 0.5f, doorWidth: 2.6f, storyHeight: H);
        PartitionX(parent, "U_Bed_Part", -3f, -2f, hd, midY, wallH, wall, doorAtZ: 2f, storyHeight: H);
        PartitionX(parent, "U_Study_Part", 3f, -2f, hd, midY, wallH, wall, doorAtZ: 3f, storyHeight: H);
        PartitionZ(parent, "U_Bath_Part", 4f, -3f, 6f, midY, wallH, wall, doorAtX1: -1f, storyHeight: H);

        Label(parent, "MasterBedroom", new Vector3(-7f, 0.05f, 3f));
        Label(parent, "Study", new Vector3(9.2f, 0.05f, -0.5f));
        Label(parent, "Bathroom", new Vector3(0f, 0.05f, 6f));
        Label(parent, "UpperHall", new Vector3(0f, 0.05f, -4f));
    }

    static void BuildBasementRooms(Transform parent, Material wall, Material trim)
    {
        float midY = FloorH * 0.5f - 0.1f;
        float wallH = FloorH - 0.2f;
        float hd = HouseD * 0.5f;
        float hw = HouseW * 0.5f;

        // Doorways are intentionally wide (CharacterController + sprint + NavMesh).
        const float door = 2.6f;

        // Outer chase ring uses clear ~2.4m+ lanes. Interior walls stop short of
        // creating sub-meter "almost doors" between parallel partitions.
        //
        // Layout (XZ):
        //   South lane  z ≈ -7.2 .. -5.8
        //   East lane   x ≈  7.8 .. 10.5
        //   North lane  z ≈  5.8 ..  7.5 (cut by stair well on the west)
        //   West lane   x ≈ -10.5 .. -7.8 south of the stair only
        //   Ritual core center with four full-width doors
        //   Cellar / boiler as side pockets off the south-west / south-east ring

        // --- Ritual core (shortcut) ---
        PartitionX(parent, "B_Core_W", -3.0f, -2.6f, 2.6f, midY, wallH, wall, doorAtZ: 0f, doorDepth: door);
        PartitionX(parent, "B_Core_E", 3.0f, -2.6f, 2.6f, midY, wallH, wall, doorAtZ: 0f, doorDepth: door);
        PartitionZ(parent, "B_Core_S", -2.6f, -3.0f, 6.0f, midY, wallH, wall, doorAtX1: 0f, doorWidth: door);
        PartitionZ(parent, "B_Core_N", 2.6f, -3.0f, 6.0f, midY, wallH, wall, doorAtX1: 0f, doorWidth: door);

        // --- South baffle: two wide ring gates; wall fully spans so no side slits ---
        PartitionZ(parent, "B_Baffle_S", -5.6f, -hw, HouseW, midY, wallH, wall,
            doorAtX1: -4.5f, doorAtX2: 4.5f, doorWidth: door);

        // --- North baffle: keep clear of WellWest (x < -6.65). Wide gates only. ---
        PartitionZ(parent, "B_Baffle_N", 5.5f, -6.2f, hw - (-6.2f), midY, wallH, wall,
            doorAtX1: -2.0f, doorAtX2: 5.0f, doorWidth: door);

        // --- East baffle: continuous with one wide door into the east lane ---
        PartitionX(parent, "B_Baffle_E", 7.4f, -5.6f, hd, midY, wallH, wall, doorAtZ: 0f, doorDepth: door);

        // --- West baffle + stair east hall share one plane so no thin interstitial gap.
        float stepW = Mathf.Max(1.6f, WellWest.width - 0.85f);
        float stairCx = WellWest.xMin + WellWest.width * 0.5f;
        float stairHallE = stairCx + stepW * 0.5f + 0.08f;
        PartitionX(parent, "B_Baffle_W", stairHallE, -hd, WellWest.yMax, midY, wallH, wall,
            doorAtZ: -3.2f, doorDepth: door);

        // --- Alcove dead-end (SW): single open mouth from the west lane only.
        //     North wall meets the south baffle so there is no thin parallel gap. ---
        PartitionX(parent, "B_Alcove_E", -5.6f, -hd, -5.6f, midY, wallH, wall);
        float alcoveX0 = -hw;
        float alcoveX1 = -5.6f + CornerPad;
        Box(parent, "B_Alcove_N",
            new Vector3((alcoveX0 + alcoveX1) * 0.5f, midY, -5.6f),
            new Vector3(alcoveX1 - alcoveX0, wallH, WallT), wall);
        CornerPost(parent, "B_Alcove_SE", -5.6f, -5.6f, midY, wallH, wall);
        CornerPost(parent, "B_Alcove_SW", -hw, -5.6f, midY, wallH, wall);

        PartitionZ(parent, "B_Cellar_Front", -1.4f, -hw, 3.9f, midY, wallH, wall,
            doorAtX1: -8.5f, doorWidth: door);
        PartitionZ(parent, "B_Boiler_Front", -1.4f, 7.2f, hw - 7.2f, midY, wallH, wall,
            doorAtX1: 8.5f, doorWidth: door);

        // Stair shaft enclosed after stair meshes in Build().

        float midYPosts = midY;
        CornerPost(parent, "B_Corner_SE_Baffle", 7.4f, -5.6f, midYPosts, wallH, wall);
        CornerPost(parent, "B_Corner_NE_Baffle", 7.4f, 5.5f, midYPosts, wallH, wall);
        CornerPost(parent, "B_Corner_SW_Baffle", stairHallE, -5.6f, midYPosts, wallH, wall);
        CornerPost(parent, "B_Corner_Core_SW", -3.0f, -2.6f, midYPosts, wallH, wall);
        CornerPost(parent, "B_Corner_Core_SE", 3.0f, -2.6f, midYPosts, wallH, wall);
        CornerPost(parent, "B_Corner_Core_NW", -3.0f, 2.6f, midYPosts, wallH, wall);
        CornerPost(parent, "B_Corner_Core_NE", 3.0f, 2.6f, midYPosts, wallH, wall);
        CornerPost(parent, "B_Bound_SE", hw, -5.6f, midYPosts, wallH, wall);
        CornerPost(parent, "B_Bound_NE", hw, 5.5f, midYPosts, wallH, wall);
        CornerPost(parent, "B_Bound_SW", -hw, -5.6f, midYPosts, wallH, wall);
        CornerPost(parent, "B_Bound_Cellar", -hw, -1.4f, midYPosts, wallH, wall);
        CornerPost(parent, "B_Bound_Boiler", hw, -1.4f, midYPosts, wallH, wall);

        Label(parent, "Cellar", new Vector3(-8.5f, 0.05f, -3.4f));
        Label(parent, "BoilerRoom", new Vector3(8.5f, 0.05f, -3.4f));
        Label(parent, "RitualChamber", new Vector3(0f, 0.05f, 0f));
        Label(parent, "LoopSouth", new Vector3(0f, 0.05f, -6.7f));
        Label(parent, "LoopNorth", new Vector3(2f, 0.05f, 6.6f));
        Label(parent, "AlcoveDeadEnd", new Vector3(-8.6f, 0.05f, -6.7f));
        Label(parent, "StairApproach", new Vector3(-8.5f, 0.05f, 1.0f));

        Box(parent, "B_Baseboard_S", new Vector3(0f, 0.15f, -hd + 0.08f),
            new Vector3(HouseW - 0.5f, 0.25f, 0.08f), trim);
        Box(parent, "B_Baseboard_N", new Vector3(0f, 0.15f, hd - 0.08f),
            new Vector3(HouseW - 0.5f, 0.25f, 0.08f), trim);
    }

    /// <summary>
    /// Seals the basement↔ground stair shaft: full-height side walls, west closet,
    /// and a ceiling collar so looking up the stairs does not show a slab gap.
    /// </summary>
    static void EncloseBasementStairBack(Transform basement, Transform ground, Material wall)
    {
        float hw = HouseW * 0.5f;
        float z0 = WellWest.yMin;
        float z1 = WellWest.yMax;
        float cx = WellWest.xMin + WellWest.width * 0.5f;
        float cz = (z0 + z1) * 0.5f;

        float stepW = Mathf.Max(1.6f, WellWest.width - 0.85f);
        float innerL = cx - stepW * 0.5f - 0.08f;
        float innerR = cx + stepW * 0.5f + 0.08f;

        // Full story height (kiss basement ceiling / poke into ground well).
        float shaftH = FloorH + 0.2f;
        float shaftMid = shaftH * 0.5f;

        Box(basement, "B_StairHall_W",
            new Vector3(innerL, shaftMid, cz),
            new Vector3(WallT, shaftH, WellWest.height + WallT), wall);
        Box(basement, "B_StairHall_E",
            new Vector3(innerR, shaftMid, cz),
            new Vector3(WallT, shaftH, WellWest.height + WallT), wall);

        float westFace = -hw;
        float closetW = (innerL + CornerPad) - westFace;
        Box(basement, "B_StairCloset",
            new Vector3(westFace + closetW * 0.5f, shaftMid, cz),
            new Vector3(closetW, shaftH, WellWest.height + WallT), wall);

        // This one stops FLUSH with the ground floor instead of using shaftH. The +0.2 "poke"
        // the side walls use is harmless for them, but B_StairBack_N spans the full width of the
        // well directly across the stair exit, so poking above the floor turned it into a barrier
        // over the whole mouth: the player's capsule contacts it at z~5.98 where the ramp is still
        // at -0.37, a 0.57 rise that exceeds stepOffset, leaving jumping as the only way out.
        // The rim is already sealed by B_StairCollar_N, so nothing opens up by lowering this.
        Box(basement, "B_StairBack_N",
            new Vector3(cx, FloorH * 0.5f, z1),
            new Vector3((innerR + CornerPad) - westFace, FloorH, WallT), wall);

        // Ceiling collar: fill the WellWest hole rim on the basement ceiling plane
        // between the outer well rect and the stair channel (kills the visible slab gap).
        float ceilY = FloorH - 0.05f;
        float collarT = 0.2f;
        // West strip (closet top already covers; thin fascia at hole west edge)
        Box(basement, "B_StairCollar_W",
            new Vector3((WellWest.xMin + innerL) * 0.5f, ceilY, cz),
            new Vector3(Mathf.Max(WallT, innerL - WellWest.xMin), collarT, WellWest.height + WallT), wall);
        Box(basement, "B_StairCollar_E",
            new Vector3((innerR + WellWest.xMax) * 0.5f, ceilY, cz),
            new Vector3(Mathf.Max(WallT, WellWest.xMax - innerR), collarT, WellWest.height + WallT), wall);
        Box(basement, "B_StairCollar_S",
            new Vector3(cx, ceilY, z0),
            new Vector3(WellWest.width + WallT, collarT, WallT), wall);
        Box(basement, "B_StairCollar_N",
            new Vector3(cx, ceilY, z1),
            new Vector3(WellWest.width + WallT, collarT, WallT), wall);

        // Ground-floor well walls (continue shaft upward so the hole rim is sealed).
        float gWallH = BasementJumpCurbH;   // was 1.05: not jumpable under the 3.0 m ceiling
        float gMid = gWallH * 0.5f;
        // Length is the well depth exactly, NOT well + WallT. The extra 0.175m of overhang at
        // each end pinched the north exit corridor to 0.96m clear (G_WellWest_E north face z=6.73
        // against Wainscot_N at z=7.76). The player needs 0.96m (r 0.40 + 0.08 skin) and squeezed
        // through; a NavMesh agent needs 1.00m at the 0.50 bake radius and did not fit, so the
        // monster could climb the stairs and then never leave the stairwell. The walls still span
        // the full well (z 2.15..6.55), so nothing opens up as a fall hazard.
        Box(ground, "G_WellWest_W",
            new Vector3(innerL, gMid, cz),
            new Vector3(WallT, gWallH, WellWest.height), wall);
        Box(ground, "G_WellWest_E",
            new Vector3(innerR, gMid, cz),
            new Vector3(WallT, gWallH, WellWest.height), wall);
        // South closed on ground (fall guard); north open for landing exit.
        Box(ground, "G_WellWest_S",
            new Vector3(cx, gMid, z0),
            new Vector3(innerR - innerL + WallT, gWallH, WallT), wall);

        CornerPost(basement, "B_StairMouth_SW", innerL, z0, shaftMid, shaftH, wall);
        CornerPost(basement, "B_StairMouth_SE", innerR, z0, shaftMid, shaftH, wall);
        CornerPost(basement, "B_StairBack_NW", innerL, z1, shaftMid, shaftH, wall);
        CornerPost(basement, "B_StairBack_NE", innerR, z1, shaftMid, shaftH, wall);
        CornerPost(basement, "B_StairCloset_SW", westFace + WallT * 0.5f, z0, shaftMid, shaftH, wall);
        CornerPost(basement, "B_StairCloset_NW", westFace + WallT * 0.5f, z1, shaftMid, shaftH, wall);
    }

    /// <summary>
    /// Ground↔upper stair shaft — same pattern as basement stairs:
    /// full side walls on the lower story, low well guards on the upper story,
    /// north open with L/R curb stubs (see PlaceStairWellGuards).
    /// </summary>
    static void EncloseUpperStairShaft(Transform ground, Transform upper, Material wall)
    {
        float z0 = WellEast.yMin;
        float z1 = WellEast.yMax;
        float cx = WellEast.xMin + WellEast.width * 0.5f;
        float cz = (z0 + z1) * 0.5f;
        float stepW = Mathf.Max(1.6f, WellEast.width - 0.85f);
        float innerL = cx - stepW * 0.5f - 0.08f;
        float innerR = cx + stepW * 0.5f + 0.08f;

        // Lower story (ground): seal shaft sides, stop under the upper slab (no poke into rim).
        float shaftH = FloorH - 0.15f;
        float shaftMid = shaftH * 0.5f;
        Box(ground, "G_StairHall_W",
            new Vector3(innerL, shaftMid, cz),
            new Vector3(WallT, shaftH, WellEast.height + WallT), wall);
        Box(ground, "G_StairHall_E",
            new Vector3(innerR, shaftMid, cz),
            new Vector3(WallT, shaftH, WellEast.height + WallT), wall);

        // Upper story well guards. North mouth matches basement (open + L/R curbs).
        // South uses the jumpable curb height so the shortcut still works.
        float sideH = JumpCurbH;
        float sideMid = sideH * 0.5f;
        Box(upper, "U_WellEast_W",
            new Vector3(innerL, sideMid, cz),
            new Vector3(WallT, sideH, WellEast.height + WallT), wall);
        Box(upper, "U_WellEast_E",
            new Vector3(innerR, sideMid, cz),
            new Vector3(WallT, sideH, WellEast.height + WallT), wall);
        Box(upper, "U_WellEast_S",
            new Vector3(cx, sideMid, z0),
            new Vector3(innerR - innerL + WallT, sideH, WallT), wall);
    }

    /// <summary>Solid WallT cube that buries an L/T junction so corners read as one piece.</summary>
    static void CornerPost(Transform parent, string name, float x, float z, float midY, float wallH, Material mat)
    {
        Box(parent, name, new Vector3(x, midY, z), new Vector3(WallT, wallH, WallT), mat);
    }

    static void BuildStairGroundToUpper(Transform ground, Transform upper, Material stairMat, Material wallMat)
    {
        float x = WellEast.xMin + WellEast.width * 0.5f;
        // Same flight builder as basement stairs (shared TraversalRamp).
        BuildStairFlight(ground, "Stairs_ToUpper", new Vector3(x, 0f, WellEast.yMin), WellEast, stairMat, wallMat,
            buildRails: false);
        Label(ground, "StairsUp", new Vector3(x, 0.05f, WellEast.yMin - 0.4f));
        Label(upper, "StairsFromGround", new Vector3(x, 0.05f, WellEast.yMax + 0.6f));
    }

    static void BuildStairGroundToBasement(Transform ground, Transform basement, Material stairMat, Material wallMat)
    {
        float x = WellWest.xMin + WellWest.width * 0.5f;
        // Enclosed flight: side walls are added in EncloseBasementStairBack (no thin rails
        // that leave walkable slits beside the steps).
        BuildStairFlight(basement, "Stairs_ToGround", new Vector3(x, 0f, WellWest.yMin), WellWest, stairMat, wallMat,
            buildRails: false);
        Label(basement, "StairsBasement", new Vector3(x, 0.05f, WellWest.yMin - 0.4f));
        Label(ground, "StairsFromBasement", new Vector3(x, 0.05f, WellWest.yMax + 0.6f));
    }

    static void BuildStairFlight(Transform parent, string name, Vector3 origin, Rect well, Material stairMat, Material wallMat,
        bool buildRails = true)
    {
        int steps = StairSteps;
        float stepH = FloorH / steps;
        float stepD = well.height / steps;
        float stepW = Mathf.Max(1.6f, well.width - 0.85f);
        var stairs = MakeEmpty(parent, name, origin).transform;
        BuildStraightStairs(stairs, steps, stepW, stepD, stepH, stairMat);
        if (!buildRails) return;
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
            var step = Box(parent, $"Step_{i}", new Vector3(0f, y, z), new Vector3(stepW, stepH, stepD), mat);
            var stepCollider = step.GetComponent<Collider>();
            if (stepCollider != null)
                stepCollider.enabled = false;
        }

        // Hidden ramp for basement stairs only. Upper stairs replace this in
        // ExtendUpperStairRampThroughMouth with a mouth-to-foot ramp.
        float rise = stepH * stepCount;
        float run = stepD * stepCount;
        float angle = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;
        float rampLength = Mathf.Sqrt(rise * rise + run * run);
        const float rampThickness = 0.18f;
        var ramp = Box(parent, "TraversalRamp",
            new Vector3(0f, rise * 0.5f - rampThickness * 0.35f, run * 0.5f),
            new Vector3(stepW, rampThickness, rampLength), mat);
        ramp.transform.localRotation = Quaternion.Euler(-angle, 0f, 0f);
        var rampRenderer = ramp.GetComponent<Renderer>();
        if (rampRenderer != null)
            rampRenderer.enabled = false;
    }

    static void PlaceBasementChaseZones(Transform basement)
    {
        Zone(basement, "BasementLoop_South", HouseZone.ZoneKind.Corridor,
            new Vector3(0f, 1.15f, -6.45f), new Vector3(16.5f, 2.3f, 1.8f));
        Zone(basement, "BasementLoop_East", HouseZone.ZoneKind.Corridor,
            new Vector3(8.6f, 1.15f, 0f), new Vector3(2.2f, 2.3f, 11.5f));
        Zone(basement, "BasementLoop_North", HouseZone.ZoneKind.Corridor,
            new Vector3(1f, 1.15f, 6.45f), new Vector3(12.5f, 2.3f, 1.8f));
        Zone(basement, "BasementLoop_West", HouseZone.ZoneKind.Corridor,
            new Vector3(-8.6f, 1.15f, -1.8f), new Vector3(2.2f, 2.3f, 8.2f));
        Zone(basement, "RitualShortcut", HouseZone.ZoneKind.Room,
            new Vector3(0f, 1.15f, 0f), new Vector3(5.9f, 2.3f, 5.2f));
        Zone(basement, "CellarRoom", HouseZone.ZoneKind.Room,
            new Vector3(-8.4f, 1.15f, -3.2f), new Vector3(2.4f, 2.3f, 3.2f));
        Zone(basement, "BoilerRoom", HouseZone.ZoneKind.Room,
            new Vector3(8.4f, 1.15f, -3.2f), new Vector3(2.4f, 2.3f, 3.2f));
        Zone(basement, "AlcoveDeadEnd", HouseZone.ZoneKind.Corridor,
            new Vector3(-8.6f, 1.15f, -6.6f), new Vector3(2.2f, 2.3f, 1.6f));
        Zone(basement, "StairApproach", HouseZone.ZoneKind.Corridor,
            new Vector3(-8.4f, 1.15f, 1.2f), new Vector3(2.4f, 2.3f, 2.2f));

        Vector3[] patrol =
        {
            // -4.9, not -6.4: z -6.4 is inside the sealed SW alcove (behind B_Alcove_N), which minRegionArea culls from
            // the NavMesh, so this point was unreachable from both spawns (found by VerifyMonsterRoutes, 26 Sep 2026).
            new Vector3(-8.4f, 0.1f, -4.9f),
            new Vector3(0f, 0.1f, -6.4f),
            new Vector3(8.5f, 0.1f, -5.8f),
            new Vector3(8.5f, 0.1f, 5.8f),
            new Vector3(2f, 0.1f, 6.4f),
            new Vector3(-5.2f, 0.1f, 6.4f),
            new Vector3(-8.5f, 0.1f, -1.6f),
            new Vector3(-4.5f, 0.1f, -3.8f),
            new Vector3(0f, 0.1f, 0f),
            new Vector3(4.5f, 0.1f, 3.8f)
        };
        for (int i = 0; i < patrol.Length; i++)
            Zone(basement, $"BasementPatrol_{i:00}", HouseZone.ZoneKind.Patrol,
                patrol[i], new Vector3(0.75f, 2f, 0.75f));

        Zone(basement, "BasementSpawn_West", HouseZone.ZoneKind.MonsterSpawn,
            new Vector3(-8.5f, 0.1f, -5.7f), new Vector3(1.2f, 2f, 1.2f));
        Zone(basement, "BasementSpawn_East", HouseZone.ZoneKind.MonsterSpawn,
            new Vector3(8.5f, 0.1f, 5.7f), new Vector3(1.2f, 2f, 1.2f));
    }

    static void PlaceFallRecovery(Transform basement)
    {
        var safe = MakeEmpty(basement, "RecoveryPoint_Basement",
            new Vector3(-8.35f, 1.05f, 1.1f)).transform;
        Zone(basement, "BasementRecovery", HouseZone.ZoneKind.Recovery,
            safe.localPosition, new Vector3(1f, 2f, 1f));

        var recovery = MakeEmpty(basement, "FallRecovery_BelowBasement",
            new Vector3(0f, -2f, 0f));
        var trigger = recovery.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.size = new Vector3(HouseW + 4f, 1.5f, HouseD + 4f);
        recovery.AddComponent<FallRecoveryZone>().Configure(safe);
    }

    static void PlaceStairWellGuards(Transform ground, Transform upper, Material wall)
    {
        // Basement shaft north mouth (ground floor) — proven walkable gap.
        PlaceNorthWellCurbs(ground, "WellWest_N", WellWest, wall);
        // Upper shaft north mouth — same curb geometry as basement.
        PlaceNorthWellCurbs(upper, "WellEast_N", WellEast, wall);
    }

    static void PlaceNorthWellCurbs(Transform parent, string prefix, Rect well, Material wall)
    {
        float x0 = well.xMin, x1 = well.xMax, z1 = well.yMax;
        float h = MouthCurbH, t = WallT, y = h * 0.5f;
        float gap = Mathf.Max(1.6f, well.width - 0.85f);
        float mid = (x0 + x1) * 0.5f;
        float leftW = mid - gap * 0.5f - x0;
        float rightW = x1 - (mid + gap * 0.5f);
        if (leftW > 0.2f)
            Box(parent, prefix + "_L", new Vector3(x0 + leftW * 0.5f, y, z1), new Vector3(leftW, h, t), wall);
        if (rightW > 0.2f)
            Box(parent, prefix + "_R", new Vector3(x1 - rightW * 0.5f, y, z1), new Vector3(rightW, h, t), wall);
    }

    static void PlaceLights(Transform basement, Transform ground, Transform upper)
    {
        MakePointLight(ground, "Light_Foyer", new Vector3(0f, 2.6f, -5f), new Color(1f, 0.75f, 0.45f), 1.1f, 10f);
        MakePointLight(ground, "Light_Living", new Vector3(-7f, 2.5f, -2f), new Color(1f, 0.55f, 0.3f), 0.7f, 9f);
        MakePointLight(ground, "Light_Kitchen", new Vector3(7f, 2.5f, -2f), new Color(0.7f, 0.85f, 1f), 0.55f, 8f);
        MakePointLight(ground, "Light_Stair", new Vector3(8.5f, 2.4f, 5f), new Color(1f, 0.7f, 0.4f), 0.8f, 8f);

        MakePointLight(upper, "Light_UpperHall", new Vector3(0f, 3.4f, -4f), new Color(0.9f, 0.7f, 0.5f), 0.7f, 10f);
        MakePointLight(upper, "Light_Bedroom", new Vector3(-7f, 3.3f, 3f), new Color(0.6f, 0.4f, 0.8f), 0.5f, 8f);
        MakePointLight(upper, "Light_Study", new Vector3(9.2f, 3.3f, -0.5f), new Color(0.5f, 0.65f, 1f), 0.55f, 8f);

        MakePointLight(basement, "Light_Cellar", new Vector3(-8.4f, 2.2f, -3.2f), new Color(0.4f, 0.7f, 0.45f), 0.4f, 7f);
        MakePointLight(basement, "Light_Ritual", new Vector3(0f, 2.1f, 0.2f), new Color(0.85f, 0.15f, 0.1f), 0.6f, 8f);
        MakePointLight(basement, "Light_Boiler", new Vector3(8.4f, 2.2f, -3.2f), new Color(1f, 0.45f, 0.15f), 0.45f, 7f);
        MakePointLight(basement, "Light_LoopSouth", new Vector3(0f, 2.15f, -6.5f), new Color(0.55f, 0.45f, 0.35f), 0.35f, 7f);
        MakePointLight(basement, "Light_LoopNorth", new Vector3(1f, 2.15f, 6.4f), new Color(0.45f, 0.5f, 0.65f), 0.35f, 7f);
        MakePointLight(basement, "Light_StairApproach", new Vector3(-8.4f, 2.2f, 1.3f), new Color(0.7f, 0.55f, 0.35f), 0.5f, 6f);
    }

    static void PlaceProps(Transform basement, Transform ground, Transform upper, Material trim, Material cobble)
    {
        // Simple furniture proxies (cubes) for haunted silhouette
        Box(ground, "Prop_Table", new Vector3(-7f, 0.4f, -2f), new Vector3(1.6f, 0.8f, 0.9f), trim);
        Box(ground, "Prop_Couch", new Vector3(-7f, 0.35f, 0.5f), new Vector3(2.4f, 0.7f, 0.9f), trim);
        Box(ground, "Prop_Counter", new Vector3(9.2f, 0.45f, -4.5f), new Vector3(1.6f, 0.9f, 0.65f), trim);
        Box(ground, "Prop_Shelf", new Vector3(-1.5f, 1.2f, 6.6f), new Vector3(1.6f, 2.2f, 0.35f), trim);

        Box(upper, "Prop_Bed", new Vector3(-7.5f, 0.35f, 4f), new Vector3(2.2f, 0.55f, 1.8f), trim);
        // Keep east of WellEast (x 6.65–10.15, z 2.15–6.55) — desk used to sit in the hole.
        Box(upper, "Prop_Desk", new Vector3(9.4f, 0.4f, -0.4f), new Vector3(1.4f, 0.75f, 0.7f), trim);
        Box(upper, "Prop_Bookcase", new Vector3(-1.5f, 1.1f, -5.5f), new Vector3(0.4f, 2.2f, 1.6f), trim);

        // Keep outer chase lanes clear. Props hug far corners / walls only —
        // never sit in door mouths or the ring corridors.
        Box(basement, "Prop_Crates", new Vector3(2.15f, 0.4f, -1.95f), new Vector3(0.75f, 0.75f, 0.75f), trim);
        Box(basement, "Prop_Boiler", new Vector3(9.45f, 0.85f, -4.45f), new Vector3(0.95f, 1.7f, 0.85f),
            cobble != null ? cobble : trim);
        // Keep altar off the north doorway centerline (x=0).
        Box(basement, "Prop_Altar", new Vector3(-1.7f, 0.4f, 1.9f), new Vector3(1.0f, 0.75f, 0.55f),
            cobble != null ? cobble : trim);
        Box(basement, "Prop_Barrels", new Vector3(-9.55f, 0.45f, -4.45f), new Vector3(0.8f, 0.9f, 0.8f), trim);
    }

    static void PlaceInteriorDetails(Transform root, Transform basement, Transform ground, Transform upper, HouseMaterials mats)
    {
        float hw = HouseW * 0.5f;
        float hd = HouseD * 0.5f;

        // Kitchen tile overlay (east rooms) + living-room hearth (west)
        // Runs to the front wall (z -7.83 .. 2.0). It stopped at z -6.4, leaving a 1.4 m strip of the lower wood floor along
        // the entrance wall: two floors at two heights in one room (user, 26 Sep 2026).
        Box(ground, "Kitchen_Tile", new Vector3(7.2f, 0.03f, -2.915f), new Vector3(7.2f, 0.05f, 9.83f), mats.tile);
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
        Material mat, float? doorAtZ = null, float doorDepth = 2.4f, float storyHeight = -1f)
    {
        float H = storyHeight > 0f ? storyHeight : FloorH;
        // Extend past the authored z range so this wall buries into any crossing
        // PartitionZ / shell wall instead of stopping at a knife-edge corner.
        float z0 = Mathf.Min(zStart, zEnd) - CornerPad;
        float z1 = Mathf.Max(zStart, zEnd) + CornerPad;
        float len = z1 - z0;
        if (doorAtZ == null)
        {
            if (len > 0.05f)
                Box(parent, name, new Vector3(x, midY, (z0 + z1) * 0.5f), new Vector3(WallT, wallH, len), mat);
            return;
        }
        float doorZ = doorAtZ.Value;
        float doorD = Mathf.Max(2.2f, doorDepth);
        float a0 = z0;
        float a1 = doorZ - doorD * 0.5f;
        if (a1 > a0 + 0.1f)
            Box(parent, name + "_A", new Vector3(x, midY, (a0 + a1) * 0.5f), new Vector3(WallT, wallH, a1 - a0), mat);
        float b0 = doorZ + doorD * 0.5f;
        float b1 = z1;
        if (b1 > b0 + 0.1f)
            Box(parent, name + "_B", new Vector3(x, midY, (b0 + b1) * 0.5f), new Vector3(WallT, wallH, b1 - b0), mat);
        Box(parent, name + "_Hdr", new Vector3(x, H - 0.45f, doorZ), new Vector3(WallT, 0.5f, doorD), mat);
    }

    /// <param name="headerH">
    /// Thickness of the block above each door opening. The default 0.5 leaves a 2.5m opening,
    /// which is fine on flat floor but NOT where a stair ramp is still climbing through the
    /// doorway - the player's head hits it. Pass a smaller value there to raise the opening.
    /// </param>
    static void PartitionZ(Transform parent, string name, float z, float xStart, float width, float midY, float wallH,
        Material mat, float? doorAtX1 = null, float? doorAtX2 = null, float doorWidth = 2.4f, float storyHeight = -1f,
        float headerH = 0.5f)
    {
        float H = storyHeight > 0f ? storyHeight : FloorH;
        float x0 = xStart - CornerPad;
        float x1 = xStart + width + CornerPad;
        var doors = new List<float>();
        if (doorAtX1.HasValue) doors.Add(doorAtX1.Value);
        if (doorAtX2.HasValue) doors.Add(doorAtX2.Value);
        doors.Sort();

        float cursor = x0;
        float doorW = Mathf.Max(2.2f, doorWidth);
        int i = 0;
        foreach (var dx in doors)
        {
            float segEnd = dx - doorW * 0.5f;
            if (segEnd > cursor + 0.1f)
                Box(parent, name + "_S" + i, new Vector3((cursor + segEnd) * 0.5f, midY, z),
                    new Vector3(segEnd - cursor, wallH, WallT), mat);
            // Top stays flush with the wall top (H - 0.2); only the underside moves.
            Box(parent, name + "_Hdr" + i, new Vector3(dx, H - 0.2f - headerH * 0.5f, z),
                new Vector3(doorW, headerH, WallT), mat);
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

    static HouseZone Zone(Transform parent, string id, HouseZone.ZoneKind kind,
        Vector3 localPos, Vector3 size)
    {
        var go = new GameObject("Zone_" + id);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        var zone = go.AddComponent<HouseZone>();
        zone.Configure(id, kind, size);
        return zone;
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
