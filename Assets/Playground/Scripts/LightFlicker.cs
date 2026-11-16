using UnityEngine;

namespace MoodyBlues.Playground
{
    /// <summary>Flickers a light's intensity (and slightly its range) with Perlin noise.</summary>
    [RequireComponent(typeof(Light))]
    public class LightFlicker : MonoBehaviour
    {
        [SerializeField, Range(0f, 1f)] private float amount = 0.3f;
        [SerializeField] private float speed = 8f;
        [SerializeField, Range(0f, 0.5f)] private float rangeAmount = 0.05f;

        private Light _light;
        private float _baseIntensity;
        private float _baseRange;
        private float _seed;

        private void Awake()
        {
            _light = GetComponent<Light>();
            _baseIntensity = _light.intensity;
            _baseRange = _light.range;
            _seed = Random.value * 100f;
        }

        private void Update()
        {
            float n = Mathf.PerlinNoise(_seed, Time.time * speed) * 2f - 1f;
            _light.intensity = _baseIntensity * (1f + n * amount);
            _light.range = _baseRange * (1f + n * rangeAmount);
        }

        private void OnDisable()
        {
            if (_light == null) return;
            _light.intensity = _baseIntensity;
            _light.range = _baseRange;
        }
    }
}
