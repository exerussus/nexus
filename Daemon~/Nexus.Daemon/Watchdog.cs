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
    /// <item>Будильник (настройка <c>Wake</c> проекта: off | soft | quiet | focus). Unity в фоне не продолжает работу
    /// после перезагрузки домена, пока процесс не станет активным приложением. Будим, только если окно отвечает,
    /// Unity в фоне, нет диалога Windows и человек ≥ 1,5 с не трогает мышь и клавиатуру. Шаги:
    /// <c>soft</c> — WM_ACTIVATEAPP без смены фокуса (проверено: не помогает, оставлен для сравнения);
    /// <c>window</c> — активировать окно-пробудитель Nexus (1×1 за экраном): процесс активен, главное окно на месте;
    /// <c>quiet</c> — активировать главное окно без разворачивания и сразу опустить под остальные;
    /// <c>focus</c> — прежний жёсткий: развернуть и на передний план.
    /// Режим quiet идёт window → quiet → focus, пока пульс не оживёт. Ожил — фокус сразу возвращается (после
    /// focus — через 1,5 с). Каждая попытка — в <c>wake.json</c>.</item>
    /// </list>
    /// Модальные окна не закрываются никогда — это решение человека.
    /// </summary>
    internal static class Watchdog
    {
        private const double PingInterval = 2.0;
        private const int PingTimeout = 3;
        private const double StallSeconds = 10;
        private const double WakeSeconds = 4;
        private const double IdleNeeded = 1.5;

        private sealed class WakeState
        {
            public string Mode;
            public string Step;
            public DateTime Started;
            public long PrevForeground;
            public long Main;
            public bool WasIconic;
            public string Phase;
            public Queue<string> Pending;
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
            public bool WaitLogged;
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
                    // после тихих шагов фокус отдаём сразу; после жёсткого — через 1,5 с, чтобы Unity успела дорисоваться
                    w.FreshSince ??= now;
                    var hold = w.Wake.Step == "focus" ? 1.5 : 0;
                    if ((now - w.FreshSince.Value).TotalSeconds >= hold) FinishWake(p, w, true, now);
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
                if (w.Wake.Step != "soft" && elapsed > 0.3 && WinApi.IdleSeconds() is double idle && idle < elapsed - 0.2)
                {
                    // человек взялся за мышь или клавиатуру посреди пробуждения — фокус ему обратно сразу,
                    // попытку не засчитываем, повторим на следующей паузе
                    WriteWake(p, w.Wake.Step, false, elapsed, w.Wake.Phase + " · прервано вводом человека");
                    FinishWake(p, w, false, now, false);
                    w.Attempts = Math.Max(0, w.Attempts - 1);
                    w.CooldownUntil = now.AddSeconds(3);
                }
                else if (elapsed >= StepSeconds(w.Wake.Step))
                {
                    if (w.Wake.Pending.Count == 0 || dialog || mode == "off")
                    {
                        FinishWake(p, w, false, now);
                        w.CooldownUntil = now.AddSeconds(15);
                    }
                    else if (UserIdle(p, w))
                    {
                        // шаг не помог — следующий, сильнее
                        WriteWake(p, w.Wake.Step, false, elapsed, w.Wake.Phase);
                        if (w.Wake.Step == "soft") WinApi.ActivateApp(windows, false);
                        if (!NextStep(p, w.Wake, windows, now))
                        {
                            FinishWake(p, w, false, now, false);
                            w.CooldownUntil = now.AddSeconds(15);
                        }
                    }
                }
            }
            else if (WinApi.Supported && mode != "off" && responding == true && !foreground && !dialog && now >= w.CooldownUntil && w.Attempts < 3 && UserIdle(p, w))
            {
                // окно отвечает (поток свободен), Unity в фоне, диалога нет, человек не печатает — редактор ждёт активации
                w.Attempts++;
                var main = WinApi.MainWindow(windows);
                var wake = new WakeState
                {
                    Mode = mode,
                    Phase = $"{p.EditorState} / {p.Phase}",
                    PrevForeground = WinApi.ForegroundHandle(),
                    Main = main,
                    WasIconic = WinApi.Iconic(main),
                    Pending = new Queue<string>(Chain(mode)),
                };
                Log.Info($"{p.Id}: будильник ({mode}), молчит {age:0} с");
                if (NextStep(p, wake, windows, now)) w.Wake = wake;
                else w.CooldownUntil = now.AddSeconds(15);
            }

            if (age < StallSeconds || (now - w.LastReport).TotalSeconds < 3) return;
            w.LastReport = now;
            WriteStall(p, w, age, responding, foreground, dialog, mode, windows);
        }

        /// <summary>Шаги режима по порядку: от незаметного к жёсткому.</summary>
        private static string[] Chain(string mode) => mode switch
        {
            "soft" => new[] { "soft" },
            "focus" => new[] { "focus" },
            _ => new[] { "window", "quiet", "focus" },
        };

        /// <summary>Сколько ждать оживления пульса на шаге, прежде чем переходить к следующему.</summary>
        private static double StepSeconds(string step) => step switch
        {
            "soft" => 4,
            "window" => 3,
            "quiet" => 3,
            _ => 10,
        };

        /// <summary>
        /// Человек не трогает мышь и клавиатуру ≥ 1,5 с: иначе его нажатия ушли бы в Unity. Ждём паузы,
        /// попытку не засчитываем; в журнал — один раз на ожидание.
        /// </summary>
        private static bool UserIdle(Project p, Watch w)
        {
            var idle = WinApi.IdleSeconds();
            if (idle == null || idle >= IdleNeeded)
            {
                w.WaitLogged = false;
                return true;
            }

            if (!w.WaitLogged) Log.Info($"{p.Id}: будильник ждёт паузы во вводе");
            w.WaitLogged = true;
            return false;
        }

        /// <summary>Запустить следующий выполнимый шаг; невыполнимые (нет окна-пробудителя, Windows отказал) — в журнал и дальше.</summary>
        private static bool NextStep(Project p, WakeState wake, List<WinApi.Window> windows, DateTime now)
        {
            while (wake.Pending.Count > 0)
            {
                var step = wake.Pending.Dequeue();
                var why = TryStep(step, wake, windows);
                if (why == null)
                {
                    wake.Step = step;
                    wake.Started = now;
                    Log.Info($"{p.Id}: будильник — шаг {step}");
                    return true;
                }

                WriteWake(p, step, false, 0, wake.Phase + " · " + why);
                Log.Info($"{p.Id}: будильник — шаг {step} пропущен: {why}");
            }

            return false;
        }

        /// <summary>Выполнить шаг. null — выполнен, иначе причина, почему нельзя.</summary>
        private static string TryStep(string step, WakeState wake, List<WinApi.Window> windows)
        {
            switch (step)
            {
                case "soft":
                    WinApi.ActivateApp(windows, true);
                    return null;
                case "window":
                {
                    // процесс Unity становится активным приложением, главное окно не двигается;
                    // случайные нажатия в это время уходят в пустое окно, а не в сцену
                    var h = WinApi.WakeWindow(windows);
                    if (h == 0) return "нет окна-пробудителя (пакет старше 2.0.0-pre.5 или CLI выключен)";
                    return WinApi.Focus(h, false, false) ? null : "Windows не дал фокус";
                }
                case "quiet":
                {
                    // без разворачивания: свёрнутое остаётся свёрнутым, развёрнутое тут же уходит под остальные окна
                    if (!WinApi.Focus(wake.Main, false, false)) return "Windows не дал фокус";
                    if (!wake.WasIconic) WinApi.SendToBottom(wake.Main);
                    return null;
                }
                default:
                    return WinApi.Focus(wake.Main, true) ? null : "Windows не дал фокус";
            }
        }

        private static void FinishWake(Project p, Watch w, bool ok, DateTime now, bool log = true)
        {
            var wake = w.Wake;
            w.Wake = null;
            w.FreshSince = null;
            if (wake == null) return;
            var seconds = (now - wake.Started).TotalSeconds;
            if (wake.Step == "soft")
            {
                if (!WinApi.Foreground(p.Pid)) WinApi.ActivateApp(WinApi.Windows(p.Pid), false);
            }
            else if (WinApi.Foreground(p.Pid))
            {
                // фокус возвращаем, только если он всё ещё у Unity: человек мог переключиться сам
                if (wake.Step == "focus" && wake.WasIconic) WinApi.Minimize(wake.Main);
                WinApi.Focus(wake.PrevForeground, false);
            }

            if (log) WriteWake(p, wake.Step, ok, seconds, wake.Phase);
            Log.Info($"{p.Id}: будильник {wake.Mode}/{wake.Step} — {(ok ? "ожил" : "не помогло")} за {seconds:0.0} с");
        }

        private static string WakeMode(Project p)
        {
            var o = Json.ReadFile(Path.Combine(p.Path, "UserSettings", "Exerussus.Nexus", "nexus-cli.json"));
            var mode = Json.Str(o, "Wake") ?? Json.Str(o, "wake");
            return mode == "off" || mode == "soft" || mode == "quiet" || mode == "focus" ? mode : "quiet";
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
