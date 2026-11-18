using GLTF.Schema;
using Newtonsoft.Json.Linq;

namespace MoodyBlues
{
    /// <summary>
    /// The glTF root extension <c>MOODYBLUES_scene</c>: the scene's lighting and post-processing
    /// description (Spec.md Section 13.3). Always listed in extensionsUsed and never in
    /// extensionsRequired, so any glTF viewer can still open the file. The payload is built by
    /// <see cref="SceneLookExporter"/>; this class only carries it through UnityGLTF's serializer.
    /// </summary>
    public sealed class MoodyBluesSceneExtension : IExtension
    {
        public const string ExtensionName = "MOODYBLUES_scene";

        public JObject Payload { get; }

        public MoodyBluesSceneExtension(JObject payload) => Payload = payload;

        public JProperty Serialize() => new JProperty(ExtensionName, Payload);

        public IExtension Clone(GLTFRoot root) => new MoodyBluesSceneExtension((JObject)Payload.DeepClone());
    }
}
