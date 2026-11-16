using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>Awards points once per thrown Pickable that hits it.</summary>
    public class ThrowTarget : MonoBehaviour
    {
        [SerializeField] private int points = 10;
        [SerializeField] private float punchTime = 0.25f;
        [SerializeField] private float punchScale = 0.25f;

        private readonly HashSet<Pickable> _hitBy = new();
        private Vector3 _baseScale;
        private Coroutine _punch;

        public static int TotalHitScore { get; private set; }

        public static event Action<ThrowTarget, Pickable, int> Hit;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            TotalHitScore = 0;
            Hit = null;
        }

        private void Awake() => _baseScale = transform.localScale;

        private void OnCollisionEnter(Collision c)
        {
            Pickable p = c.collider.GetComponentInParent<Pickable>();
            if (p == null || p.IsHeld || p.Deposited || !p.WasThrown || !_hitBy.Add(p)) return;
            TotalHitScore += points;
            Hit?.Invoke(this, p, points);
            if (_punch != null) StopCoroutine(_punch);
            _punch = StartCoroutine(Punch());
        }

        private IEnumerator Punch()
        {
            float t = 0f;
            while (t < punchTime)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / punchTime);
                float squash = Mathf.Sin(k * Mathf.PI) * punchScale;
                transform.localScale = new Vector3(_baseScale.x * (1f + squash), _baseScale.y * (1f - squash), _baseScale.z * (1f + squash));
                yield return null;
            }
            transform.localScale = _baseScale;
            _punch = null;
        }
    }
}
