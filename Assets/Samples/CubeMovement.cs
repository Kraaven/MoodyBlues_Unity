using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MoodyBlues.Samples
{
    public class CubeMovement : MonoBehaviour
    {
        [SerializeField] private float radius = 5f;
        [SerializeField] private float minSpeed = 0.1f;
        [SerializeField] private float maxSpeed = 1f;
        public float speed = 0;

        [SerializeField] private Transform CubeHolder;
        [SerializeField] private float minReparentInterval = 5f;
        [SerializeField] private float maxReparentInterval = 15f;

        private Vector3 origin;

        private void Start()
        {
            origin = transform.position;

            GameObject holder = GameObject.Find("CubesHolder");
            CubeHolder = holder != null ? holder.transform : null;

            StartCoroutine(WanderRoutine());

            if (CubeHolder != null)
            {
                StartCoroutine(ReparentRoutine());
            }
        }

        private IEnumerator ReparentRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(Random.Range(minReparentInterval, maxReparentInterval));
                TryReparentRandomly();
            }
        }

        private void TryReparentRandomly()
        {
            // Recursive: returns every Transform under CubeHolder, including CubeHolder itself
            List<Transform> candidates = new List<Transform>(CubeHolder.GetComponentsInChildren<Transform>());

            // Exclude the holder itself, self, our own descendants (would create a cycle), and the current parent
            candidates.RemoveAll(t => t == transform || t == transform.parent || t.IsChildOf(transform));
            if (candidates.Count == 0)
            {
                return;
            }

            Transform newParent = candidates[Random.Range(0, candidates.Count)];

            MoodyBluesLog.Info($"ob:{name} moved under {newParent.name}");

            // keepWorldPosition = true so the cube doesn't jump when re-parented
            // transform.SetParent(newParent, true);
            MoodyBluesRuntimeManager.Instance.SetObjectParent(this.gameObject, newParent, true);
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
