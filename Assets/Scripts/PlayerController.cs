using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] float walkSpeed = 5f;
    [SerializeField] float sprintSpeed = 8f;
    [SerializeField] float jumpHeight = 1.2f;
    [SerializeField] float gravity = -15f;

    [Header("Look")]
    [SerializeField] Transform cameraPivot;
    [SerializeField] float lookSensitivity = 0.15f;
    [SerializeField] float minPitch = -80f;
    [SerializeField] float maxPitch = 80f;

    [Header("Input")]
    [SerializeField] InputActionAsset inputActions;

    CharacterController _controller;
    InputAction _moveAction;
    InputAction _lookAction;
    InputAction _jumpAction;
    InputAction _sprintAction;

    Vector3 _velocity;
    float _pitch;
    bool _cursorLocked = true;

    void Awake()
    {
        _controller = GetComponent<CharacterController>();

        if (cameraPivot == null)
        {
            var cam = GetComponentInChildren<Camera>();
            if (cam != null)
                cameraPivot = cam.transform;
        }

        if (inputActions == null)
        {
            inputActions = Resources.Load<InputActionAsset>("InputSystem_Actions");
        }
    }

    void OnEnable()
    {
        if (inputActions == null)
            return;

        var map = inputActions.FindActionMap("Player");
        _moveAction = map.FindAction("Move");
        _lookAction = map.FindAction("Look");
        _jumpAction = map.FindAction("Jump");
        _sprintAction = map.FindAction("Sprint");
        map.Enable();
        SetCursorLocked(true);
    }

    void OnDisable()
    {
        inputActions?.FindActionMap("Player")?.Disable();
        SetCursorLocked(false);
    }

    void Update()
    {
        if (GameStartMenu.Instance != null && !GameStartMenu.GameplayActive)
            return;

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            SetCursorLocked(!_cursorLocked);

        HandleLook();
        HandleMove();
    }

    void HandleLook()
    {
        if (!_cursorLocked || _lookAction == null || cameraPivot == null)
            return;

        Vector2 look = _lookAction.ReadValue<Vector2>();
        transform.Rotate(0f, look.x * lookSensitivity, 0f);

        _pitch -= look.y * lookSensitivity;
        _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
        cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }

    void HandleMove()
    {
        bool grounded = _controller.isGrounded;
        if (grounded && _velocity.y < 0f)
            _velocity.y = -2f;

        Vector2 moveInput = _moveAction != null ? _moveAction.ReadValue<Vector2>() : Vector2.zero;
        bool sprinting = _sprintAction != null && _sprintAction.IsPressed();
        float speed = sprinting ? sprintSpeed : walkSpeed;

        Vector3 move = transform.right * moveInput.x + transform.forward * moveInput.y;
        _controller.Move(move * speed * Time.deltaTime);

        if (grounded && _jumpAction != null && _jumpAction.WasPressedThisFrame())
            _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);

        _velocity.y += gravity * Time.deltaTime;
        var hit = _controller.Move(_velocity * Time.deltaTime);
        // Head hit the ceiling: stop rising. The 1.2 m jump reaches past the 3.0 m ground-floor and basement
        // ceilings (2 m capsule), and without this the leftover upward speed pinned the player there for ~0.2-0.5 s.
        if ((hit & CollisionFlags.Above) != 0 && _velocity.y > 0f)
            _velocity.y = 0f;
    }

    void SetCursorLocked(bool locked)
    {
        _cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}