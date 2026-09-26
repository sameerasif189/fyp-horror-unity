using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Basement chase AI: dormant → reveal → patrol → chase → cooldown.
/// Uses NavMeshAgent for movement; Animator Speed if present, else EntityIdleMotion.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class MonsterAgent : MonoBehaviour
{
    public enum State
    {
        Dormant,
        Reveal,
        Patrol,
        Chase,
        Cooldown
    }

    [Header("Detection")]
    [SerializeField] float hearRange = 7f;
    [SerializeField] float sightRange = 14f;
    [SerializeField] float sightAngle = 70f;
    [SerializeField] float loseSightSeconds = 3.5f;
    [SerializeField] float revealSeconds = 0.65f;
    [SerializeField] float cooldownSeconds = 8f;

    [Header("Movement")]
    [SerializeField] float patrolSpeed = 2.2f;
    [SerializeField] float chaseSpeed = 4.4f;
    [SerializeField] float patrolArriveDistance = 1.1f;

    public State CurrentState { get; private set; } = State.Dormant;
    public bool IsActiveThreat =>
        CurrentState == State.Reveal || CurrentState == State.Patrol || CurrentState == State.Chase;

    NavMeshAgent _agent;
    Animator _animator;
    EntityIdleMotion _idleMotion;
    Renderer[] _renderers;
    Transform _player;
    readonly List<Transform> _patrol = new();
    int _patrolIndex;
    float _stateTimer;
    float _lostTimer;
    float _aggression = 0.5f;
    float _opacity = 1f;
    int _speedHash;
    /// <summary>Frames to keep re-seating after a spawn while the rig settles into its pose.</summary>
    const int SeatSettleFrames = 10;
    int _seatFramesLeft;

    void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _animator = GetComponentInChildren<Animator>();
        _idleMotion = GetComponentInChildren<EntityIdleMotion>();
        _renderers = GetComponentsInChildren<Renderer>(true);
        _speedHash = Animator.StringToHash("Speed");
        ConfigureAgentDefaults();
        SetVisualOpacity(0f);
        // Do NOT SetActive(false) here: first Activate() would re-enter Awake and instantly
        // deactivate the monster again. Builder parks agents inactive until spawn.
    }

    void ConfigureAgentDefaults()
    {
        _agent.acceleration = 10f;
        _agent.angularSpeed = 220f;
        _agent.stoppingDistance = 1.35f;
        _agent.autoBraking = true;
        _agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
    }

    public void Configure(Transform player, IList<Transform> patrolPoints, float scale)
    {
        _player = player;
        _patrol.Clear();
        if (patrolPoints != null)
        {
            for (int i = 0; i < patrolPoints.Count; i++)
                if (patrolPoints[i] != null)
                    _patrol.Add(patrolPoints[i]);
        }

        transform.localScale = Vector3.one * Mathf.Max(0.01f, scale);
        if (_idleMotion != null)
            _idleMotion.enabled = _animator == null;
    }

    public void Activate(Vector3 spawnPosition, float aggression, float opacity)
    {
        _aggression = Mathf.Clamp01(aggression);
        _opacity = Mathf.Clamp01(Mathf.Max(0.35f, opacity));
        CurrentState = State.Reveal;
        _stateTimer = 0f;
        _lostTimer = 0f;
        gameObject.SetActive(true);

        if (_agent != null)
        {
            _agent.enabled = false;
            transform.position = spawnPosition;
            _agent.enabled = true;
            _agent.Warp(spawnPosition);
            _agent.isStopped = true;
            _agent.speed = patrolSpeed;
        }
        else
        {
            transform.position = spawnPosition;
        }

        SetVisualOpacity(0f);
        _seatFramesLeft = SeatSettleFrames;
        FaceNearestPatrolOrPlayer();
    }

    public void ForceDespawn()
    {
        EnterCooldown(immediate: true);
    }

    void Update()
    {
        if (GameStartMenu.Instance != null && !GameStartMenu.GameplayActive)
            return;
        if (_player == null)
            _player = FindPlayer();

        _stateTimer += Time.deltaTime;
        switch (CurrentState)
        {
            case State.Reveal:
                TickReveal();
                break;
            case State.Patrol:
                TickPatrol();
                break;
            case State.Chase:
                TickChase();
                break;
            case State.Cooldown:
                TickCooldown();
                break;
        }

        UpdateAnimator();
    }

    void TickReveal()
    {
        float u = Mathf.Clamp01(_stateTimer / Mathf.Max(0.1f, revealSeconds));
        SetVisualOpacity(Mathf.Lerp(0f, _opacity, u));
        if (u < 1f) return;

        CurrentState = State.Patrol;
        _stateTimer = 0f;
        if (_agent != null)
        {
            _agent.isStopped = false;
            _agent.speed = Mathf.Lerp(patrolSpeed * 0.85f, patrolSpeed * 1.15f, _aggression);
            GoToNextPatrol();
        }
    }

    void TickPatrol()
    {
        if (_agent != null && _agent.enabled && !_agent.pathPending)
        {
            if (!_agent.hasPath || _agent.remainingDistance <= patrolArriveDistance)
                GoToNextPatrol();
        }

        if (CanDetectPlayer())
            BeginChase();
    }

    void TickChase()
    {
        if (_player == null || _agent == null || !_agent.enabled)
        {
            EnterCooldown(immediate: false);
            return;
        }

        _agent.speed = Mathf.Lerp(chaseSpeed * 0.85f, chaseSpeed * 1.25f, _aggression);
        if (_agent.isOnNavMesh)
            _agent.SetDestination(_player.position);

        if (CanDetectPlayer())
        {
            _lostTimer = 0f;
            return;
        }

        _lostTimer += Time.deltaTime;
        float loseLimit = Mathf.Lerp(loseSightSeconds * 1.25f, loseSightSeconds * 0.7f, _aggression);
        if (_lostTimer >= loseLimit)
            EnterCooldown(immediate: false);
    }

    void TickCooldown()
    {
        float u = Mathf.Clamp01(_stateTimer / Mathf.Max(0.1f, cooldownSeconds * 0.25f));
        SetVisualOpacity(Mathf.Lerp(_opacity, 0f, u));
        if (_stateTimer < cooldownSeconds) return;

        CurrentState = State.Dormant;
        if (_agent != null)
        {
            _agent.isStopped = true;
            _agent.ResetPath();
        }
        gameObject.SetActive(false);
        MonsterDirector.Instance?.NotifyMonsterDespawned(this);
    }

    void BeginChase()
    {
        CurrentState = State.Chase;
        _stateTimer = 0f;
        _lostTimer = 0f;
        if (_agent != null)
        {
            _agent.isStopped = false;
            _agent.speed = chaseSpeed;
            if (_player != null && _agent.isOnNavMesh)
                _agent.SetDestination(_player.position);
        }
    }

    void EnterCooldown(bool immediate)
    {
        CurrentState = State.Cooldown;
        _stateTimer = immediate ? cooldownSeconds : 0f;
        if (_agent != null)
        {
            _agent.isStopped = true;
            _agent.ResetPath();
        }
        if (immediate)
        {
            SetVisualOpacity(0f);
            gameObject.SetActive(false);
            CurrentState = State.Dormant;
            MonsterDirector.Instance?.NotifyMonsterDespawned(this);
        }
    }

    void GoToNextPatrol()
    {
        if (_patrol.Count == 0 || _agent == null || !_agent.isOnNavMesh)
            return;

        _patrolIndex = (_patrolIndex + 1) % _patrol.Count;
        var target = _patrol[_patrolIndex];
        if (target != null)
            _agent.SetDestination(target.position);
    }

    bool CanDetectPlayer()
    {
        if (_player == null) return false;
        Vector3 toPlayer = _player.position - transform.position;
        float dist = toPlayer.magnitude;
        float hear = hearRange * Mathf.Lerp(0.75f, 1.35f, _aggression);
        if (dist <= hear)
            return true;

        float sight = sightRange * Mathf.Lerp(0.8f, 1.3f, _aggression);
        if (dist > sight)
            return false;

        Vector3 flat = toPlayer;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.01f)
            return true;

        float angle = Vector3.Angle(transform.forward, flat.normalized);
        if (angle > sightAngle)
            return false;

        Vector3 eye = transform.position + Vector3.up * 1.4f;
        Vector3 target = _player.position + Vector3.up * 1.0f;
        if (Physics.Linecast(eye, target, out var hit, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.transform != _player && !hit.transform.IsChildOf(_player))
                return false;
        }
        return true;
    }

    void FaceNearestPatrolOrPlayer()
    {
        Vector3 look = transform.position + transform.forward;
        if (_patrol.Count > 0 && _patrol[0] != null)
            look = _patrol[0].position;
        else if (_player != null)
            look = _player.position;
        look.y = transform.position.y;
        if ((look - transform.position).sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.LookRotation((look - transform.position).normalized);
    }

    /// <summary>
    /// Drop the visual so its lowest point rests at the agent's feet.
    ///
    /// Several entity GLBs are pivoted at their centre rather than their base, which sank them
    /// into the floor - CameramanFred was buried ~1.2m at the shipped scale, DistortusRex ~0.6m.
    /// The bug was latent for a long time because MonsterDirector only ever spawned monsters[0]
    /// (Illiakan), whose pivot happens to be correct.
    ///
    /// This runs per spawn rather than being baked by the builder on purpose: a SkinnedMeshRenderer
    /// reports BIND POSE bounds outside play mode, which differ substantially from the animated
    /// pose (measured 2.53 vs 1.70 for CameramanFred), so a baked offset would be wrong in game.
    ///
    /// It is driven from LateUpdate for SeatSettleFrames after a spawn rather than once from
    /// Activate(): on the activation frame the skinned renderers still report collapsed bounds
    /// (measured 0.04 against the 1.73 actually needed), so a single early call corrected almost
    /// nothing. LateUpdate runs after the animator has written the pose.
    /// </summary>
    void SeatVisualOnGround()
    {
        if (_renderers == null || _renderers.Length == 0) return;
        var visual = _animator != null ? _animator.transform : transform.Find("Visual");
        if (visual == null || visual == transform) return;

        // What to move. With an Animator (Illiakan, Velociraptor) the Visual itself is safe: the
        // animator never writes its own transform. WITHOUT one (DistortusRex, CameramanFred) the
        // Visual carries EntityIdleMotion, which caches its localPosition in OnEnable and rewrites
        // it every Update - so seating the Visual was undone one frame later (DistortusRex sat
        // 0.93m deep). Seat the import node beneath it instead; nothing drives that node when
        // there are no clips.
        Transform target = visual;
        if (_animator == null && _idleMotion != null && _idleMotion.transform == visual)
        {
            var node = visual.Find("Sketchfab_model") ?? visual.Find("root");
            if (node == null && visual.childCount > 0) node = visual.GetChild(0);
            if (node != null) target = node;
        }

        float lowest = LowestPointY(out bool any);
        if (!any) return;

        float footGap = lowest - transform.position.y;
        if (Mathf.Abs(footGap) < 0.01f) return;
        // World-space move, so it is correct whatever scale sits between the root and the target.
        target.position += Vector3.up * -footGap;
    }

    /// <summary>
    /// World height of the monster's lowest point. Skinned meshes are skinned on the CPU here (bone x bindpose, a few
    /// thousand sampled vertices) instead of trusting renderer bounds, which can sit far below the animated body:
    /// measured 26 Sep 2026, Tillagemon's (import-pose box) 0.81-0.91 m and FingerMaiden's (estimated box) 0.52-0.63 m
    /// below it, so both were seated floating by that much. Unreadable meshes fall back to their bounds.
    /// </summary>
    float LowestPointY(out bool any)
    {
        any = false;
        float min = float.MaxValue;
        for (int i = 0; i < _renderers.Length; i++)
        {
            var r = _renderers[i];
            if (r == null || !r.gameObject.activeInHierarchy) continue;
            float y = r is SkinnedMeshRenderer smr ? SkinnedMinY(smr) : r.bounds.min.y;
            if (y < min) min = y;
            any = true;
        }
        return min;
    }

    sealed class SkinCache
    {
        public Mesh mesh;
        public Vector3[] verts;
        public BoneWeight[] weights;
        public Matrix4x4[] bind;
        public Matrix4x4[] mats;
    }

    readonly Dictionary<SkinnedMeshRenderer, SkinCache> _skinCache = new();

    float SkinnedMinY(SkinnedMeshRenderer smr)
    {
        var mesh = smr.sharedMesh;
        if (mesh == null || !mesh.isReadable) return smr.bounds.min.y;
        if (!_skinCache.TryGetValue(smr, out var c) || c.mesh != mesh)
        {
            c = new SkinCache { mesh = mesh, verts = mesh.vertices, weights = mesh.boneWeights, bind = mesh.bindposes };
            c.mats = new Matrix4x4[c.bind.Length];
            _skinCache[smr] = c;
        }
        var bones = smr.bones;
        if (c.weights.Length != c.verts.Length || bones.Length != c.bind.Length) return smr.bounds.min.y;
        for (int b = 0; b < bones.Length; b++)
            c.mats[b] = bones[b] != null ? bones[b].localToWorldMatrix * c.bind[b] : smr.transform.localToWorldMatrix;
        int stride = Mathf.Max(1, c.verts.Length / 4000);
        float min = float.MaxValue;
        for (int v = 0; v < c.verts.Length; v += stride)
        {
            var w = c.weights[v];
            var p = c.verts[v];
            float y = c.mats[w.boneIndex0].MultiplyPoint3x4(p).y * w.weight0 + c.mats[w.boneIndex1].MultiplyPoint3x4(p).y * w.weight1
                    + c.mats[w.boneIndex2].MultiplyPoint3x4(p).y * w.weight2 + c.mats[w.boneIndex3].MultiplyPoint3x4(p).y * w.weight3;
            if (y < min) min = y;
        }
        return min;
    }

    void SetVisualOpacity(float a)
    {
        a = Mathf.Clamp01(a);
        if (_renderers == null) return;
        for (int i = 0; i < _renderers.Length; i++)
        {
            var r = _renderers[i];
            if (r == null) continue;
            var mats = r.materials;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (mat == null) continue;
                if (mat.HasProperty("_BaseColor"))
                {
                    var c = mat.GetColor("_BaseColor");
                    c.a = a;
                    mat.SetColor("_BaseColor", c);
                }
                if (mat.HasProperty("_Color"))
                {
                    var c = mat.GetColor("_Color");
                    c.a = a;
                    mat.SetColor("_Color", c);
                }
            }
        }
    }

    void LateUpdate()
    {
        if (_seatFramesLeft <= 0) return;
        _seatFramesLeft--;
        SeatVisualOnGround();
    }

    void UpdateAnimator()
    {
        if (_animator == null || !_animator.isActiveAndEnabled) return;
        float speed = 0f;
        if (_agent != null && _agent.enabled && !_agent.isStopped)
            speed = _agent.velocity.magnitude;
        if (_animator.parameters == null) return;
        for (int i = 0; i < _animator.parameterCount; i++)
        {
            var p = _animator.GetParameter(i);
            if (p.nameHash == _speedHash && p.type == AnimatorControllerParameterType.Float)
            {
                _animator.SetFloat(_speedHash, speed);
                break;
            }
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
