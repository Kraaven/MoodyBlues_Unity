using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MoodyBlues
{
    /// <summary>
    /// Turns a Unity lightmap into linear HDR colour and then into the RGBM texture the scene export
    /// embeds (see <see cref="SceneLookMath.RgbmEncode"/>).
    ///
    /// Unity stores lightmaps in one of three encodings, chosen per platform and quality setting
    /// (manual: "Lightmaps: Technical information"), and the texture itself tells which:
    /// - full HDR (BC6H, half or float formats; desktop and console): the sample already is linear colour;
    /// - RGBM (a linear 8-bit format; mobile and Normal Quality): colour = rgb * pow(a, 2.2) * 34.49 in
    ///   a Linear colour space (5 * a in Gamma), Unity's unity_Lightmap_HDR instructions;
    /// - double-LDR (an sRGB 8-bit format, only where RGBM is unavailable): the hardware's sRGB read
    ///   makes the sample linear, colour = sample * 4.59 (2 in Gamma).
    /// A GPU blit through <c>Hidden/MoodyBlues/LightmapDecode</c> does the decode, which also works
    /// for unreadable (compressed) textures. Projects in Gamma colour space keep Unity's gamma-space
    /// lighting numbers; the export does not convert them.
    /// </summary>
    internal static class LightmapDecoder
    {
        private const string ShaderResource = "MoodyBluesLightmapDecode";

        private enum Encoding { FullHdr = 0, Rgbm = 1, DoubleLdr = 2 }

        private static Encoding DetectEncoding(Texture lightmap)
        {
            GraphicsFormat format = lightmap.graphicsFormat;
            if (GraphicsFormatUtility.IsFloatFormat(format) ||
                format == GraphicsFormat.RGB_BC6H_UFloat || format == GraphicsFormat.RGB_BC6H_SFloat)
            {
                return Encoding.FullHdr;
            }
            return GraphicsFormatUtility.IsSRGBFormat(format) ? Encoding.DoubleLdr : Encoding.Rgbm;
        }

        /// <summary>
        /// The lightmap as linear HDR colour, row 0 at the bottom like every Unity texture. The caller
        /// owns <paramref name="width"/> x <paramref name="height"/> pixels.
        /// </summary>
        public static Color[] DecodeToLinear(Texture lightmap, out int width, out int height)
        {
            Shader shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null) throw new System.InvalidOperationException($"Shader '{ShaderResource}' is missing from Resources.");

            Encoding encoding = DetectEncoding(lightmap);
            bool linearSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Vector4 instructions = encoding switch
            {
                Encoding.Rgbm => linearSpace ? new Vector4(34.493242f, 2.2f, 0, 0) : new Vector4(5f, 1f, 0, 0),
                Encoding.DoubleLdr => linearSpace ? new Vector4(4.59f, 1f, 0, 0) : new Vector4(2f, 1f, 0, 0),
                _ => new Vector4(1f, 1f, 0, 0),
            };

            width = lightmap.width;
            height = lightmap.height;

            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetFloat("_Mode", (float)encoding);
            material.SetVector("_Decode", instructions);

            RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            RenderTexture previousActive = RenderTexture.active;
            bool previousSrgbWrite = GL.sRGBWrite;
            var readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            try
            {
                GL.sRGBWrite = false;
                Graphics.Blit(lightmap, target, material);
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readback.Apply(false);
                return readback.GetPixels();
            }
            finally
            {
                GL.sRGBWrite = previousSrgbWrite;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                DestroyObject(readback);
                DestroyObject(material);
            }
        }

        /// <summary>
        /// A linear (non-sRGB) RGBA32 texture holding the lightmap RGBM-encoded. The caller destroys it
        /// once the export is written.
        /// </summary>
        public static Texture2D EncodeRgbm(Texture lightmap)
        {
            Color[] linear = DecodeToLinear(lightmap, out int width, out int height);
            var pixels = new Color32[linear.Length];
            for (int i = 0; i < linear.Length; i++) pixels[i] = SceneLookMath.RgbmEncode(linear[i]);

            var rgbm = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                name = lightmap.name + "_rgbm",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            rgbm.SetPixels32(pixels);
            rgbm.Apply(false);
            return rgbm;
        }

        private static void DestroyObject(Object o)
        {
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
