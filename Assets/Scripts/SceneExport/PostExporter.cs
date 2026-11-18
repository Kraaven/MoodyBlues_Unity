using Newtonsoft.Json.Linq;
#if MOODYBLUES_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#endif

namespace MoodyBlues
{
    /// <summary>
    /// The <c>post</c> member of the MOODYBLUES_scene extension: the tone mapping, exposure and bloom a
    /// URP project applies. URP is an optional dependency of this assembly (the MOODYBLUES_URP define
    /// comes from MoodyBlues.asmdef's versionDefines), so in other projects, or when the active render
    /// pipeline is not URP, there is nothing to export and <see cref="Build"/> returns null.
    /// </summary>
    internal static class PostExporter
    {
        public static JObject Build()
        {
#if MOODYBLUES_URP
            if (!(UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)) return null;

            // Every profile that can contribute, lowest priority first so later ones override earlier
            // ones: the project's default volume profiles, then the scene's global volumes by priority.
            // Local (non-global) volumes depend on the camera position and are not exported.
            var profiles = new List<VolumeProfile>();
            VolumeManager manager = VolumeManager.instance;
            if (manager.globalDefaultProfile != null) profiles.Add(manager.globalDefaultProfile);
            if (manager.qualityDefaultProfile != null) profiles.Add(manager.qualityDefaultProfile);
            if (manager.customDefaultProfiles != null)
            {
                foreach (VolumeProfile p in manager.customDefaultProfiles)
                {
                    if (p != null) profiles.Add(p);
                }
            }

            Volume[] volumes = Object.FindObjectsByType<Volume>(FindObjectsInactive.Exclude);
            System.Array.Sort(volumes, (a, b) => a.priority.CompareTo(b.priority));
            foreach (Volume volume in volumes)
            {
                if (volume.enabled && volume.isGlobal && volume.weight > 0f && volume.sharedProfile != null)
                {
                    profiles.Add(volume.sharedProfile);
                }
            }

            TonemappingMode tonemapping = TonemappingMode.None;
            float postExposure = 0f;
            bool bloomActive = false;
            float bloomIntensity = 0f, bloomThreshold = 0.9f;
            Color bloomTint = Color.white;

            foreach (VolumeProfile profile in profiles)
            {
                if (profile.TryGet(out Tonemapping t))
                {
                    if (t.active && t.mode.overrideState) tonemapping = t.mode.value;
                }
                if (profile.TryGet(out ColorAdjustments c))
                {
                    if (c.active && c.postExposure.overrideState) postExposure = c.postExposure.value;
                }
                if (profile.TryGet(out Bloom b))
                {
                    bloomActive = b.active;
                    if (b.intensity.overrideState) bloomIntensity = b.intensity.value;
                    if (b.threshold.overrideState) bloomThreshold = b.threshold.value;
                    if (b.tint.overrideState) bloomTint = b.tint.value;
                }
            }

            JToken bloom = JValue.CreateNull();
            if (bloomActive && bloomIntensity > 0f)
            {
                Color tint = bloomTint.linear; // authored in gamma space; URP uploads it as linear.
                bloom = new JObject
                {
                    ["intensity"] = bloomIntensity,
                    ["threshold"] = bloomThreshold,
                    ["tint"] = new JArray(tint.r, tint.g, tint.b),
                };
            }

            return new JObject
            {
                ["tonemapping"] = tonemapping switch
                {
                    TonemappingMode.Neutral => "neutral",
                    TonemappingMode.ACES => "aces",
                    _ => "none",
                },
                ["postExposure"] = postExposure,
                ["bloom"] = bloom,
            };
#else
            return null;
#endif
        }
    }
}
