using UnityEngine;
using UnityEngine.Rendering;

namespace MoodyBlues
{
    /// <summary>
    /// The pure conversions behind the "game look" data of a scene export (Spec.md Section 13.3):
    /// lightmap UV transforms, RGBM lightmap encoding and the ambient cube. Kept free of scene and
    /// GLB state so the EditMode tests can pin each one down.
    /// </summary>
    public static class SceneLookMath
    {
        /// <summary>The largest linear value an RGBM lightmap can hold (decode: rgb * a * range).</summary>
        public const float RgbmRange = 8f;

        /// <summary>The MOODYBLUES_scene name of a mixed lighting mode.</summary>
        public static string MixedModeName(MixedLightingMode mode) => mode switch
        {
            MixedLightingMode.Subtractive => "subtractive",
            MixedLightingMode.Shadowmask => "shadowmask",
            _ => "indirect",
        };

        /// <summary>
        /// Converts Unity's <c>renderer.lightmapScaleOffset</c> (x, y = scale, z, w = offset, in Unity
        /// UV space) to glTF UV space: <c>lightmapUV = TEXCOORD_1 * (sx, sy) + (ox, oy)</c>.
        /// UnityGLTF writes mesh.uv2 as TEXCOORD_1 with V flipped (v' = 1 - v) and the lightmap image
        /// is stored top row first like every glTF image, so only V needs converting:
        /// 1 - (y * (1 - v') + w) = y * v' + (1 - y - w).
        /// </summary>
        public static Vector4 LightmapScaleOffsetToGltf(Vector4 unityScaleOffset)
        {
            return new Vector4(
                unityScaleOffset.x,
                unityScaleOffset.y,
                unityScaleOffset.z,
                1f - unityScaleOffset.y - unityScaleOffset.w);
        }

        /// <summary>
        /// Encodes a linear HDR colour as RGBM (alpha = multiplier, 8-bit channels). The multiplier is
        /// rounded up to a 1/255 step so the colour channels never need to exceed 1; colours brighter
        /// than <see cref="RgbmRange"/> clip.
        /// </summary>
        public static Color32 RgbmEncode(Color linear, float range = RgbmRange)
        {
            float max = Mathf.Max(linear.r, Mathf.Max(linear.g, linear.b));
            float m = Mathf.Clamp(max / range, 1f / 255f, 1f);
            m = Mathf.Ceil(m * 255f) / 255f;
            float scale = 1f / (m * range);
            return new Color32(
                ToByte(linear.r * scale),
                ToByte(linear.g * scale),
                ToByte(linear.b * scale),
                (byte)Mathf.RoundToInt(m * 255f));
        }

        /// <summary>Inverse of <see cref="RgbmEncode"/>: <c>rgb * a * range</c>.</summary>
        public static Color RgbmDecode(Color32 rgbm, float range = RgbmRange)
        {
            float k = rgbm.a / 255f * range;
            return new Color(rgbm.r / 255f * k, rgbm.g / 255f * k, rgbm.b / 255f * k, 1f);
        }

        private static byte ToByte(float v) => (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f);

        /// <summary>
        /// The ambient diffuse light Unity adds to a non-lightmapped surface facing each axis, in glTF
        /// order +X, -X, +Y, -Y, +Z, -Z. glTF space is Unity space with X mirrored, so glTF +X is Unity
        /// -X. <see cref="SphericalHarmonicsL2.Evaluate"/> is exactly what the shaders' ShadeSH9 returns
        /// (checked against a ShadeSH9 blit), but the probe does not contain
        /// <see cref="RenderSettings.ambientIntensity"/>, which Unity applies on top.
        /// </summary>
        public static Color[] AmbientCube(SphericalHarmonicsL2 probe, float intensity)
        {
            // Unity directions, in glTF cube order.
            var directions = new[]
            {
                Vector3.left, Vector3.right, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
            };
            var cube = new Color[6];
            probe.Evaluate(directions, cube);
            for (int i = 0; i < cube.Length; i++)
            {
                cube[i] = new Color(cube[i].r * intensity, cube[i].g * intensity, cube[i].b * intensity, 1f);
            }
            return cube;
        }
    }
}
