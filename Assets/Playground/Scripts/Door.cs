using System;
using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>Hinged door; rotates this transform about its local Y. Optionally locked until the player holds a key.</summary>
    public class Door : MonoBehaviour, IInteractable
    {
        [SerializeField] private float openAngle = 100f;
        [SerializeField] private float speed = 4f;
        [SerializeField] private bool locked;
        [SerializeField] private string requiredItemId = "Key";
        [SerializeField] private bool consumeKey;

        private Quaternion _closed;
        private Quaternion _open;

        public bool IsOpen { get; private set; }
        public bool IsLocked => locked;

        public event Action<Door, bool> OpenChanged;
        public event Action<Door> Unlocked;

        private void Awake()
        {
            _closed = transform.localRotation;
            _open = _closed * Quaternion.Euler(0f, openAngle, 0f);
        }

        private void Update()
        {
            Quaternion target = IsOpen ? _open : _closed;
            transform.localRotation = Quaternion.Slerp(transform.localRotation, target, 1f - Mathf.Exp(-speed * Time.deltaTime));
        }

        public string GetPrompt(Interactor i)
        {
            if (locked) return "Locked (needs a key)";
            return IsOpen ? "[E] Close door" : "[E] Open door";
        }

        public void Interact(Interactor i)
        {
            if (locked) return;
            SetOpen(!IsOpen);
        }

        /// <summary>Called by a key-holding player; unlocks when the held item matches.</summary>
        public bool TryUnlock(Pickable held)
        {
            if (!locked || held == null || held.ItemId != requiredItemId) return false;
            locked = false;
            Unlocked?.Invoke(this);
            return true;
        }

        public void SetOpen(bool open)
        {
            if (IsOpen == open) return;
            IsOpen = open;
            OpenChanged?.Invoke(this, open);
        }

        public bool ConsumeKey => consumeKey;
        public string RequiredItemId => requiredItemId;
    }
}
