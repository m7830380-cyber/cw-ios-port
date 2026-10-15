using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CWIOS
{
    /// <summary>
    /// Colour grading on iOS came out tinted: dark areas green, bright areas purple (the colour-cast ablation showed
    /// the colour grading LUT is the culprit - only "no post-processing" looks right). That pattern is the LUT's
    /// green axis running upside down. This pass runs right after URP builds the LUT each frame and mirrors its rows
    /// (pure CopyTexture, no render target changes). PurpleProbe compares both orientations against the
    /// no-post-processing image and keeps whichever matches (saved in PlayerPrefs).
    /// </summary>
    public static class LutFix
    {
        const string Pref = "cwios_lut_flip";
        public static bool Flip { get; private set; } = true;
        public static bool Decided { get; private set; }

        static readonly FlipPass s_Pass = new FlipPass();
        static FieldInfo s_PassesField, s_LutField;
        static bool s_LoggedRun, s_LoggedFail;

        public static void Install()
        {
            try
            {
                if (PlayerPrefs.HasKey(Pref)) { Flip = PlayerPrefs.GetInt(Pref) == 1; Decided = true; }
            }
            catch { }
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            Log.Info($"LUT fix installed: flip={(Flip ? "on" : "off")} ({(Decided ? "saved" : "default, will be checked")})");
        }

        public static void Set(bool flip, bool save)
        {
            Flip = flip;
            if (!save) return;
            Decided = true;
            try { PlayerPrefs.SetInt(Pref, flip ? 1 : 0); PlayerPrefs.Save(); } catch { }
        }

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (!Flip || cam.cameraType != CameraType.Game) return;
            if (!cam.TryGetComponent<UniversalAdditionalCameraData>(out var data) || !data.renderPostProcessing) return;
            if (data.renderType != CameraRenderType.Base) return;
            if (data.scriptableRenderer is UniversalRenderer r) r.EnqueuePass(s_Pass);
        }

        static RTHandle Lut(ScriptableRenderer renderer)
        {
            s_PassesField ??= typeof(UniversalRenderer).GetField("m_PostProcessPasses", BindingFlags.NonPublic | BindingFlags.Instance);
            object passes = s_PassesField?.GetValue(renderer);
            if (passes == null) return null;
            s_LutField ??= passes.GetType().GetField("m_ColorGradingLut", BindingFlags.NonPublic | BindingFlags.Instance);
            return s_LutField?.GetValue(passes) as RTHandle;
        }

        sealed class FlipPass : ScriptableRenderPass
        {
            RenderTexture m_Tmp;

            // Right after the LUT pass (BeforeRenderingPrePasses), before anything uses it.
            public FlipPass() { renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses + 1; }

#pragma warning disable 618, 672
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (!Flip || !renderingData.cameraData.postProcessEnabled) return;
                RenderTexture lut;
                try { lut = Lut(renderingData.cameraData.renderer)?.rt; }
                catch (Exception e)
                {
                    if (!s_LoggedFail) { s_LoggedFail = true; Log.Warn("LUT fix: " + e.Message); }
                    return;
                }
                if (lut == null)
                {
                    if (!s_LoggedFail) { s_LoggedFail = true; Log.Warn("LUT fix: no colour grading LUT found"); }
                    return;
                }
                if (m_Tmp == null || m_Tmp.width != lut.width || m_Tmp.height != lut.height || m_Tmp.graphicsFormat != lut.graphicsFormat)
                {
                    if (m_Tmp != null) m_Tmp.Release();
                    var d = lut.descriptor;
                    d.depthBufferBits = 0;
                    d.msaaSamples = 1;
                    d.useMipMap = false;
                    m_Tmp = new RenderTexture(d) { name = "CW LUT flip" };
                    m_Tmp.Create();
                }
                int w = lut.width, h = lut.height;
                var cmd = CommandBufferPool.Get("CW LUT flip");
                for (int y = 0; y < h; y++)
                    cmd.CopyTexture(lut, 0, 0, 0, y, w, 1, m_Tmp, 0, 0, 0, h - 1 - y);
                cmd.CopyTexture(m_Tmp, lut);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
                if (!s_LoggedRun) { s_LoggedRun = true; Log.Info($"LUT fix: flipping {w}x{h} {lut.graphicsFormat} colour grading LUT each frame"); }
            }
#pragma warning restore 618, 672
        }
    }
}
