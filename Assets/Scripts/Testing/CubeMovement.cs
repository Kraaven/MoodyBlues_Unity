using System.Collections;
using UnityEngine;

namespace MoodyBlues.Samples
{
    public class CubeMovement : MonoBehaviour
    {
        [SerializeField] private float radius = 5f;
        [SerializeField] private float minSpeed = 0.1f;
        [SerializeField] private float maxSpeed = 1f;
        public float speed = 0;

        private Vector3 origin;

        private void Start()
        {
            origin = transform.position;
            StartCoroutine(WanderRoutine());
        }

        private IEnumerator WanderRoutine()
        {
            while (true)
            {
                // Pick a random point inside a sphere of given radius around origin
                Vector2 randomCircle = Random.insideUnitCircle * radius;
                Vector3 targetPosition = origin + new Vector3(randomCircle.x, 0f, randomCircle.y);

                Vector3 startPosition = transform.position;
                float distance = Vector3.Distance(startPosition, targetPosition);

                // Skip movement if target happens to be the exact current position
                if (distance > 0.001f)
                {
                    speed = Random.Range(minSpeed, maxSpeed);
                    float duration = distance / speed;
                    float elapsedTime = 0f;

                    while (elapsedTime < duration)
                    {
                        elapsedTime += Time.deltaTime;
                        float t = Mathf.Clamp01(elapsedTime / duration);

                        // Linear interpolation over duration gives constant velocity
                        transform.position = Vector3.Lerp(startPosition, targetPosition, t);
                        yield return null;
                    }

                    transform.position = targetPosition;
                }

                yield return null;
            }
        }

        private void OnDrawGizmosSelected()
        {
            // Visualize the wander bounds in the scene view
            Gizmos.color = Color.cyan;
            Vector3 center = Application.isPlaying ? origin : transform.position;
            Gizmos.DrawWireSphere(center, radius);
        }


    }

}
