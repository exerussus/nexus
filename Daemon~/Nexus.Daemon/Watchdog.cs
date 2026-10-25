using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Сторож — компонент демона: следит за главным потоком каждого редактора и будит застрявший в фоне.
    /// <list type="bullet">
    /// <item>Пульс главного потока — <c>ping</c> раз в 2 с (выполняется в главном потоке Unity) и любые ответы на
    /// вызовы. Перезагрузка домена — отсчёт от записи <c>reloading</c> в файл экземпляра.</item>
    /// <item>Молчит ≥ 10 с — <c>NexusTemp/cli/stall.json</c>: фаза, отвечает ли окно, на переднем ли плане, окна
    /// процесса (модальное окно видно по заголовку), ошибки из Editor.log.</item>
    /// <item>Будильник (настройка <c>Wake</c> проекта: off | soft | focus). Unity в фоне не продолжает работу после
    /// перезагрузки домена, пока окно не в фокусе. Будим, только если окно отвечает, Unity в фоне и нет диалога
    /// Windows: soft — WM_ACTIVATEAPP без смены фокуса; focus — если мягкого мало, Unity на передний план,
    /// пульс ожил — фокус возвращается. Каждая попытка — в <c>wake.json</c>.</item>
    /// </list>
    /// Модальные окна не закрываются никогда — это решение человека.
    /// </summary>
    internal static class Watchdog
    {
        private const double PingInterval = 2.0;
        private const int PingTimeout = 3;
        private const double StallSeconds = 10;
        private const double WakeSeconds = 4;

        private sealed class WakeState
        {
            public string Step;
            public DateTime Started;
            public long PrevForeground;
            public long Main;
            public bool WasIconic;
            public string Phase;
        }

        private sealed class Watch
        {
            public DateTime NextPing;
            public int PingBusy;
            public DateTime LastReport = DateTime.MinValue;
            public WakeState Wake;
            public DateTime CooldownUntil = DateTime.MinValue;
            public int Attempts;
            public DateTime? FreshSince;
        }

        private static readonly ConcurrentDictionary<Project, Watch> Watches = new ConcurrentDictionary<Project, Watch>();

        public static void Tick()
        {
            foreach (var p in Registry.All)
            {
                var w = Watches.GetOrAdd(p, _ => new Watch());
                try
                {
                    Step(p, w);
                }
                catch (Exception e)
                {
                    Log.Error("сторож " + p.Id, e);
                }
            }

            foreach (var p in Watches.Keys)
                if (p.Removed)
                    Watches.TryRemove(p, out _);
        }

        private static void Step(Project p, Watch w)
        {
            var now = DateTime.UtcNow;
            if (p.State == "closed")
            {
                p.Stalled = false;
                return;
            }

            // пульс главного потока
            if (p.EditorState == "ready" && p.Port > 0 && now >= w.NextPing && Interlocked.CompareExchange(ref w.PingBusy, 1, 0) == 0)
            {
                w.NextPing = now.AddSeconds(PingInterval);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (status, r, _) = await Bridge.Send(p, Bridge.Request("ping", Array.Empty<string>(), p.Path, PingTimeout), PingTimeout, CancellationToken.None);
                        if (status == BridgeStatus.Ok && (int)Json.Num(r, "exitCode", 1) == 0)
                        {
                            p.MarkAlive();
                            var phase = Json.Str(Json.TryParseObject(Json.Str(r, "result")), "phase");
                            if (phase != null) p.EditorPhase = phase;
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref w.PingBusy, 0);
                    }
                });
            }

            var age = p.SilentSeconds;
            if (age < WakeSeconds)
            {
                p.Stalled = false;
                Json.TryDelete(Path.Combine(p.CliDir, "stall.json"));
                w.Attempts = 0;
                if (w.Wake != null)
                {
                    w.FreshSince ??= now;
                    if ((now - w.FreshSince.Value).TotalSeconds >= 1.5) FinishWake(p, w, true, now);
                }

                return;
            }

            w.FreshSince = null;
            p.Stalled = age >= StallSeconds;

            var windows = WinApi.Windows(p.Pid);
            var responding = WinApi.Responding(windows);
            var foreground = WinApi.Foreground(p.Pid);
            var dialog = WinApi.HasDialog(windows);
            var mode = WakeMode(p);

            if (w.Wake != null)
            {
                var elapsed = (now - w.Wake.Started).TotalSeconds;
                if (w.Wake.Step == "soft" && elapsed >= 4 && mode == "focus" && !foreground && !dialog)
                {
                    w.Wake.PrevForeground = WinApi.ForegroundHandle();
                    w.Wake.Main = WinApi.MainWindow(windows);
                    w.Wake.WasIconic = WinApi.Iconic(w.Wake.Main);
                    WinApi.ActivateApp(windows, false);
                    if (WinApi.Focus(w.Wake.Main, true))
                    {
                        WriteWake(p, "soft", false, elapsed, w.Wake.Phase);
                        w.Wake.Step = "focus";
                        w.Wake.Started = now;
                    }
                }
                else if (elapsed >= 10 || (w.Wake.Step == "soft" && elapsed >= 4 && mode != "focus"))
                {
                    FinishWake(p, w, false, now);
                    w.CooldownUntil = now.AddSeconds(15);
                }
            }
            else if (WinApi.Supported && mode != "off" && responding == true && !foreground && !dialog && now >= w.CooldownUntil && w.Attempts < 3)
            {
                // окно отвечает (поток свободен), Unity в фоне, диалога нет — редактор ждёт фокуса
                w.Attempts++;
                var phase = $"{p.EditorState} / {p.Phase}";
                if (mode == "focus")
                {
                    // проверено: после перезагрузки домена Unity в фоне ждёт настоящего фокуса, мягкий шаг не помогает
                    var main = WinApi.MainWindow(windows);
                    var wake = new WakeState { Step = "focus", Started = now, Phase = phase, PrevForeground = WinApi.ForegroundHandle(), Main = main, WasIconic = WinApi.Iconic(main) };
                    var ok = WinApi.Focus(main, true);
                    Log.Info($"{p.Id}: будильник (фокус) — {(ok ? "Unity на переднем плане" : "Windows не дал фокус")}, молчит {age:0} с");
                    if (ok) w.Wake = wake;
                    else
                    {
                        WriteWake(p, "focus", false, 0, phase + " · Windows не дал фокус");
                        w.CooldownUntil = now.AddSeconds(15);
                    }
                }
                else
                {
                    w.Wake = new WakeState { Step = "soft", Started = now, Phase = phase };
                    WinApi.ActivateApp(windows, true);
                    Log.Info($"{p.Id}: будильник (мягко), молчит {age:0} с");
                }
            }

            if (age < StallSeconds || (now - w.LastReport).TotalSeconds < 3) return;
            w.LastReport = now;
            WriteStall(p, w, age, responding, foreground, dialog, mode, windows);
        }

        private static void FinishWake(Project p, Watch w, bool ok, DateTime now)
        {
            var wake = w.Wake;
            w.Wake = null;
            w.FreshSince = null;
            if (wake == null) return;
            var seconds = (now - wake.Started).TotalSeconds;
            var windows = WinApi.Windows(p.Pid);
            if (wake.Step == "soft")
            {
                if (!WinApi.Foreground(p.Pid)) WinApi.ActivateApp(windows, false);
            }
            else if (WinApi.Foreground(p.Pid))
            {
                // фокус возвращаем, только если он всё ещё у Unity: человек мог переключиться сам
                if (wake.WasIconic) WinApi.Minimize(wake.Main);
                WinApi.Focus(wake.PrevForeground, false);
            }

            WriteWake(p, wake.Step, ok, seconds, wake.Phase);
            Log.Info($"{p.Id}: будильник {wake.Step} — {(ok ? "ожил" : "не помогло")} за {seconds:0.0} с");
        }

        private static string WakeMode(Project p)
        {
            var o = Json.ReadFile(Path.Combine(p.Path, "UserSettings", "Exerussus.Nexus", "nexus-cli.json"));
            var mode = Json.Str(o, "Wake") ?? Json.Str(o, "wake");
            return mode == "off" || mode == "soft" || mode == "focus" ? mode : "soft";
        }

        private static void WriteWake(Project p, string step, bool ok, double seconds, string phase)
        {
            if (!Directory.Exists(p.CliDir)) return;
            var path = Path.Combine(p.CliDir, "wake.json");
            var list = new JsonArray();
            try
            {
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonArray old)
                    foreach (var e in old.Skip(Math.Max(0, old.Count - 19)))
                        list.Add(e?.DeepClone());
            }
            catch
            {
                // битый журнал — начать заново
            }

            list.Add(new JsonObject
            {
                ["at"] = DateTime.UtcNow.ToString("O"),
                ["step"] = step,
                ["ok"] = ok,
                ["seconds"] = Math.Round(seconds, 1),
                ["phase"] = phase,
            });
            Json.WriteAtomic(path, Json.Write(list));
        }

        private static readonly Regex ErrorLine = new Regex("(error|exception|failed|assert)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void WriteStall(Project p, Watch w, double age, bool? responding, bool foreground, bool dialog, string mode, List<WinApi.Window> windows)
        {
            if (!Directory.Exists(p.CliDir)) return;
            var lines = LogTail(p.EditorLog);
            var errors = new JsonArray();
            foreach (var l in lines.Where(l => ErrorLine.IsMatch(l)).TakeLast(15)) errors.Add(Cut(l, 300));
            var tail = new JsonArray();
            foreach (var l in lines.Where(l => l.Trim().Length > 0).TakeLast(25)) tail.Add(Cut(l, 300));
            var wins = new JsonArray();
            foreach (var win in windows)
                wins.Add(new JsonObject { ["class"] = win.Class, ["enabled"] = win.Enabled, ["title"] = Cut(win.Title, 200) });

            var o = new JsonObject
            {
                ["seconds"] = (int)age,
                ["at"] = DateTime.UtcNow.ToString("O"),
                ["state"] = p.EditorState,
                ["phase"] = p.Phase,
                ["responding"] = responding,
                ["foreground"] = WinApi.Supported ? foreground : null,
                ["dialog"] = dialog,
                ["wake"] = mode,
                ["waking"] = w.Wake?.Step,
                ["windows"] = wins,
                ["errors"] = errors,
                ["tail"] = tail,
                ["log"] = p.EditorLog,
                ["source"] = "nexusd",
            };
            Json.WriteAtomic(Path.Combine(p.CliDir, "stall.json"), Json.Write(o));
        }

        private static string Cut(string s, int n) => s != null && s.Length > n ? s.Substring(0, n) + "…" : s;

        /// <summary>Хвост Editor.log (128 КБ): файл открыт Unity на запись — читаем с общим доступом.</summary>
        private static List<string> LogTail(string path)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var take = (int)Math.Min(fs.Length, 131072);
                fs.Seek(-take, SeekOrigin.End);
                var buffer = new byte[take];
                var read = fs.Read(buffer, 0, take);
                result.AddRange(Json.Utf8.GetString(buffer, 0, read).Split('\n').Select(l => l.TrimEnd('\r')));
            }
            catch (Exception e)
            {
                Log.Warn("Editor.log: " + e.Message);
            }

            return result;
        }
    }
}
