using UnityEngine;

/// <summary>
/// Shows horror entities at high stress (4-5), hides them at 0-3.
/// Plays Animator idle/motion clips when revealed.
/// </summary>
public class EntityVisibilityController : MonoBehaviour
{
    [SerializeField] GameObject[] entities;
    [SerializeField] int revealFromLevel = 4;
    [SerializeField] int allEntitiesFromLevel = 5;

    void OnEnable()
    {
        TrySubscribe();
        Apply(StressController.Instance != null ? StressController.Instance.StressLevel : 0);
    }

    void Start()
    {
        TrySubscribe();
        Apply(StressController.Instance != null ? StressController.Instance.StressLevel : 0);
    }

    void OnDisable()
    {
        if (StressController.Instance != null)
            StressController.Instance.OnStressChanged -= Apply;
    }

    void TrySubscribe()
    {
        if (StressController.Instance == null) return;
        StressController.Instance.OnStressChanged -= Apply;
        StressController.Instance.OnStressChanged += Apply;
    }

    public void Apply(int level)
    {
        if (entities == null) return;

        for (int i = 0; i < entities.Length; i++)
        {
            var go = entities[i];
            if (go == null) continue;

            // At 4: first half; at 5: all.
            bool show = level >= allEntitiesFromLevel
                || (level >= revealFromLevel && i < Mathf.CeilToInt(entities.Length * 0.5f));

            if (go.activeSelf != show)
                go.SetActive(show);

            if (show)
                PlayAnims(go);
        }

        Debug.Log($"[Entities] stress={level} revealFrom={revealFromLevel}");
    }

    static void PlayAnims(GameObject go)
    {
        var animators = go.GetComponentsInChildren<Animator>(true);
        foreach (var a in animators)
        {
            a.enabled = true;
            a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            a.speed = 1f;
            if (a.runtimeAnimatorController != null)
                a.Play(0, 0, 0f);
        }

        var legacy = go.GetComponentsInChildren<Animation>(true);
        foreach (var anim in legacy)
        {
            anim.enabled = true;
            anim.playAutomatically = true;
            if (anim.clip != null)
                anim.Play();
            else
            {
                foreach (AnimationState st in anim)
                {
                    anim.Play(st.name);
                    break;
                }
            }
        }
    }
}