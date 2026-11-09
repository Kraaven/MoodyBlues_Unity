using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MoodyBlues.EditorTools
{
    /// <summary>
    /// Editor-side scene export (Spec.md Section 9), shared by the build step and the
    /// MoodyBlues menu. For one scene it:
    ///
    /// 1. opens the scene as saved on disk;
    /// 2. builds the prefab template library exactly as MoodyBluesRuntimeManager.Awake does;
    /// 3. assigns ObjectIDs with the same walk as the running game (SceneWalk);
    /// 4. computes the scene hash and exports the GLB;
    /// 5. destroys the templates, and later restores the scenes that were open;
    /// 6. saves the export under Library/MoodyBlues/Exports, then uploads it if the backend lacks it.
    ///
    /// The saved copy is deleted once the backend has the version, so whatever is left in that
    /// folder is what Upload Pending Exports retries.
    /// </summary>
    public static class MoodyBluesSceneExport
    {
        public const string ExportFolder = "Library/MoodyBlues/Exports";

        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Metadata saved next to each exported .glb.</summary>
        [Serializable]
        public sealed class SavedExport
        {
            public string sceneId;
            public string sceneHash;
            public string scenePath;
            public string sceneName;
            public int skippedMeshes;
            public string createdUtc;

            [NonSerialized] public string GlbPath;
            [NonSerialized] public string JsonPath;
        }

        // ---- Finding recorder scenes ------------------------------------------------------------

        private static string _sessionManagerScriptPath;

        /// <summary>Path of the MoodyBluesSessionManager script, which every recorded scene depends on.</summary>
        private static string SessionManagerScriptPath
        {
            get
            {
                if (_sessionManagerScriptPath != null) return _sessionManagerScriptPath;

                foreach (string guid in AssetDatabase.FindAssets($"t:MonoScript {nameof(MoodyBluesSessionManager)}"))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                    if (script != null && script.GetClass() == typeof(MoodyBluesSessionManager))
                    {
                        _sessionManagerScriptPath = path;
                        break;
                    }
                }

                return _sessionManagerScriptPath ?? string.Empty;
            }
        }

        /// <summary>True if the scene asset contains a MoodyBluesSessionManager (directly or through a prefab).</summary>
        public static bool IsRecorderScene(string scenePath)
        {
            string script = SessionManagerScriptPath;
            if (string.IsNullOrEmpty(script) || string.IsNullOrEmpty(scenePath)) return false;
            return Array.IndexOf(AssetDatabase.GetDependencies(scenePath, recursive: true), script) >= 0;
        }

        /// <summary>Every scene under Assets/ that has a recorder.</summary>
        public static List<string> FindRecorderScenesInProject()
        {
            var result = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (IsRecorderScene(path)) result.Add(path);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// The enabled scenes of the next build. Under a build profile with its own scene list,
        /// EditorBuildSettings.scenes already reflects that list.
        /// </summary>
        public static List<string> FindRecorderScenesInBuild()
        {
            var result = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled && IsRecorderScene(scene.path)) result.Add(scene.path);
            }
            return result;
        }

        /// <summary>
        /// The config a scene uses, read from its dependencies without opening it. Null if the scene
        /// references none; the first one if it references several.
        /// </summary>
        public static MoodyBluesClientConfig PeekConfig(string scenePath)
        {
            foreach (string path in AssetDatabase.GetDependencies(scenePath, recursive: true))
            {
                if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(MoodyBluesClientConfig))
                {
                    return AssetDatabase.LoadAssetAtPath<MoodyBluesClientConfig>(path);
                }
            }
            return null;
        }

        private static MoodyBluesClientConfig _defaults;

        /// <summary>The settings a scene records with: its config, or the defaults if it has none.</summary>
        public static MoodyBluesClientConfig PeekSettings(string scenePath)
        {
            MoodyBluesClientConfig config = PeekConfig(scenePath);
            if (config != null) return config;

            if (_defaults == null)
            {
                _defaults = ScriptableObject.CreateInstance<MoodyBluesClientConfig>();
                _defaults.hideFlags = HideFlags.HideAndDontSave;
            }
            return _defaults;
        }

        // ---- Opening scenes ---------------------------------------------------------------------

        /// <summary>
        /// Runs <paramref name="perScene"/> with each scene opened alone, then reopens what was open
        /// before. Refuses (returns false, after logging) if an open scene has unsaved changes,
        /// since opening another scene would lose them.
        /// </summary>
        public static bool WithEachSceneOpen(IReadOnlyList<string> scenePaths, Action<Scene> perScene, string what)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene open = SceneManager.GetSceneAt(i);
                if (open.isDirty)
                {
                    Debug.LogWarning(
                        $"MoodyBlues: {what} skipped because scene '{open.name}' has unsaved changes. Save it and try again.");
                    return false;
                }
            }

            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                for (int i = 0; i < scenePaths.Count; i++)
                {
                    if (scenePaths.Count > 1)
                    {
                        EditorUtility.DisplayProgressBar("MoodyBlues", $"{what}: {Path.GetFileNameWithoutExtension(scenePaths[i])}", (float)i / scenePaths.Count);
                    }

                    Scene scene;
                    try
                    {
                        scene = EditorSceneManager.OpenScene(scenePaths[i], OpenSceneMode.Single);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"MoodyBlues: could not open '{scenePaths[i]}' - {ex.Message}");
                        continue;
                    }

                    try
                    {
                        perScene(scene);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"MoodyBlues: {what} failed for '{scenePaths[i]}' - {ex}");
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (setup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(setup);
                else EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            }

            return true;
        }

        // ---- Exporting --------------------------------------------------------------------------

        /// <summary>
        /// Exports an open scene and saves the result. Returns null (after logging) if the scene
        /// cannot be exported.
        /// </summary>
        public static SavedExport ExportOpenScene(Scene scene)
        {
            MoodyBluesSessionManager manager = FindInScene<MoodyBluesSessionManager>(scene);
            if (manager == null)
            {
                Debug.LogWarning($"MoodyBlues: '{scene.path}' has no MoodyBluesSessionManager, so there is nothing to export.");
                return null;
            }

            string sceneId = AssetDatabase.AssetPathToGUID(scene.path);
            if (string.IsNullOrEmpty(sceneId))
            {
                Debug.LogWarning($"MoodyBlues: '{scene.name}' is not saved as an asset, so it has no scene ID.");
                return null;
            }

            // The running game builds the library in Awake, which only runs on an active object.
            MoodyBluesRuntimeManager runtime = FindInScene<MoodyBluesRuntimeManager>(scene);
            GameObject library = null;
            try
            {
                if (runtime != null && runtime.gameObject.activeInHierarchy)
                {
                    library = MoodyBluesRuntimeManager.BuildTemplateLibrary(runtime.transform, runtime.Prefabs, new List<GameObject>());
                }

                var walk = new List<Transform>();
                SceneWalk.Collect(scene, walk);
                if (walk.Count > ushort.MaxValue)
                {
                    Debug.LogWarning($"MoodyBlues: '{scene.name}' has {walk.Count} objects, more than the {ushort.MaxValue} ObjectIDs a recording can address.");
                    return null;
                }

                var ids = new Dictionary<Transform, ushort>(walk.Count);
                for (int i = 0; i < walk.Count; i++) ids[walk[i]] = (ushort)(i + 1);

                string sceneHash = SceneHasher.Combine(
                    SceneContentHashEditor.Compute(scene.path),
                    SceneHasher.ComputeStructuralHash(scene.name, walk));

                byte[] glb = SceneGltfExporter.Export(scene, ids, out int skippedMeshes);

                var saved = new SavedExport
                {
                    sceneId = sceneId,
                    sceneHash = sceneHash,
                    scenePath = scene.path,
                    sceneName = scene.name,
                    skippedMeshes = skippedMeshes,
                    createdUtc = DateTime.UtcNow.ToString("o"),
                };
                Save(saved, glb);

                Debug.Log($"MoodyBlues: exported '{scene.name}' ({glb.Length / 1024} KB, {walk.Count} objects, hash {sceneHash.Substring(0, 12)}).");
                return saved;
            }
            finally
            {
                if (library != null) Object.DestroyImmediate(library);
            }
        }

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T found = root.GetComponentInChildren<T>(includeInactive: true);
                if (found != null) return found;
            }
            return null;
        }

        // ---- Saved exports ----------------------------------------------------------------------

        private static void Save(SavedExport saved, byte[] glb)
        {
            Directory.CreateDirectory(ExportFolder);

            // One file per scene version; a re-export of the same version overwrites it.
            string stem = Path.Combine(ExportFolder, $"{saved.sceneId}_{saved.sceneHash.Substring(0, 16)}");
            saved.GlbPath = stem + ".glb";
            saved.JsonPath = stem + ".json";

            File.WriteAllBytes(saved.GlbPath, glb);
            File.WriteAllText(saved.JsonPath, JsonUtility.ToJson(saved, prettyPrint: true));
        }

        /// <summary>Every saved export still waiting to be confirmed on the backend.</summary>
        public static List<SavedExport> LoadSavedExports()
        {
            var result = new List<SavedExport>();
            if (!Directory.Exists(ExportFolder)) return result;

            foreach (string jsonPath in Directory.GetFiles(ExportFolder, "*.json"))
            {
                try
                {
                    var saved = JsonUtility.FromJson<SavedExport>(File.ReadAllText(jsonPath));
                    saved.JsonPath = jsonPath;
                    saved.GlbPath = Path.ChangeExtension(jsonPath, ".glb");
                    if (File.Exists(saved.GlbPath)) result.Add(saved);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"MoodyBlues: ignoring unreadable saved export '{jsonPath}' - {ex.Message}");
                }
            }

            return result;
        }

        private static void Delete(SavedExport saved)
        {
            try
            {
                File.Delete(saved.GlbPath);
                File.Delete(saved.JsonPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"MoodyBlues: could not delete '{saved.GlbPath}' - {ex.Message}");
            }
        }

        // ---- Uploading --------------------------------------------------------------------------

        /// <summary>
        /// Uploads each export the backend does not have yet, one after another, and deletes the
        /// saved copy of every version the backend holds. Fire-and-forget; reports to the console.
        /// </summary>
        public static async void UploadInBackground(IReadOnlyList<SavedExport> exports)
        {
            try
            {
                if (!MoodyBluesProjectSettings.TryGetConnection(out MoodyBluesConnection connection, out string error))
                {
                    Debug.LogWarning($"MoodyBlues: {exports.Count} scene export(s) not uploaded. {error} Then use MoodyBlues > Upload Pending Exports.");
                    return;
                }

                int uploaded = 0, alreadyThere = 0, failed = 0;
                foreach (SavedExport saved in exports)
                {
                    switch (await UploadAsync(saved, connection))
                    {
                        case SceneSendResult.Uploaded: uploaded++; break;
                        case SceneSendResult.AlreadyOnServer: alreadyThere++; break;
                        default: failed++; break;
                    }
                }

                string summary = $"MoodyBlues: scene upload to {connection.DisplayHost} finished - {uploaded} uploaded, {alreadyThere} already on the server, {failed} failed.";
                if (failed > 0) Debug.LogWarning(summary + " Failed exports are kept; retry with MoodyBlues > Upload Pending Exports.");
                else Debug.Log(summary);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"MoodyBlues: scene upload failed unexpectedly - {ex}");
            }
        }

        /// <summary>
        /// Checks one saved export against the project's backend and uploads it if missing. The
        /// connection is the project's current one, not the one at export time, so an export made
        /// before the key was rotated (or before the project was connected) still goes through.
        /// </summary>
        private static async Task<SceneSendResult> UploadAsync(SavedExport saved, MoodyBluesConnection connection)
        {
            var request = new SceneCheckRequest
            {
                sceneId = saved.sceneId,
                sceneHash = saved.sceneHash,
            };

            SceneCheckResponse check = await MoodyBluesHandshakeClient.CheckSceneAsync(connection, request, DateTime.UtcNow + CheckTimeout);
            if (check == null)
            {
                Debug.LogWarning($"MoodyBlues: could not upload '{saved.sceneName}' (the scene check failed; see the error above).");
                return SceneSendResult.Failed;
            }

            if (!check.sceneUploadRequired)
            {
                Delete(saved);
                return SceneSendResult.AlreadyOnServer;
            }

            byte[] glb = File.ReadAllBytes(saved.GlbPath);
            if (!await MoodyBluesHandshakeClient.UploadSceneAsync(check.sceneUploadUrl, connection.IngestKey, glb, saved.skippedMeshes))
            {
                return SceneSendResult.Failed;
            }

            Delete(saved);
            Debug.Log($"MoodyBlues: uploaded '{saved.sceneName}'.");
            return SceneSendResult.Uploaded;
        }
    }
}
