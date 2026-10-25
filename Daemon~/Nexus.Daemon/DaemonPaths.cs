using System;
using System.IO;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>Пути демона: общий каталог пользователя (тот же, что у Unity-части: %LOCALAPPDATA%\Exerussus\Nexus).</summary>
    internal static class DaemonPaths
    {
        public static string Root =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exerussus", "Nexus");

        /// <summary>Файлы экземпляров редакторов — их пишет Editor Bridge.</summary>
        public static string Instances => Path.Combine(Root, "instances");

        public static string DaemonFile => Path.Combine(Root, "daemon.json");
        public static string TokensFile => Path.Combine(Root, "tokens.json");
        public static string LogFile => Path.Combine(Root, "daemon.log");

        /// <summary>Папка, из которой запущен демон (Tools~/daemon пакета): рядом лежит Tools~/cli.</summary>
        public static string Home => AppContext.BaseDirectory;

        /// <summary>Клиенты (nexus-fs): копия рядом с демоном (Home/cli), иначе Tools~/cli пакета.</summary>
        public static string ClientDir
        {
            get
            {
                var local = Path.Combine(Home, "cli");
                return Directory.Exists(local) ? local : Path.GetFullPath(Path.Combine(Home, "..", "cli"));
            }
        }
    }
}
