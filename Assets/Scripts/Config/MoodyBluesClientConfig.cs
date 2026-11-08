using UnityEngine;

namespace MoodyBlues
{
    /// <summary>How a scene's recording is started and stopped.</summary>
    public enum RecordingMode
    {
        /// <summary>
        /// Recording starts when MoodyBluesSessionManager starts, and ends when it is disabled or
        /// destroyed. The client has 60 seconds to reach the backend before it gives up.
        /// </summary>
        [InspectorName("Start Recording on Load")]
        StartOnLoad = 0,

        /// <summary>
        /// Nothing records until game code calls MoodyBluesSessionManager.StartRecording. Use this
        /// on Quest and mobile, and call StopRecording from OnApplicationPause(true).
        /// </summary>
        [InspectorName("Custom Recording Control")]
        Custom = 1,
    }

    /// <summary>Largest WebSocket message the recorder sends (see Spec.md Section 7.4).</summary>
    public enum MessageSize
    {
        [InspectorName("8 KB")] KB8 = 8,
        [InspectorName("32 KB")] KB32 = 32,
        [InspectorName("128 KB")] KB128 = 128,
    }

    /// <summary>
    /// Per-scene recorder settings (see Spec.md Section 9). Each scene that records gets its own
    /// asset, assigned to that scene's MoodyBluesSessionManager.
    /// </summary>
    [CreateAssetMenu(fileName = "MoodyBluesSceneConfig", menuName = "MoodyBlues/Scene Config")]
    public class MoodyBluesClientConfig : ScriptableObject
    {
        // Defaults, shared by the field initializers, OnValidate and the inspector's reset button.
        public const float DefaultPositionRange = 500f;
        public const float DefaultScaleMin = -1f;
        public const float DefaultScaleMax = 10f;
        public const float DefaultDeltaPositionRange = 1.5f;
        public const float DefaultDeltaRotationComponentRange = 0.5f;
        public const float DefaultDeltaSingleAxisAngleRange = 2f;
        public const float DefaultDeltaScaleRange = 1f;

        [Header("Backend")]
        [Tooltip("Identifies this developer/build to the backend.")]
        [SerializeField] private string developerId = "";

        [Tooltip("Base HTTP URL of the backend (no trailing slash), e.g. http://localhost:8765")]
        [SerializeField] private string backendBaseUrl = "http://localhost:8765";

        [Header("Recording")]
        [Tooltip("Master switch. Turn off to ship a build that never records or contacts the backend.")]
        [SerializeField] private bool recordingEnabled = true;

        [Tooltip("Start Recording on Load: records from scene start until the component is disabled " +
                 "or destroyed.\nCustom Recording Control: game code calls StartRecording and " +
                 "StopRecording. Use this on Quest and mobile.")]
        [SerializeField] private RecordingMode recordingMode = RecordingMode.StartOnLoad;

        [Tooltip("Largest WebSocket message. Larger messages mean fewer, bigger sends; data waits " +
                 "on the device a little longer before it goes out (at most one keepalive interval, 5 s).")]
        [SerializeField] private MessageSize messageSize = MessageSize.KB8;

        [Header("Scene Export")]
        [Tooltip("When a Player is built, export this scene in the Editor and upload it if the server " +
                 "does not have this version. Needs no Read/Write on meshes. A failed upload only warns; " +
                 "use Tools > MoodyBlues > Upload Pending Exports later.")]
        [SerializeField] private bool exportSceneOnBuild = true;

        [Tooltip("If the server does not have this scene version when the game starts, export it in the " +
                 "running game and upload it.\n\nCost: every model in this scene must have Read/Write " +
                 "enabled (Tools > MoodyBlues > Make Scene Meshes Readable does it, and builds do it " +
                 "automatically). That keeps a CPU copy of each mesh in memory, roughly doubling mesh " +
                 "memory on Quest. Statically batched meshes cannot be exported at runtime.")]
        [SerializeField] private bool exportSceneOnRuntimeStart = false;

        [Tooltip("For scenes that build meshes or materials in code. Always export the scene at runtime " +
                 "Start and fold the export into the scene hash, so generated content that differs between " +
                 "runs gets its own version. Costs an export every time the scene loads.")]
        [SerializeField] private bool alwaysExportAtRuntime = false;

        [Tooltip("Let the runtime export also run in Editor Play mode. Off: in the Editor, export with " +
                 "Tools > MoodyBlues instead.")]
        [SerializeField] private bool exportSceneFromEditor = false;

        // ---- Quantization ranges --------------------------------------------------------------
        // These trade precision against headroom: a tighter range spends its bits on a smaller
        // span of values, so it is more precise but overflows sooner. Overflow is not a
        // correctness problem (the dispatcher promotes to a wider encoding instead of clamping),
        // but every promotion costs bytes, so these should bracket your game's real motion.
        //
        // Declared on the wire in BeginSession, so a receiver never has to assume them. The
        // inspector (MoodyBluesClientConfigEditor) hides them behind a foldout.

