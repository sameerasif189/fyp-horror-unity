using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Milestone M4 stress lighting for the house (user, 25 Sep 2026: "normal 0-3, dimmer at 4 and flickering at 5").
/// The builder's point lights (intensity 0.35-1.1) barely light anything under URP's inverse-square falloff - walls
/// rendered ~20/255 at every level - and several rooms had none. Owned by <see cref="TextureCorruptionRunner"/>,
/// play mode only (everything is created at runtime and nothing is saved into the scene):
///  - the builder's lights are scaled up, and every room marker (the builder's <c>Zone_*</c> labels and room /
///    corridor <see cref="HouseZone"/>s, sampled every few metres) that no light can see gets an unshadowed fill
///    light under the ceiling;
///  - per stress level: normal at 0-3, <see cref="Level4"/> at 4, and at 5 the approved near-pure red at
///    <see cref="Level5"/> with independent flicker per light (mains wobble, short cuts, stutter bursts, and long
///    outages on the few "dying" bulbs);
///  - inside the house the default-skybox reflection, which laid a flat grey-blue sheen over every surface, is
///    replaced by a dark interior one (<see cref="ReflectionColor"/>);
///  - every light hangs from the ceiling as a bare-bulb pendant (rose, cord, holder, bulb) whose bulb glows with
///    its light's colour and brightness, so it dims at 4 and flickers red at 5. Built from Unity's built-in meshes -
///    no downloaded model - with no colliders or shadows. A builder light that sat right above a walkable surface
///    (the stair light was 0.28 m over the ramp) is moved up under the ceiling above it first.
/// Values are statics so they can be tuned live; call <see cref="Refresh"/> after changing them.
/// </summary>
public sealed class HouseLighting : IDisposable
{
    /// <summary>
    /// Multiplier on the builder's light intensities. Tuned 25 Sep 2026 on 12 rooms: mean frame luma at stress 0
    /// went 17 -> 43 / 255 with no blown-out pixels.
    /// </summary>
    public static float Boost = 10f;
    /// <summary>Floor for a scaled builder light, so the dim coloured mood lights (bedroom, study) still light their room.</summary>
    public static float MinIntensity = 7f;
    public static float FillIntensity = 6.5f;
    public static float FillRange = 7f;
    public static Color FillColor = new Color(1f, 0.8f, 0.6f);
    /// <summary>
    /// Brightness at stress 4 and (before flicker) at stress 5, relative to 0-3. Red light carries about a third of
    /// white's luminance, so stress 5 still reads darker than 4; 0.62 keeps the absolute red level chosen in the
    /// stress-5 A/B (red lights at the old full level, <see cref="HouseDreadLook.BaseGlow"/> 0.05).
    /// </summary>
    public static float Level4 = 0.45f, Level5 = 0.62f;
    /// <summary>Slow +/- wobble of every light at stress 5.</summary>
    public static float FlickerWobble = 0.12f;
    /// <summary>Share of lights that are "dying" at stress 5: longer, deeper outages.</summary>
    public static float DyingShare = 0.25f;
    /// <summary>Linear colour of the interior reflection (about the flat ambient); the sky it replaces is far brighter.</summary>
    public static Color ReflectionColor = new Color(0.013f, 0.011f, 0.015f);
    public static bool DarkReflections = true;
    /// <summary>At 0-4, light every room with <see cref="FillColor"/> instead of the builder's per-room mood colours.</summary>
    public static bool NeutralColour;
    /// <summary>Linear HDR brightness of a bulb at full power (bloom picks it up).</summary>
    public static float BulbGlow = 6f;

    const float LitRadius = 4.5f;
    const float CorridorStep = 4f;
    /// <summary>A builder light closer than this above whatever is under it is moved up under the ceiling.</summary>
    const float MinHeadroom = 1.9f;
    const float HangDrop = 0.6f;
    const float BulbHalfHeight = 0.0475f;

    sealed class Lamp
    {
        public Light light;
        public Color color;
        public Vector3 position;
        public float intensity, range;
        public bool added, dying;
        public float seed, cutUntil, nextCut, cutLevel;
        public int burst;
        public Renderer bulb;
    }

    readonly List<Lamp> _lamps = new List<Lamp>();
    readonly List<GameObject> _owned = new List<GameObject>();
    readonly List<UnityEngine.Object> _assets = new List<UnityEngine.Object>();
    readonly System.Random _rng;
    readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    Cubemap _reflection;
    ReflectionProbe _probe;
    int _level = -1;

