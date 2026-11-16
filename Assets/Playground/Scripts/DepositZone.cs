using System;
using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>Trigger volume (chest): consumes loose Pickables that enter it and scores them.</summary>
    [RequireComponent(typeof(Collider))]
    public class DepositZone : MonoBehaviour
    {
        [SerializeField] private Inventory inventory;

        public event Action<Pickable> ItemDeposited;

        private void OnTriggerEnter(Collider other) => TryDeposit(other);
        private void OnTriggerStay(Collider other) => TryDeposit(other);

        private void TryDeposit(Collider other)
        {
            Pickable p = other.GetComponentInParent<Pickable>();
            if (p == null || p.IsHeld || p.Deposited) return;
            p.MarkDeposited();
            if (inventory != null) inventory.Deposit(p);
            ItemDeposited?.Invoke(p);
            // Park the item inside the chest, out of play.
            Rigidbody rb = p.GetComponent<Rigidbody>();
            rb.isKinematic = true;
            p.transform.position = transform.position;
            foreach (Collider c in p.GetComponentsInChildren<Collider>()) c.enabled = false;
            foreach (Renderer r in p.GetComponentsInChildren<Renderer>()) r.enabled = false;
        }
    }
}
