using System;
using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>Raycasts from the camera, tracks the focused interactable and the held item.</summary>
    public class Interactor : MonoBehaviour
    {
        [SerializeField] private Camera viewCamera;
        [SerializeField] private float reach = 3f;
        [SerializeField] private LayerMask mask = ~0;
        [SerializeField] private Transform holdPoint;
        [SerializeField] private float throwForce = 9f;

        public Pickable Held { get; private set; }
        public IInteractable Focus { get; private set; }
        public Camera ViewCamera => viewCamera;
        public Transform HoldPoint => holdPoint;

        public event Action<IInteractable> FocusChanged;
        public event Action<Pickable> Picked;
        public event Action<Pickable> Dropped;
        public event Action<Pickable> Thrown;

        private void Update()
        {
            IInteractable next = null;
            if (Held == null && viewCamera != null)
            {
                Ray ray = new Ray(viewCamera.transform.position, viewCamera.transform.forward);
                if (Physics.Raycast(ray, out RaycastHit hit, reach, mask, QueryTriggerInteraction.Ignore))
                    next = hit.collider.GetComponentInParent<IInteractable>();
            }
            if (!ReferenceEquals(next, Focus))
            {
                Focus = next;
                FocusChanged?.Invoke(Focus);
            }
        }

        public string CurrentPrompt()
        {
            if (Held != null) return $"[Q] Drop {Held.DisplayName}   [LMB] Throw";
            return Focus?.GetPrompt(this);
        }

        public void TryInteract()
        {
            if (Held != null) return;
            Focus?.Interact(this);
        }

        public void Hold(Pickable p)
        {
            if (Held != null || p == null) return;
            Held = p;
            p.Grab(this);
            Picked?.Invoke(p);
        }

        public void Drop()
        {
            if (Held == null) return;
            Pickable p = Held;
            Held = null;
            p.Release(Vector3.zero);
            Dropped?.Invoke(p);
        }

        public void Throw()
        {
            if (Held == null) return;
            Pickable p = Held;
            Held = null;
            p.Release(viewCamera.transform.forward * throwForce);
            Thrown?.Invoke(p);
        }
    }
}
