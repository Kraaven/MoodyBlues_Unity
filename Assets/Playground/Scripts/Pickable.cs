using System;
using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>A rigidbody item that can be picked up, carried in front of the camera, dropped or thrown.</summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Pickable : MonoBehaviour, IInteractable
    {
        [SerializeField] private string displayName = "Item";
        [SerializeField] private string itemId = "";
        [SerializeField] private int scoreValue = 1;
        [SerializeField] private float followSpeed = 18f;

        private Rigidbody _rb;
        private Interactor _holder;

        public string DisplayName => displayName;
        public string ItemId => string.IsNullOrEmpty(itemId) ? displayName : itemId;
        public int ScoreValue => scoreValue;
        public bool IsHeld => _holder != null;
        public bool Deposited { get; private set; }
        public bool WasThrown { get; private set; }

        public event Action<Pickable> PickedUp;
        public event Action<Pickable> Released;
        public event Action<Pickable> DepositedEvent;

        private void Awake() => _rb = GetComponent<Rigidbody>();

        public string GetPrompt(Interactor i) => Deposited ? null : $"[E] Pick up {displayName}";

        public void Interact(Interactor i)
        {
            if (!Deposited) i.Hold(this);
        }

        public void Grab(Interactor i)
        {
            _holder = i;
            WasThrown = false;
            _rb.useGravity = false;
            _rb.linearDamping = 10f;
            _rb.angularDamping = 10f;
            PickedUp?.Invoke(this);
        }

        public void Release(Vector3 velocity)
        {
            _holder = null;
            WasThrown = velocity.sqrMagnitude > 1f;
            _rb.useGravity = true;
            _rb.linearDamping = 0f;
            _rb.angularDamping = 0.05f;
            _rb.linearVelocity = velocity;
            Released?.Invoke(this);
        }

        public void MarkDeposited()
        {
            Deposited = true;
            DepositedEvent?.Invoke(this);
        }

        private void FixedUpdate()
        {
            if (_holder == null) return;
            Vector3 target = _holder.HoldPoint.position;
            _rb.linearVelocity = (target - _rb.position) * followSpeed;
            _rb.angularVelocity = Vector3.zero;
        }
    }
}
