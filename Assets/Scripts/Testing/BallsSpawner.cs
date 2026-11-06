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

        private readonly Queue<GameObject> spawnedBalls = new Queue<GameObject>();

        private void Start()
        {
            StartCoroutine(SpawnBallsRoutine());
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