    public int AddedCount { get; private set; }
    public int MovedCount { get; private set; }
    public int Level => _level;

    public HouseLighting(Transform house, int seed)
    {
        _rng = new System.Random(seed);
        if (house == null) return;
        foreach (var l in house.GetComponentsInChildren<Light>(false))
            if (l.type == LightType.Point && l.enabled)
            {
                Add(l, false);
                Settle(_lamps[_lamps.Count - 1]);
            }
        AddRoomFills(house);
        AddReflection(house);
        AddFixtures();
    }

    void Add(Light l, bool added)
    {
        _lamps.Add(new Lamp
        {
            light = l, color = l.color, position = l.transform.position, intensity = l.intensity, range = l.range,
            added = added, dying = _rng.NextDouble() < DyingShare, seed = (float)_rng.NextDouble() * 100f,
        });
    }

    /// <summary>
    /// Move a builder light that sits too close above a surface (stairs, a landing) up under the ceiling above it,
    /// reaching far enough to still light what it was lighting.
    /// </summary>
    void Settle(Lamp lamp)
    {
        var p = lamp.position;
        if (!Physics.Raycast(p, Vector3.down, out var down, 8f, ~0, QueryTriggerInteraction.Ignore) || down.distance >= MinHeadroom) return;
        if (!Physics.Raycast(p, Vector3.up, out var up, 8f, ~0, QueryTriggerInteraction.Ignore) || up.distance < 1.2f) return;
        var t = lamp.light.transform;
        t.position = up.point - Vector3.up * HangDrop;
        lamp.light.range = Mathf.Max(lamp.range, t.position.y - down.point.y + 4f);
        MovedCount++;
    }

    /// <summary>
    /// One fill light per room marker that no light can see: labels at their position, room / corridor zones
    /// sampled every <see cref="CorridorStep"/> m, so the long basement lanes get more than one.
    /// </summary>
    void AddRoomFills(Transform house)
    {
        var candidates = new List<(Vector3 p, Transform floor, string name)>();
        foreach (var t in house.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("Zone_")) continue;
            var zone = t.GetComponent<HouseZone>();
            if (zone == null)
            {
                candidates.Add((t.position, t.parent, t.name.Substring(5)));
                continue;
            }
            if (zone.Kind != HouseZone.ZoneKind.Room && zone.Kind != HouseZone.ZoneKind.Corridor) continue;
            int nx = Mathf.Max(1, Mathf.CeilToInt(zone.Size.x / CorridorStep));
            int nz = Mathf.Max(1, Mathf.CeilToInt(zone.Size.z / CorridorStep));
            for (int ix = 0; ix < nx; ix++)
            for (int iz = 0; iz < nz; iz++)
            {
                var local = new Vector3((ix + 0.5f) / nx - 0.5f, 0f, (iz + 0.5f) / nz - 0.5f);
                var p = t.TransformPoint(Vector3.Scale(local, zone.Size));
                p.y = t.position.y - zone.Size.y * 0.5f;   // zones are centred on their volume
                candidates.Add((p, t.parent, zone.ZoneId));
            }
        }

