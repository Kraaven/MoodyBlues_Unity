using System.Collections;
using UnityEngine;

namespace MoodyBlues.Samples
{
    /// <summary>
    /// Drives the sample scene with Custom Recording Control: uploads the scene if the backend
    /// needs it, then starts recording and the scene's motion (cubes and ball spawner), and stops
    /// the recording after <see cref="recordingDurationSeconds"/>.
    ///
    /// The cubes and the spawner are paused in Awake and only enabled once the scene send has
    /// finished, so their Start (which begins their coroutines) runs at that point.
    /// </summary>
    public class SampleSceneController : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Real-time seconds from the recording connecting until it is stopped.")]
        private float recordingDurationSeconds = 60f;

        private CubeMovement[] _cubes;
        private BallsSpawner[] _spawners;

        private void Awake()
        {
            // Every Awake runs before any Start, so disabling here stops their Start from running
            // until they are enabled again.
            _cubes = FindObjectsByType<CubeMovement>();
            _spawners = FindObjectsByType<BallsSpawner>();
            SetMotionEnabled(false);
        }

        private void Start()
        {
            MoodyBluesSessionManager session = MoodyBluesSessionManager.Instance;
            if (session == null)
            {
                Debug.LogError("SampleSceneController: no MoodyBluesSessionManager in the scene. Starting the motion without recording.");
                SetMotionEnabled(true);
                return;
            }

            session.SendScene(OnSceneSent);
        }

        private void OnSceneSent(SceneSendResult result)
        {
            Debug.Log($"SampleSceneController: scene send finished ({result}).");

            MoodyBluesSessionManager session = MoodyBluesSessionManager.Instance;
            if (session != null) session.StartRecording(OnRecordingStarted);

            SetMotionEnabled(true);
        }

        private void OnRecordingStarted(bool started)
        {
            if (!started)
            {
                Debug.LogError("SampleSceneController: the recording did not start.");
                return;
            }

            Debug.Log($"SampleSceneController: recording started; it stops in {recordingDurationSeconds} s.");
            StartCoroutine(StopAfterDelay());
        }

        private IEnumerator StopAfterDelay()
        {
            yield return new WaitForSecondsRealtime(recordingDurationSeconds);

            MoodyBluesLog.Error("Session has Ended");

            MoodyBluesSessionManager session = MoodyBluesSessionManager.Instance;
            if (session == null || !session.IsRecording) yield break;

            session.StopRecording(result => Debug.Log($"SampleSceneController: recording stopped ({result})."));
        }

        private void SetMotionEnabled(bool value)
        {
            foreach (CubeMovement cube in _cubes)
            {
                if (cube != null) cube.enabled = value;
            }

            foreach (BallsSpawner spawner in _spawners)
            {
                if (spawner != null) spawner.enabled = value;
            }
        }
    }
}
