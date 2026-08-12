using UnityEngine;

/// <summary>
/// Simple idle sway/bob for entities without animation clips.
/// </summary>
public class EntityIdleMotion : MonoBehaviour
{
    [SerializeField] float swayDegrees = 6f;
    [SerializeField] float swaySpeed = 1.2f;
    [SerializeField] float bobHeight = 0.08f;
    [SerializeField] float bobSpeed = 1.6f;

    Vector3 _basePos;
    Quaternion _baseRot;
    float _seed;

    void OnEnable()
    {
        _basePos = transform.localPosition;
        _baseRot = transform.localRotation;
        _seed = Random.value * 20f;
    }

    void Update()
    {
        float t = Time.time + _seed;
        float yaw = Mathf.Sin(t * swaySpeed) * swayDegrees;
        float pitch = Mathf.Sin(t * swaySpeed * 0.7f) * (swayDegrees * 0.35f);
        transform.localRotation = _baseRot * Quaternion.Euler(pitch, yaw, 0f);
        transform.localPosition = _basePos + Vector3.up * (Mathf.Sin(t * bobSpeed) * bobHeight);
    }
}