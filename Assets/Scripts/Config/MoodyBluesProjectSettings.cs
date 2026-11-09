using System;
using UnityEngine;

namespace MoodyBlues
{
    /// <summary>
    /// Settings shared by the whole Unity project: today, only the connection to the backend.
    /// Edited in Project Settings > MoodyBlues (MoodyBlues > Settings...), stored at
    /// <see cref="AssetPath"/>, and loaded from Resources at runtime, so builds carry it with no
    /// extra step. Per-scene behaviour (recording mode, export, ranges) stays on each scene's
    /// optional <see cref="MoodyBluesClientConfig"/>.
    /// </summary>
    public sealed class MoodyBluesProjectSettings : ScriptableObject
    {
        /// <summary>Name under Resources, for Resources.Load.</summary>
        public const string ResourceName = "MoodyBluesProjectSettings";

        /// <summary>Where the editor creates the asset.</summary>
        public const string AssetPath = "Assets/Resources/" + ResourceName + ".asset";

        public const string NotSetMessage =
            "MoodyBlues is not connected to a project. Copy the connection string from your project's page in the " +
            "MoodyBlues dashboard and paste it into MoodyBlues > Settings.";

        [Tooltip("From your project's page in the MoodyBlues dashboard, e.g. https://mbk_...@moodyblues.example.com. " +
                 "It names the server and holds the project's ingest key. The key only allows uploading recordings and " +
                 "scenes, but it ships inside builds; rotate it in the dashboard if a build leaks.")]
        [SerializeField] private string connectionString = "";

        public string ConnectionString => connectionString ?? "";

        private static MoodyBluesProjectSettings _cached;

        /// <summary>The project's settings asset, or null if it has not been created yet.</summary>
        public static MoodyBluesProjectSettings Load()
        {
            if (_cached == null) _cached = Resources.Load<MoodyBluesProjectSettings>(ResourceName);
            return _cached;
        }

        /// <summary>The project's connection. False (with a message saying what to fix) if none is set or it does not parse.</summary>
        public static bool TryGetConnection(out MoodyBluesConnection connection, out string error)
        {
            MoodyBluesProjectSettings settings = Load();
            if (settings == null || string.IsNullOrWhiteSpace(settings.connectionString))
            {
                connection = default;
                error = NotSetMessage;
                return false;
            }
            return MoodyBluesConnection.TryParse(settings.connectionString, out connection, out error);
        }

#if UNITY_EDITOR
        /// <summary>Editor only: forget the cached asset after it was created, moved or edited.</summary>
        public static void ClearCache() => _cached = null;
#endif
    }

    /// <summary>
    /// A parsed connection string: the backend's base URL and the project's ingest key. The key is
    /// sent in the X-MoodyBlues-Ingest-Key header; the URL with the key in it is never requested.
    /// </summary>
    public readonly struct MoodyBluesConnection
    {
        public const string KeyPrefix = "mbk_";

        /// <summary>Scheme, host, port and path, with no trailing slash, e.g. https://moodyblues.example.com.</summary>
        public readonly string BaseUrl;

        public readonly string IngestKey;

        public MoodyBluesConnection(string baseUrl, string ingestKey)
        {
            BaseUrl = baseUrl;
            IngestKey = ingestKey;
        }

        /// <summary>Host (and port), for messages. Never includes the key.</summary>
        public string DisplayHost => Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri uri) ? uri.Authority : BaseUrl;

        /// <summary>Parses <c>http(s)://mbk_...@host[:port][/path]</c>.</summary>
        public static bool TryParse(string text, out MoodyBluesConnection connection, out string error)
        {
            connection = default;
            string trimmed = text?.Trim() ?? "";

            if (trimmed.Length == 0)
            {
                error = MoodyBluesProjectSettings.NotSetMessage;
                return false;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                error = "The connection string should look like https://mbk_...@your-server. Copy it again from the project page of the dashboard.";
                return false;
            }

            string key = Uri.UnescapeDataString(uri.UserInfo);
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal) || key.Contains(":"))
            {
                error = "The connection string has no ingest key (the mbk_... part before the @). Copy it again from the project page of the dashboard.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "The connection string should not contain ? or #. Copy it again from the project page of the dashboard.";
                return false;
            }

            connection = new MoodyBluesConnection($"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}", key);
            error = null;
            return true;
        }
    }
}