        foreach (var (marker, floor, name) in candidates)
        {
            // Probe at head height above whatever floor is under the marker.
            var p = marker + Vector3.up * 0.5f;
            if (Physics.Raycast(p, Vector3.down, out var down, 3f, ~0, QueryTriggerInteraction.Ignore)) p = down.point;
            p += Vector3.up * 1.4f;
            if (Physics.CheckSphere(p, 0.2f, ~0, QueryTriggerInteraction.Ignore) || Lit(p)) continue;
            float y = Physics.Raycast(p, Vector3.up, out var up, 4f, ~0, QueryTriggerInteraction.Ignore)
                ? up.point.y - 0.35f : p.y + 1.1f;
            // Only under a real ceiling: the Stairwell marker sits in the open stair shaft, and its light floated
            // 4.6 m below the shaft top with nothing to hang from (the next marker, at the stair foot, lights it).
            if (!Physics.Raycast(new Vector3(p.x, y, p.z), Vector3.up, 1f, ~0, QueryTriggerInteraction.Ignore)) continue;

            var go = new GameObject("Light_Fill_" + name);
            go.transform.SetParent(floor != null ? floor : house, false);
            go.transform.position = new Vector3(p.x, y, p.z);
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = FillColor;
            l.intensity = FillIntensity;
            l.range = FillRange;
            l.shadows = LightShadows.None;
            _owned.Add(go);
            Add(l, true);
            AddedCount++;
        }
    }

    /// <summary>True when a light on the same floor is within <see cref="LitRadius"/> and has line of sight.</summary>
    bool Lit(Vector3 p)
    {
        foreach (var lamp in _lamps)
        {
            if (lamp.light == null) continue;
            var lp = lamp.light.transform.position;
            if (Mathf.Abs(lp.y - p.y) > 2.6f || (lp - p).sqrMagnitude > LitRadius * LitRadius) continue;
            if (!Physics.Linecast(lp, p, ~0, QueryTriggerInteraction.Ignore)) return true;
        }
        return false;
    }

    void AddReflection(Transform house)
    {
        bool any = false;
        var b = new Bounds();
        foreach (var r in house.GetComponentsInChildren<Renderer>())
        {
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        if (!any) return;

        _reflection = new Cubemap(8, TextureFormat.RGBAHalf, false) { name = "HouseInteriorReflection" };
        FillReflection();
        var go = new GameObject("HouseInteriorReflection");
        go.transform.SetParent(house, false);
        go.transform.position = b.center;
        _probe = go.AddComponent<ReflectionProbe>();
        _probe.mode = ReflectionProbeMode.Custom;
        _probe.customBakedTexture = _reflection;
        _probe.size = b.size + Vector3.one * 0.5f;
        _probe.blendDistance = 0f;
        _probe.importance = 1;
        _owned.Add(go);
    }

    /// <summary>A bare-bulb pendant from the ceiling above every light (lights with no ceiling within 3 m get none).</summary>
    void AddFixtures()
    {
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
        if (lit == null || unlit == null) return;
        var metal = Track(new Material(lit) { name = "Fixture_Metal" });
        metal.SetColor(BaseColorId, new Color(0.09f, 0.085f, 0.08f));
        metal.SetFloat("_Metallic", 0.7f);
        metal.SetFloat("_Smoothness", 0.45f);
        var cord = Track(new Material(lit) { name = "Fixture_Cord" });
        cord.SetColor(BaseColorId, new Color(0.02f, 0.02f, 0.02f));
        cord.SetFloat("_Smoothness", 0.25f);
        var bulb = Track(new Material(unlit) { name = "Fixture_Bulb" });
        var cylinder = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
        var sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");

        // Scene root with an identity transform, so part scales are world sizes.
        var root = new GameObject("HouseLightFixtures (runtime)");
        _owned.Add(root);
        foreach (var lamp in _lamps)
        {
            if (lamp.light == null) continue;
            var p = lamp.light.transform.position;
            if (!Physics.Raycast(p, Vector3.up, out var up, 3f, ~0, QueryTriggerInteraction.Ignore)) continue;
            float ceiling = up.point.y;
            var fixture = new GameObject("Fixture_" + lamp.light.name).transform;
            fixture.SetParent(root.transform, false);

            // Ceiling rose, cord, holder, bulb (the built-in cylinder is 2 m tall, the sphere 1 m across).
            Part(fixture, cylinder, metal, new Vector3(p.x, ceiling - 0.0125f, p.z), new Vector3(0.12f, 0.0125f, 0.12f));
            float holderTop = p.y + BulbHalfHeight + 0.055f, cordTop = ceiling - 0.025f;
            if (cordTop > holderTop)
                Part(fixture, cylinder, cord, new Vector3(p.x, (cordTop + holderTop) * 0.5f, p.z), new Vector3(0.008f, (cordTop - holderTop) * 0.5f, 0.008f));
            Part(fixture, cylinder, metal, new Vector3(p.x, p.y + BulbHalfHeight + 0.027f, p.z), new Vector3(0.036f, 0.03f, 0.036f));
            lamp.bulb = Part(fixture, sphere, bulb, p, new Vector3(0.075f, BulbHalfHeight * 2f, 0.075f));
        }
    }

    static Renderer Part(Transform parent, Mesh mesh, Material material, Vector3 position, Vector3 scale)
    {
        var go = new GameObject(mesh.name);
        go.transform.SetParent(parent, false);
        go.transform.position = position;
        go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = ShadowCastingMode.Off;   // the bulb encloses its light
        r.receiveShadows = false;
        return r;
    }

    T Track<T>(T asset) where T : UnityEngine.Object
    {
        _assets.Add(asset);
        return asset;
    }

    /// <summary>Bulb colour follows its light: glass grey when off, up to <see cref="BulbGlow"/> x colour at full power.</summary>
    void UpdateBulbs()
    {
        foreach (var lamp in _lamps)
        {
            if (lamp.bulb == null || lamp.light == null) continue;
            float k = lamp.light.enabled ? lamp.light.intensity / Mathf.Max(0.01f, Base(lamp)) : 0f;
            var c = lamp.light.color.linear * (BulbGlow * k);
            _mpb.SetVector(BaseColorId, new Vector4(0.04f + c.r, 0.04f + c.g, 0.04f + c.b, 1f));
            lamp.bulb.SetPropertyBlock(_mpb);
        }
    }

    void FillReflection()
    {
        if (_reflection == null) return;
        var px = new Color[_reflection.width * _reflection.width];
        for (int i = 0; i < px.Length; i++) px[i] = ReflectionColor;
        for (int f = 0; f < 6; f++) _reflection.SetPixels(px, (CubemapFace)f);
        _reflection.Apply(false);
    }

    /// <summary>Apply stress level <paramref name="level"/> (0-5); repeated calls with the same level do nothing.</summary>
    public void SetLevel(int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        if (level == _level) return;
        _level = level;
        Refresh();
    }

    /// <summary>Re-apply the current level (after changing the static tunables).</summary>
    public void Refresh()
    {
        float k = _level >= 5 ? Level5 : _level == 4 ? Level4 : 1f;
        float now = Time.time;
        foreach (var lamp in _lamps)
        {
            if (lamp.light == null) continue;
            lamp.light.color = _level >= 5 ? Dread(lamp.color) : NeutralColour ? FillColor : lamp.color;
            lamp.light.intensity = Base(lamp) * k;
            if (lamp.added) lamp.light.range = FillRange;
            lamp.cutUntil = 0f;
            lamp.burst = 0;
            lamp.nextCut = now + Range(0.2f, 2.5f);
        }
        FillReflection();
        if (_probe != null) _probe.enabled = DarkReflections;
        UpdateBulbs();
    }

    float Base(Lamp lamp) => lamp.added ? FillIntensity : Mathf.Max(lamp.intensity * Boost, MinIntensity);

    /// <summary>The approved stress-5 light colour: a 75% blend toward red 0.60, forced near-pure red.</summary>
    static Color Dread(Color c)
    {
        float r = Mathf.Lerp(c.r, 0.60f, 0.75f);
        return new Color(r, r * 0.06f, r * 0.04f);
    }

    /// <summary>Main thread, every frame: the stress-5 flicker, and the bulbs following their lights.</summary>
    public void Tick()
    {
        if (_level >= 5) Flicker();
        UpdateBulbs();
    }

    void Flicker()
    {
        float t = Time.time;
        foreach (var lamp in _lamps)
        {
            if (lamp.light == null) continue;
            if (t >= lamp.nextCut)
            {
                bool outage = lamp.dying && lamp.burst == 0 && _rng.NextDouble() < 0.3;
                lamp.cutUntil = t + (outage ? Range(0.5f, 1.8f) : Range(0.04f, 0.2f));
                lamp.cutLevel = outage ? Range(0f, 0.04f) : Range(0.03f, 0.3f);
                if (lamp.burst > 0) lamp.burst--;
                else if (_rng.NextDouble() < 0.35) lamp.burst = _rng.Next(1, 5);
                lamp.nextCut = lamp.cutUntil + (lamp.burst > 0 ? Range(0.03f, 0.12f) : Range(0.6f, lamp.dying ? 2.5f : 5f));
            }
            float wobble = 1f + FlickerWobble * (Mathf.PerlinNoise(lamp.seed, t * 9f) - 0.5f) * 2f;
            float cut = t < lamp.cutUntil ? lamp.cutLevel : 1f;
            lamp.light.intensity = Base(lamp) * Level5 * wobble * cut;
        }
    }

    float Range(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    /// <summary>Restore the builder's lights and remove everything this class created.</summary>
    public void Dispose()
    {
        foreach (var lamp in _lamps)
        {
            if (lamp.added || lamp.light == null) continue;
            lamp.light.color = lamp.color;
            lamp.light.intensity = lamp.intensity;
            lamp.light.range = lamp.range;
            lamp.light.transform.position = lamp.position;
        }
        _lamps.Clear();
        foreach (var go in _owned) if (go != null) HorrorTextureCorruptor.SafeDestroy(go);
        _owned.Clear();
        foreach (var a in _assets) if (a != null) HorrorTextureCorruptor.SafeDestroy(a);
        _assets.Clear();
        if (_reflection != null) HorrorTextureCorruptor.SafeDestroy(_reflection);
        _reflection = null;
        _probe = null;
        _level = -1;
    }
}
