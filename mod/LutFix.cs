using System;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CWIOS
{
    /// <summary>
    /// Colour grading came out tinted on iOS (purple/green) while the image without post-processing looks right, so
    /// the colour grading LUT is suspect. This rebuilds the same LUT on the CPU with URP's LutBuilderLdr maths (same
    /// parameters, read back from the LUT builder material right after URP sets them) and, in Cpu mode, copies it over
    /// the GPU-built one every frame. Post-processing stays fully on - only the 1024x32 table is replaced.
    /// Diagnostics: once per scene the GPU-built LUT is read back and compared with the CPU one ("LUT check:" lines),
    /// and PurpleProbe compares each mode against a prediction made from the no-post image and keeps the best.
    /// </summary>
    public static class LutFix
    {
        public enum Mode { Gpu = 0, Cpu = 1, CpuFlipped = 2 }

        const string Pref = "cwios_lut_mode";
        public static Mode Current { get; private set; } = Mode.Cpu;
        public static bool Decided { get; private set; }
        /// True when the LUT on the GPU this frame is what Current asks for.
        public static bool UpToDate { get; private set; }
        /// Last CPU table (r fastest, then g, then b; linear 0..1 RGB) and its size, for PurpleProbe's prediction.
        public static float[] Table { get; private set; }
        public static int TableSize { get; private set; }

        static readonly LutPass s_Pass = new LutPass();
        static FieldInfo s_PassesField, s_LutField, s_LutPassField, s_LdrMatField;
        static bool s_LoggedRun, s_LoggedFail, s_LoggedHdr;
        static bool s_ReadbackWanted = true;

        public static void Install()
        {
            try
            {
                if (PlayerPrefs.HasKey(Pref))
                {
                    int m = PlayerPrefs.GetInt(Pref);
                    if (m >= 0 && m <= 2) { Current = (Mode)m; Decided = true; }
                }
                PlayerPrefs.DeleteKey("cwios_lut_flip");
            }
            catch { }
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) => s_ReadbackWanted = true;
            Log.Info($"LUT fix installed: mode={Current} ({(Decided ? "saved" : "default, will be checked")})");
        }

        public static void Set(Mode mode, bool save)
        {
            if (mode != Current) UpToDate = false;
            Current = mode;
            if (!save) return;
            Decided = true;
            try { PlayerPrefs.SetInt(Pref, (int)mode); PlayerPrefs.Save(); } catch { }
        }

        public static void RequestCheck() => s_ReadbackWanted = true;

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam.cameraType != CameraType.Game) return;
            if (!cam.TryGetComponent<UniversalAdditionalCameraData>(out var data) || !data.renderPostProcessing) return;
            if (data.renderType != CameraRenderType.Base) return;
            if (data.scriptableRenderer is UniversalRenderer r) r.EnqueuePass(s_Pass);
        }

        static void Find(ScriptableRenderer renderer, out RTHandle lut, out Material ldr)
        {
            lut = null; ldr = null;
            s_PassesField ??= typeof(UniversalRenderer).GetField("m_PostProcessPasses", BindingFlags.NonPublic | BindingFlags.Instance);
            object passes = s_PassesField?.GetValue(renderer);
            if (passes == null) return;
            var t = passes.GetType();
            s_LutField ??= t.GetField("m_ColorGradingLut", BindingFlags.NonPublic | BindingFlags.Instance);
            s_LutPassField ??= t.GetField("m_ColorGradingLutPass", BindingFlags.NonPublic | BindingFlags.Instance);
            lut = s_LutField?.GetValue(passes) as RTHandle;
            object lutPass = s_LutPassField?.GetValue(passes);
            if (lutPass == null) return;
            s_LdrMatField ??= lutPass.GetType().GetField("m_LutBuilderLdr", BindingFlags.NonPublic | BindingFlags.Instance);
            ldr = s_LdrMatField?.GetValue(lutPass) as Material;
        }

        // ---------------- parameters ----------------

        sealed class Params
        {
            public int N;
            public Vector4 ColorBalance, ColorFilter, MixR, MixG, MixB, HueSatCon, Lift, Gamma, Gain, Shadows, Midtones, Highlights, ShaHi, SplitSh, SplitHi;
            public float[][] Curves = new float[8][]; // master, red, green, blue, hueVsHue, hueVsSat, satVsSat, lumVsSat (128 texels each)
            public long Hash;

            public override string ToString()
            {
                string V(Vector4 v) => $"({v.x:0.###},{v.y:0.###},{v.z:0.###},{v.w:0.###})";
                var sb = new StringBuilder();
                sb.Append($"size={N} balance={V(ColorBalance)} filter={V(ColorFilter)} hueSatCon={V(HueSatCon)} mix=[{V(MixR)} {V(MixG)} {V(MixB)}] ");
                sb.Append($"lift={V(Lift)} gamma={V(Gamma)} gain={V(Gain)} shadows={V(Shadows)} mid={V(Midtones)} high={V(Highlights)} limits={V(ShaHi)} ");
                sb.Append($"split={V(SplitSh)}/{V(SplitHi)} curves[");
                string[] names = { "master", "red", "green", "blue", "hueVsHue", "hueVsSat", "satVsSat", "lumVsSat" };
                for (int i = 0; i < 8; i++)
                    sb.Append($"{names[i]}={Curves[i][0]:0.##}/{Curves[i][32]:0.##}/{Curves[i][64]:0.##}/{Curves[i][96]:0.##}/{Curves[i][127]:0.##} ");
                sb.Append(']');
                return sb.ToString();
            }
        }

        static readonly int[] s_Ids =
        {
            Shader.PropertyToID("_ColorBalance"), Shader.PropertyToID("_ColorFilter"), Shader.PropertyToID("_ChannelMixerRed"),
            Shader.PropertyToID("_ChannelMixerGreen"), Shader.PropertyToID("_ChannelMixerBlue"), Shader.PropertyToID("_HueSatCon"),
            Shader.PropertyToID("_Lift"), Shader.PropertyToID("_Gamma"), Shader.PropertyToID("_Gain"), Shader.PropertyToID("_Shadows"),
            Shader.PropertyToID("_Midtones"), Shader.PropertyToID("_Highlights"), Shader.PropertyToID("_ShaHiLimits"),
            Shader.PropertyToID("_SplitShadows"), Shader.PropertyToID("_SplitHighlights"),
        };

        static float[][] s_CurveCache;
        static long s_CurveHash;

        static Params Snapshot(Material m, int n)
        {
            var p = new Params { N = n };
            p.ColorBalance = m.GetVector(s_Ids[0]); p.ColorFilter = m.GetVector(s_Ids[1]);
            p.MixR = m.GetVector(s_Ids[2]); p.MixG = m.GetVector(s_Ids[3]); p.MixB = m.GetVector(s_Ids[4]);
            p.HueSatCon = m.GetVector(s_Ids[5]); p.Lift = m.GetVector(s_Ids[6]); p.Gamma = m.GetVector(s_Ids[7]); p.Gain = m.GetVector(s_Ids[8]);
            p.Shadows = m.GetVector(s_Ids[9]); p.Midtones = m.GetVector(s_Ids[10]); p.Highlights = m.GetVector(s_Ids[11]);
            p.ShaHi = m.GetVector(s_Ids[12]); p.SplitSh = m.GetVector(s_Ids[13]); p.SplitHi = m.GetVector(s_Ids[14]);

            // Curves: the same TextureCurves URP binds; their 128 texels are Evaluate(i / 128).
            var cc = VolumeManager.instance.stack.GetComponent<ColorCurves>();
            TextureCurve[] tc = { cc.master.value, cc.red.value, cc.green.value, cc.blue.value, cc.hueVsHue.value, cc.hueVsSat.value, cc.satVsSat.value, cc.lumVsSat.value };
            long ch = 17;
            for (int i = 0; i < 8; i++)
                for (int k = 0; k < 128; k += 16) ch = ch * 31 + BitConverter.SingleToInt32Bits(tc[i].Evaluate(k / 128f + 0.003f));
            if (s_CurveCache == null || ch != s_CurveHash)
            {
                s_CurveHash = ch;
                s_CurveCache = new float[8][];
                for (int i = 0; i < 8; i++)
                {
                    s_CurveCache[i] = new float[128];
                    for (int k = 0; k < 128; k++) s_CurveCache[i][k] = tc[i].Evaluate(k * (1f / 128f));
                }
            }
            p.Curves = s_CurveCache;

            long h = ch * 31 + n;
            for (int i = 0; i < s_Ids.Length; i++)
            {
                var v = m.GetVector(s_Ids[i]);
                h = h * 31 + BitConverter.SingleToInt32Bits(v.x);
                h = h * 31 + BitConverter.SingleToInt32Bits(v.y);
                h = h * 31 + BitConverter.SingleToInt32Bits(v.z);
                h = h * 31 + BitConverter.SingleToInt32Bits(v.w);
            }
            p.Hash = h;
            return p;
        }

        // ---------------- URP LutBuilderLdr on the CPU ----------------

        static float Sat(float x) => x < 0 ? 0 : x > 1 ? 1 : x;
        static float Lum(float r, float g, float b) => r * 0.212672904f + g * 0.715152204f + b * 0.0721750036f;

        /// Bilinear, clamped sample of a 128x1 curve texture (what the GPU does), saturated like EvaluateCurve.
        static float Curve(float[] c, float t)
        {
            float x = t * 128f - 0.5f;
            int i0 = (int)Math.Floor(x);
            float f = x - i0;
            int a = i0 < 0 ? 0 : i0 > 127 ? 127 : i0;
            int b = i0 + 1 < 0 ? 0 : i0 + 1 > 127 ? 127 : i0 + 1;
            return Sat(c[a] + (c[b] - c[a]) * f);
        }

        static float SoftLight(float bs, float bl)
        {
            float r1 = 2f * bs * bl + bs * bs * (1f - 2f * bl);
            float r2 = (float)Math.Sqrt(bs) * (2f * bl - 1f) + 2f * bs * (1f - bl);
            return bl >= 0.5f ? r2 : r1;
        }

        static float Pw(float x, float e) => x <= 0 ? 0 : (float)Math.Pow(x, e);

        static void Grade(Params p, float r, float g, float b, out float or, out float og, out float ob)
        {
            // White balance (LMS)
            float l = 0.390404999f * r + 0.549941003f * g + 0.00892631989f * b;
            float m = 0.070841603f * r + 0.963172019f * g + 0.00135775004f * b;
            float s = 0.0231081992f * r + 0.128021002f * g + 0.936245024f * b;
            l *= p.ColorBalance.x; m *= p.ColorBalance.y; s *= p.ColorBalance.z;
            r = 2.85846996f * l - 1.62879002f * m - 0.0248910002f * s;
            g = -0.210181996f * l + 1.15820003f * m + 0.000324280991f * s;
            b = -0.0418119989f * l - 0.118169002f * m + 1.06867003f * s;

            // Contrast in LogC
            float Con(float c)
            {
                float lc = 0.244161f * (float)Math.Log10(Math.Max(5.555556f * c + 0.047996f, 1e-30f)) + 0.386036f;
                lc = (lc - 0.4135884f) * p.HueSatCon.z + 0.4135884f;
                return ((float)Math.Pow(10.0, (lc - 0.386036f) / 0.244161f) - 0.047996f) / 5.555556f;
            }
            r = Con(r); g = Con(g); b = Con(b);

            // Colour filter, clamp negatives
            r = Math.Max(0, r * p.ColorFilter.x); g = Math.Max(0, g * p.ColorFilter.y); b = Math.Max(0, b * p.ColorFilter.z);

            // Split toning (gamma space)
            float gr = Pw(r, 1f / 2.2f), gg = Pw(g, 1f / 2.2f), gb = Pw(b, 1f / 2.2f);
            float luma = Sat(Lum(Math.Min(gr, 1), Math.Min(gg, 1), Math.Min(gb, 1)) + p.SplitSh.w);
            float ss = 1f - luma;
            gr = SoftLight(gr, 0.5f + (p.SplitSh.x - 0.5f) * ss); gg = SoftLight(gg, 0.5f + (p.SplitSh.y - 0.5f) * ss); gb = SoftLight(gb, 0.5f + (p.SplitSh.z - 0.5f) * ss);
            gr = SoftLight(gr, 0.5f + (p.SplitHi.x - 0.5f) * luma); gg = SoftLight(gg, 0.5f + (p.SplitHi.y - 0.5f) * luma); gb = SoftLight(gb, 0.5f + (p.SplitHi.z - 0.5f) * luma);
            r = Pw(Math.Abs(gr), 2.2f); g = Pw(Math.Abs(gg), 2.2f); b = Pw(Math.Abs(gb), 2.2f);

            // Channel mixer
            float mr = r * p.MixR.x + g * p.MixR.y + b * p.MixR.z;
            float mg = r * p.MixG.x + g * p.MixG.y + b * p.MixG.z;
            float mb = r * p.MixB.x + g * p.MixB.y + b * p.MixB.z;
            r = mr; g = mg; b = mb;

            // Shadows / midtones / highlights
            float Smooth(float e0, float e1, float x) { float t = Sat((x - e0) / (e1 - e0)); return t * t * (3f - 2f * t); }
            luma = Lum(r, g, b);
            float shF = 1f - Smooth(p.ShaHi.x, p.ShaHi.y, luma);
            float hiF = Smooth(p.ShaHi.z, p.ShaHi.w, luma);
            float miF = 1f - shF - hiF;
            r = r * p.Shadows.x * shF + r * p.Midtones.x * miF + r * p.Highlights.x * hiF;
            g = g * p.Shadows.y * shF + g * p.Midtones.y * miF + g * p.Highlights.y * hiF;
            b = b * p.Shadows.z * shF + b * p.Midtones.z * miF + b * p.Highlights.z * hiF;

            // Lift / gamma / gain
            r = r * p.Gain.x + p.Lift.x; g = g * p.Gain.y + p.Lift.y; b = b * p.Gain.z + p.Lift.z;
            r = Math.Sign(r) * Pw(Math.Abs(r), p.Gamma.x); g = Math.Sign(g) * Pw(Math.Abs(g), p.Gamma.y); b = Math.Sign(b) * Pw(Math.Abs(b), p.Gamma.z);

            // HSV: RgbToHsv
            float px, py, pz, pw;
            if (g >= b) { px = g; py = b; pz = 0f; pw = -1f / 3f; } else { px = b; py = g; pz = -1f; pw = 2f / 3f; }
            float qx, qy, qz, qw;
            if (r >= px) { qx = r; qy = py; qz = pz; qw = px; } else { qx = px; qy = py; qz = pw; qw = r; }
            float d = qx - Math.Min(qw, qy);
            float hue = Math.Abs(qz + (qw - qy) / (6f * d + 1e-4f));
            float sat = d / (qx + 1e-4f);
            float val = qx;

            float satMult = Curve(p.Curves[5], hue) * 2f;           // hue vs sat
            satMult *= Curve(p.Curves[6], sat) * 2f;                // sat vs sat
            satMult *= Curve(p.Curves[7], Lum(r, g, b)) * 2f;       // lum vs sat

            float h2 = hue + p.HueSatCon.x;
            h2 += Curve(p.Curves[4], h2) - 0.5f;                    // hue vs hue
            if (h2 > 1f) h2 -= 1f; else if (h2 < 0f) h2 += 1f;

            // HsvToRgb
            float Hc(float k) { float t = h2 + k; t -= (float)Math.Floor(t); return Sat(Math.Abs(t * 6f - 3f) - 1f); }
            r = val * (1f + sat * (Hc(1f) - 1f));
            g = val * (1f + sat * (Hc(2f / 3f) - 1f));
            b = val * (1f + sat * (Hc(1f / 3f) - 1f));

            // Global saturation
            luma = Lum(r, g, b);
            float k2 = p.HueSatCon.y * satMult;
            r = luma + k2 * (r - luma); g = luma + k2 * (g - luma); b = luma + k2 * (b - luma);

            // YRGB curves
            const float half = 1f / 256f;
            r = Curve(p.Curves[0], r + half); g = Curve(p.Curves[0], g + half); b = Curve(p.Curves[0], b + half);
            or = Curve(p.Curves[1], r + half); og = Curve(p.Curves[2], g + half); ob = Curve(p.Curves[3], b + half);
        }

        static float[] BuildTable(Params p)
        {
            int n = p.N;
            var t = new float[n * n * n * 3];
            float inv = 1f / (n - 1);
            Parallel.For(0, n, bi =>
            {
                for (int gi = 0; gi < n; gi++)
                    for (int ri = 0; ri < n; ri++)
                    {
                        Grade(p, ri * inv, gi * inv, bi * inv, out float r, out float g, out float b);
                        int o = ((bi * n + gi) * n + ri) * 3;
                        t[o] = r; t[o + 1] = g; t[o + 2] = b;
                    }
            });
            return t;
        }

        /// Texel (x, y) of the 2D strip (N*N wide, N high): x = b*N + r, row y = g (or N-1-g when flipped).
        static byte[] ToBytes(float[] t, int n, bool flip)
        {
            var bytes = new byte[n * n * n * 4];
            for (int bi = 0; bi < n; bi++)
                for (int gi = 0; gi < n; gi++)
                {
                    int row = flip ? n - 1 - gi : gi;
                    for (int ri = 0; ri < n; ri++)
                    {
                        int o = ((bi * n + gi) * n + ri) * 3;
                        int d = (row * n * n + bi * n + ri) * 4;
                        bytes[d] = (byte)(Sat(t[o]) * 255f + 0.5f);
                        bytes[d + 1] = (byte)(Sat(t[o + 1]) * 255f + 0.5f);
                        bytes[d + 2] = (byte)(Sat(t[o + 2]) * 255f + 0.5f);
                        bytes[d + 3] = 255;
                    }
                }
            return bytes;
        }

        // ---------------- GPU readback check ----------------

        static void OnReadback(AsyncGPUReadbackRequest req, Params p, int w, int h)
        {
            try
            {
                if (req.hasError) { Log.Warn("LUT check: GPU readback failed"); return; }
                NativeArray<byte> data = req.GetData<byte>();
                int n = p.N;
                if (w != n * n || h != n || data.Length < w * h * 4) { Log.Warn($"LUT check: unexpected LUT {w}x{h}, {data.Length} bytes"); return; }
                var gpu = data.ToArray();
                var table = BuildTable(p);
                Log.Info("LUT check params: " + p);
                double[] err = new double[2]; int[] max = new int[2];
                for (int f = 0; f < 2; f++)
                {
                    var cpu = ToBytes(table, n, f == 1);
                    long sum = 0; int mx = 0;
                    for (int i = 0; i < cpu.Length; i++)
                    {
                        if ((i & 3) == 3) continue;
                        int e = Math.Abs(cpu[i] - gpu[i]);
                        sum += e; if (e > mx) mx = e;
                    }
                    err[f] = sum / (double)(n * n * n * 3); max[f] = mx;
                }
                Log.Info($"LUT check: GPU vs CPU mean error {err[0]:0.0}/255 (max {max[0]}), vs CPU rows flipped {err[1]:0.0}/255 (max {max[1]})");
                var straight = ToBytes(table, n, false);
                var sb = new StringBuilder("LUT check samples (in -> GPU | CPU):");
                int[][] pts = { new[] { 16, 16, 16 }, new[] { 31, 0, 0 }, new[] { 0, 31, 0 }, new[] { 0, 0, 31 }, new[] { 25, 19, 12 }, new[] { 6, 9, 19 }, new[] { 31, 31, 31 }, new[] { 4, 4, 4 } };
                foreach (var q in pts)
                {
                    int d = (q[1] * n * n + q[2] * n + q[0]) * 4;
                    sb.Append($"\n  ({q[0]},{q[1]},{q[2]}) -> GPU ({gpu[d]},{gpu[d + 1]},{gpu[d + 2]}) | CPU ({straight[d]},{straight[d + 1]},{straight[d + 2]})");
                }
                Log.Info(sb.ToString());
                // Per-row green check: tells which way the GPU wrote the g axis.
                int RowG(byte[] a, int row) { long s = 0; for (int x = 0; x < w; x++) s += a[(row * w + x) * 4 + 1]; return (int)(s / w); }
                Log.Info($"LUT check: average green GPU row0={RowG(gpu, 0)} row{n - 1}={RowG(gpu, n - 1)}, CPU row0={RowG(straight, 0)} row{n - 1}={RowG(straight, n - 1)}");
                if (!Decided && Math.Min(err[0], err[1]) < 2.0)
                {
                    Log.Info("LUT check: the GPU-built LUT matches URP's maths - the LUT is not the problem");
                    Set(err[0] <= err[1] ? Mode.Cpu : Mode.CpuFlipped, false);
                }
            }
            catch (Exception e) { Log.Warn("LUT check: " + e); }
        }

        // ---------------- the pass ----------------

        static Texture2D s_Tex;
        static Task<(float[] table, byte[] bytes)> s_Job;
        static long s_JobHash, s_TexHash = long.MinValue;
        static Params s_JobParams;

        sealed class LutPass : ScriptableRenderPass
        {
            // Right after URP's LUT pass (BeforeRenderingPrePasses), before anything samples the LUT.
            public LutPass() { renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses + 1; }

#pragma warning disable 618, 672
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (!renderingData.cameraData.postProcessEnabled) return;
                RTHandle handle; Material mat;
                try { Find(renderingData.cameraData.renderer, out handle, out mat); }
                catch (Exception e)
                {
                    if (!s_LoggedFail) { s_LoggedFail = true; Log.Warn("LUT fix: " + e.Message); }
                    return;
                }
                var lut = handle?.rt;
                if (lut == null || mat == null)
                {
                    if (!s_LoggedFail) { s_LoggedFail = true; Log.Warn($"LUT fix: LUT {(lut == null ? "missing" : "ok")}, builder material {(mat == null ? "missing" : "ok")}"); }
                    return;
                }
                if (renderingData.postProcessingData.gradingMode == ColorGradingMode.HighDynamicRange)
                {
                    if (!s_LoggedHdr) { s_LoggedHdr = true; Log.Warn("LUT fix: HDR grading mode - CPU LUT not available, leaving the GPU LUT"); }
                    UpToDate = Current == Mode.Gpu;
                    return;
                }
                int n = lut.height, w = lut.width;
                if (w != n * n) return;

                Params p;
                try { p = Snapshot(mat, n); }
                catch (Exception e)
                {
                    if (!s_LoggedFail) { s_LoggedFail = true; Log.Warn("LUT fix snapshot: " + e); }
                    return;
                }

                var cmd = CommandBufferPool.Get("CW LUT");
                if (s_ReadbackWanted)
                {
                    s_ReadbackWanted = false;
                    var snap = p;
                    cmd.RequestAsyncReadback(lut, 0, req => OnReadback(req, snap, w, n));
                }

                if (Current == Mode.Gpu) UpToDate = true;
                else
                {
                    bool flip = Current == Mode.CpuFlipped;
                    long want = p.Hash * 3 + (flip ? 1 : 0);
                    if (s_Job != null && s_Job.IsCompleted)
                    {
                        if (s_Job.Status == TaskStatus.RanToCompletion)
                        {
                            var (table, bytes) = s_Job.Result;
                            if (s_Tex == null || s_Tex.width != w || s_Tex.height != n)
                            {
                                if (s_Tex != null) UnityEngine.Object.Destroy(s_Tex);
                                s_Tex = new Texture2D(w, n, TextureFormat.RGBA32, false, true) { name = "CW CPU LUT", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                            }
                            s_Tex.LoadRawTextureData(bytes);
                            s_Tex.Apply(false);
                            s_TexHash = s_JobHash;
                            Table = table; TableSize = n;
                        }
                        else Log.Warn("LUT fix: CPU LUT build failed: " + s_Job.Exception?.GetBaseException().Message);
                        s_Job = null;
                    }
                    if (s_Job == null && s_TexHash != want)
                    {
                        s_JobHash = want; s_JobParams = p;
                        var jp = p;
                        s_Job = Task.Run(() => { var t = BuildTable(jp); return (t, ToBytes(t, jp.N, flip)); });
                    }
                    if (s_Tex != null && s_Tex.width == w && s_Tex.height == n)
                    {
                        cmd.CopyTexture(s_Tex, lut);
                        if (!s_LoggedRun) { s_LoggedRun = true; Log.Info($"LUT fix: using the CPU-built {w}x{n} colour grading LUT ({Current})"); }
                    }
                    UpToDate = s_TexHash == want;
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore 618, 672
        }
    }
}
