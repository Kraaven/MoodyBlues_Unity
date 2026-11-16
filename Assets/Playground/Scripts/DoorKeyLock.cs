using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>
    /// Trigger in front of a locked door: unlocks it when the player carrying the matching key walks into it
    /// (the interactor cannot interact while holding, so the lock reacts to the held item instead).
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class DoorKeyLock : MonoBehaviour
    {
        [SerializeField] private Door door;
        [SerializeField] private Interactor player;

        private void OnTriggerStay(Collider other)
        {
            if (door == null || player == null || !door.IsLocked) return;
            if (other.GetComponentInParent<Interactor>() != player) return;
            Pickable held = player.Held;
            if (!door.TryUnlock(held)) return;
            if (door.ConsumeKey)
            {
                player.Drop();
                if (MoodyBluesRuntimeManager.Instance != null) MoodyBluesRuntimeManager.Instance.DeleteObject(held.gameObject);
                else Destroy(held.gameObject);
            }
        }
    }
}
