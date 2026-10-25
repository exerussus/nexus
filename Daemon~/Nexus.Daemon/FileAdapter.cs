using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Файловый адаптер Nexus API — для клиентов без сети до localhost (агент в VM, WSL). Формат 1.0 без
    /// изменений: <c>NexusTemp/cli/requests → active → done</c>, <c>cli/tasks/…</c>, пульс <c>alive.json</c>.
    /// Обслуживает его демон, а не Unity: вызов принят сразу, даже пока редактор перезагружает домен или спит.
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

        private static readonly Regex SafeId = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

        /// <summary>Пути проектов, чьи хвосты прошлого демона уже разобраны (раз за жизнь демона).</summary>
        private static readonly ConcurrentDictionary<string, bool> Recovered = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private sealed class Delivery
        {
            public string Path;
            public string Content;
            public string Remove;
        }

        private sealed class Channel
        {
            public readonly ConcurrentQueue<Delivery> Retry = new ConcurrentQueue<Delivery>();
            public Project Project;
            public bool Prepared;
            public long Seq;
            public DateTime NextBeat;
            public DateTime NextSweep;
            public readonly ConcurrentDictionary<string, Call> Calls = new ConcurrentDictionary<string, Call>();
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
                    PollRequests(ch);
                    PollTasks(ch);
                    if (now >= ch.NextSweep)
                    {
                        ch.NextSweep = now.AddSeconds(SweepInterval);
                        Sweep(ch);
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

        private static void Prepare(Channel ch)
        {
            var root = ch.Project.TempDir;
            Directory.CreateDirectory(root);
            var ignore = Path.Combine(root, ".gitignore");
            if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n");
            foreach (var d in new[] { "requests", "active", "done", Path.Combine("tasks", "requests"), Path.Combine("tasks", "active"), Path.Combine("tasks", "done"), Path.Combine("tasks", "defs") })
                Directory.CreateDirectory(Dir(ch, d));
            InstallClient(ch);
            // сторож 1.0 (nexus-watchdog.ps1) уступает тому, чей pid в watchdog.json: теперь сторожит демон
            Json.WriteAtomic(Dir(ch, "watchdog.json"), Json.Write(new JsonObject
            {
                ["pid"] = Environment.ProcessId, ["unityPid"] = ch.Project.Pid, ["source"] = "nexusd", ["version"] = DaemonInfo.Version,
                ["started"] = DateTime.UtcNow.ToString("O"),
            }));
            if (Recovered.TryAdd(Project.Normalize(ch.Project.Path), true)) RecoverTails(ch);
            ch.Prepared = true;
            Beat(ch);
        }

        /// <summary>Клиент nexus-fs — в NexusTemp/, чтобы агент нашёл его в папке проекта.</summary>
        private static void InstallClient(Channel ch)
        {
            try
            {
                var from = Path.Combine(DaemonPaths.ClientDir, "nexus-fs");
                if (!File.Exists(from)) return;
                var to = Path.Combine(ch.Project.TempDir, "nexus-fs");
                var bytes = File.ReadAllBytes(from);
                if (File.Exists(to) && File.ReadAllBytes(to).AsSpan().SequenceEqual(bytes)) return;
                File.WriteAllBytes(to, bytes);
            }
            catch (Exception e)
            {
                Log.Warn("копия nexus-fs: " + e.Message);
            }
        }

        /// <summary>Вызовы прошлого демона в active/ — их итог потерян вместе с ним.</summary>
        private static void RecoverTails(Channel ch)
        {
            foreach (var path in Files(Dir(ch, "active")))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                var o = Json.ReadFile(path);
                WriteDone(ch, id, 4, "✗ Демон перезапущен во время вызова — результат неизвестен, проверьте status.\n", null, Json.Num(o, "keep"), null);
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
            var o = new JsonObject
            {
                ["seq"] = ch.Seq,
                ["state"] = p.State,
                ["phase"] = p.Phase,
                ["project"] = p.Id,
                ["path"] = p.Path,
                ["pid"] = p.Pid,
                ["daemon"] = true,
                ["daemonPid"] = Environment.ProcessId,
                ["daemonVersion"] = DaemonInfo.Version,
                ["silent"] = Math.Round(p.SilentSeconds, 1),
                ["at"] = DateTime.UtcNow.ToString("O"),
            };
            Json.WriteAtomic(Path.Combine(p.CliDir, "alive.json"), Json.Write(o));
        }

        // ============================================================ вызовы

        private static void PollRequests(Channel ch)
        {
            foreach (var path in Sorted(Dir(ch, "requests")))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!SafeId.IsMatch(id) || ch.Calls.ContainsKey(id)) continue;
                var active = Dir(ch, "active", id + ".json");
                if (!TryMove(path, active)) continue;

                var o = Json.ReadFile(active, MaxRequestBytes);
                var keep = Json.Num(o, "keep");
                var command = Json.Str(o, "command");
                if (o == null || string.IsNullOrWhiteSpace(command))
                {
                    WriteDone(ch, id, 2, "Неверный запрос.\n", null, keep, null);
                    continue;
                }

                // брошенный запрос (клиент ушёл, демона не было) не выполняется задним числом
                var timeout = (int)Json.Num(o, "timeout");
                if (Age(active, DateTime.UtcNow, created: true) > (timeout > 0 ? timeout : Calls.DefaultTimeout) + 60)
                {
                    WriteDone(ch, id, 5, "✗ Запрос просрочен: пролежал дольше своего срока, не выполнялся.\n", null, keep, null);
                    continue;
                }

                var call = Calls.Create(ch.Project, command, Json.StrArray(o, "args"), Json.Str(o, "cwd") ?? ch.Project.Path,
                    timeout, keep, Json.Bool(o, "force"), "file", id, c => OnCallPhase(ch, c));
                ch.Calls[id] = call;
                WriteActive(ch, call);
                Calls.Begin(call);
            }
        }

        private static void OnCallPhase(Channel ch, Call c)
        {
            if (c.State == "done")
            {
                ch.Calls.TryRemove(c.Id, out _);
                WriteDone(ch, c.Id, c.ExitCode, c.Output, c.Result, c.Keep, c);
                return;
            }

            WriteActive(ch, c);
        }

        private static void WriteActive(Channel ch, Call c)
        {
            if (c.State == "done") return;
            var o = new JsonObject
            {
                ["id"] = c.Id,
                ["state"] = "running",
                ["command"] = c.Command,
                ["args"] = new JsonArray(c.Args.Select(a => (JsonNode)a).ToArray()),
                ["job"] = c.Job ?? c.Id, // принят демоном: --no-wait может уходить
                ["keep"] = c.Keep,
                ["phase"] = c.Phase,
            };
            lock (c) if (c.State != "done") Json.WriteAtomic(Dir(ch, "active", c.Id + ".json"), Json.Write(o));
        }

        private static void WriteDone(Channel ch, string id, int exit, string output, string result, double keep, Call c)
        {
            if (output != null && output.Length > MaxOutputChars) output = output.Substring(0, MaxOutputChars) + "\n… (вывод обрезан)\n";
            var o = new JsonObject
            {
                ["id"] = id,
                ["state"] = "done",
                ["ok"] = exit == 0,
                ["exitCode"] = exit,
                ["output"] = output ?? string.Empty,
                ["project"] = ch.Project.Id,
            };
            if (result != null) o["result"] = result;
            if (c?.Job != null) o["job"] = c.Job;
            if (c != null) o["seconds"] = Math.Round(c.Seconds, 2);
            keep = Math.Clamp(keep, 0, Calls.MaxKeep);
            if (keep > 0) o["expires"] = DateTime.UtcNow.AddSeconds(keep).ToString("O");
            Deliver(ch, Dir(ch, "done", id + ".json"), Json.Write(o), Dir(ch, "active", id + ".json"), c);
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
                if (Age(active, DateTime.UtcNow, created: true) > timeout + 60)
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
            Deliver(ch, Dir(ch, "tasks", "done", id + ".json"), Json.Write(o), Dir(ch, "tasks", "active", id + ".json"), null);
        }

        // ============================================================ уборка и файлы

        private static void Sweep(Channel ch)
        {
            var now = DateTime.UtcNow;
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
            foreach (var (dir, prefix) in new[] { (Dir(ch, "active"), ""), (Dir(ch, "tasks", "active"), "t:") })
                foreach (var path in Files(dir))
                {
                    var id = Path.GetFileNameWithoutExtension(path);
                    if (!ch.Calls.ContainsKey(prefix + id) && Age(path, now) > OrphanAge) Json.TryDelete(path);
                }

            foreach (var dir in new[] { "requests", "active", "done", Path.Combine("tasks", "requests"), Path.Combine("tasks", "active"), Path.Combine("tasks", "done"), Path.Combine("tasks", "defs") })
            {
                var full = Dir(ch, dir);
                if (!Directory.Exists(full)) continue;
                foreach (var tmp in Directory.GetFiles(full, "*.tmp"))
                    if (Age(tmp, now) > 600)
                        Json.TryDelete(tmp);
            }
        }

        /// <summary>Итог — в done/, затем убрать active/. Не записалось — повтор на следующих тиках, active/ остаётся.</summary>
        private static void Deliver(Channel ch, string path, string content, string remove, Call c)
        {
            if (c != null)
                lock (c)
                {
                    if (Json.WriteAtomic(path, content)) Json.TryDelete(remove);
                    else ch.Retry.Enqueue(new Delivery { Path = path, Content = content, Remove = remove });
                    return;
                }

            if (Json.WriteAtomic(path, content)) Json.TryDelete(remove);
            else ch.Retry.Enqueue(new Delivery { Path = path, Content = content, Remove = remove });
        }

        private static void Redeliver(Channel ch)
        {
            var n = ch.Retry.Count;
            for (var i = 0; i < n && ch.Retry.TryDequeue(out var d); i++)
            {
                if (Json.WriteAtomic(d.Path, d.Content)) Json.TryDelete(d.Remove);
                else ch.Retry.Enqueue(d);
            }
        }

        /// <summary>Возраст файла по времени записи (клиент пишет запрос один раз, перенос в active/ дату сохраняет).</summary>
        private static double Age(string path, DateTime now, bool created = false)
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
                return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").ToList() : new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static List<string> Sorted(string dir)
        {
            var list = Files(dir);
            list.Sort((a, b) =>
            {
                try
                {
                    return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
                }
                catch
                {
                    return 0;
                }
            });
            return list;
        }

        private static bool TryMove(string from, string to)
        {
            try
            {
                File.Move(from, to, true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
