using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Nexus API v1 (этап 1): проекты, вызовы, артефакты, управление демоном. Каждый запрос — с
    /// <c>Authorization: Bearer &lt;token&gt;</c>; запросы из браузера (заголовок Origin) отклоняются.
    /// </summary>
    internal static class Api
    {
        public static Action ShutdownRequested;

        public static async Task<HttpResponse> Handle(HttpRequest r)
        {
            if (r.Header("Origin") != null) return HttpResponse.Error(403, "запросы из браузера запрещены");
            if (!Tokens.Check(r.Header("Authorization"))) return HttpResponse.Error(401, "нужен токен: Authorization: Bearer <token из tokens.json>");

            var parts = r.Path.Trim('/').Split('/');
            if (parts.Length < 2 || parts[0] != "v1") return HttpResponse.Error(404, "нет такого пути: " + r.Path);

            switch (parts[1])
            {
                case "daemon":
                    if (parts.Length == 2 && r.Method == "GET") return Ok(DaemonJson());
                    if (parts.Length == 3 && parts[2] == "shutdown" && r.Method == "POST")
                    {
                        Log.Info("остановка по запросу API");
                        _ = Task.Delay(200).ContinueWith(_ => ShutdownRequested?.Invoke());
                        return Ok(new JsonObject { ["stopping"] = true });
                    }

                    break;

                case "projects":
                    if (parts.Length == 2 && r.Method == "GET")
                        return Ok(new JsonObject { ["projects"] = new JsonArray(Registry.All.Select(p => (JsonNode)ProjectJson(p)).ToArray()) });
                    if (parts.Length >= 3)
                    {
                        var p = Registry.Resolve(parts[2], out var problem);
                        if (p == null) return HttpResponse.Error(404, problem);
                        if (parts.Length == 3 && r.Method == "GET") return Ok(ProjectJson(p));
                        if (parts.Length == 4 && parts[3] == "calls" && r.Method == "POST") return StartCall(p, Json.TryParseObject(r.BodyText));
                        if (parts.Length >= 5 && parts[3] == "artifacts" && r.Method == "GET") return Artifact(p, string.Join('/', parts.Skip(4)));
                        if (parts.Length == 4 && parts[3] == "commands" && r.Method == "GET")
                        {
                            // список команд — от самого Bridge (bridge.hello), с ожиданием готовности редактора
                            var call = Calls.Start(p, "bridge.hello", null, p.Path, 30, 0, false, "http");
                            await Task.WhenAny(call.Done.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                            var data = call.State == "done" ? Json.TryParseObject(call.Result) : null;
                            var status = 200;
                            if (data == null)
                            {
                                // редактор не ответил (модальное окно?) — команды демона всё равно доступны
                                status = call.State != "done" ? 504 : 502;
                                data = new JsonObject
                                {
                                    ["error"] = call.State != "done" ? "редактор не ответил: " + call.Phase : call.Output,
                                    ["status"] = status,
                                    ["editor"] = "unavailable",
                                };
                            }

                            // команды самого демона (работают и при зависшем редакторе)
                            data["daemonCommands"] = new JsonArray(DaemonCommands.All.Select(c => (JsonNode)new JsonObject
                            {
                                ["name"] = c.Name,
                                ["usage"] = c.Usage,
                                ["summary"] = c.Summary,
                            }).ToArray());
                            return HttpResponse.Json(status, Json.Write(data));
                        }
                    }

                    break;

                case "calls":
                    if (parts.Length == 2 && r.Method == "POST")
                    {
                        var body = Json.TryParseObject(r.BodyText);
                        var explicitProject = Json.Str(body, "project");
                        var p = Registry.Resolve(explicitProject ?? Json.Str(body, "cwd"), out var problem);
                        // без --project и не из папки проекта — единственный открытый проект
                        if (p == null && explicitProject == null) p = Registry.Resolve(null, out problem);
                        if (p == null) return HttpResponse.Error(404, problem);
                        return StartCall(p, body);
                    }

                    if (parts.Length == 3 && r.Method == "GET")
                    {
                        var call = Calls.Find(parts[2]);
                        if (call == null) return HttpResponse.Error(404, $"нет вызова «{parts[2]}» (итог хранится 2 минуты или keep)");
                        var wait = Math.Clamp(Json.Num(new JsonObject { ["w"] = r.Q("wait") }, "w"), 0, 60);
                        if (wait > 0 && call.State != "done") await Task.WhenAny(call.Done.Task, Task.Delay(TimeSpan.FromSeconds(wait)));
                        return Ok(call.ToJson());
                    }

                    break;
            }

            return HttpResponse.Error(404, "нет такого пути: " + r.Method + " " + r.Path);
        }

        private static HttpResponse Ok(JsonObject o) => HttpResponse.Json(200, Json.Write(o));

        private static HttpResponse StartCall(Project p, JsonObject body)
        {
            var command = Json.Str(body, "command");
            if (string.IsNullOrWhiteSpace(command)) return HttpResponse.Error(400, "нужно поле command");
            var call = Calls.Start(p, command, Json.StrArray(body, "args"), Json.Str(body, "cwd") ?? p.Path, (int)Json.Num(body, "timeout"),
                Json.Num(body, "keep"), Json.Bool(body, "force"), Json.Str(body, "client") ?? "http");
            return HttpResponse.Json(202, Json.Write(call.ToJson()));
        }

        private static HttpResponse Artifact(Project p, string relative)
        {
            // только из NexusTemp проекта: путь нормализуется, выход за папку — 404
            var root = Path.GetFullPath(p.TempDir);
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return HttpResponse.Error(404, "нет артефакта: " + relative);
            var type = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".xml" => "application/xml",
                ".json" => "application/json",
                ".txt" or ".log" => "text/plain; charset=utf-8",
                _ => "application/octet-stream",
            };
            return new HttpResponse { ContentType = type, Body = File.ReadAllBytes(full) };
        }

        public static JsonObject DaemonJson() => new JsonObject
        {
            ["version"] = DaemonInfo.Version,
            ["protocol"] = DaemonInfo.ProtocolVersion,
            ["pid"] = Environment.ProcessId,
            ["started"] = DaemonInfo.Started.ToString("O"),
            ["projects"] = Registry.All.Length,
            ["calls"] = Calls.Active.Count(),
        };

        public static JsonObject ProjectJson(Project p) => new JsonObject
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["path"] = p.Path,
            ["state"] = p.State,
            ["editorState"] = p.EditorState,
            ["phase"] = p.Phase,
            ["unity"] = p.Unity,
            ["pid"] = p.Pid,
            ["calls"] = p.InFlight,
            ["silent"] = Math.Round(p.SilentSeconds, 1),
        };
    }
}
