using UnityEngine;
using UnityEngine.InputSystem;

namespace MoodyBlues.Playground
{
    /// <summary>
    /// Custom recording control for the playground: recording starts once the scene has loaded and
    /// stops when the backslash key is pressed (or the scene is torn down). Also forwards gameplay
    /// events (pick up, drop, throw, doors, switches, deposits) to MoodyBluesLog so they are visible in the stream.
    /// </summary>
    public class PlaygroundSession : MonoBehaviour
    {
        [SerializeField] private bool autoRecord = true;
        [SerializeField] private Interactor interactor;
        [SerializeField] private Door[] doors;
        [SerializeField] private LightSwitch[] switches;
        [SerializeField] private DepositZone[] zones;

        private void OnEnable()
        {
            if (interactor != null)
            {
                interactor.Picked += p => MoodyBluesLog.Info($"pickup:{p.DisplayName}");
                interactor.Dropped += p => MoodyBluesLog.Info($"drop:{p.DisplayName}");
                interactor.Thrown += p => MoodyBluesLog.Info($"throw:{p.DisplayName}");
            }
            foreach (Door d in doors)
            {
                if (d == null) continue;
                d.OpenChanged += (door, open) => MoodyBluesLog.Info($"door:{door.name} {(open ? "open" : "closed")}");
                d.Unlocked += door => MoodyBluesLog.Info($"door:{door.name} unlocked");
            }
            foreach (LightSwitch s in switches)
                if (s != null) s.Toggled += (sw, on) => MoodyBluesLog.Info($"switch:{sw.name} {(on ? "on" : "off")}");
            foreach (DepositZone z in zones)
                if (z != null) z.ItemDeposited += p => MoodyBluesLog.Info($"deposit:{p.DisplayName}");
        }

        /// <summary>True while a recording is running or starting.</summary>
        public bool IsRecording => MoodyBluesSessionManager.Instance != null && MoodyBluesSessionManager.Instance.IsRecording;

        private void Start()
        {
            if (!autoRecord) return;
            MoodyBluesSessionManager session = MoodyBluesSessionManager.Instance;
            if (session == null) return;
            // The session manager initializes after every other Start; a call made before that is
            // queued and runs once the scene is loaded and its load-time state is captured.
            session.StartRecording(started =>
            {
                if (!started) Debug.LogWarning("PlaygroundSession: the recording did not start.");
            });
        }

        private void Update()
        {
            if (Keyboard.current == null || !Keyboard.current.backslashKey.wasPressedThisFrame) return;
            MoodyBluesSessionManager session = MoodyBluesSessionManager.Instance;
            if (session == null || !session.IsRecording) return;
            session.StopRecording(result => Debug.Log($"PlaygroundSession: recording stopped ({result})."));
        }

        private void OnDestroy()
        {
            MoodyBluesSessionManager session = MoodyBluesSessionManager.Instance;
            if (session != null && session.IsRecording) session.StopRecording(_ => { });
        }
    }
}
