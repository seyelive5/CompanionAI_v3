// MachineSpirit/VisionCapture.cs
// ★ v3.60.0: Screenshot capture + resize for Gemma 3 vision
using System;
using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    public static class VisionCapture
    {
        private const int TARGET_WIDTH = 512;
        private const int TARGET_HEIGHT = 384;

        /// <summary>
        /// Capture current screen, resize to 512x384, encode as base64 PNG.
        /// Returns null on failure. All textures are destroyed internally.
        /// </summary>
        public static string CaptureBase64()
        {
            Texture2D screenshot = null;
            Texture2D resized = null;
            try
            {
                screenshot = ScreenCapture.CaptureScreenshotAsTexture();
                if (screenshot == null) return null;

                resized = ResizeTexture(screenshot, TARGET_WIDTH, TARGET_HEIGHT);
                byte[] png = ImageConversion.EncodeToPNG(resized);
                return Convert.ToBase64String(png);
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[VisionCapture] Failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (screenshot != null) UnityEngine.Object.Destroy(screenshot);
                if (resized != null) UnityEngine.Object.Destroy(resized);
            }
        }

        private static Texture2D ResizeTexture(Texture2D source, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture.active = rt;
            Graphics.Blit(source, rt);

            var result = new Texture2D(width, height, TextureFormat.RGB24, false);
            result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            result.Apply();

            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);
            return result;
        }
    }
}