        [Header("Quantization: absolute ranges")]
        [Tooltip("Half-extent of trackable local position, in units. 16 bits are spread across " +
                 "+/- this value, so +/-500 gives roughly 1.5cm precision. Objects outside this " +
                 "range CANNOT be recorded correctly -- nothing is wider.")]
        [SerializeField] private float positionRange = DefaultPositionRange;

        [Tooltip("Minimum trackable local scale. Negative values are only needed for mirrored objects.")]
        [SerializeField] private float scaleMin = DefaultScaleMin;

        [Tooltip("Maximum trackable local scale.")]
        [SerializeField] private float scaleMax = DefaultScaleMax;

        [Header("Quantization: per-tick delta ranges")]
        [Tooltip("Max local position change per capture tick, in units. At 40Hz, 1.5 units/tick " +
                 "is a 60 units/second speed limit before events get promoted to absolute.")]
        [SerializeField] private float deltaPositionRange = DefaultDeltaPositionRange;

        [Tooltip("Max per-tick rotation as a quaternion component magnitude. 0.5 covers about 60 " +
                 "degrees of rotation in a single tick (2400 deg/s at 40Hz).")]
        [SerializeField] private float deltaRotationComponentRange = DefaultDeltaRotationComponentRange;

        [Tooltip("Max per-tick rotation for the 2-byte single-axis encoding, in degrees. This is " +
                 "the tightest range in the protocol: 2 degrees at 40Hz tops out at 80 deg/s, " +
                 "which a typical spinning pickup already exceeds. Exceeding it costs one extra " +
                 "byte, not correctness.")]
        [SerializeField] private float deltaSingleAxisAngleRange = DefaultDeltaSingleAxisAngleRange;

        [Tooltip("Max local scale change per capture tick.")]
        [SerializeField] private float deltaScaleRange = DefaultDeltaScaleRange;

        public string DeveloperId => developerId;
        public string BackendBaseUrl => backendBaseUrl;
        public bool RecordingEnabled => recordingEnabled;
        public RecordingMode RecordingMode => recordingMode;
        public int MessageSizeBytes => (int)messageSize * 1024;
        public bool ExportSceneOnBuild => exportSceneOnBuild;
        public bool ExportSceneOnRuntimeStart => exportSceneOnRuntimeStart;
        public bool AlwaysExportAtRuntime => exportSceneOnRuntimeStart && alwaysExportAtRuntime;
        public bool ExportSceneFromEditor => exportSceneFromEditor;

        /// <summary>Whether the running game (or Play mode, if allowed) may export and upload this scene.</summary>
        public bool RuntimeExportAllowed =>
            exportSceneOnRuntimeStart && (!Application.isEditor || exportSceneFromEditor);

        public float PositionRange => positionRange;
        public float ScaleMin => scaleMin;
        public float ScaleMax => scaleMax;
        public float DeltaPositionRange => deltaPositionRange;
        public float DeltaRotationComponentRange => deltaRotationComponentRange;
        public float DeltaSingleAxisAngleRange => deltaSingleAxisAngleRange;
        public float DeltaScaleRange => deltaScaleRange;

        /// <summary>
        /// Pushes the configured ranges into <see cref="Serializer"/>. Must run before anything is
        /// encoded; MoodyBluesSessionManager.Awake calls it ahead of the scene walk.
        /// </summary>
        public void ApplyQuantizationRanges()
        {
            Serializer.ConfigureRanges(
                positionRange,
                scaleMin,
                scaleMax,
                deltaPositionRange,
                deltaRotationComponentRange,
                deltaSingleAxisAngleRange,
                deltaScaleRange);
        }

        private void OnValidate()
        {
            // Ranges that are zero or inverted would make every quantized value degenerate, and
            // the failure would show up as garbled playback rather than an error, so catch it here.
            if (positionRange <= 0f) positionRange = DefaultPositionRange;
            if (deltaPositionRange <= 0f) deltaPositionRange = DefaultDeltaPositionRange;
            if (deltaRotationComponentRange <= 0f) deltaRotationComponentRange = DefaultDeltaRotationComponentRange;
            if (deltaSingleAxisAngleRange <= 0f) deltaSingleAxisAngleRange = DefaultDeltaSingleAxisAngleRange;
            if (deltaScaleRange <= 0f) deltaScaleRange = DefaultDeltaScaleRange;
            if (scaleMax <= scaleMin) scaleMax = scaleMin + 1f;

            // BeginSession sends the ranges as 16-bit floats, which top out at 65504.
            positionRange = Mathf.Min(positionRange, Serializer.MaxHalfValue);
            scaleMin = Mathf.Clamp(scaleMin, -Serializer.MaxHalfValue, Serializer.MaxHalfValue);
            scaleMax = Mathf.Clamp(scaleMax, -Serializer.MaxHalfValue, Serializer.MaxHalfValue);
            deltaPositionRange = Mathf.Min(deltaPositionRange, Serializer.MaxHalfValue);
            deltaRotationComponentRange = Mathf.Min(deltaRotationComponentRange, 1f);
            deltaSingleAxisAngleRange = Mathf.Min(deltaSingleAxisAngleRange, 180f);
            deltaScaleRange = Mathf.Min(deltaScaleRange, Serializer.MaxHalfValue);
        }
    }
}
