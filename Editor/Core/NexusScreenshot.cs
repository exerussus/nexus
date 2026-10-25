using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Скриншоты для CLI: Game (с UI Toolkit, через ScreenCapture), Scene (камера вида сцены) и любое окно редактора
    /// (пиксели экрана). По умолчанию уменьшаются до 1280 px по длинной стороне — картинку дешевле читать.
    /// </summary>
    [InitializeOnLoad]
    internal static class NexusScreenshot
    {
        private const int KeepDefault = 30;
        private const double EditModeFallbackAfter = 3.0;

        static NexusScreenshot()
        {
            NexusCliJobs.Register("screenshot", Step);
        }

        private static string DefaultDir => NexusTempPaths.Screenshots;

        [NexusCommand("screenshot", "Скриншот: game (по умолчанию, с UI), scene или окно редактора; печатает путь к PNG",
            Usage = "screenshot [game|scene|window] [--window <заголовок окна>] [--out <файл.png>] [--max 1280|--full] [--super 1] [--timeout 10]")]
        private static int Shot(NexusCommandContext ctx)
        {
            var target = (ctx.Arg(0) ?? (ctx.Has("window") ? "window" : "game")).ToLowerInvariant();
            var max = ctx.Flag("full") ? 0 : Math.Max(64, ctx.GetInt("max", 1280));
            var custom = NexusCliText.ResolveOut(ctx.Get("out"), ctx.WorkingDirectory, NexusPaths.ProjectRoot);
            var path = custom ?? Path.Combine(DefaultDir, $"{target}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
            if (custom == null) NexusTempPaths.Ensure(DefaultDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            Prune();

            switch (target)
            {
                case "game":
                {
                    // сырой кадр — всегда во временной папке: запоздалая запись Unity не мусорит рядом с --out
                    NexusTempPaths.Ensure(DefaultDir);
                    var raw = Path.Combine(DefaultDir, "raw-" + Guid.NewGuid().ToString("N") + ".png");
                    var super = Math.Max(1, Math.Min(4, ctx.GetInt("super", 1)));
                    var timeout = Math.Max(2, ctx.GetInt("timeout", 10));
                    ScreenCapture.CaptureScreenshot(raw, super);
                    RepaintGameView();

                    var job = NexusCliJobs.Start("screenshot", timeout + 5, ctx);
                    job.Set("raw", raw);
                    job.Set("out", path);
                    job.Set("max", max.ToString(CultureInfo.InvariantCulture));
                    job.Set("limit", timeout.ToString(CultureInfo.InvariantCulture));
                    return NexusCommandContext.Exit.Ok;
                }

                case "scene":
                {
                    var view = SceneView.lastActiveSceneView;
                    if (view == null || view.camera == null) throw new NexusCommandException("Нет открытого окна Scene.", NexusCommandContext.Exit.Failed);
                    var ppp = EditorGUIUtility.pixelsPerPoint;
                    var w = Math.Max(16, (int)(view.position.width * ppp));
                    var h = Math.Max(16, (int)(view.position.height * ppp));
                    var tex = RenderCameras(new[] { view.camera }, w, h);
                    return Done(ctx, Save(tex, path, max), "scene", null);
                }

                case "window":
                {
                    var title = ctx.Get("window") ?? ctx.Arg(1) ?? throw new NexusCommandException("нужен заголовок окна: screenshot window --window Inspector");
                    var window = FindWindow(title);

                    // координаты экрана в точках, как у пипетки Unity (GUIToScreenPoint без масштаба)
                    var rect = window.position;
                    var w = Math.Max(1, (int)rect.width);
                    var h = Math.Max(1, (int)rect.height);
                    var pixels = InternalEditorUtility.ReadScreenPixel(new Vector2(rect.x, rect.y), w, h);
                    if (pixels == null || pixels.Length < w * h) throw new NexusCommandException("Пиксели окна не прочитались.", NexusCommandContext.Exit.Failed);
                    var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                    try
                    {
                        tex.SetPixels(pixels);
                        tex.Apply();
                    }
                    catch
                    {
                        UnityEngine.Object.DestroyImmediate(tex);
                        throw;
                    }

                    return Done(ctx, Save(tex, path, max), "window", "пиксели экрана: окно должно быть видно и не перекрыто");
                }

                default:
                    throw new NexusCommandException("game, scene или window");
            }
        }

        private static void Step(NexusCliJob job)
        {
            var raw = job.Get("raw");
            var output = job.Get("out");
            int.TryParse(job.Get("max", "1280"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max);
            int.TryParse(job.Get("limit", "10"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit);
            var elapsed = NexusCliJobs.Now - job.StartedAt;

            if (File.Exists(raw))
            {
                // файл дописан, когда размер не меняется между кадрами
                var size = new FileInfo(raw).Length;
                var previous = job.Get("size");
                job.Set("size", size.ToString(CultureInfo.InvariantCulture));
                if (size <= 0 || previous != size.ToString(CultureInfo.InvariantCulture)) return;

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(raw);
                }
                catch (IOException)
                {
                    bytes = null; // Unity ещё пишет файл
                }

                if (bytes == null || !tex.LoadImage(bytes))
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                    if (elapsed <= limit) return; // дописывается — подождать
                    TryDelete(raw);
                    NexusCliJobs.Finish(job, NexusCommandContext.Exit.Failed, "✗ Снимок не читается как PNG.\n");
                    return;
                }

                TryDelete(raw);
                var saved = Save(tex, output, max);
                NexusCliJobs.Finish(job, NexusCommandContext.Exit.Ok, $"{saved.Path}\n{saved.Width}×{saved.Height}\n",
                    new { path = saved.Path, width = saved.Width, height = saved.Height, target = "game" });
                return;
            }

            RepaintGameView();
            NexusCliJobs.SetPhase(job, "ждём кадр Game");

            if (!EditorApplication.isPlaying && elapsed > EditModeFallbackAfter)
            {
                // в Edit Mode Game может не перерисоваться: рендер камер сцены (без оверлея UI Toolkit)
                var cameras = Camera.allCameras;
                if (cameras.Length == 0)
                {
                    NexusCliJobs.Finish(job, NexusCommandContext.Exit.Failed, "✗ Game не отрисовался, и в сцене нет камер.\n");
                    return;
                }

                Array.Sort(cameras, (a, b) => a.depth.CompareTo(b.depth));
                var size = GameViewSize();
                var saved = Save(RenderCameras(cameras, size.x, size.y), output, max);
                NexusCliJobs.Finish(job, NexusCommandContext.Exit.Ok,
                    $"{saved.Path}\n{saved.Width}×{saved.Height}\n! Edit Mode: рендер камер без UI Toolkit — для снимка с UI запустите Play\n",
                    new { path = saved.Path, width = saved.Width, height = saved.Height, target = "game", note = "cameras-only" });
                return;
            }

            if (elapsed > limit)
            {
                TryDelete(raw);
                NexusCliJobs.Finish(job, NexusCommandContext.Exit.Failed,
                    "✗ Кадр Game не отрисовался: Play на паузе, окно Game закрыто или редактор свёрнут без «Run In Background».\n");
                return;
            }

            if (EditorApplication.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
        }

        // ---------------------------------------------------------------- рендер и сохранение

        private struct Saved
        {
            public string Path;
            public int Width;
            public int Height;
        }

        private static int Done(NexusCommandContext ctx, Saved saved, string target, string note)
        {
            ctx.Print(saved.Path);
            ctx.Print($"{saved.Width}×{saved.Height}");
            if (note != null) ctx.Print("! " + note);
            ctx.Data = new { path = saved.Path, width = saved.Width, height = saved.Height, target, note };
            return NexusCommandContext.Exit.Ok;
        }

        private static Texture2D RenderCameras(Camera[] cameras, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var previousActive = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);
                foreach (var camera in cameras)
                {
                    if (camera == null) continue;
                    var previous = camera.targetTexture;
                    camera.targetTexture = rt;
                    camera.Render();
                    camera.targetTexture = previous;
                }

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                return tex;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Уменьшить до <paramref name="max"/> по длинной стороне (0 — как есть), записать PNG, освободить текстуру.</summary>
        private static Saved Save(Texture2D tex, string path, int max)
        {
            try
            {
                var w = tex.width;
                var h = tex.height;
                var output = tex;
                if (max > 0 && (w > max || h > max))
                {
                    var k = (float)max / Math.Max(w, h);
                    var nw = Math.Max(1, Mathf.RoundToInt(w * k));
                    var nh = Math.Max(1, Mathf.RoundToInt(h * k));
                    var rt = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    var previousActive = RenderTexture.active;
                    try
                    {
                        Graphics.Blit(tex, rt);
                        RenderTexture.active = rt;
                        output = new Texture2D(nw, nh, TextureFormat.RGB24, false);
                        output.ReadPixels(new Rect(0, 0, nw, nh), 0, 0);
                        output.Apply();
                    }
                    finally
                    {
                        RenderTexture.active = previousActive;
                        RenderTexture.ReleaseTemporary(rt);
                    }
                }

                try
                {
                    File.WriteAllBytes(path, output.EncodeToPNG());
                    return new Saved { Path = path.Replace('\\', '/'), Width = output.width, Height = output.height };
                }
                finally
                {
                    if (output != tex) UnityEngine.Object.DestroyImmediate(output);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static EditorWindow FindWindow(string title)
        {
            EditorWindow exact = null;
            EditorWindow partial = null;
            var partialCount = 0;
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (w == null) continue;
                var t = w.titleContent != null ? w.titleContent.text : string.Empty;
                var type = w.GetType().Name;
                if (string.Equals(t, title, StringComparison.OrdinalIgnoreCase) || string.Equals(type, title, StringComparison.OrdinalIgnoreCase))
                {
                    exact = w;
                    break;
                }

                if (t.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    partial = w;
                    partialCount++;
                }
            }

            if (exact != null) return exact;
            if (partialCount == 1) return partial;
            throw new NexusCommandException(partialCount == 0 ? $"Окно «{title}» не открыто." : $"«{title}» подходит к нескольким окнам — уточните заголовок.", NexusCommandContext.Exit.Failed);
        }

        private static void RepaintGameView()
        {
            try
            {
                var type = Type.GetType("UnityEditor.GameView, UnityEditor");
                if (type == null) return;
                foreach (var w in Resources.FindObjectsOfTypeAll(type))
                    if (w is EditorWindow window)
                        window.Repaint();
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: перерисовка Game", e);
            }
        }

        /// <summary>Размер Game View (внутренний API; не нашёлся — 1920×1080).</summary>
        private static Vector2Int GameViewSize()
        {
            try
            {
                foreach (var name in new[] { "UnityEditor.PlayModeView", "UnityEditor.GameView" })
                {
                    var type = Type.GetType(name + ", UnityEditor");
                    if (type == null) continue;
                    foreach (var methodName in new[] { "GetMainPlayModeViewTargetSize", "GetMainGameViewTargetSize" })
                    {
                        var m = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (m == null || m.GetParameters().Length != 0) continue;
                        if (m.Invoke(null, null) is Vector2 size && size.x >= 16 && size.y >= 16) return new Vector2Int((int)size.x, (int)size.y);
                    }
                }
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: размер Game", e);
            }

            return new Vector2Int(1920, 1080);
        }

        private static void Prune()
        {
            try
            {
                var dir = DefaultDir;
                if (!Directory.Exists(dir)) return;
                // забытые сырые кадры (запоздалая запись после запасного рендера) — старше минуты
                foreach (var f in new DirectoryInfo(dir).GetFiles("raw-*.png"))
                    if (DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromMinutes(1))
                        TryDelete(f.FullName);

                var files = new DirectoryInfo(dir).GetFiles("*.png");
                if (files.Length <= KeepDefault) return;
                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                for (var i = 0; i < files.Length - KeepDefault; i++) TryDelete(files[i].FullName);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: чистка скриншотов", e);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: удаление файла", e);
            }
        }
    }
}
