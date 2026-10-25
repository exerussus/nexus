using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Токены API. Этап 1: один локальный токен «local» для клиентов этой машины (nexus, Unity). Файл —
    /// %LOCALAPPDATA%\Exerussus\Nexus\tokens.json, создаётся при первом запуске. Права (scopes) — этап 3.
    /// </summary>
    internal static class Tokens
    {
        private static string _local;

        public static string Local => _local;

        public static void Load()
        {
            try
            {
                Directory.CreateDirectory(DaemonPaths.Root);
                var o = Json.ReadFile(DaemonPaths.TokensFile);
                var token = Json.Str(o?["local"], "token");
                if (string.IsNullOrEmpty(token))
                {
                    token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                    o ??= new JsonObject();
                    o["schemaVersion"] = 1;
                    o["local"] = new JsonObject { ["token"] = token, ["scopes"] = new JsonArray("read", "run", "tasks", "intents", "danger") };
                    Json.WriteAtomic(DaemonPaths.TokensFile, Json.Write(o));
                }

                _local = token;
            }
            catch (Exception e)
            {
                Log.Error("токены", e);
                _local = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            }
        }

        /// <summary>Проверка «Bearer &lt;token&gt;» без утечки по времени.</summary>
        public static bool Check(string authorization)
        {
            if (string.IsNullOrEmpty(authorization) || _local == null) return false;
            const string prefix = "Bearer ";
            if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var given = authorization.Substring(prefix.Length).Trim();
            if (given.Length != _local.Length) return false;
            var diff = 0;
            for (var i = 0; i < given.Length; i++) diff |= given[i] ^ _local[i];
            return diff == 0;
        }
    }
}
