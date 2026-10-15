using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace CWIOS
{
    /// <summary>
    /// Finds what draws purple (Unity's error colour) without needing a description of it: a few seconds after each
    /// scene load (and when the log viewer header is tapped) the screen is captured, saved as probe-N.png in
    /// Documents, scanned for magenta pixels, and every object under a magenta area is logged with its material,
    /// shader and texture formats. Also logs every shader in use, materials without a shader and the URP renderer
    /// features once per scene.
    /// </summary>
    public sealed class PurpleProbe : MonoBehaviour
    {
        static PurpleProbe s_Instance;
        static int s_Shot;
        static readonly HashSet<string> s_Inventoried = new HashSet<string>();

        public static void Request(string why, float delay = 0.5f)
        {
            if (s_Instance != null) s_Instance.StartCoroutine(s_Instance.Run(why, delay));
        }

        void Awake()
        {
            s_Instance = this;
            SceneManager.sceneLoaded += (s, m) =>
            {
                if (m != LoadSceneMode.Single) return;
                Request("scene " + s.name, 4f);
                Request("scene " + s.name + " (later)", 15f);
            };
        }

        IEnumerator Run(string why, float delay)
        {
            string scene = SceneManager.GetActiveScene().name;
            yield return new WaitForSecondsRealtime(delay);
            if (SceneManager.GetActiveScene().name != scene) yield break;
            if (s_Inventoried.Add(scene)) Safe("inventory", Inventory);
            while (DebugOverlay.ShowingLog) yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D shot = null;
            try { shot = ScreenCapture.CaptureScreenshotAsTexture(); }
            catch (Exception e) { Log.Warn("probe capture: " + e.Message); }
            if (shot == null) yield break;
            Safe("probe", () => Analyze(shot, why));
            Destroy(shot);
        }

        static void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Log.Warn($"probe {what}: {e}"); }
        }

        static bool Magenta(Color32 c) => c.r > 140 && c.b > 140 && c.g < 90 && Math.Abs(c.r - c.b) < 90;

        static void Analyze(Texture2D shot, string why)
        {
            int w = shot.width, h = shot.height;
            var px = shot.GetPixels32();
            const int GX = 32, GY = 16, STEP = 3;
            var cells = new int[GX * GY];
            var sumX = new long[GX * GY];
            var sumY = new long[GX * GY];
            int total = 0, sampled = 0;
            for (int y = 0; y < h; y += STEP)
                for (int x = 0; x < w; x += STEP)
                {
                    sampled++;
                    if (!Magenta(px[y * w + x])) continue;
                    total++;
                    int c = (y * GY / h) * GX + x * GX / w;
                    cells[c]++; sumX[c] += x; sumY[c] += y;
                }

            // Half-size PNG for the user to send.
            int sw = w / 2, sh = h / 2;
            var small = new Texture2D(sw, sh, TextureFormat.RGB24, false);
            var spx = new Color32[sw * sh];
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++) spx[y * sw + x] = px[(y * 2) * w + x * 2];
            small.SetPixels32(spx);
            small.Apply();
            string file = $"probe-{s_Shot++ % 8}.png";
            File.WriteAllBytes(System.IO.Path.Combine(Application.persistentDataPath, file), small.EncodeToPNG());
            Destroy(small);

            float pct = sampled == 0 ? 0 : 100f * total / sampled;
            string scene = SceneManager.GetActiveScene().name;
            Log.Info($"purple probe [{why}] scene '{scene}': {pct:0.00}% magenta pixels, screenshot saved as {file}");
            if (total < 20) return;

            var cam = MainCamera();
            var order = Enumerable.Range(0, cells.Length).Where(i => cells[i] >= 6).OrderByDescending(i => cells[i]).Take(6).ToList();
            var described = new HashSet<string>();
            foreach (int i in order)
            {
                var p = new Vector2(sumX[i] / (float)cells[i], sumY[i] / (float)cells[i]);
                var sb = new StringBuilder();
                sb.Append($"  magenta area at ({p.x / w:0.00},{p.y / h:0.00}) [{cells[i]} samples]:");
                if (cam != null) DescribeWorldAt(cam, p, sb, described);
                DescribeUiAt(p, sb, described);
                Log.Info(sb.ToString());
            }
        }

        static Camera MainCamera()
        {
            Camera best = null;
            foreach (var c in Camera.allCameras)
                if (c.targetTexture == null && c.isActiveAndEnabled && (best == null || c.depth > best.depth)) best = c;
            return best ?? Camera.main;
        }

        static void DescribeWorldAt(Camera cam, Vector2 p, StringBuilder sb, HashSet<string> done)
        {
            var ray = cam.ScreenPointToRay(p);
            var hits = Physics.RaycastAll(ray, 1000f, ~0, QueryTriggerInteraction.Ignore).OrderBy(x => x.distance).Take(2);
            foreach (var hit in hits)
            {
                var r = hit.collider.GetComponentInParent<Renderer>() ?? hit.collider.GetComponentInChildren<Renderer>();
                sb.Append($"\n    ray hit '{Path(hit.collider.transform)}' at {hit.distance:0.0}m");
                if (r != null) DescribeRenderer(r, sb, done);
            }
            // Renderers without colliders (particles, decals, effects): visible ones whose screen bounds contain p.
            var under = new List<(float, Renderer)>();
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (!r.isVisible || !r.enabled) continue;
                var b = r.bounds;
                if (!ScreenRect(cam, b, out var rect) || !rect.Contains(p)) continue;
                under.Add((Vector3.Distance(cam.transform.position, b.center), r));
            }
            foreach (var (d, r) in under.OrderBy(x => x.Item1).Take(5))
            {
                sb.Append($"\n    under point ({d:0.0}m): {r.GetType().Name} '{Path(r.transform)}'");
                DescribeRenderer(r, sb, done);
            }
        }

        static bool ScreenRect(Camera cam, Bounds b, out Rect rect)
        {
            rect = default;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var s = cam.WorldToScreenPoint(c);
                if (s.z <= 0) return false;
                x0 = Mathf.Min(x0, s.x); y0 = Mathf.Min(y0, s.y); x1 = Mathf.Max(x1, s.x); y1 = Mathf.Max(y1, s.y);
            }
            rect = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        static void DescribeUiAt(Vector2 p, StringBuilder sb, HashSet<string> done)
        {
            foreach (var g in FindObjectsByType<UnityEngine.UI.Graphic>(FindObjectsSortMode.None))
            {
                if (!g.isActiveAndEnabled || g.canvas == null) continue;
                var cv = g.canvas.rootCanvas;
                var ccam = cv.renderMode == RenderMode.ScreenSpaceOverlay ? null : cv.worldCamera;
                if (!RectTransformUtility.RectangleContainsScreenPoint(g.rectTransform, p, ccam)) continue;
                var m = g.materialForRendering;
                if (m == null || m.shader == null || m.shader.name.StartsWith("UI/Default")) continue;
                sb.Append($"\n    UI {g.GetType().Name} '{Path(g.transform)}'");
                DescribeMaterial(m, sb, done);
            }
        }

        static void DescribeRenderer(Renderer r, StringBuilder sb, HashSet<string> done)
        {
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) { sb.Append("\n      material: NULL (draws the error shader)"); continue; }
                DescribeMaterial(m, sb, done);
            }
        }

        static void DescribeMaterial(Material m, StringBuilder sb, HashSet<string> done)
        {
            var sh = m.shader;
            if (!done.Add(m.name + "|" + (sh ? sh.name : "null"))) { sb.Append($"\n      material '{m.name}' (above)"); return; }
            if (sh == null) { sb.Append($"\n      material '{m.name}': shader NULL"); return; }
            sb.Append($"\n      material '{m.name}' shader '{sh.name}' supported={sh.isSupported} passes={sh.passCount} " +
                      $"subshaders={sh.subshaderCount} queue={m.renderQueue} keywords=[{string.Join(" ", m.shaderKeywords)}]");
            var tex = new List<string>();
            foreach (var n in m.GetTexturePropertyNames())
            {
                var t = m.GetTexture(n);
                if (t == null) continue;
                string fmt = t is Texture2D t2 ? t2.format.ToString() : t.graphicsFormat.ToString();
                tex.Add($"{n}={t.name}:{t.GetType().Name}/{fmt}/{t.width}x{t.height}");
            }
            if (tex.Count > 0) sb.Append("\n        textures: " + string.Join(", ", tex));
        }

        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 4; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// Once per scene: shaders in use, materials without shaders, compressed-texture formats the GPU lacks,
        /// URP renderer features and the RenderFeatureHandler references.
        static void Inventory()
        {
            var shaders = new Dictionary<string, int>();
            var problems = new List<string>();
            var badTex = new HashSet<string>();
            void Mat(Material m, string owner)
            {
                if (m == null) return;
                var sh = m.shader;
                string key = sh == null ? "<null>" : sh.name + (sh.isSupported ? "" : " (UNSUPPORTED)");
                shaders[key] = shaders.TryGetValue(key, out var n) ? n + 1 : 1;
                if (sh == null || !sh.isSupported || sh.name == "Hidden/InternalErrorShader")
                    if (problems.Count < 30) problems.Add($"{owner}: material '{m.name}' shader '{(sh ? sh.name : "null")}'");
                foreach (var tn in m.GetTexturePropertyNames())
                    if (m.GetTexture(tn) is Texture2D t && !SystemInfo.SupportsTextureFormat(t.format) && badTex.Count < 30)
                        badTex.Add($"{t.name} ({t.format}) in '{m.name}'");
            }
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                foreach (var m in r.sharedMaterials) Mat(m, r.GetType().Name + " " + Path(r.transform));
            foreach (var g in FindObjectsByType<UnityEngine.UI.Graphic>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Mat(g.material, "UI " + Path(g.transform));
            foreach (var t in FindObjectsByType<Terrain>(FindObjectsSortMode.None))
                Mat(t.materialTemplate, "Terrain " + t.name);
            Mat(RenderSettings.skybox, "skybox");

            Log.Info("shaders in use: " + string.Join(", ", shaders.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}")));
            foreach (var p in problems) Log.Warn("  shader problem: " + p);
            foreach (var t in badTex) Log.Warn("  texture format the GPU can't sample: " + t);

            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                var list = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(urp) as ScriptableRendererData[];
                if (list != null)
                    foreach (var d in list)
                    {
                        if (d == null) { Log.Info("URP renderer data: NULL"); continue; }
                        var feats = d.rendererFeatures.Select(f => ReferenceEquals(f, null) ? "null" : f == null ? "destroyed" : $"{f.GetType().Name}({f.name}, active={f.isActive})");
                        Log.Info($"URP renderer '{d.name}': features [{string.Join(", ", feats)}]");
                    }
            }
            var loaded = Resources.FindObjectsOfTypeAll<ScriptableRendererFeature>();
            Log.Info($"loaded renderer features: {loaded.Length} [{string.Join(", ", loaded.Select(f => f.GetType().Name + " id" + f.GetInstanceID()))}]");
            foreach (var h in FindObjectsByType<RenderFeatureHandler>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                foreach (var fn in new[] { "m_HighFidelity", "m_MediumFidelity1", "m_MediumFidelity2", "m_LowFidelity" })
                {
                    var v = typeof(RenderFeatureHandler).GetField(fn, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(h) as UnityEngine.Object;
                    string s = ReferenceEquals(v, null) ? "null reference" : v == null ? $"dead object id{v.GetInstanceID()}" : $"{v.GetType().Name} id{v.GetInstanceID()}";
                    Log.Info($"RenderFeatureHandler.{fn}: {s}");
                }
        }
    }
}
