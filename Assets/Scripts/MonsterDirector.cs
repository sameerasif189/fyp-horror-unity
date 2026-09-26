using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Stress/fusion-driven monster director. Spawns chase agents mainly at levels 4–5
/// on basement HouseZone spawn points, avoiding the player's view cone.
/// </summary>
public class MonsterDirector : MonoBehaviour
{
    public static MonsterDirector Instance { get; private set; }

    [SerializeField] MonsterAgent[] monsters;
    [SerializeField] float minSpawnDistance = 6f;
    [SerializeField] float viewAvoidDegrees = 55f;
    [SerializeField] float respawnCooldown = 6f;
    [SerializeField] int maxActive = 1;

    readonly List<Transform> _spawns = new();
    readonly List<Transform> _patrol = new();
    readonly HashSet<MonsterAgent> _active = new();
    // Reused scratch list of spawn candidates, so picking one allocates nothing per spawn.
    readonly List<MonsterAgent> _free = new();
    float _cooldown;
    int _lastStress = -1;
    FusionDirector.FusionParams _fusion;

    public int ActiveCount => _active.Count;
    public bool AnyChasing
    {
        get
        {
            foreach (var m in _active)
                if (m != null && m.CurrentState == MonsterAgent.State.Chase)
                    return true;
            return false;
        }
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void OnEnable()
    {
        RefreshZones();
        TrySubscribe();
    }

    void Start()
    {
        RefreshZones();
        TrySubscribe();
        if (FusionDirector.Instance != null && FusionDirector.Instance.Latest != null)
            OnFusion(FusionDirector.Instance.Latest);
        ApplyStress(StressController.Instance != null ? StressController.Instance.StressLevel : 0);
    }

    void OnDisable()
    {
        if (StressController.Instance != null)
            StressController.Instance.OnStressChanged -= OnStress;
        if (FusionDirector.Instance != null)
            FusionDirector.Instance.OnParamsChanged -= OnFusion;
    }

    void Update()
    {
        if (GameStartMenu.Instance != null && !GameStartMenu.GameplayActive)
            return;

        if (_cooldown > 0f)
            _cooldown -= Time.deltaTime;

        int stress = StressController.Instance != null ? StressController.Instance.StressLevel : 0;
        if (stress >= 4 && _active.Count < maxActive && _cooldown <= 0f)
            TrySpawn(stress);
    }

    public void BindMonsters(MonsterAgent[] agents)
    {
        monsters = agents ?? System.Array.Empty<MonsterAgent>();
        RefreshZones();
    }

    public void NotifyMonsterDespawned(MonsterAgent agent)
    {
        _active.Remove(agent);
        _cooldown = respawnCooldown;
    }

    void TrySubscribe()
    {
        if (StressController.Instance != null)
        {
            StressController.Instance.OnStressChanged -= OnStress;
            StressController.Instance.OnStressChanged += OnStress;
        }
        if (FusionDirector.Instance != null)
        {
            FusionDirector.Instance.OnParamsChanged -= OnFusion;
            FusionDirector.Instance.OnParamsChanged += OnFusion;
        }
    }

    void OnStress(int level) => ApplyStress(level);

    void OnFusion(FusionDirector.FusionParams p)
    {
        _fusion = p;
        if (p != null)
            ApplyStress(p.stressLevel);
    }

    void ApplyStress(int level)
    {
        level = Mathf.Clamp(level, 0, 5);
        if (level == _lastStress) return;
        _lastStress = level;

        if (level < 4)
        {
            DespawnAll();
            return;
        }

        float chance = _fusion != null ? _fusion.entity_probability : (level >= 5 ? 0.9f : 0.65f);
        if (chance < 0.2f)
            chance = level >= 5 ? 0.75f : 0.55f;

        if (_active.Count == 0 && Random.value <= chance)
            TrySpawn(level);
    }

    void TrySpawn(int level)
    {
        if (monsters == null || monsters.Length == 0) return;
        RefreshZones();
        if (_spawns.Count == 0) return;

        var player = FindPlayer();
        if (player == null) return;

        Transform spawn = PickHiddenSpawn(player);
        if (spawn == null) return;

        // Pick at RANDOM among the free monsters. (Taking the first free entry used to spawn monsters[0] every time.)
        // At stress 5, only monsters whose corrupted skin is already painted: a monster repaints for 3-5 s after it
        // despawns, and cycling quickly (the demo) otherwise spawned it with a clean skin.
        _free.Clear();
        for (int i = 0; i < monsters.Length; i++)
        {
            var m = monsters[i];
            if (m == null) continue;
            if (_active.Contains(m)) continue;
            if (m.IsActiveThreat)
            {
                // A parked monster still flagged as a threat would be skipped for the rest of the session - how
                // Tillagemon went 0 of 38 spawns on 26 Sep 2026 while the other six got 5-9 each.
                if (m.gameObject.activeSelf) continue;
                Debug.LogWarning($"[MonsterDirector] {m.name} is parked but still flagged {m.CurrentState} - resetting it so it can spawn again");
                m.ForceDespawn();
            }
            if (level >= 5)
            {
                var skin = m.GetComponent<MonsterCorruptionController>();
                if (skin != null && !skin.SkinReady) continue;
            }
            _free.Add(m);
        }
        if (_free.Count == 0) return;   // everyone free is still repainting: try again next frame
        MonsterAgent free = _free[Random.Range(0, _free.Count)];

        float aggression = _fusion != null ? _fusion.entity_aggression : (level >= 5 ? 0.9f : 0.55f);
        float opacity = _fusion != null ? Mathf.Max(0.45f, _fusion.entity_opacity) : 1f;
        free.Configure(player, _patrol, free.transform.localScale.x);
        free.Activate(spawn.position, aggression, opacity);
        _active.Add(free);
        _cooldown = 2f;
        Debug.Log($"[MonsterDirector] spawned {free.name} at {spawn.name} stress={level} agg={aggression:F2}");
    }

    Transform PickHiddenSpawn(Transform player)
    {
        Camera cam = Camera.main;
        Vector3 camFwd = cam != null ? cam.transform.forward : player.forward;
        Vector3 camPos = cam != null ? cam.transform.position : player.position;

        Transform best = null;
        float bestScore = float.MinValue;
        for (int i = 0; i < _spawns.Count; i++)
        {
            var s = _spawns[i];
            if (s == null) continue;
            float dist = Vector3.Distance(s.position, player.position);
            if (dist < minSpawnDistance) continue;

            Vector3 toSpawn = (s.position - camPos).normalized;
            float angle = Vector3.Angle(camFwd, toSpawn);
            if (angle < viewAvoidDegrees) continue;

            // Prefer farther, more off-angle spawns.
            float score = angle + dist * 0.35f + Random.Range(0f, 4f);
            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }
        return best;
    }

    void DespawnAll()
    {
        if (monsters == null) return;
        for (int i = 0; i < monsters.Length; i++)
        {
            var m = monsters[i];
            if (m == null) continue;
            if (m.IsActiveThreat || m.gameObject.activeSelf)
                m.ForceDespawn();
        }
        _active.Clear();
    }

    public void RefreshZones()
    {
        _spawns.Clear();
        _patrol.Clear();
        var zones = Object.FindObjectsByType<HouseZone>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < zones.Length; i++)
        {
            var z = zones[i];
            if (z == null) continue;
            if (z.Kind == HouseZone.ZoneKind.MonsterSpawn)
                _spawns.Add(z.transform);
            else if (z.Kind == HouseZone.ZoneKind.Patrol)
                _patrol.Add(z.transform);
        }
    }

    static Transform FindPlayer()
    {
        var tagged = GameObject.FindGameObjectWithTag("Player");
        if (tagged != null) return tagged.transform;
        var pc = Object.FindAnyObjectByType<PlayerController>();
        return pc != null ? pc.transform : null;
    }
}
