using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityGLTF;

namespace MoodyBlues
{
    /// <summary>
    /// Builds the <c>MOODYBLUES_scene</c> root extension and the per-node lighting extras of a scene
    /// export (Spec.md Section 13.3): ambient cube, fog, sky, sun, mixed lighting mode, post-processing
    /// and the baked lightmaps. All colours are linear RGB.
    /// </summary>
    internal static class SceneLookExporter
    {
        private const int ExtensionVersion = 1;

        /// <summary>Unity's marker values for "no lightmap" (-1) and "lightmap of a removed renderer" (0xFFFE, 0xFFFF).</summary>
        private const int FirstInvalidLightmapIndex = 0xFFFE;

        /// <summary>Adds the extension to the root. Lightmap textures go into the GLB buffer, so this must run while the exporter is still open (AfterSceneExport).</summary>
        public static void AddToRoot(
            GLTFSceneExporter exporter,
            GLTFRoot root,
            Scene scene,
            IReadOnlyDictionary<Transform, ushort> ids,
            List<Texture2D> temporaryTextures)
        {
            var look = new JObject
            {
                ["version"] = ExtensionVersion,
                ["ambient"] = new JObject { ["cube"] = AmbientCube() },
                ["fog"] = Fog(),
                ["sky"] = Sky(),
                ["sun"] = Sun(ids),
                ["lighting"] = new JObject { ["mixedMode"] = MixedMode(scene) },
            };

            JObject post = PostExporter.Build();
            if (post != null) look["post"] = post;

            look["lightmaps"] = Lightmaps(exporter, temporaryTextures);

            exporter.DeclareExtensionUsage(MoodyBluesSceneExtension.ExtensionName, false);
            root.AddExtension(MoodyBluesSceneExtension.ExtensionName, new MoodyBluesSceneExtension(look));
        }

        // ---- Node extras ------------------------------------------------------------------------

        /// <summary>
        /// Adds static, lightmap and lightBake to a node's extras. static is Editor-only because the
        /// static flags do not exist in a Player; lightBake works everywhere.
        /// </summary>
        public static void AddNodeExtras(Transform t, JObject extras)
        {
#if UNITY_EDITOR
            if (UnityEditor.GameObjectUtility.AreStaticEditorFlagsSet(t.gameObject, UnityEditor.StaticEditorFlags.ContributeGI))
            {
                extras["static"] = true;
            }
#endif

            if (t.TryGetComponent(out MeshRenderer meshRenderer) &&
                meshRenderer.lightmapIndex >= 0 && meshRenderer.lightmapIndex < FirstInvalidLightmapIndex &&
                meshRenderer.lightmapIndex < LightmapSettings.lightmaps.Length)
            {
                Vector4 so = SceneLookMath.LightmapScaleOffsetToGltf(meshRenderer.lightmapScaleOffset);
                extras["lightmap"] = new JObject
                {
                    ["index"] = meshRenderer.lightmapIndex,
                    ["scaleOffset"] = new JArray(so.x, so.y, so.z, so.w),
                };
            }

            if (t.TryGetComponent(out Light light))
            {
                extras["lightBake"] = light.bakingOutput.lightmapBakeType switch
                {
                    LightmapBakeType.Baked => "baked",
                    LightmapBakeType.Mixed => "mixed",
                    _ => "realtime",
                };
            }
        }

        // ---- Root extension members -------------------------------------------------------------

        private static JArray AmbientCube()
        {
            Color[] cube = SceneLookMath.AmbientCube(RenderSettings.ambientProbe, RenderSettings.ambientIntensity);
            var result = new JArray();
            foreach (Color c in cube) result.Add(Rgb(c));
            return result;
        }

        private static JToken Fog()
        {
            if (!RenderSettings.fog) return JValue.CreateNull();

            return new JObject
            {
                ["mode"] = RenderSettings.fogMode switch
                {
                    FogMode.Linear => "linear",
                    FogMode.Exponential => "exp",
                    _ => "exp2",
                },
                // fogColor is authored in gamma space like every Unity colour property.
                ["color"] = Rgb(RenderSettings.fogColor.linear),
                ["density"] = RenderSettings.fogDensity,
                ["start"] = RenderSettings.fogStartDistance,
                ["end"] = RenderSettings.fogEndDistance,
            };
        }

