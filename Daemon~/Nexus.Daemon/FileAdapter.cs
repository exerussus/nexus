using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Файловый адаптер Nexus API — канал <c>NexusTemp/cli</c> для клиентов без сети до localhost (агент в VM, WSL).
    /// Канал соответствует стандарту efc/1 (репозиторий efc, SPEC.md): паспорт <c>channel.json</c>, пульс,
    /// <c>requests → active → done</c>, отмена <c>cancel/</c>, корзина <c>trash/</c> (клиенту в ВМ удалять нельзя),
    /// пакеты с шагами и блокировкой — их ведёт сам демон. Формат 1.0 (<c>nexus-fs</c>) по-прежнему понимается:
    /// запрос без поля <c>v</c> — старый клиент. Подканал задач <c>cli/tasks/…</c> — как раньше.
    /// </summary>
    /// <remarks>Папки канала создаются только для зарегистрированных проектов (CLI включён); выключенный CLI
    /// удаляет их сам (Editor Bridge).</remarks>
    internal static class FileAdapter
    {
        private const double BeatInterval = 2.0;
        private const double SweepInterval = 10.0;
        private const double OrphanAge = 3600;
        private const long MaxRequestBytes = 1 << 20;
        private const int MaxOutputChars = 512 * 1024;
        private const int MaxItemOutputChars = 4000;
        private const double DefaultKeep = 300;
        private const double MinKeep = 60;
        private const double ParseGrace = 2.0;
        private const double CancelOrphanAge = 60;
        private const double TrashMaxAge = 86400;
        private const double CleanupStepSeconds = 60;
        private const double HelloRetry = 60;

        // id — только строчные (NTFS не различает регистр) и не имена устройств Windows (SPEC 3.5)
        private static readonly Regex SafeIdRe = new Regex("^[a-z0-9_-]{1,64}$", RegexOptions.Compiled);
        private static readonly Regex DeviceRe = new Regex("^(con|prn|aux|nul|com[0-9]|lpt[0-9])$", RegexOptions.Compiled);
        private static readonly SafeIdCheck SafeId = new SafeIdCheck();

        private sealed class SafeIdCheck
        {
            public bool IsMatch(string id) => SafeIdRe.IsMatch(id) && !DeviceRe.IsMatch(id);
        }
        private static readonly Regex TaskLockRe = new Regex(@"Идёт задача .* task\.cancel (\S+)", RegexOptions.Compiled);

        /// <summary>Пути проектов, чьи хвосты прошлого демона уже разобраны (раз за жизнь демона).</summary>
        private static readonly ConcurrentDictionary<string, bool> Recovered = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private sealed class Delivery
        {
            public string Path;
            public string Content;
            public string[] Remove;
        }

        /// <summary>Пакет efc: шаги ведёт демон, каждый шаг — обычный вызов.</summary>
        private sealed class BatchRun
        {
            public string Id;
            public volatile bool CancelRequested;
            /// <summary>Пакет остановлен (ошибка, отмена, срок): дальше только уборка, её отмена не прерывает.</summary>
            public volatile bool Stopped;
            /// <summary>Итог записан: active/ больше не писать.</summary>
            public bool Finished;
            public volatile Call Current;
            public string Phase = "в очереди";
            public double Progress;
            public string Accepted;
            public double Keep;
            public int Steps;
        }

        private sealed class Channel
        {
            public readonly ConcurrentQueue<Delivery> Retry = new ConcurrentQueue<Delivery>();
            public Project Project;
            public bool Prepared;
            public long Seq;
            public DateTime NextBeat;
            public DateTime NextSweep;
            public DateTime NextTrash;
            public readonly ConcurrentDictionary<string, Call> Calls = new ConcurrentDictionary<string, Call>();
            public readonly ConcurrentDictionary<string, BatchRun> Batches = new ConcurrentDictionary<string, BatchRun>();
            /// <summary>Выполненные id (итог в done/ или забран клиентом): повтор не выполняется.</summary>
            public readonly ConcurrentDictionary<string, DateTime> Seen = new ConcurrentDictionary<string, DateTime>();
            /// <summary>Запросы, которые ждут дописывания (неатомарный писатель), — в журнал один раз.</summary>
            public readonly ConcurrentDictionary<string, bool> Waiting = new ConcurrentDictionary<string, bool>();
            public string Instance;
            public volatile string Foreign;
            public JsonArray Clients = new JsonArray();
            public string Started;
            public volatile string HelloToken;
            public volatile bool HelloRunning;
            public DateTime HelloRetryAt;
        }

        private static readonly ConcurrentDictionary<Project, Channel> Channels = new ConcurrentDictionary<Project, Channel>();

        public static void Tick()
        {
            var now = DateTime.UtcNow;
            foreach (var p in Registry.All)
            {
                if (!p.Exists) continue;
                var ch = Channels.GetOrAdd(p, x => new Channel { Project = x });
                try
                {
                    if (!ch.Prepared) Prepare(ch);
                    if (now >= ch.NextBeat)
                    {
                        ch.NextBeat = now.AddSeconds(BeatInterval);
                        Beat(ch);
                    }

                    Redeliver(ch);
                    FetchCommands(ch);
                    if (ch.Foreign == null)
                    {
                        PollCancels(ch);
                        PollRequests(ch);
                        PollTasks(ch);
                    }

                    if (now >= ch.NextSweep)
                    {
                        ch.NextSweep = now.AddSeconds(SweepInterval);
                        CheckPassport(ch);
                        Sweep(ch);
                    }

                    if (now >= ch.NextTrash)
                    {
                        ch.NextTrash = now.AddHours(24);
                        var (files, _) = DaemonCommands.CleanTrash(Dir(ch, "trash"), false, TrashMaxAge);
                        if (files > 0) Log.Info($"канал {p.Id}: корзина — удалено {files} файлов старше суток");
                    }
                }
                catch (Exception e)
                {
                    Log.Error("файловый адаптер " + p.Id, e);
                }
            }

            foreach (var p in Channels.Keys)
                if (p.Removed)
                    Channels.TryRemove(p, out _);
        }

        private static string Dir(Channel ch, params string[] parts) => Path.Combine(new[] { ch.Project.CliDir }.Concat(parts).ToArray());

        private static readonly string[] Folders =
        {
            "requests", "active", "done", "cancel", "trash",
            Path.Combine("tasks", "requests"), Path.Combine("tasks", "active"), Path.Combine("tasks", "done"), Path.Combine("tasks", "defs"),
        };

        // ============================================================ подготовка, паспорт

        private static void Prepare(Channel ch)
        {
            var p = ch.Project;
            var root = p.TempDir;
            Directory.CreateDirectory(root);
            var ignore = Path.Combine(root, ".gitignore");
            if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n");
            foreach (var d in Folders) Directory.CreateDirectory(Dir(ch, d));
            ch.Instance = $"nexusd@{Environment.MachineName}:{Project.Normalize(p.Path)}";
            ch.Started = DateTime.UtcNow.ToString("O");

            // чужой паспорт (папку скопировали, синхронизировали с другой машины) — канал не обслуживается
            var old = Json.ReadFile(Dir(ch, "channel.json"));
            var oldInstance = Json.Str(old, "instance");
            if (oldInstance != null && !string.Equals(oldInstance, ch.Instance, StringComparison.OrdinalIgnoreCase))
            {
                ch.Foreign = oldInstance;
                Log.Warn($"канал {p.Id}: channel.json принадлежит {oldInstance} — канал не обслуживается (удалите NexusTemp/cli/channel.json, если это копия проекта)");
                ch.Prepared = true;
                Beat(ch);
                return;
            }

            InstallClients(ch);
            // сторож 1.0 (nexus-watchdog.ps1) уступает тому, чей pid в watchdog.json: теперь сторожит демон
            Json.WriteAtomic(Dir(ch, "watchdog.json"), Json.Write(new JsonObject
            {
                ["pid"] = Environment.ProcessId, ["unityPid"] = p.Pid, ["source"] = "nexusd", ["version"] = DaemonInfo.Version,
                ["started"] = DateTime.UtcNow.ToString("O"),
            }));
            if (Recovered.TryAdd(Project.Normalize(p.Path), true)) RecoverTails(ch);

            // выполненные id: итоги в done/ и забранные клиентом (trash/done-<id>.json)
            var until = DateTime.UtcNow.AddSeconds(OrphanAge);
            foreach (var f in Files(Dir(ch, "done"))) ch.Seen[Path.GetFileNameWithoutExtension(f)] = until;
            foreach (var f in Files(Dir(ch, "trash")))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.StartsWith("done-", StringComparison.Ordinal)) ch.Seen[name.Substring(5)] = until;
            }

            WritePassport(ch);
            ch.NextTrash = DateTime.UtcNow; // корзина — сразу при старте, потом раз в сутки
            ch.Prepared = true;
            Beat(ch);
        }

        /// <summary>Клиенты — в NexusTemp/, чтобы агент нашёл их в папке проекта: nexus-fs (python) и efc (Linux).</summary>
        private static void InstallClients(Channel ch)
        {
            ch.Clients = new JsonArray();
            foreach (var name in new[] { "nexus-fs", "efc" })
            {
                try
                {
                    var from = Path.Combine(DaemonPaths.ClientDir, name);
                    if (!File.Exists(from)) continue;
                    var to = Path.Combine(ch.Project.TempDir, name);
                    var bytes = File.ReadAllBytes(from);
                    if (!File.Exists(to) || !File.ReadAllBytes(to).AsSpan().SequenceEqual(bytes))
                    {
                        var tmp = to + ".tmp";
                        File.WriteAllBytes(tmp, bytes);
                        File.Move(tmp, to, true);
                    }

                    // не Windows (демон на Linux/macOS): клиент должен запускаться
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(to, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                                 UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

                    if (name == "efc")
                        ch.Clients.Add(new JsonObject { ["file"] = "../efc", ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
                }
                catch (Exception e)
                {
                    Log.Warn($"копия {name}: {e.Message}");
                }
            }
        }

        private static JsonObject Passport(Channel ch)
        {
            var p = ch.Project;
            var commands = new JsonArray();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in DaemonCommands.All)
            {
                names.Add(c.Name);
                commands.Add(new JsonObject { ["name"] = c.Name, ["summary"] = c.Summary, ["usage"] = c.Usage, ["readOnly"] = c.Name != "trash.clean" });
            }

            var editor = p.Commands;
            if (editor != null)
                foreach (var c in editor)
                {
                    var name = Json.Str(c, "name");
                    if (name == null || !names.Add(name)) continue;
                    var o = new JsonObject { ["name"] = name, ["summary"] = Json.Str(c, "summary") ?? "", ["usage"] = Json.Str(c, "usage") ?? name };
                    if (Json.Bool(c, "readOnly")) o["readOnly"] = true;
                    commands.Add(o);
                }

            return new JsonObject
            {
                ["protocol"] = Efc.Protocol,
                ["instance"] = ch.Instance,
                ["server"] = "nexusd",
                ["version"] = DaemonInfo.Version,
                ["pid"] = Environment.ProcessId,
                ["started"] = ch.Started,
                ["title"] = "Nexus — " + p.Id,
                ["howToStart"] = $"Откройте проект {p.Id} в Unity с включённым CLI (Nexus → Manage → CLI) — редактор сам запустит Nexus Daemon.",
                ["roots"] = new JsonObject { ["channel"] = ".", ["project"] = "../..", ["temp"] = ".." },
                ["beat"] = BeatInterval,
                ["limits"] = new JsonObject
                {
                    ["requestBytes"] = MaxRequestBytes, ["outputChars"] = MaxOutputChars, ["defaultTimeout"] = Calls.DefaultTimeout,
                    ["defaultKeep"] = DefaultKeep, ["minKeep"] = MinKeep, ["maxKeep"] = Calls.MaxKeep, ["trashMaxAge"] = TrashMaxAge,
                    ["artifactKeep"] = 86400, ["queue"] = 100,
                },
                ["features"] = new JsonArray("cancel", "batch", "steps", "lock", "state"),
                ["commands"] = commands,
                ["reasons"] = new JsonArray(Efc.Expired, Efc.Cancelled, Efc.ServerRestarted, Efc.Busy, Efc.BadRequest, Efc.UnknownCommand, Efc.TooLarge,
                    Efc.Timeout, Efc.BadReference, Efc.ExpectFailed, Efc.StepTimeout, "crashed", "no-editor", "failed"),
                ["subchannels"] = new JsonObject { ["tasks"] = "tasks/" },
                ["state"] = new JsonObject { ["stall"] = "stall.json", ["domain"] = "domain.json", ["wake"] = "wake.json", ["startupError"] = "startup-error.txt" },
                ["clients"] = ch.Clients.DeepClone(),
            };
        }

        private static void WritePassport(Channel ch)
        {
            Json.WriteAtomic(Dir(ch, "channel.json"), Json.Write(Passport(ch)));
            Json.WriteAtomic(Dir(ch, "README.txt"),
                $"Файловый канал efc/1 проекта {ch.Project.Id} — его ведёт Nexus Daemon.\n" +
                "Клиент кладёт запросы в requests/ и просьбы отменить в cancel/, прочитанное переносит в trash/.\n" +
                "Остальное пишет демон — не правьте.\n\n" +
                "Клиент: ../efc --channel <эта папка> call <команда> [аргументы]   (или python3 ../nexus-fs <команда>)\n" +
                "Команды: ../efc --channel <эта папка> help\n");
        }

        /// <summary>Паспорт на месте и наш? Пропал — записать заново; подменён чужим — перестать обслуживать.</summary>
        private static void CheckPassport(Channel ch)
        {
            if (ch.Instance == null) return;
            var path = Dir(ch, "channel.json");
            var o = Json.ReadFile(path);
            var instance = Json.Str(o, "instance");
            if (o == null)
            {
                if (File.Exists(path)) return; // занят или дописывается — в следующий раз
                if (ch.Foreign != null)
                {
                    // чужой паспорт убрали (копия проекта) — подготовить канал заново, он теперь наш
                    Log.Info($"канал {ch.Project.Id}: чужой паспорт удалён — канал снова обслуживается");
                    ch.Foreign = null;
                    ch.Prepared = false;
                    return;
                }

                WritePassport(ch);
                return;
            }

            if (!string.Equals(instance, ch.Instance, StringComparison.OrdinalIgnoreCase))
            {
                if (ch.Foreign == null) Log.Warn($"канал {ch.Project.Id}: channel.json подменён ({instance}) — канал больше не обслуживается");
                ch.Foreign = instance;
            }
            else if (ch.Foreign != null)
            {
                ch.Foreign = null;
                Log.Info($"канал {ch.Project.Id}: паспорт снова наш — канал обслуживается");
            }
        }

        /// <summary>Команды редактора (bridge.hello) — для паспорта и блокировки; заново после каждой перезагрузки домена.</summary>
        private static void FetchCommands(Channel ch)
        {
            var p = ch.Project;
            if (ch.HelloRunning || !p.Ready || p.Token == ch.HelloToken || DateTime.UtcNow < ch.HelloRetryAt) return;
            ch.HelloRunning = true;
            var token = p.Token;
            var call = Calls.Start(p, "bridge.hello", null, p.Path, 30, 0, false, "file-hello");
            _ = call.Done.Task.ContinueWith(_ =>
            {
                try
                {
                    var data = call.ExitCode == 0 ? Json.TryParseObject(call.Result) : null;
                    if (data?["commands"] is JsonArray list)
                    {
                        p.Commands = (JsonArray)list.DeepClone();
                        ch.HelloToken = token;
                        if (ch.Foreign == null && ch.Instance != null) WritePassport(ch);
                    }
                    else ch.HelloRetryAt = DateTime.UtcNow.AddSeconds(HelloRetry);
                }
                finally
                {
                    ch.HelloRunning = false;
                }
            });
        }

        /// <summary>Вызовы прошлого демона в active/ — их итог потерян вместе с ним.</summary>
        private static void RecoverTails(Channel ch)
        {
            foreach (var path in Files(Dir(ch, "active")))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (File.Exists(Dir(ch, "done", id + ".json")))
                {
                    // итог записан, active/ просто не успели убрать — итог не трогать
                    Json.TryDelete(path);
                    continue;
                }

                var o = Json.ReadFile(path);
                WriteDone(ch, id, new Outcome
                {
                    Exit = 4, Output = "✗ Демон перезапущен во время вызова — результат неизвестен, проверьте status.\n", Reason = Efc.ServerRestarted,
                    Keep = Json.Num(o, "keep", DefaultKeep), Accepted = Json.Str(o, "accepted"),
                });
            }

            foreach (var path in Files(Dir(ch, "tasks", "active")))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                var o = Json.ReadFile(path);
                WriteTaskDone(ch, id, 4, "✗ Демон перезапущен во время задачи — результат неизвестен.", null, Json.Num(o, "keep"));
            }
        }

        // ============================================================ пульс

        private static void Beat(Channel ch)
        {
            if (!Directory.Exists(ch.Project.CliDir)) return;
            var p = ch.Project;
            ch.Seq++;
            var editor = p.State;
            // состояние сервера по efc: ready — принимает и выполняет; busy — принимает, выполнит позже (перезагрузка
            // домена); degraded — принимает, редактор недоступен (завис, закрыт) или канал чужой
            var state = ch.Foreign != null ? "degraded" : editor == "ready" ? "ready" : editor == "reloading" ? "busy" : "degraded";
            var detail = new JsonObject
            {
                ["editor"] = editor,
                ["phase"] = p.Phase,
                ["silent"] = Math.Round(p.SilentSeconds, 1),
                ["project"] = p.Id,
            };
            if (ch.Foreign != null) detail["foreignInstance"] = ch.Foreign;
            if (p.LockHolder != null) detail["lockedBy"] = p.LockHolder;
            var o = new JsonObject
            {
                ["v"] = 1,
                ["seq"] = ch.Seq,
                ["at"] = DateTime.UtcNow.ToString("O"),
                ["beat"] = BeatInterval,
                ["server"] = "nexusd",
                ["version"] = DaemonInfo.Version,
                ["pid"] = Environment.ProcessId,
                ["state"] = state,
                ["detail"] = detail,
                // поля формата 1.0 — для nexus-fs 5 и старше
                ["phase"] = p.Phase,
                ["project"] = p.Id,
                ["path"] = p.Path,
                ["daemon"] = true,
                ["daemonPid"] = Environment.ProcessId,
                ["daemonVersion"] = DaemonInfo.Version,
                ["silent"] = Math.Round(p.SilentSeconds, 1),
            };
            Json.WriteAtomic(Path.Combine(p.CliDir, "alive.json"), Json.Write(o));
        }

        // ============================================================ запросы

        private static bool Known(Channel ch, string id) =>
            ch.Calls.ContainsKey(id) || ch.Batches.ContainsKey(id) || ch.Seen.ContainsKey(id) || File.Exists(Dir(ch, "done", id + ".json")) ||
            File.Exists(Dir(ch, "trash", "done-" + id + ".json"));

        private static void PollRequests(Channel ch)
        {
            foreach (var path in Sorted(Dir(ch, "requests")))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!SafeId.IsMatch(id)) continue; // чужое имя — не трогаем
                if (Known(ch, id))
                {
                    Json.TryDelete(path); // повтор id: уже выполнен или выполняется
                    continue;
                }

                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    continue;
                }

                var age = Age(path, DateTime.UtcNow);
                JsonObject o = null;
                if (size <= MaxRequestBytes)
                {
                    o = Json.ReadFile(path, MaxRequestBytes);
                    // неатомарный писатель: дать дописать
                    if (o == null && age < ParseGrace) continue;
                }

                // забрать: rename в active/ (тот же rename, что у клиента при отзыве, — выигрывает один)
                var active = Dir(ch, "active", id + ".json");
                if (!TryMove(path, active)) continue;
                ch.Waiting.TryRemove(id, out _);

                var efc = o?["v"] != null;
                // старый клиент без keep — как в формате 1.0: итог живёт час (nexus-fs --no-wait, потом result <id>)
                var keep = Math.Clamp(o != null && o["keep"] != null ? Json.Num(o, "keep") : efc ? DefaultKeep : OrphanAge, MinKeep, Calls.MaxKeep);
                var accepted = DateTime.UtcNow.ToString("O");
                Outcome Fail(int exit, string text, string reason) => new Outcome { Exit = exit, Output = "✗ " + text + "\n", Reason = reason, Keep = keep, Accepted = accepted };

                if (o == null)
                {
                    WriteDone(ch, id, size > MaxRequestBytes ? Fail(2, $"Запрос больше {MaxRequestBytes} байт.", Efc.TooLarge) : Fail(2, "Неверный запрос: не JSON.", Efc.BadRequest));
                    continue;
                }

                var declaredId = Json.Str(o, "id");
                if (declaredId != null && declaredId != id)
                {
                    WriteDone(ch, id, Fail(2, $"id в запросе («{declaredId}») не совпадает с именем файла.", Efc.BadRequest));
                    continue;
                }

                // брошенный запрос (клиент ушёл, демона не было) не выполняется задним числом
                var timeout = (int)Json.Num(o, "timeout");
                if (age > (timeout > 0 ? timeout : Calls.DefaultTimeout) + 60)
                {
                    WriteDone(ch, id, Fail(5, "Запрос просрочен: пролежал дольше своего срока, не выполнялся.", Efc.Expired));
                    continue;
                }

                if (o["batch"] is JsonArray batch)
                {
                    StartBatch(ch, id, o, batch, keep, accepted, timeout > 0 ? timeout : Calls.DefaultTimeout);
                    continue;
                }

                var command = Json.Str(o, "command");
                if (string.IsNullOrWhiteSpace(command))
                {
                    WriteDone(ch, id, Fail(2, "Неверный запрос: нужно command или batch.", Efc.BadRequest));
                    continue;
                }

                var p = ch.Project;
                var call = Calls.Create(p, command, Json.StrArray(o, "args"), Json.Str(o, "cwd") ?? p.Path,
                    timeout, keep, Json.Bool(o, "force"), efc ? "file-efc" : "file", id, c => OnCallPhase(ch, c));
                ch.Calls[id] = call;
                // блокировка одиночного вызова: держит проект, пока идёт
                if (efc && Json.Bool(o, "lock") && p.LockHolder == null) p.LockHolder = id;
                WriteActive(ch, call, accepted);
                Calls.Begin(call);
            }
        }

        private static void OnCallPhase(Channel ch, Call c)
        {
            if (c.State == "done")
            {
                // под тем же замком, что WriteActive: active/ не перепишется после done/; id в Seen — до снятия из Calls
                lock (c) WriteDone(ch, c.Id, Outcome.From(c, c.Source == "file-efc"));
                ch.Calls.TryRemove(c.Id, out _);
                if (c.Project.LockHolder == c.Id) c.Project.LockHolder = null;
                return;
            }

            WriteActive(ch, c, null);
        }

        private static void WriteActive(Channel ch, Call c, string accepted)
        {
            if (c.State == "done") return;
            var o = new JsonObject
            {
                ["v"] = 1,
                ["id"] = c.Id,
                ["state"] = "running",
                ["command"] = c.Command,
                ["args"] = new JsonArray(c.Args.Select(a => (JsonNode)a).ToArray()),
                ["job"] = c.Job ?? c.Id, // принят демоном: --no-wait может уходить
                ["keep"] = c.Keep,
                ["phase"] = c.CancelRequested ? "отменяется: " + c.Phase : c.Phase,
                ["accepted"] = accepted ?? c.Created.ToString("O"),
                ["updated"] = DateTime.UtcNow.ToString("O"),
            };
            lock (c) if (c.State != "done") Json.WriteAtomic(Dir(ch, "active", c.Id + ".json"), Json.Write(o));
        }

        // ============================================================ итог

        /// <summary>Итог вызова в формате efc (плюс поля формата 1.0: <c>result</c>, <c>project</c>, <c>job</c>).</summary>
        private sealed class Outcome
        {
            public int Exit;
            public string Output = string.Empty;
            public string Error;
            public string Reason;
            public JsonNode Data;
            public string Result;
            public string Job;
            public double? Seconds;
            public double Keep = DefaultKeep;
            public string Accepted;
            public bool CancelIgnored;
            public JsonArray Items;

            public static Outcome From(Call c, bool efc)
            {
                var o = new Outcome
                {
                    Exit = c.ExitCode, Output = c.Output ?? string.Empty, Result = c.Result, Job = c.Job, Seconds = Math.Round(c.Seconds, 2),
                    Keep = c.Keep, Accepted = c.Created.ToString("O"), CancelIgnored = c.CancelRequested && c.ExitCode == 0,
                };
                o.Data = ParseData(c.Result);
                // отказ из-за блокировки — для клиента efc это «занято» (6); старый клиент получает код 1, как от задачи Unity
                var busyWith = c.BusyWith;
                if (busyWith == null && c.ExitCode == 1)
                {
                    var m = TaskLockRe.Match(o.Output);
                    if (m.Success) busyWith = m.Groups[1].Value;
                }

                if (busyWith != null && efc)
                {
                    o.Exit = 6;
                    o.Reason = Efc.Busy;
                    o.Data = new JsonObject { ["busyWith"] = busyWith };
                }

                if (c.ExitCode == 7) o.Reason = Efc.Cancelled;
                return o;
            }
        }

        private static JsonNode ParseData(string result)
        {
            if (string.IsNullOrWhiteSpace(result)) return null;
            var t = result.TrimStart();
            if (t.StartsWith("{") || t.StartsWith("["))
                try
                {
                    return JsonNode.Parse(t);
                }
                catch
                {
                    return null;
                }

            return null;
        }

        private static void WriteDone(Channel ch, string id, Outcome r)
        {
            var output = r.Output ?? string.Empty;
            var truncated = false;
            if (output.Length > MaxOutputChars)
            {
                output = output.Substring(0, MaxOutputChars) + "\n… (вывод обрезан)\n";
                truncated = true;
            }

            var keep = Math.Clamp(r.Keep, MinKeep, Calls.MaxKeep);
            var o = new JsonObject
            {
                ["v"] = 1,
                ["id"] = id,
                ["state"] = "done",
                ["ok"] = r.Exit == 0,
                ["exitCode"] = r.Exit,
            };
            var error = r.Error ?? Efc.ErrorText(output, r.Exit);
            if (error != null) o["error"] = error;
            var reason = r.Reason ?? Efc.ReasonFor(r.Exit);
            if (reason != null) o["reason"] = reason;
            o["output"] = output;
            if (r.Data != null) o["data"] = r.Data.DeepClone();
            if (r.Items != null) o["items"] = r.Items;
            if (truncated) o["truncated"] = true;
            if (r.CancelIgnored) o["cancelIgnored"] = true;
            o["project"] = ch.Project.Id;
            if (r.Result != null) o["result"] = r.Result;
            if (r.Job != null) o["job"] = r.Job;
            if (r.Seconds != null) o["seconds"] = r.Seconds;
            if (r.Accepted != null) o["accepted"] = r.Accepted;
            o["finished"] = DateTime.UtcNow.ToString("O");
            o["expires"] = DateTime.UtcNow.AddSeconds(keep).ToString("O");
            ch.Seen[id] = DateTime.UtcNow.AddSeconds(Math.Max(keep, OrphanAge));
            Deliver(ch, Dir(ch, "done", id + ".json"), Json.Write(o), Dir(ch, "active", id + ".json"), Dir(ch, "active", id + ".log"));
        }

        // ============================================================ пакеты (efc, шаги ведёт демон)

        private static void StartBatch(Channel ch, string id, JsonObject req, JsonArray ops, double keep, string accepted, int timeout)
        {
            var p = ch.Project;
            Outcome Fail(int exit, string text, string reason, JsonNode data = null) =>
                new Outcome { Exit = exit, Output = "✗ " + text + "\n", Reason = reason, Keep = keep, Accepted = accepted, Data = data };

            if (ops.Count == 0 || ops.Any(x => !(x is JsonObject) || string.IsNullOrWhiteSpace(Json.Str(x, "command"))))
            {
                WriteDone(ch, id, Fail(2, "Пакет: нужен непустой batch из объектов с command.", Efc.BadRequest));
                return;
            }

            var ids = Efc.StepIds(ops, out var problem);
            if (ids == null)
            {
                WriteDone(ch, id, Fail(2, "Пакет: " + problem, Efc.BadRequest));
                return;
            }

            var force = Json.Bool(req, "force");
            var holder = p.LockHolder;
            var locks = Json.Bool(req, "lock");
            if (locks && holder != null && !force)
            {
                WriteDone(ch, id, Fail(6, $"Идёт вызов {holder} с блокировкой — дождитесь его, отмените или повторите с force.", Efc.Busy, new JsonObject { ["busyWith"] = holder }));
                return;
            }

            if (locks && holder == null) p.LockHolder = id;
            var run = new BatchRun { Id = id, Accepted = accepted, Keep = keep, Steps = ops.Count };
            ch.Batches[id] = run;
            WriteBatchActive(ch, run);
            var stopOnError = Json.Str(req, "onError") != "continue";
            var cwd = Json.Str(req, "cwd") ?? p.Path;
            _ = Task.Run(async () =>
            {
                Outcome result;
                try
                {
                    result = await RunBatch(ch, run, ops, ids, stopOnError, force, locks ? id : null, cwd, timeout);
                }
                catch (Exception e)
                {
                    Log.Error("пакет " + id, e);
                    result = Fail(4, $"Демон: {e.GetType().Name}: {e.Message}", "crashed");
                }
                finally
                {
                    if (p.LockHolder == id) p.LockHolder = null;
                }

                result.Keep = keep;
                result.Accepted = accepted;
                lock (run)
                {
                    run.Finished = true;
                    WriteDone(ch, id, result);
                }

                ch.Batches.TryRemove(id, out _);
            });
        }

        private static async Task<Outcome> RunBatch(Channel ch, BatchRun run, JsonArray ops, List<string> ids, bool stopOnError, bool force, string lockOwner, string cwd, int timeout)
        {
            var p = ch.Project;
            var started = DateTime.UtcNow;
            var deadline = started.AddSeconds(timeout);
            var results = new Dictionary<string, Efc.StepResult>(StringComparer.Ordinal);
            var items = new JsonArray();
            var output = new StringBuilder();
            (int Exit, string Error, string Reason)? firstFail = null;
            string stopped = null; // "failed" | "cancelled"

            for (var i = 0; i < ops.Count; i++)
            {
                var op = (JsonObject)ops[i];
                var stepId = ids[i];
                var command = Json.Str(op, "command");
                var always = Json.Bool(op, "always");

                if (stopped == null && (run.CancelRequested || DateTime.UtcNow > deadline))
                {
                    stopped = "cancelled";
                    run.Stopped = true;
                    firstFail ??= run.CancelRequested ? (7, "Отменено", Efc.Cancelled) : (5, $"Пакет не уложился в срок ({timeout} с)", Efc.Timeout);
                }

                if (stopped != null && !always)
                {
                    var status = stopped == "cancelled" ? "cancelled" : "skipped";
                    items.Add(new JsonObject { ["id"] = stepId, ["command"] = command, ["status"] = status, ["ok"] = false, ["exitCode"] = status == "cancelled" ? 7 : 0 });
                    continue;
                }

                run.Phase = $"{stepId}: {command} ({i + 1}/{ops.Count})";
                run.Progress = (double)i / ops.Count;
                WriteBatchActive(ch, run);

                // подстановки — в аргументы и проверки
                string[] args;
                JsonObject expect;
                var stepStart = DateTime.UtcNow;
                int exit;
                string stepOut, reason = null, error = null;
                JsonNode data = null;
                List<string> misses;
                try
                {
                    args = Json.StrArray(op, "args").Select(a => Efc.SubstText(a, results)).ToArray();
                    if (op["params"] != null) Efc.SubstJson(op["params"], results); // проверка ссылок; команды Nexus params не принимают
                    expect = op["expect"] != null ? Efc.SubstJson(op["expect"], results) as JsonObject : null;
                }
                catch (Efc.StepError e)
                {
                    exit = 2;
                    stepOut = "✗ " + e.Message + "\n";
                    reason = Efc.BadReference;
                    error = e.Message;
                    output.Append($"▸ {stepId} {command} — ошибка\n").Append(stepOut);
                    items.Add(Item(stepId, command, "failed", exit, error, reason, null, stepOut, null, 0));
                    results[stepId] = new Efc.StepResult { Exit = exit, Output = stepOut };
                    firstFail ??= (exit, $"{stepId} ({command}): {error}", reason);
                    if (StopsHere(op, stopOnError) && stopped == null)
                    {
                        stopped = run.CancelRequested ? "cancelled" : "failed";
                        run.Stopped = true;
                    }

                    continue;
                }

                // предел шага: свой timeout; для уборки после остановки — 60 с; иначе — остаток срока пакета
                var stepLimit = Json.Num(op, "timeout") > 0 ? Json.Num(op, "timeout") : stopped != null ? CleanupStepSeconds : Math.Max(1, (deadline - DateTime.UtcNow).TotalSeconds);
                var call = Calls.Create(p, command, args, cwd, (int)Math.Ceiling(stepLimit), 0, force, "file-batch", null, c =>
                {
                    if (c.State != "done" && c.Phase != null)
                    {
                        run.Phase = $"{stepId}: {c.Phase}";
                        WriteBatchActive(ch, run);
                    }
                });
                call.LockOwner = lockOwner;
                run.Current = call;
                Calls.Begin(call);

                var stepDeadline = stepStart.AddSeconds(stepLimit);
                var stepTimedOut = false;   // свой срок шага
                var batchTimedOut = false;  // срок всего пакета (не уборки)
                while (call.State != "done")
                {
                    await Task.WhenAny(call.Done.Task, Task.Delay(100));
                    if (call.State == "done") break;
                    // отмена пакета прерывает шаг, кроме уборки после остановки
                    if (stopped == null && run.CancelRequested) call.CancelRequested = true;
                    if (stopped == null && !batchTimedOut && DateTime.UtcNow > deadline)
                    {
                        batchTimedOut = true;
                        call.CancelRequested = true;
                    }

                    if (!stepTimedOut && DateTime.UtcNow > stepDeadline)
                    {
                        stepTimedOut = true;
                        call.CancelRequested = true;
                    }

                    // шаг не остановился за 10 с после срока — идём дальше без него
                    if ((stepTimedOut || batchTimedOut) && DateTime.UtcNow > (stepTimedOut ? stepDeadline : deadline).AddSeconds(10)) break;
                }

                run.Current = null;
                var finished = call.State == "done";
                stepOut = finished ? call.Output ?? string.Empty : $"✗ Шаг не остановился за 10 с после срока — дальше без него.\n";
                data = finished ? ParseData(call.Result) : null;
                bool failed;
                misses = new List<string>();
                if (!finished || ((stepTimedOut || batchTimedOut) && call.ExitCode != 0))
                {
                    // прерван сроком: шага или пакета (не по просьбе отменить)
                    failed = true;
                    exit = 5;
                    reason = batchTimedOut && !stepTimedOut ? Efc.Timeout : Efc.StepTimeout;
                    error = batchTimedOut && !stepTimedOut ? $"пакет не уложился в срок ({timeout} с)" : $"шаг не уложился в {stepLimit:0} с";
                }
                else if (call.BusyWith != null)
                {
                    failed = true;
                    exit = 6;
                    reason = Efc.Busy;
                    error = Efc.ErrorText(stepOut, 1);
                    data = new JsonObject { ["busyWith"] = call.BusyWith };
                }
                else
                {
                    exit = call.ExitCode;
                    misses = Efc.CheckExpect(expect, exit, stepOut, data);
                    failed = misses.Count > 0;
                    if (failed && exit == 0)
                    {
                        exit = 1;
                        reason = Efc.ExpectFailed;
                        error = "проверка не прошла: " + string.Join("; ", misses);
                    }
                    else if (!failed)
                    {
                        exit = 0;
                        misses.Clear();
                    }
                    else
                    {
                        reason = exit == 7 ? Efc.Cancelled : Efc.ReasonFor(exit);
                        error = Efc.ErrorText(stepOut, exit);
                    }
                }

                output.Append($"▸ {stepId} {command} — {(failed ? "ошибка" : "ok")}\n").Append(stepOut);
                if (stepOut.Length > 0 && !stepOut.EndsWith("\n")) output.Append('\n');
                results[stepId] = new Efc.StepResult { Exit = exit, Output = stepOut, Data = data };
                items.Add(Item(stepId, command, failed ? "failed" : "ok", exit, error, reason, data, stepOut, misses, Math.Round((DateTime.UtcNow - stepStart).TotalSeconds, 2)));
                if (!failed) continue;

                firstFail ??= (exit, $"{stepId} ({command}): {error}", reason);
                if (stopped == null && (run.CancelRequested || batchTimedOut))
                {
                    // отмена или срок всего вызова — дальше только уборка, остальные шаги cancelled
                    stopped = "cancelled";
                    run.Stopped = true;
                }
                else if (stopped == null && StopsHere(op, stopOnError))
                {
                    stopped = "failed";
                    run.Stopped = true;
                }
            }

            var (code, err, why) = firstFail ?? (0, null, null);
            return new Outcome
            {
                Exit = code, Error = err, Reason = why, Output = output.ToString(), Items = items,
                Seconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 2),
            };
        }

        private static bool StopsHere(JsonObject op, bool stopOnError) => Json.Str(op, "onError") switch
        {
            "continue" => false,
            "stop" => true,
            _ => stopOnError,
        };

        private static JsonObject Item(string id, string command, string status, int exit, string error, string reason, JsonNode data, string output, List<string> misses, double seconds)
        {
            var o = new JsonObject { ["id"] = id, ["command"] = command, ["status"] = status, ["ok"] = status == "ok", ["exitCode"] = exit };
            if (error != null) o["error"] = error;
            if (reason != null) o["reason"] = reason;
            if (data != null) o["data"] = data.DeepClone();
            if (!string.IsNullOrEmpty(output)) o["output"] = output.Length > MaxItemOutputChars ? output.Substring(0, MaxItemOutputChars) + "\n…" : output;
            if (misses != null && misses.Count > 0) o["expectFailed"] = new JsonArray(misses.Select(m => (JsonNode)m).ToArray());
            o["seconds"] = seconds;
            return o;
        }

        private static void WriteBatchActive(Channel ch, BatchRun run)
        {
            if (run.Finished) return;
            var o = new JsonObject
            {
                ["v"] = 1,
                ["id"] = run.Id,
                ["state"] = "running",
                ["command"] = $"batch ({run.Steps})",
                ["job"] = run.Id,
                ["phase"] = run.CancelRequested ? "отменяется: " + run.Phase : run.Phase,
                ["progress"] = Math.Round(run.Progress, 2),
                ["keep"] = run.Keep,
                ["accepted"] = run.Accepted,
                ["updated"] = DateTime.UtcNow.ToString("O"),
            };
            lock (run) if (!run.Finished) Json.WriteAtomic(Dir(ch, "active", run.Id + ".json"), Json.Write(o));
        }

        // ============================================================ отмена

        private static void PollCancels(Channel ch)
        {
            foreach (var path in Files(Dir(ch, "cancel")))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!SafeId.IsMatch(id))
                {
                    if (Age(path, DateTime.UtcNow) > CancelOrphanAge) Json.TryDelete(path);
                    continue;
                }

                if (ch.Calls.TryGetValue(id, out var call))
                {
                    call.CancelRequested = true;
                    WriteActive(ch, call, null);
                    Json.TryDelete(path);
                    continue;
                }

                if (ch.Batches.TryGetValue(id, out var run))
                {
                    run.CancelRequested = true;
                    // уборку после остановки пакета отмена не прерывает (SPEC 5.2.1)
                    var cur = run.Current;
                    if (cur != null && !run.Stopped) cur.CancelRequested = true;
                    WriteBatchActive(ch, run);
                    Json.TryDelete(path);
                    continue;
                }

                var req = Dir(ch, "requests", id + ".json");
                if (Known(ch, id))
                {
                    // уже выполнен: итог не трогать; повтор запроса с этим id — убрать
                    Json.TryDelete(req);
                    Json.TryDelete(path);
                    continue;
                }

                if (File.Exists(req))
                {
                    var o = Json.ReadFile(req);
                    var keep = Math.Clamp(o != null && o["keep"] != null ? Json.Num(o, "keep") : DefaultKeep, MinKeep, Calls.MaxKeep);
                    if (TryMove(req, Dir(ch, "active", id + ".json")))
                    {
                        WriteDone(ch, id, new Outcome { Exit = 7, Output = "✗ Отменено до начала выполнения.\n", Reason = Efc.Cancelled, Keep = keep });
                        Json.TryDelete(path);
                    }

                    continue;
                }

                if (Age(path, DateTime.UtcNow) > CancelOrphanAge) Json.TryDelete(path);
            }
        }

        // ============================================================ задачи (этап 1: исполняет Unity, task.run <файл>)

        private static void PollTasks(Channel ch)
        {
            foreach (var path in Sorted(Dir(ch, "tasks", "requests")))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!SafeId.IsMatch(id) || ch.Calls.ContainsKey("t:" + id)) continue;
                var active = Dir(ch, "tasks", "active", id + ".json");
                if (!TryMove(path, active)) continue;

                var o = Json.ReadFile(active, MaxRequestBytes);
                var keep = Json.Num(o, "keep");
                if (o == null || !(o["steps"] is JsonArray))
                {
                    WriteTaskDone(ch, id, 2, "✗ Неверная задача: нужен массив steps.", null, keep);
                    continue;
                }

                o.Remove("token");
                o.Remove("id");
                var def = Dir(ch, "tasks", "defs", id + ".json");
                Json.WriteAtomic(def, Json.Write(o));
                var timeout = (int)Math.Max(60, Json.Num(o, "timeout", 1800)) + 600;
                if (Age(active, DateTime.UtcNow) > timeout + 60)
                {
                    WriteTaskDone(ch, id, 5, "✗ Задача просрочена: пролежала дольше своего срока, не выполнялась.", null, keep);
                    continue;
                }

                var call = Calls.Create(ch.Project, "task.run", new[] { def }, ch.Project.Path, timeout, keep, false, "file-task", "t" + id,
                    c => OnTaskPhase(ch, id, c));
                ch.Calls["t:" + id] = call;
                WriteTaskActive(ch, id, call, keep);
                Calls.Begin(call);
            }
        }

        private static void OnTaskPhase(Channel ch, string id, Call c)
        {
            if (c.State != "done")
            {
                WriteTaskActive(ch, id, c, c.Keep);
                return;
            }

            ch.Calls.TryRemove("t:" + id, out _);
            Json.TryDelete(Dir(ch, "tasks", "defs", id + ".json"));
            WriteTaskDone(ch, id, c.ExitCode, (c.Output ?? string.Empty).TrimEnd('\n'), c.Result, c.Keep);
        }

        private static void WriteTaskActive(Channel ch, string id, Call c, double keep)
        {
            if (c.State == "done") return;
            var o = new JsonObject
            {
                ["id"] = id,
                ["state"] = "running",
                ["status"] = "running",
                ["job"] = c.Job ?? c.Id,
                ["phase"] = c.Phase,
                ["keep"] = keep,
            };
            lock (c) if (c.State != "done") Json.WriteAtomic(Dir(ch, "tasks", "active", id + ".json"), Json.Write(o));
        }

        private static void WriteTaskDone(Channel ch, string id, int exit, string summary, string report, double keep)
        {
            var o = Json.TryParseObject(report) ?? new JsonObject { ["status"] = exit == 0 ? "ok" : "failed", ["steps"] = new JsonArray(), ["artifacts"] = new JsonArray() };
            o["job"] = Json.Str(o, "id");
            o["id"] = id;
            o["state"] = "done";
            o["exitCode"] = exit;
            if (o["summary"] == null) o["summary"] = summary;
            keep = Math.Clamp(keep, 0, Calls.MaxKeep);
            if (keep > 0) o["expires"] = DateTime.UtcNow.AddSeconds(keep).ToString("O");
            Deliver(ch, Dir(ch, "tasks", "done", id + ".json"), Json.Write(o), Dir(ch, "tasks", "active", id + ".json"));
        }

        // ============================================================ уборка и файлы

        private static void Sweep(Channel ch)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in ch.Seen)
                if (kv.Value < now)
                    ch.Seen.TryRemove(kv.Key, out _);

            foreach (var dir in new[] { Dir(ch, "done"), Dir(ch, "tasks", "done") })
                foreach (var path in Files(dir))
                {
                    var o = Json.ReadFile(path);
                    var raw = Json.Str(o, "expires");
                    if (raw == null)
                    {
                        if (Age(path, now) > OrphanAge) Json.TryDelete(path);
                        continue;
                    }

                    if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var expires) && expires < now)
                        Json.TryDelete(path);
                }

            // active/ без живого вызова старше часа — хвост (итог не дописан или потерян)
            foreach (var path in AllFiles(Dir(ch, "active")))
            {
                var id = Path.GetFileName(path).Split('.')[0];
                if (!ch.Calls.ContainsKey(id) && !ch.Batches.ContainsKey(id) && Age(path, now) > OrphanAge) Json.TryDelete(path);
            }

            foreach (var path in Files(Dir(ch, "tasks", "active")))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!ch.Calls.ContainsKey("t:" + id) && Age(path, now) > OrphanAge) Json.TryDelete(path);
            }

            foreach (var dir in Folders.Select(d => Dir(ch, d)).Append(ch.Project.CliDir))
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var tmp in Directory.GetFiles(dir, "*.tmp*"))
                    if (Age(tmp, now) > 600)
                        Json.TryDelete(tmp);
            }
        }

        /// <summary>Итог — в done/, затем убрать active/. Не записалось или не убралось — повтор на следующих тиках.</summary>
        private static void Deliver(Channel ch, string path, string content, params string[] remove)
        {
            if (Json.WriteAtomic(path, content))
            {
                var left = remove.Where(r => !TryRemove(r)).ToArray();
                if (left.Length > 0) ch.Retry.Enqueue(new Delivery { Remove = left });
            }
            else ch.Retry.Enqueue(new Delivery { Path = path, Content = content, Remove = remove });
        }

        private static void Redeliver(Channel ch)
        {
            var n = ch.Retry.Count;
            for (var i = 0; i < n && ch.Retry.TryDequeue(out var d); i++)
            {
                if (d.Path != null && !Json.WriteAtomic(d.Path, d.Content))
                {
                    ch.Retry.Enqueue(d);
                    continue;
                }

                var left = d.Remove.Where(r => !TryRemove(r)).ToArray();
                if (left.Length > 0) ch.Retry.Enqueue(new Delivery { Remove = left });
            }
        }

        /// <summary>Удалить файл; true — его нет (удалён или и не было).</summary>
        private static bool TryRemove(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Возраст файла по времени записи (клиент пишет запрос один раз, перенос в active/ дату сохраняет).</summary>
        private static double Age(string path, DateTime now)
        {
            try
            {
                return (now - File.GetLastWriteTimeUtc(path)).TotalSeconds;
            }
            catch
            {
                return 0;
            }
        }

        private static List<string> Files(string dir)
        {
            try
            {
                return Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "*.json").Where(f => !Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal)).ToList()
                    : new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static List<string> AllFiles(string dir)
        {
            try
            {
                return Directory.Exists(dir) ? Directory.GetFiles(dir).ToList() : new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>По имени (id сортируются по времени; время изменения на общих папках ВМ ненадёжно).</summary>
        private static List<string> Sorted(string dir)
        {
            var list = Files(dir);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static bool TryMove(string from, string to)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.Move(from, to, true);
                    return true;
                }
                catch (FileNotFoundException)
                {
                    return false;
                }
                catch
                {
                    // Windows: файл держит антивирус или клиент дочитывает — повторить, а дальше — на следующем такте
                    System.Threading.Thread.Sleep(10);
                }
            }

            return false;
        }
    }
}
