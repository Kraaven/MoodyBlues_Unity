using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>Rotates around an axis and optionally bobs up and down.</summary>
    public class Spinner : MonoBehaviour
    {
        [SerializeField] private Vector3 axis = Vector3.up;
        [SerializeField] private float degreesPerSecond = 90f;
        [SerializeField] private bool bob = true;
        [SerializeField] private float bobAmplitude = 0.15f;
        [SerializeField] private float bobFrequency = 1f;

        private Vector3 _startLocalPos;
        private float _phase;

        private void Awake()
        {
            _startLocalPos = transform.localPosition;
            _phase = Random.value * Mathf.PI * 2f;
        }

        private void Update()
        {
            transform.Rotate(axis, degreesPerSecond * Time.deltaTime, Space.Self);
            if (!bob) return;
            float y = Mathf.Sin(Time.time * bobFrequency * Mathf.PI * 2f + _phase) * bobAmplitude;
            transform.localPosition = _startLocalPos + new Vector3(0f, y, 0f);
        }
    }
}
