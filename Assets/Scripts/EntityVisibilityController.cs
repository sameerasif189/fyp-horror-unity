using UnityEngine;

/// <summary>
/// Keeps any leftover entity objects hidden. The haunted house has no creatures.
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
