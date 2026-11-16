using UnityEngine;
using UnityEngine.InputSystem;

namespace MoodyBlues.Playground
{
    /// <summary>First-person CharacterController using the new Input System (actions built in code).</summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] private Transform cameraPivot;
        [SerializeField] private Interactor interactor;
        [SerializeField] private float walkSpeed = 4f;
        [SerializeField] private float sprintSpeed = 7f;
        [SerializeField] private float jumpHeight = 1.1f;
        [SerializeField] private float gravity = -20f;
        [SerializeField] private float lookSensitivity = 0.1f;

        private CharacterController _cc;
        private InputAction _move, _look, _jump, _sprint, _interact, _drop, _throw, _unlock;
        private float _pitch;
        private float _vy;
        private bool _locked = true;

        public bool CursorLocked => _locked;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _move = new InputAction("Move", InputActionType.Value);
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            _move.AddBinding("<Gamepad>/leftStick");
            _look = new InputAction("Look", InputActionType.Value, "<Mouse>/delta");
            _look.AddBinding("<Gamepad>/rightStick").WithProcessor("scaleVector2(x=8,y=8)");
            _jump = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
            _sprint = new InputAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
            _interact = new InputAction("Interact", InputActionType.Button, "<Keyboard>/e");
            _drop = new InputAction("Drop", InputActionType.Button, "<Keyboard>/q");
            _drop.AddBinding("<Keyboard>/g");
            _throw = new InputAction("Throw", InputActionType.Button, "<Mouse>/leftButton");
            _unlock = new InputAction("UnlockCursor", InputActionType.Button, "<Keyboard>/escape");
        }

        private void OnEnable()
        {
            foreach (var a in new[] { _move, _look, _jump, _sprint, _interact, _drop, _throw, _unlock }) a.Enable();
            SetCursorLocked(true);
        }

        private void OnDisable()
        {
            foreach (var a in new[] { _move, _look, _jump, _sprint, _interact, _drop, _throw, _unlock }) a.Disable();
            SetCursorLocked(false);
        }

        private void OnDestroy()
        {
            foreach (var a in new[] { _move, _look, _jump, _sprint, _interact, _drop, _throw, _unlock }) a?.Dispose();
        }

        private void SetCursorLocked(bool locked)
        {
            _locked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void Update()
        {
            if (_unlock.WasPressedThisFrame()) SetCursorLocked(false);
            else if (!_locked && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) { SetCursorLocked(true); return; }

            if (_locked)
            {
                Vector2 look = _look.ReadValue<Vector2>() * lookSensitivity;
                transform.Rotate(0f, look.x, 0f);
                _pitch = Mathf.Clamp(_pitch - look.y, -85f, 85f);
                cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

                if (_interact.WasPressedThisFrame()) interactor.TryInteract();
                if (_drop.WasPressedThisFrame()) interactor.Drop();
                if (_throw.WasPressedThisFrame()) interactor.Throw();
            }

            Vector2 m = _locked ? _move.ReadValue<Vector2>() : Vector2.zero;
            float speed = _sprint.IsPressed() ? sprintSpeed : walkSpeed;
            Vector3 horizontal = (transform.right * m.x + transform.forward * m.y);
            if (horizontal.sqrMagnitude > 1f) horizontal.Normalize();

            if (_cc.isGrounded && _vy < 0f) _vy = -2f;
            if (_locked && _cc.isGrounded && _jump.WasPressedThisFrame()) _vy = Mathf.Sqrt(jumpHeight * -2f * gravity);
            _vy += gravity * Time.deltaTime;

            _cc.Move((horizontal * speed + Vector3.up * _vy) * Time.deltaTime);
        }
    }
}
