using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>Итог одного запуска git.</summary>
    internal sealed class GitResult
    {
        public int Exit;
        public string Out = string.Empty;
        public string Err = string.Empty;
        /// <summary>Вывод длиннее предела: хвост отброшен.</summary>
        public bool Truncated;
        public bool TimedOut;

        public bool Ok => Exit == 0 && !TimedOut;

        /// <summary>Первая строка ошибки (для коротких сообщений).</summary>
        public string FirstError
        {
            get
            {
                var text = (Err.Length > 0 ? Err : Out).Trim();
                var nl = text.IndexOf('\n');
                return GitRunner.Mask(nl < 0 ? text : text.Substring(0, nl).TrimEnd());
            }
        }
    }

    /// <summary>
    /// Запуск git: только через ArgumentList (никакой склейки командной строки), рабочая папка — репозиторий,
    /// без интерактивных запросов (учётные данные — системные, демон их не видит), с пределом времени и вывода.
    /// Чтение stdout/stderr — асинхронное и параллельное: переполненный канал не подвешивает процесс.
    /// </summary>
    internal static class GitRunner
    {
        public const int DefaultTimeout = 60;
        public const int NetworkTimeout = 300;
        public const int DefaultMaxChars = 1 << 20;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        // user:pass@ и user@ в URL (https://token@host, https://user:pass@host) — в выводе и журнале маскируются
        private static readonly Regex UrlCredentials = new Regex(@"([a-zA-Z][a-zA-Z0-9+.-]*://)[^/@\s]+@", RegexOptions.Compiled);

        /// <summary>Переменные окружения, которые могут подменить репозиторий, личность или ввод — убираются.</summary>
        private static readonly string[] DropEnv =
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
            "GIT_COMMON_DIR", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES",
            "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_AUTHOR_DATE", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "GIT_COMMITTER_DATE",
            "GIT_EDITOR", "GIT_SEQUENCE_EDITOR",
        };

        public static string Mask(string text) => string.IsNullOrEmpty(text) ? text ?? string.Empty : UrlCredentials.Replace(text, "$1***@");

        /// <summary>Есть ли git вообще (кэша нет: вызывается по требованию).</summary>
        public static async Task<string> Version(CancellationToken ct)
        {
            var r = await Run(Environment.CurrentDirectory, new[] { "--version" }, 15, ct);
            return r.Ok ? r.Out.Trim() : null;
        }

        /// <summary>
        /// Запустить git в папке <paramref name="repo"/>. <paramref name="timeout"/> — секунды; отмена ct (отмена или срок
        /// вызова) и срок убивают дерево процессов git. <paramref name="input"/> — stdin в UTF-8 (сообщения коммитов и тегов:
        /// не через аргументы — кодировка командной строки Windows их не портит).
        /// </summary>
        public static async Task<GitResult> Run(string repo, IReadOnlyList<string> args, int timeout, CancellationToken ct,
            string input = null, int maxChars = DefaultMaxChars, bool readOnly = false)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = repo,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
                StandardInputEncoding = Utf8,
            };
            // общие -c: без цвета и пейджера, пути без экранирования \ooo (кириллица в именах файлов)
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("color.ui=false");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("core.quotepath=false");
            psi.ArgumentList.Add("--no-pager");
            foreach (var a in args) psi.ArgumentList.Add(a);

            foreach (var name in DropEnv) psi.Environment.Remove(name);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";
            psi.Environment["GIT_PAGER"] = "cat";
            psi.Environment["PAGER"] = "cat";
            psi.Environment["GIT_EDITOR"] = "true";
            // чтение не берёт index.lock (status обновляет индекс) — не мешает параллельной записи пользователя
            if (readOnly) psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

            var result = new GitResult();
            var started = DateTime.UtcNow;
            Process process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception e)
            {
                result.Exit = 127;
                result.Err = "git не запускается: " + e.Message + " (установлен ли git и есть ли он в PATH демона?)";
                return result;
            }

            if (process == null)
            {
                result.Exit = 127;
                result.Err = "git не запустился";
                return result;
            }

            using (process)
            {
                var stdout = ReadLimited(process.StandardOutput, maxChars);
                var stderr = ReadLimited(process.StandardError, 64 * 1024);
                try
                {
                    if (input != null) await process.StandardInput.WriteAsync(input);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // git закрыл stdin раньше (ошибка аргументов) — итог скажет код выхода
                }

                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeout)));
                try
                {
                    await process.WaitForExitAsync(limit.Token);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch
                    {
                        // уже вышел
                    }

                    result.TimedOut = !ct.IsCancellationRequested;
                    try
                    {
                        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch
                    {
                        // не дождались — потоки чтения закроются вместе с процессом
                    }

                    var removed = readOnly ? null : RemoveOwnLocks(repo, started);
                    if (removed != null) Log.Warn($"git остановлен в {repo}: снят оставленный им {removed}");
                    ct.ThrowIfCancellationRequested();
                    if (removed != null) result.Err = $"снят {removed}, оставленный остановленным git; проверьте состояние (git.status).\n" + result.Err;
                }

                var (outText, outCut) = await stdout;
                var (errText, _) = await stderr;
                result.Out = outText;
                result.Err = Mask(errText);
                result.Truncated = outCut;
                result.Exit = result.TimedOut ? 124 : process.HasExited ? process.ExitCode : 124;
                if (result.TimedOut) result.Err = $"git {FirstArg(args)}: не завершился за {timeout} с — процесс остановлен.\n" + result.Err;
                return result;
            }
        }

        private static string FirstArg(IReadOnlyList<string> args) => args.Count > 0 ? args[0] : "";

        /// <summary>
        /// Убитый git (отмена, срок) не успевает убрать свои замки: index.lock/HEAD.lock, созданные после его старта,
        /// — его, и без снятия репозиторий останется заблокированным. Более старые замки не трогаются (чужой процесс).
        /// Запись демона в репозиторий одна за раз (замок GitCommands), поэтому свежий замок — наш.
        /// </summary>
        private static string RemoveOwnLocks(string repo, DateTime started)
        {
            var gitDir = Path.Combine(repo, ".git");
            if (!Directory.Exists(gitDir)) return null;
            var removed = new List<string>();
            foreach (var name in new[] { "index.lock", "HEAD.lock" })
            {
                var path = Path.Combine(gitDir, name);
                try
                {
                    if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= started.AddSeconds(-1))
                    {
                        File.Delete(path);
                        removed.Add(".git/" + name);
                    }
                }
                catch (Exception e)
                {
                    Log.Warn($"замок {path}: {e.Message}");
                }
            }

            return removed.Count == 0 ? null : string.Join(", ", removed);
        }

        /// <summary>Читать поток до конца, сохраняя не больше max символов (остальное читается и отбрасывается).</summary>
        private static async Task<(string, bool)> ReadLimited(StreamReader reader, int max)
        {
            var sb = new StringBuilder();
            var buffer = new char[8192];
            var cut = false;
            try
            {
                int n;
                while ((n = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    var room = max - sb.Length;
                    if (room <= 0)
                    {
                        cut = true;
                        continue;
                    }

                    if (n > room)
                    {
                        sb.Append(buffer, 0, room);
                        cut = true;
                    }
                    else sb.Append(buffer, 0, n);
                }
            }
            catch (Exception)
            {
                // процесс убит — что успели прочитать
            }

            return (sb.ToString(), cut);
        }
    }
}
