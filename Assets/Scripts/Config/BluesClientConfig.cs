using UnityEngine;

/// <summary>
/// Singleton config asset for the backend handshake (see Spec.md Section 9). Loaded via
/// Resources.Load, so the asset must live at Assets/Resources/BluesClientConfig.asset.
/// </summary>
[CreateAssetMenu(fileName = "BluesClientConfig", menuName = "MoodyBlues/Client Config")]
public class BluesClientConfig : ScriptableObject
{
    private const string ResourcePath = "BluesClientConfig";

    [Tooltip("Identifies this developer/build to the backend.")]
    [SerializeField] private string developerId = "";

    [Tooltip("Base HTTP URL of the backend (no trailing slash), e.g. http://localhost:8765")]
    [SerializeField] private string backendBaseUrl = "http://localhost:8765";

    public string DeveloperId => developerId;
    public string BackendBaseUrl => backendBaseUrl;

    private static BluesClientConfig _instance;

    public static BluesClientConfig Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = Resources.Load<BluesClientConfig>(ResourcePath);
                if (_instance == null)
                {
                    Debug.LogError(
                        $"BluesClientConfig: no asset found at Resources/{ResourcePath}. Create one via " +
                        "Assets > Create > MoodyBlues > Client Config, place it under a Resources folder, " +
                        "and fill in DeveloperId/BackendBaseUrl.");
                }
            }
            return _instance;
        }
    }
}
