using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoodyBlues.Tests
{
    /// <summary>Pins down the scene-export lighting conversions the viewer relies on (Spec.md Section 13.3).</summary>
    public class SceneLookTests
    {
        // ---- Lightmap scaleOffset ---------------------------------------------------------------

        [Test]
        public void ScaleOffset_FlipsOnlyV()
        {
            Vector4 so = SceneLookMath.LightmapScaleOffsetToGltf(new Vector4(0.5f, 0.25f, 0.125f, 0.5f));
            Assert.AreEqual(0.5f, so.x, 1e-6f);
            Assert.AreEqual(0.25f, so.y, 1e-6f);
            Assert.AreEqual(0.125f, so.z, 1e-6f);
            Assert.AreEqual(1f - 0.25f - 0.5f, so.w, 1e-6f);
        }

        [Test]
        public void ScaleOffset_MapsFlippedUvToTheSameLightmapPointAsUnity()
        {
            var unity = new Vector4(0.4f, 0.3f, 0.1f, 0.6f);
            Vector4 gltf = SceneLookMath.LightmapScaleOffsetToGltf(unity);

            foreach (Vector2 uv2 in new[] { Vector2.zero, Vector2.one, new Vector2(0.2f, 0.9f), new Vector2(0.75f, 0.1f) })
            {
                // Unity: lightmap point (u, v), v up. glTF image coordinates are v' = 1 - v, and the
                // mesh exports uv2 flipped the same way.
                var unityPoint = new Vector2(uv2.x * unity.x + unity.z, uv2.y * unity.y + unity.w);
                var gltfUv = new Vector2(uv2.x, 1f - uv2.y);
                var gltfPoint = new Vector2(gltfUv.x * gltf.x + gltf.z, gltfUv.y * gltf.y + gltf.w);

                Assert.AreEqual(unityPoint.x, gltfPoint.x, 1e-5f);
                Assert.AreEqual(1f - unityPoint.y, gltfPoint.y, 1e-5f);
            }
        }

        [Test]
        public void MixedModeName_MapsEveryMode()
        {
            Assert.AreEqual("subtractive", SceneLookMath.MixedModeName(MixedLightingMode.Subtractive));
            Assert.AreEqual("shadowmask", SceneLookMath.MixedModeName(MixedLightingMode.Shadowmask));
            Assert.AreEqual("indirect", SceneLookMath.MixedModeName(MixedLightingMode.IndirectOnly));
        }

        // ---- RGBM -------------------------------------------------------------------------------

        [Test]
        public void Rgbm_RoundTripStaysWithinQuantizationError()
        {
            var rng = new System.Random(1234);
            for (int i = 0; i < 2000; i++)
            {
                // Mix of dim and bright colours, never above the range.
                float scale = (float)rng.NextDouble() * SceneLookMath.RgbmRange;
                var c = new Color((float)rng.NextDouble() * scale, (float)rng.NextDouble() * scale, (float)rng.NextDouble() * scale);

                Color32 enc = SceneLookMath.RgbmEncode(c);
                Color dec = SceneLookMath.RgbmDecode(enc);

                // Channel rounding is half a step of m * range; the multiplier is at most one step above exact.
                float m = enc.a / 255f;
                float bound = 0.5f / 255f * m * SceneLookMath.RgbmRange + 1e-4f;
                Assert.AreEqual(c.r, dec.r, bound);
                Assert.AreEqual(c.g, dec.g, bound);
                Assert.AreEqual(c.b, dec.b, bound);
            }
        }

        [Test]
        public void Rgbm_BlackKeepsMinimumMultiplier()
        {
            Color32 enc = SceneLookMath.RgbmEncode(Color.black);
            Assert.AreEqual(1, enc.a);
            Assert.AreEqual(0f, SceneLookMath.RgbmDecode(enc).r, 1e-6f);
        }

        [Test]
        public void Rgbm_ClipsAboveRange()
        {
            Color dec = SceneLookMath.RgbmDecode(SceneLookMath.RgbmEncode(new Color(100f, 0f, 0f)));
            Assert.AreEqual(SceneLookMath.RgbmRange, dec.r, 1e-4f);
        }

        [Test]
        public void Rgbm_MaxChannelUsesFullByteRange()
        {
            Color32 enc = SceneLookMath.RgbmEncode(new Color(4f, 1f, 0f));
            Assert.AreEqual(128, enc.a); // ceil(4 / 8 * 255) = 128
            Assert.GreaterOrEqual(enc.r, 250);
        }

        // ---- Ambient cube -----------------------------------------------------------------------

        [Test]
        public void AmbientCube_ConstantAmbientGivesThatColorOnEveryFace()
        {
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(new Color(0.2f, 0.4f, 0.6f));

            foreach (Color c in SceneLookMath.AmbientCube(probe, 1f))
            {
                Assert.AreEqual(0.2f, c.r, 1e-4f);
                Assert.AreEqual(0.4f, c.g, 1e-4f);
                Assert.AreEqual(0.6f, c.b, 1e-4f);
            }
        }

        [Test]
        public void AmbientCube_AppliesIntensity()
        {
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(new Color(0.2f, 0.2f, 0.2f));
            Assert.AreEqual(0.5f, SceneLookMath.AmbientCube(probe, 2.5f)[2].g, 1e-4f);
        }

        [Test]
        public void AmbientCube_XIsMirroredForGltf_YAndZAreNot()
        {
            // Light only from Unity +X, +Y and +Z at once, each with a distinct colour.
            var probe = new SphericalHarmonicsL2();
            probe.AddDirectionalLight(Vector3.right, Color.red, 1f);
            probe.AddDirectionalLight(Vector3.up, Color.green, 1f);
            probe.AddDirectionalLight(Vector3.forward, Color.blue, 1f);

            Color[] cube = SceneLookMath.AmbientCube(probe, 1f);

            // glTF order +X, -X, +Y, -Y, +Z, -Z; glTF +X is Unity -X.
            Assert.Greater(cube[1].r, cube[0].r, "red came from Unity +X, which is glTF -X");
            Assert.Greater(cube[2].g, cube[3].g, "green came from +Y");
            Assert.Greater(cube[4].b, cube[5].b, "blue came from +Z");
            Assert.Greater(cube[1].r, 0.1f);
        }
    }
}
