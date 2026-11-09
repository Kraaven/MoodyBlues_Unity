using System.IO;
using UnityEditor;
using UnityEngine;

namespace MoodyBlues.EditorTools
{
    /// <summary>
    /// Project Settings > MoodyBlues (also MoodyBlues > Settings...). Holds the one thing every
    /// project has to set, the connection string, with a Test Connection button, and the MoodyBlues
    /// tools as buttons. The connection is saved in <see cref="MoodyBluesProjectSettings.AssetPath"/>,
    /// which builds include through Resources.
    /// </summary>
    public static class MoodyBluesSettingsProvider
    {
        public const string SettingsPath = "Project/MoodyBlues";

        private static string _testMessage;
        private static MessageType _testType;
        private static bool _testing;

        public static void Open() => SettingsService.OpenProjectSettings(SettingsPath);

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "MoodyBlues",
                guiHandler = _ => DrawGui(),
                keywords = new[] { "MoodyBlues", "connection", "ingest", "key", "server", "recording", "export" },
            };
        }

        /// <summary>The settings asset, created under Assets/Resources if it does not exist yet.</summary>
        public static MoodyBluesProjectSettings GetOrCreate()
        {
            MoodyBluesProjectSettings settings = MoodyBluesProjectSettings.Load();
            if (settings != null) return settings;

            string folder = Path.GetDirectoryName(MoodyBluesProjectSettings.AssetPath)!.Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder(Path.GetDirectoryName(folder)!.Replace('\\', '/'), Path.GetFileName(folder));
            }

            settings = ScriptableObject.CreateInstance<MoodyBluesProjectSettings>();
            AssetDatabase.CreateAsset(settings, MoodyBluesProjectSettings.AssetPath);
            AssetDatabase.SaveAssets();
            MoodyBluesProjectSettings.ClearCache();
            return settings;
        }

        private static void DrawGui()
        {
            MoodyBluesProjectSettings existing = MoodyBluesProjectSettings.Load();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Copy the connection string from your project's page in the MoodyBlues dashboard and paste it here. " +
                "It is set once for the whole Unity project and included in builds.",
                EditorStyles.wordWrappedMiniLabel);

            string current = existing != null ? existing.ConnectionString : "";
            EditorGUI.BeginChangeCheck();
            string edited = EditorGUILayout.TextField("Connection String", current);
            if (EditorGUI.EndChangeCheck())
            {
                Save(edited.Trim());
                _testMessage = null;
            }

            if (string.IsNullOrWhiteSpace(current))
            {
                EditorGUILayout.HelpBox(MoodyBluesProjectSettings.NotSetMessage + " Until then nothing is recorded.", MessageType.Warning);
            }
            else if (!MoodyBluesConnection.TryParse(current, out MoodyBluesConnection connection, out string error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }
            else
            {
                string prefix = connection.IngestKey.Length > 10 ? connection.IngestKey.Substring(0, 10) + "..." : connection.IngestKey;
                EditorGUILayout.LabelField("Server", connection.DisplayHost);
                EditorGUILayout.LabelField("Ingest key", prefix);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(_testing))
                    {
                        if (GUILayout.Button(_testing ? "Testing..." : "Test Connection", GUILayout.Width(140)))
                        {
                            RunTest(connection, report: false);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(_testMessage)) EditorGUILayout.HelpBox(_testMessage, _testType);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Tools", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "The same actions are in the MoodyBlues menu. Per-scene settings (recording mode, export, ranges) are on " +
                "each scene's optional config: Assets > Create > MoodyBlues > Scene Config.",
                EditorStyles.wordWrappedMiniLabel);

            // Deferred: these open dialogs and scenes, which must not happen inside a layout pass.
            ToolButton("Export This Scene", "Export the active scene and upload it if the server lacks this version.", MoodyBluesMenu.ExportThisScene);
            ToolButton("Export All Scenes", "Every scene under Assets/ that has a MoodyBluesSessionManager.", MoodyBluesMenu.ExportAllScenes);
            ToolButton("Upload Pending Exports", "Retry exports that were saved but not uploaded.", MoodyBluesMenu.UploadPendingExports);
            ToolButton("Make Scene Meshes Readable", "Enable Read/Write on models used by scenes with runtime export.", MoodyBluesMenu.MakeSceneMeshesReadable);
            ToolButton("Validate Scenes", "List problems that would make a replay differ from the game.", MoodyBluesMenu.ValidateScenes);
        }

        private static void ToolButton(string label, string tooltip, System.Action action)
        {
            if (GUILayout.Button(new GUIContent(label, tooltip), GUILayout.Width(220)))
            {
                EditorApplication.delayCall += () => action();
            }
        }

        private static void Save(string connectionString)
        {
            MoodyBluesProjectSettings settings = GetOrCreate();
            var so = new SerializedObject(settings);
            so.FindProperty("connectionString").stringValue = connectionString;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        /// <summary>MoodyBlues > Test Connection: tests and reports in a dialog.</summary>
        public static void TestConnectionAndReport()
        {
            if (!MoodyBluesProjectSettings.TryGetConnection(out MoodyBluesConnection connection, out string error))
            {
                if (EditorUtility.DisplayDialog("MoodyBlues", error, "Open Settings", "Close")) Open();
                return;
            }
            RunTest(connection, report: true);
        }

        private static async void RunTest(MoodyBluesConnection connection, bool report)
        {
            _testing = true;
            _testMessage = null;
            RepaintSettings();
            try
            {
                (bool ok, string message) = await MoodyBluesHandshakeClient.TestConnectionAsync(connection);
                _testMessage = message;
                _testType = ok ? MessageType.Info : MessageType.Error;

                if (ok) Debug.Log($"MoodyBlues: {message}");
                else Debug.LogWarning($"MoodyBlues: connection test failed. {message}");

                if (report) EditorUtility.DisplayDialog("MoodyBlues", message, "OK");
            }
            finally
            {
                _testing = false;
                RepaintSettings();
            }
        }

        private static void RepaintSettings()
        {
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window.GetType().Name == "ProjectSettingsWindow") window.Repaint();
            }
        }
    }
}
