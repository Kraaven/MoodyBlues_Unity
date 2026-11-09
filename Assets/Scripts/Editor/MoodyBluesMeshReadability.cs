using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoodyBlues.EditorTools
{
    /// <summary>
    /// Read/Write for runtime scene export. A Player can only export meshes whose model has
    /// Read/Write enabled, which keeps a CPU copy of each mesh in memory. Only models used by
    /// recorded scenes with Export Scene on Runtime Start are touched.
    /// </summary>
    public static class MoodyBluesMeshReadability
    {
        /// <summary>Models (by path) used by the given scenes that do not have Read/Write enabled.</summary>
        public static List<string> FindUnreadableModels(IEnumerable<string> scenePaths)
        {
            var models = new SortedSet<string>();
            foreach (string scenePath in scenePaths)
            {
                foreach (string dependency in AssetDatabase.GetDependencies(scenePath, recursive: true))
                {
                    if (AssetImporter.GetAtPath(dependency) is ModelImporter importer && !importer.isReadable)
                    {
                        models.Add(dependency);
                    }
                }
            }
            return new List<string>(models);
        }

        /// <summary>
        /// The recorded scenes among <paramref name="scenePaths"/> whose config exports at runtime.
        /// </summary>
        public static List<string> ScenesWithRuntimeExport(IEnumerable<string> scenePaths)
        {
            var result = new List<string>();
            foreach (string path in scenePaths)
            {
                MoodyBluesClientConfig config = MoodyBluesSceneExport.PeekSettings(path);
                if (config.ExportSceneOnRuntimeStart) result.Add(path);
            }
            return result;
        }

        /// <summary>
        /// Enables Read/Write on every unreadable model the scenes use. Returns how many models
        /// changed and the estimated extra memory a Player pays for the CPU copies.
        /// </summary>
        public static (int Models, long ExtraBytes) MakeReadable(IEnumerable<string> scenePaths)
        {
            List<string> models = FindUnreadableModels(scenePaths);
            if (models.Count == 0) return (0, 0);

            long extraBytes = 0;
            foreach (string path in models) extraBytes += EstimateMeshBytes(path);

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in models)
                {
                    if (AssetImporter.GetAtPath(path) is not ModelImporter importer) continue;
                    importer.isReadable = true;
                    importer.SaveAndReimport();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            return (models.Count, extraBytes);
        }

        /// <summary>Vertex and index data of every mesh in a model: roughly the size of its CPU copy.</summary>
        public static long EstimateMeshBytes(string modelPath)
        {
            long bytes = 0;
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(modelPath))
            {
                if (asset is not Mesh mesh) continue;

                for (int stream = 0; stream < mesh.vertexBufferCount; stream++)
                {
                    bytes += (long)mesh.GetVertexBufferStride(stream) * mesh.vertexCount;
                }

                int indexSize = mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    bytes += (long)mesh.GetIndexCount(sub) * indexSize;
                }
            }
            return bytes;
        }

        public static string FormatBytes(long bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / (1024f * 1024f):0.0} MB" : $"{bytes / 1024f:0} KB";
    }
}
