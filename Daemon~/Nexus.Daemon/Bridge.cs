using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>Итог обращения к Editor Bridge.</summary>
    internal enum BridgeStatus
    {
        /// <summary>Ответ получен.</summary>
        Ok,

        /// <summary>Не подключились (порт закрыт: перезагрузка домена, редактор закрыт). Запрос точно не доставлен.</summary>
        NotDelivered,

        /// <summary>Запрос ушёл, ответа нет (обрыв, срок). Результат неизвестен.</summary>
        Lost,
    }

    /// <summary>
    /// Клиент Editor Bridge: одна строка JSON на соединение (протокол сервера CLI Nexus 1.x) + поле
    /// <c>client: "nexusd"</c> — Bridge пропускает только демон (прямой TCP выключен по умолчанию).
    /// </summary>
    internal static class Bridge
    {
        private const int ConnectTimeoutMs = 2000;
        private const int MaxResponseBytes = 16 << 20;

        public static async Task<(BridgeStatus status, JsonObject response, string error)> Send(Project p, JsonObject request, double waitSeconds, CancellationToken ct)
        {
            var endpoint = p.Bridge;
            var port = endpoint.Port;
            var token = endpoint.Token;
            if (port <= 0 || string.IsNullOrEmpty(token)) return (BridgeStatus.NotDelivered, null, "редактор перезагружает домен");

            request["token"] = token;
            request["client"] = "nexusd";
            var line = Json.Write(request) + "\n";

            using var client = new TcpClient { NoDelay = true };
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(ConnectTimeoutMs);
                await client.ConnectAsync("127.0.0.1", port, connectCts.Token);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                return (BridgeStatus.NotDelivered, null, e.Message);
            }

            var acked = false;
            try
            {
                var stream = client.GetStream();
                var bytes = Json.Utf8.GetBytes(line);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, waitSeconds) + 5));
                var reader = new LineReader(stream);

                // Bridge 2.0 сначала подтверждает приём ({"ack":true}): без подтверждения запрос не доставлен
                var first = Json.TryParseObject(await reader.ReadLine(readCts.Token));
                if (first == null) return (BridgeStatus.NotDelivered, null, "соединение закрыто до подтверждения");
                if (Json.Bool(first, "ack"))
                {
                    acked = true;
                    var response = Json.TryParseObject(await reader.ReadLine(readCts.Token));
                    if (response == null) return (BridgeStatus.Lost, null, "нет ответа после подтверждения");
                    return (BridgeStatus.Ok, response, null);
                }

                return (BridgeStatus.Ok, first, null); // Bridge без подтверждения — сразу ответ
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                return (acked ? BridgeStatus.Lost : BridgeStatus.NotDelivered, null, e.Message);
            }
        }

        /// <summary>Чтение строк из потока с остатком между вызовами (две строки могут прийти одним пакетом).</summary>
        private sealed class LineReader
        {
            private readonly NetworkStream _stream;
            private readonly MemoryStream _pending = new MemoryStream();
            private readonly byte[] _chunk = new byte[16 * 1024];

            public LineReader(NetworkStream stream) => _stream = stream;

            public async Task<string> ReadLine(CancellationToken ct)
            {
                while (true)
                {
                    var buf = _pending.GetBuffer();
                    var len = (int)_pending.Length;
                    var nl = Array.IndexOf(buf, (byte)'\n', 0, len);
                    if (nl >= 0)
                    {
                        var text = Encoding.UTF8.GetString(buf, 0, nl);
                        var rest = new byte[len - nl - 1];
                        Buffer.BlockCopy(buf, nl + 1, rest, 0, rest.Length);
                        _pending.SetLength(0);
                        _pending.Write(rest, 0, rest.Length);
                        return text;
                    }

                    var n = await _stream.ReadAsync(_chunk, ct);
                    if (n <= 0)
                    {
                        if (_pending.Length == 0) return null;
                        var tail = Encoding.UTF8.GetString(_pending.GetBuffer(), 0, (int)_pending.Length);
                        _pending.SetLength(0);
                        return tail;
                    }

                    _pending.Write(_chunk, 0, n);
                    if (_pending.Length > MaxResponseBytes) throw new IOException("ответ больше 16 МБ");
                }
            }
        }

        public static JsonObject Request(string command, string[] args, string cwd, int timeout, bool force = false, string task = null)
        {
            var arr = new JsonArray();
            foreach (var a in args ?? Array.Empty<string>()) arr.Add(a);
            var o = new JsonObject
            {
                ["command"] = command,
                ["args"] = arr,
                ["cwd"] = cwd,
                ["timeout"] = timeout,
            };
            if (force) o["force"] = true;
            if (task != null) o["task"] = task;
            return o;
        }
    }
}