        private static JObject Sky()
        {
            Material skybox = RenderSettings.skybox;
            if (skybox == null)
            {
                Camera camera = Camera.main;
                return camera != null
                    ? new JObject { ["type"] = "color", ["color"] = Rgb(camera.backgroundColor.linear) }
                    : new JObject { ["type"] = "other" };
            }

            if (skybox.shader != null && skybox.shader.name == "Skybox/Procedural")
            {
                return new JObject
                {
                    ["type"] = "procedural",
                    ["skyTint"] = Rgb(skybox.GetColor("_SkyTint").linear),
                    ["groundColor"] = Rgb(skybox.GetColor("_GroundColor").linear),
                    ["exposure"] = skybox.GetFloat("_Exposure"),
                    ["sunSize"] = skybox.GetFloat("_SunSize"),
                    ["sunSizeConvergence"] = skybox.GetFloat("_SunSizeConvergence"),
                    ["atmosphereThickness"] = skybox.GetFloat("_AtmosphereThickness"),
                };
            }

            return new JObject { ["type"] = "other" };
        }

        private static JToken Sun(IReadOnlyDictionary<Transform, ushort> ids)
        {
            Light sun = RenderSettings.sun;
            if (sun != null && ids.TryGetValue(sun.transform, out ushort objectId)) return objectId;
            return JValue.CreateNull();
        }

        /// <summary>
        /// The mixed lighting mode the scene was baked with. Read from the baked mixed lights
        /// themselves (<see cref="Light.bakingOutput"/> works in a Player and records what the bake
        /// actually used, unlike the editor's LightingSettings, which can be changed after a bake).
        /// Without a baked mixed light: null if there are no lightmaps, else the Editor's
        /// LightingSettings; a Player has no other source and reports null.
        /// </summary>
        private static JToken MixedMode(Scene scene)
        {
            // All baked mixed lights of a scene share the mode they were baked with, so which one is
            // found first does not matter.
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            {
                if (light.gameObject.scene != scene) continue;
                LightBakingOutput output = light.bakingOutput;
                if (output.isBaked && output.lightmapBakeType == LightmapBakeType.Mixed)
                {
                    return SceneLookMath.MixedModeName(output.mixedLightingMode);
                }
            }

            if (LightmapSettings.lightmaps.Length == 0) return JValue.CreateNull();

#if UNITY_EDITOR
            LightingSettings settings = UnityEditor.Lightmapping.lightingSettings;
            if (settings != null && settings.bakedGI) return SceneLookMath.MixedModeName(settings.mixedBakeMode);
#endif
            return JValue.CreateNull();
        }

        private static JArray Lightmaps(GLTFSceneExporter exporter, List<Texture2D> temporaryTextures)
        {
            var result = new JArray();
            // Same settings every image needs: linear, with alpha (it holds the RGBM multiplier).
            // UnityGLTF's image path then blits the texture unchanged into a PNG (no sRGB write).
            var settings = new GLTFSceneExporter.TextureExportSettings
            {
                isValid = true,
                linear = true,
                alphaMode = GLTFSceneExporter.TextureExportSettings.AlphaMode.Always,
                conversion = GLTFSceneExporter.TextureExportSettings.Conversion.None,
            };

            foreach (LightmapData data in LightmapSettings.lightmaps)
            {
                var entry = new JObject { ["encoding"] = "rgbm", ["range"] = SceneLookMath.RgbmRange };
                if (data.lightmapColor == null)
                {
                    entry["texture"] = JValue.CreateNull();
                }
                else
                {
                    Texture2D rgbm = LightmapDecoder.EncodeRgbm(data.lightmapColor);
                    temporaryTextures.Add(rgbm);
                    entry["texture"] = exporter.ExportTexture(rgbm, GLTFSceneExporter.TextureMapType.Linear, settings).Id;
                }
                result.Add(entry);
            }
            return result;
        }

        private static JArray Rgb(Color c) => new JArray(c.r, c.g, c.b);
    }
}
