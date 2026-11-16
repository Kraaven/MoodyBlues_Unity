using System;
using System.Collections;
using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>A trigger pickup (coin) that is counted globally and pops away when the player touches it.</summary>
    [RequireComponent(typeof(Collider))]
    public class Collectible : MonoBehaviour
    {
        [SerializeField] private int value = 1;
        [SerializeField] private AudioClip pickupClip;
        [SerializeField] private float popTime = 0.25f;

        private bool _taken;
        private bool _counted;

        public static int TotalCollected { get; private set; }
        public static int TotalInScene { get; private set; }
        public int Value => value;

        public static event Action<Collectible> Collected;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            TotalCollected = 0;
            TotalInScene = 0;
            Collected = null;
        }

        private void Reset() => GetComponent<Collider>().isTrigger = true;

        private void OnEnable()
        {
            if (_taken || _counted) return;
            _counted = true;
            TotalInScene++;
        }

        private void OnDisable()
        {
            if (!_counted) return;
            _counted = false;
            TotalInScene--;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_taken || other.GetComponentInParent<PlayerController>() == null) return;
            _taken = true;
            TotalCollected += value;
            if (pickupClip != null) AudioSource.PlayClipAtPoint(pickupClip, transform.position);
            Collected?.Invoke(this);
            StartCoroutine(Pop());
        }

        private IEnumerator Pop()
        {
            Vector3 start = transform.localScale;
            float t = 0f;
            while (t < popTime)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / popTime);
                float s = k < 0.4f ? Mathf.Lerp(1f, 1.4f, k / 0.4f) : Mathf.Lerp(1.4f, 0f, (k - 0.4f) / 0.6f);
                transform.localScale = start * s;
                yield return null;
            }
            // Tracked objects must go through the recorder so the recording stays consistent.
            if (MoodyBluesRuntimeManager.Instance != null) MoodyBluesRuntimeManager.Instance.DeleteObject(gameObject);
            else Destroy(gameObject);
        }
    }
}
