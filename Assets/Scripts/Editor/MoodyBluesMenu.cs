using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MoodyBlues.EditorTools
{
    /// <summary>
    /// Everything MoodyBlues adds to the Editor lives in the top-level MoodyBlues menu: settings and
    /// the connection test first, then scene export, then checks. The same tools are buttons on the
    /// Project Settings > MoodyBlues page.
    /// </summary>
    public static class MoodyBluesMenu
    {
        internal const string Root = "MoodyBlues/";
        private const string Title = "MoodyBlues";

        [MenuItem(Root + "Settings...", priority = 0)]
        internal static void OpenSettings() => MoodyBluesSettingsProvider.Open();

        [MenuItem(Root + "Test Connection", priority = 1)]
        internal static void TestConnection() => MoodyBluesSettingsProvider.TestConnectionAndReport();

        [MenuItem(Root + "Export This Scene", priority = 20)]
        internal static void ExportThisScene()
        {
            Scene active = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(active.path))
            {
                EditorUtility.DisplayDialog(Title, "Save the scene first: only saved scenes have a scene ID.", "OK");
                return;
            }

            if (!MoodyBluesSceneExport.IsRecorderScene(active.path))
            {
                EditorUtility.DisplayDialog(Title, $"'{active.name}' has no MoodyBluesSessionManager, so it is not recorded.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            ExportAndUpload(new List<string> { active.path });
        }

        [MenuItem(Root + "Export All Scenes", priority = 21)]
        internal static void ExportAllScenes()
        {
            List<string> scenes = MoodyBluesSceneExport.FindRecorderScenesInProject();
            if (scenes.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, "No scene under Assets/ has a MoodyBluesSessionManager.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog(Title, $"Export and upload {scenes.Count} recorded scene(s)?\n\n{string.Join("\n", scenes)}", "Export", "Cancel"))
            {
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            ExportAndUpload(scenes);
        }

        [MenuItem(Root + "Upload Pending Exports", priority = 22)]
        internal static void UploadPendingExports()
        {
            List<MoodyBluesSceneExport.SavedExport> pending = MoodyBluesSceneExport.LoadSavedExports();
            if (pending.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, "There are no exports waiting to be uploaded.", "OK");
                return;
            }

            Debug.Log($"MoodyBlues: uploading {pending.Count} saved export(s)...");
            MoodyBluesSceneExport.UploadInBackground(pending);
        }

        [MenuItem(Root + "Make Scene Meshes Readable", priority = 40)]
        internal static void MakeSceneMeshesReadable()
        {
            List<string> scenes = MoodyBluesMeshReadability.ScenesWithRuntimeExport(MoodyBluesSceneExport.FindRecorderScenesInProject());
            if (scenes.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, "No recorded scene has Export Scene on Runtime Start on, so no model needs Read/Write.", "OK");
                return;
            }

            List<string> models = MoodyBluesMeshReadability.FindUnreadableModels(scenes);
            if (models.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, "Every model used by scenes with runtime export already has Read/Write enabled.", "OK");
                return;
            }

            long extraBytes = 0;
            foreach (string model in models) extraBytes += MoodyBluesMeshReadability.EstimateMeshBytes(model);

            string list = models.Count <= 15 ? string.Join("\n", models) : string.Join("\n", models.GetRange(0, 15)) + $"\n... and {models.Count - 15} more";
            if (!EditorUtility.DisplayDialog(
                    Title,
                    $"Enable Read/Write on {models.Count} model(s)? This adds about " +
                    $"{MoodyBluesMeshReadability.FormatBytes(extraBytes)} of mesh memory in the Player.\n\n{list}",
                    "Enable", "Cancel"))
            {
                return;
            }

            (int changed, long bytes) = MoodyBluesMeshReadability.MakeReadable(scenes);
            Debug.Log($"MoodyBlues: enabled Read/Write on {changed} model(s), about {MoodyBluesMeshReadability.FormatBytes(bytes)} of extra mesh memory.");
        }

        [MenuItem(Root + "Validate Scenes", priority = 41)]
        internal static void ValidateScenes()
        {
            List<string> scenes = MoodyBluesSceneExport.FindRecorderScenesInProject();
            if (scenes.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, "No scene under Assets/ has a MoodyBluesSessionManager.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var report = new StringBuilder();
            int issues = 0;

            if (!MoodyBluesProjectSettings.TryGetConnection(out _, out string connectionError))
            {
                report.AppendLine($"Project: {connectionError}");
                issues++;
            }

            foreach (string path in scenes)
            {
                MoodyBluesClientConfig config = MoodyBluesSceneExport.PeekSettings(path);

                foreach (string dependency in AssetDatabase.GetDependencies(path, recursive: true))
                {
                    // Optimize Game Objects removes the bone Transforms, so the recorder has
                    // nothing to track and the replay shows the character in its bind pose.
                    if (AssetImporter.GetAtPath(dependency) is ModelImporter importer && importer.optimizeGameObjects)
                    {
                        report.AppendLine($"{path}: model '{dependency}' has Optimize Game Objects on (Rig tab), so its bones are not recorded and the replay will not animate it.");
                        issues++;
                    }
                }

                if (!config.ExportSceneOnBuild && !config.ExportSceneOnRuntimeStart)
                {
                    report.AppendLine($"{path}: both export settings are off. Export it with MoodyBlues > Export This Scene.");
                    issues++;
                }

                if (config.ExportSceneOnRuntimeStart)
                {
                    foreach (string model in MoodyBluesMeshReadability.FindUnreadableModels(new[] { path }))
                    {
                        report.AppendLine($"{path}: model '{model}' has no Read/Write, so a runtime export leaves it out.");
                        issues++;
                    }
                }
            }

            bool opened = MoodyBluesSceneExport.WithEachSceneOpen(scenes, scene =>
            {
                MoodyBluesClientConfig config = MoodyBluesSceneExport.PeekSettings(scene.path);
                bool runtimeExport = config.ExportSceneOnRuntimeStart;
                issues += ValidateOpenScene(scene, runtimeExport, report);
            }, "Validate Scenes");

            if (!opened) report.AppendLine("Renderers were not checked: an open scene has unsaved changes.");

            string text = report.Length == 0 ? "No problems found." : report.ToString();
            if (issues > 0) Debug.LogWarning($"MoodyBlues: Validate Scenes found {issues} problem(s):\n{text}");
            else Debug.Log($"MoodyBlues: Validate Scenes checked {scenes.Count} scene(s). {text}");

            EditorUtility.DisplayDialog(Title,
                issues > 0 ? $"{issues} problem(s) found in {scenes.Count} scene(s). The list is in the Console." : $"No problems found in {scenes.Count} scene(s).",
                "OK");
        }

        private static int ValidateOpenScene(Scene scene, bool runtimeExport, StringBuilder report)
        {
            int issues = 0;
            var renderers = new List<Renderer>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                renderers.AddRange(root.GetComponentsInChildren<Renderer>(includeInactive: true));

                foreach (MoodyBluesRuntimeManager runtime in root.GetComponentsInChildren<MoodyBluesRuntimeManager>(includeInactive: true))
                {
                    foreach (GameObject prefab in runtime.Prefabs)
                    {
                        if (prefab != null) renderers.AddRange(prefab.GetComponentsInChildren<Renderer>(includeInactive: true));
                    }
                }
            }

            foreach (Renderer renderer in renderers)
            {
                Mesh mesh = renderer switch
                {
                    SkinnedMeshRenderer skinned => skinned.sharedMesh,
                    MeshRenderer => renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null,
                    _ => null,
                };
                if (mesh == null) continue;

                int materials = renderer.sharedMaterials.Length;
                if (materials != mesh.subMeshCount)
                {
                    report.AppendLine(
                        $"{scene.path}: '{GetPath(renderer.transform)}' has {materials} material(s) for {mesh.subMeshCount} submesh(es); " +
                        "the replay may show different materials than the game.");
                    issues++;
                }

                if (runtimeExport && renderer is MeshRenderer &&
                    GameObjectUtility.AreStaticEditorFlagsSet(renderer.gameObject, StaticEditorFlags.BatchingStatic))
                {
                    report.AppendLine(
                        $"{scene.path}: '{GetPath(renderer.transform)}' is Batching Static. With static batching on, a runtime " +
                        "export cannot include it; export this scene from the Editor instead.");
                    issues++;
                }
            }

            return issues;
        }

        private static string GetPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (Transform p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        private static void ExportAndUpload(List<string> scenePaths)
        {
            var exported = new List<MoodyBluesSceneExport.SavedExport>();
            bool ran = MoodyBluesSceneExport.WithEachSceneOpen(scenePaths, scene =>
            {
                MoodyBluesSceneExport.SavedExport saved = MoodyBluesSceneExport.ExportOpenScene(scene);
                if (saved != null) exported.Add(saved);
            }, "Scene export");

            if (!ran) return;

            if (exported.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, "Nothing was exported. The Console has the reason.", "OK");
                return;
            }

            Debug.Log($"MoodyBlues: exported {exported.Count} scene(s) to {Path.GetFullPath(MoodyBluesSceneExport.ExportFolder)}. Uploading...");
            MoodyBluesSceneExport.UploadInBackground(exported);
        }
    }
}
