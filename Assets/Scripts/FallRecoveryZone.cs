using UnityEngine;

/// <summary>Returns the player to a safe marker after falling into a stair well.</summary>
[RequireComponent(typeof(BoxCollider))]
public class FallRecoveryZone : MonoBehaviour
{
    [SerializeField] Transform recoveryPoint;

    public void Configure(Transform point)
    {
        recoveryPoint = point;
        var trigger = GetComponent<BoxCollider>();
        trigger.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        var player = other.GetComponentInParent<PlayerController>();
        if (player == null || recoveryPoint == null)
            return;

        var controller = player.GetComponent<CharacterController>();
        if (controller != null)
            controller.enabled = false;

        player.transform.SetPositionAndRotation(recoveryPoint.position, recoveryPoint.rotation);

        if (controller != null)
            controller.enabled = true;
    }
}
