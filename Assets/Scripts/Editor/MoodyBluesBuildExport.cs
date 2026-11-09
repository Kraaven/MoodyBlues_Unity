using System;
using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MoodyBlues.EditorTools
{
    /// <summary>
    /// Before a Player build, for every recorded scene in the build (Spec.md Section 9):
    /// - scenes that export at runtime get Read/Write enabled on their models;
    /// - scenes with Export Scene on Build are exported in the Editor and uploaded if the backend
    ///   lacks that version (the upload finishes after the build returns);
    /// - scenes with both export settings off get a warning.
    ///
    /// Nothing here can fail the build. A failed export only warns, and anything not uploaded
    /// stays in Library/MoodyBlues/Exports for MoodyBlues > Upload Pending Exports.
    ///
    /// A pre-build step rather than a per-scene build callback: Unity skips per-scene callbacks for
    /// scenes it has cached from an earlier build.
    /// </summary>
    public sealed class MoodyBluesBuildExport : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"MoodyBlues: build-time scene export failed - {ex}. Export later from the MoodyBlues menu.");
            }
        }

        private static void Run()
        {
            List<string> scenes = MoodyBluesSceneExport.FindRecorderScenesInBuild();
            if (scenes.Count == 0) return;

            if (!MoodyBluesProjectSettings.TryGetConnection(out _, out string connectionError))
            {
                // Exports are still made and saved; Upload Pending Exports sends them once connected.
                Debug.LogWarning($"MoodyBlues: {connectionError} This build will not record, and its scene exports are kept for Upload Pending Exports.");
            }

            List<string> runtimeExportScenes = MoodyBluesMeshReadability.ScenesWithRuntimeExport(scenes);
            if (runtimeExportScenes.Count > 0)
            {
                (int models, long extraBytes) = MoodyBluesMeshReadability.MakeReadable(runtimeExportScenes);
                if (models > 0)
                {
                    Debug.Log(
                        $"MoodyBlues: enabled Read/Write on {models} model(s) for runtime scene export, about " +
                        $"{MoodyBluesMeshReadability.FormatBytes(extraBytes)} of extra mesh memory in the Player.");
                }
            }

            var toExport = new List<string>();
            foreach (string path in scenes)
            {
                MoodyBluesClientConfig config = MoodyBluesSceneExport.PeekSettings(path);

                if (!config.ExportSceneOnBuild && !config.ExportSceneOnRuntimeStart)
                {
                    Debug.LogWarning(
                        $"MoodyBlues: '{path}' has scene export switched off. Its recordings wait on the backend until " +
                        "you export it with MoodyBlues > Export This Scene.");
                }
                else if (config.ExportSceneOnBuild && !config.AlwaysExportAtRuntime)
                {
                    // Always Export at Runtime folds the runtime export into the hash, so an Editor
                    // export could never match it.
                    toExport.Add(path);
                }
            }

            if (toExport.Count == 0) return;

            var exported = new List<MoodyBluesSceneExport.SavedExport>();
            MoodyBluesSceneExport.WithEachSceneOpen(toExport, scene =>
            {
                MoodyBluesSceneExport.SavedExport saved = MoodyBluesSceneExport.ExportOpenScene(scene);
                if (saved != null) exported.Add(saved);
            }, "Build-time scene export");

            if (exported.Count > 0) MoodyBluesSceneExport.UploadInBackground(exported);
        }
    }
}
