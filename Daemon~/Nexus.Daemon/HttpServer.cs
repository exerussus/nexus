using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    internal sealed class HttpRequest
    {
        public string Method;
        public string Path;
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();

        public string BodyText => Encoding.UTF8.GetString(Body);
        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public string Q(string name) => Query.TryGetValue(name, out var v) ? v : null;
    }

    internal sealed class HttpResponse
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = Array.Empty<byte>();

        public static HttpResponse Json(int status, string json) => new HttpResponse { Status = status, Body = Encoding.UTF8.GetBytes(json) };

        public static HttpResponse Error(int status, string message) =>
            Json(status, Daemon.Json.Write(new System.Text.Json.Nodes.JsonObject { ["error"] = message, ["status"] = status }));
    }

    /// <summary>
    /// Минимальный HTTP/1.1 сервер на 127.0.0.1 (только localhost — наружу демон не слушает никогда). Без
    /// ASP.NET Core: демону нужен только базовый рантайм .NET. Одно соединение — один запрос (Connection: close).
    /// </summary>
    internal sealed class HttpServer
    {
        private const int MaxHeaderBytes = 64 * 1024;
        private const int MaxBodyBytes = 4 << 20;

        private readonly Func<HttpRequest, Task<HttpResponse>> _handler;
        private TcpListener _listener;

        public int Port { get; private set; }

        public HttpServer(Func<HttpRequest, Task<HttpResponse>> handler) => _handler = handler;

        /// <summary>Занять первый свободный порт из диапазона 47800–47809 (или заданный).</summary>
        public bool Start(int? fixedPort)
        {
            var ports = fixedPort.HasValue ? new[] { fixedPort.Value } : Range(DaemonInfo.BasePort, DaemonInfo.PortRange);
            foreach (var port in ports)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, port);
                    l.Start(64);
                    _listener = l;
                    Port = port;
                    _ = Task.Run(AcceptLoop);
                    return true;
                }
                catch (SocketException)
                {
                    // занят — следующий
                }
            }

            return false;
        }

        private static int[] Range(int from, int count)
        {
            var r = new int[count];
            for (var i = 0; i < count; i++) r[i] = from + i;
            return r;
        }

        public void Stop()
        {
            try
            {
                _listener?.Stop();
            }
            catch
            {
                // уже закрыт
            }
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return; // остановлен
                }

                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var request = await Read(stream, cts.Token);
                    HttpResponse response;
                    if (request == null) response = HttpResponse.Error(400, "bad request");
                    else
                    {
                        try
                        {
                            response = await _handler(request);
                        }
                        catch (Exception e)
                        {
                            Log.Error("API " + request.Method + " " + request.Path, e);
                            response = HttpResponse.Error(500, e.Message);
                        }
                    }

                    await Write(stream, response);
                }
                catch (Exception e)
                {
                    Log.Warn("HTTP: " + e.Message);
                }
            }
        }

        private static async Task<HttpRequest> Read(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int headerEnd;
            while (true)
            {
                var n = await stream.ReadAsync(chunk, ct);
                if (n <= 0) return null;
                buffer.Write(chunk, 0, n);
                headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n");
                if (headerEnd >= 0) break;
                if (buffer.Length > MaxHeaderBytes) return null;
            }

            var all = buffer.GetBuffer();
            var head = Encoding.ASCII.GetString(all, 0, headerEnd);
            var lines = head.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;

            var request = new HttpRequest { Method = first[0].ToUpperInvariant() };
            var target = first[1];
            var q = target.IndexOf('?');
            request.Path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target);
            if (q >= 0)
                foreach (var pair in target.Substring(q + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    var key = Uri.UnescapeDataString(eq >= 0 ? pair.Substring(0, eq) : pair);
                    request.Query[key] = eq >= 0 ? Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')) : string.Empty;
                }

            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0) request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            var length = int.TryParse(request.Header("Content-Length"), out var len) ? len : 0;
            if (length < 0 || length > MaxBodyBytes) return null;

            // клиенты .NET шлют тело только после «100 Continue» (иначе ждут свой таймаут)
            if (string.Equals(request.Header("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase) && length > (int)buffer.Length - (headerEnd + 4))
            {
                var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                await stream.WriteAsync(cont, ct);
            }
            var bodyStart = headerEnd + 4;
            var body = new MemoryStream();
            body.Write(all, bodyStart, (int)buffer.Length - bodyStart);
            while (body.Length < length)
            {
                var n = await stream.ReadAsync(chunk, ct);
                if (n <= 0) break;
                body.Write(chunk, 0, n);
            }

            request.Body = body.ToArray();
            if (request.Body.Length > length) Array.Resize(ref request.Body, length);
            return request;
        }

        private static int IndexOf(byte[] data, int length, string pattern)
        {
            var p = Encoding.ASCII.GetBytes(pattern);
            for (var i = 0; i + p.Length <= length; i++)
            {
                var match = true;
                for (var j = 0; j < p.Length; j++)
                    if (data[i + j] != p[j])
                    {
                        match = false;
                        break;
                    }

                if (match) return i;
            }

            return -1;
        }

        private static async Task Write(NetworkStream stream, HttpResponse r)
        {
            var reason = r.Status switch { 200 => "OK", 201 => "Created", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 409 => "Conflict", 403 => "Forbidden", 502 => "Bad Gateway", 504 => "Gateway Timeout", 500 => "Internal Server Error", _ => "Status" };
            var head = $"HTTP/1.1 {r.Status} {reason}\r\nContent-Type: {r.ContentType}\r\nContent-Length: {r.Body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(bytes);
            await stream.WriteAsync(r.Body);
            await stream.FlushAsync();
        }
    }
}
