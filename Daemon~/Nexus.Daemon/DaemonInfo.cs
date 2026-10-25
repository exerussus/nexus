using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>daemon.json — как найти демона: pid, порт, версии. Пишется при старте, удаляется при выходе.</summary>
    internal static class DaemonInfo
    {
        public const int ProtocolVersion = 1;
        public const int BasePort = 47800;
        public const int PortRange = 10;

        public static string Version =>
            typeof(DaemonInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

        public static DateTime Started { get; } = DateTime.UtcNow;

        public static void Write(int port)
        {
            var o = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["pid"] = Environment.ProcessId,
                ["port"] = port,
                ["version"] = Version,
                ["protocol"] = ProtocolVersion,
                ["started"] = Started.ToString("O"),
                ["home"] = DaemonPaths.Home,
                ["tokens"] = DaemonPaths.TokensFile,
            };
            Json.WriteAtomic(DaemonPaths.DaemonFile, Json.Write(o));
        }

        /// <summary>Удалить daemon.json, если он наш (новый демон мог уже записать свой).</summary>
        public static void Delete()
        {
            var o = Json.ReadFile(DaemonPaths.DaemonFile);
            if (o != null && (int)Json.Num(o, "pid") != Environment.ProcessId) return;
            Json.TryDelete(DaemonPaths.DaemonFile);
        }

        public static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }
}
