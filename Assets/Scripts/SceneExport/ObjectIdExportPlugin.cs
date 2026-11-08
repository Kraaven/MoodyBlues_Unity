using GLTF.Schema;
using UnityEngine;
using UnityGLTF;
using UnityGLTF.Plugins;

namespace MoodyBlues
{
    /// <summary>
    /// UnityGLTF export plugin behind <see cref="SceneGltfExporter"/> (see Spec.md Section 9). During
    /// a MoodyBlues export it:
    /// - leaves out every transform without an ObjectID (ShouldNodeExport);
    /// - writes each node's ObjectID and MoodyBlues metadata into node.extras, so the backend can
    ///   key scene geometry back to wire-protocol ObjectIDs;
    /// - applies the export's pose overrides and drops meshes that cannot be exported.
    ///
    /// UnityGLTF registers every export plugin it finds into its settings, so this also appears in
    /// Project Settings > UnityGLTF. Outside a MoodyBlues export it does nothing.
    /// </summary>
    public class ObjectIdExportPlugin : GLTFExportPlugin
    {
        public override string DisplayName => "MoodyBlues ObjectIDs";

        public override string Description =>
            "Used by MoodyBlues scene exports only: writes ObjectIDs into node extras. Has no effect on other exports.";

        public override GLTFExportPluginContext CreateInstance(ExportContext context) => new Context();

        private sealed class Context : GLTFExportPluginContext
        {
            public override bool ShouldNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform)
            {
                SceneGltfExporter.ExportJob job = SceneGltfExporter.Current;
                return job == null || job.ShouldExport(transform);
            }

            public override void AfterNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform, Node node)
            {
                SceneGltfExporter.Current?.AfterNodeExport(transform, node);
            }
        }
    }
}
