using UnityEngine;

/// <summary>
/// Legacy hide-all helper. MonsterDirector owns reveal/chase now; this stays
/// disabled on HorrorSystems so leftover HorrorRoom entities remain hidden.
/// </summary>
public class EntityVisibilityController : MonoBehaviour
{
    [SerializeField] GameObject[] entities;

    void OnEnable() => HideAll();
    void Start() => HideAll();

    void HideAll()
    {
        if (entities == null) return;
        for (int i = 0; i < entities.Length; i++)
        {
            if (entities[i] != null && entities[i].activeSelf)
                entities[i].SetActive(false);
        }
    }
}
