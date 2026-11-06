using UnityEngine;

namespace MoodyBlues
{
    /// <summary>
    /// Singleton config asset for the backend handshake (see Spec.md Section 9). Loaded via
    /// Resources.Load, so the asset must live at Assets/Resources/MoodyBluesClientConfig.asset.
    /// </summary>
    [CreateAssetMenu(fileName = "MoodyBluesClientConfig", menuName = "MoodyBlues/Client Config")]
    public class MoodyBluesClientConfig : ScriptableObject
    {
        private const string ResourcePath = "MoodyBluesClientConfig";

        [Header("Backend")]
        [Tooltip("Identifies this developer/build to the backend.")]
        [SerializeField] private string developerId = "";

        [Tooltip("Base HTTP URL of the backend (no trailing slash), e.g. http://localhost:8765")]
        [SerializeField] private string backendBaseUrl = "http://localhost:8765";

        [Header("Recording")]
        [Tooltip("Master switch. Turn off to ship a build that never records or contacts the backend.")]
        [SerializeField] private bool recordingEnabled = true;

        // ---- Quantization ranges --------------------------------------------------------------
        // These trade precision against headroom: a tighter range spends its bits on a smaller
        // span of values, so it is more precise but overflows sooner. Overflow is no longer a
        // correctness problem (the dispatcher promotes to a wider encoding instead of clamping),
        // but every promotion costs bytes -- so these should bracket your game's real motion.
        //
        // Declared on the wire in BeginSession, so a receiver never has to assume them.

        [Header("Quantization: absolute ranges")]
        [Tooltip("Half-extent of trackable local position, in units. 16 bits are spread across " +
                 "+/- this value, so +/-500 gives roughly 1.5cm precision. Objects outside this " +
                 "range CANNOT be recorded correctly -- nothing is wider.")]
        [SerializeField] private float positionRange = 500f;

        [Tooltip("Minimum trackable local scale. Negative values are only needed for mirrored objects.")]
        [SerializeField] private float scaleMin = -1f;

        [Tooltip("Maximum trackable local scale.")]
        [SerializeField] private float scaleMax = 10f;

        [Header("Quantization: per-tick delta ranges")]
        [Tooltip("Max local position change per capture tick, in units. At 40Hz, 1.5 units/tick " +
                 "is a 60 units/second speed limit before events get promoted to absolute.")]
        [SerializeField] private float deltaPositionRange = 1.5f;

        [Tooltip("Max per-tick rotation as a quaternion component magnitude. 0.5 covers about 60 " +
                 "degrees of rotation in a single tick (2400 deg/s at 40Hz).")]
        [SerializeField] private float deltaRotationComponentRange = 0.5f;

        [Tooltip("Max per-tick rotation for the 2-byte single-axis encoding, in degrees. This is " +
                 "the tightest range in the protocol: 2 degrees at 40Hz tops out at 80 deg/s, " +
                 "which a typical spinning pickup already exceeds. Exceeding it costs one extra " +
                 "byte, not correctness.")]
        [SerializeField] private float deltaSingleAxisAngleRange = 2f;

        [Tooltip("Max local scale change per capture tick.")]
        [SerializeField] private float deltaScaleRange = 1f;

        public string DeveloperId => developerId;
        public string BackendBaseUrl => backendBaseUrl;
        public bool RecordingEnabled => recordingEnabled;

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
            if (positionRange <= 0f) positionRange = 500f;
            if (deltaPositionRange <= 0f) deltaPositionRange = 1.5f;
            if (deltaRotationComponentRange <= 0f) deltaRotationComponentRange = 0.5f;
            if (deltaSingleAxisAngleRange <= 0f) deltaSingleAxisAngleRange = 2f;
            if (deltaScaleRange <= 0f) deltaScaleRange = 1f;
            if (scaleMax <= scaleMin) scaleMax = scaleMin + 1f;
        }

        private static MoodyBluesClientConfig _instance;

        public static MoodyBluesClientConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<MoodyBluesClientConfig>(ResourcePath);
                    if (_instance == null)
                    {
                        Debug.LogError(
                            $"MoodyBluesClientConfig: no asset found at Resources/{ResourcePath}. Create one via " +
                            "Assets > Create > MoodyBlues > Client Config, place it under a Resources folder, " +
                            "and fill in DeveloperId/BackendBaseUrl.");
                    }
                }
                return _instance;
            }
        }
    }
}
