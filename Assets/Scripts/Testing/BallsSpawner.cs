using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MoodyBlues.Samples
{
    public class BallsSpawner : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Prefab name as configured on MoodyBluesRuntimeManager. Spawning by name rather " +
                 "than list index means reordering that list can't silently change what spawns.")]
        private string prefabName = "Ball";

        [SerializeField] private int maxBallCount = 5;
        [SerializeField] private float spawnInterval = 1.5f;

        [SerializeField]
        [Tooltip("Balls (children of this spawner) that fall below this world height are deleted, " +
                 "so a ball that misses the floor doesn't fall forever and leave the recorder's " +
                 "position range.")]
        private float killHeight = -300f;

        private readonly Queue<GameObject> spawnedBalls = new Queue<GameObject>();
        private readonly List<GameObject> fallenScratch = new List<GameObject>();

        private void Start()
        {
            StartCoroutine(SpawnBallsRoutine());
        }

        private void Update()
        {
            // Covers both spawned balls and any placed under the spawner in the scene, since
            // spawned balls are parented here too.
            fallenScratch.Clear();
            foreach (Transform child in transform)
            {
                if (child.position.y < killHeight) fallenScratch.Add(child.gameObject);
            }

            if (fallenScratch.Count == 0) return;

            foreach (GameObject ball in fallenScratch)
            {
                MoodyBluesRuntimeManager.Instance.DeleteObject(ball);
            }

            // Keep the oldest-first queue limited to balls that still exist.
            int remaining = spawnedBalls.Count;
            for (int i = 0; i < remaining; i++)
            {
                GameObject ball = spawnedBalls.Dequeue();
                if (ball != null && !fallenScratch.Contains(ball)) spawnedBalls.Enqueue(ball);
            }
        }

        private IEnumerator SpawnBallsRoutine()
        {
            WaitForSeconds wait = new WaitForSeconds(spawnInterval);

            while (true)
            {
                yield return wait;

                Vector3 randomPosition = new Vector3(
                    Random.Range(-12f, 11f),
                    Random.Range(6.7f, 13.8f),
                    Random.Range(-8f, 16f)
                );

                float randomScaleValue = Random.Range(0.5f, 2.0f);
                Vector3 randomScale = new Vector3(randomScaleValue, randomScaleValue, randomScaleValue);

                GameObject newBall = MoodyBluesRuntimeManager.Instance.InstantiateObject(
                    prefabName,
                    randomPosition,
                    Quaternion.identity,
                    randomScale,
                    this.transform,
                    true
                );

                if (newBall != null)
                {
                    if (spawnedBalls.Count >= maxBallCount)
                    {
                        GameObject oldestBall = spawnedBalls.Dequeue();
                        if (oldestBall != null)
                        {
                            //Destroy(oldestBall);
                            MoodyBluesRuntimeManager.Instance.DeleteObject(oldestBall);
                        }
                    }

                    spawnedBalls.Enqueue(newBall);
                }
            }
        }
    }
}
