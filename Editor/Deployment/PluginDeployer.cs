using System.Collections.Generic;
using System.IO;
using System.Linq;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Manifests;

namespace Exerussus.Nexus.Deployment
{
    /// <summary>
    /// Файловые механики развёртки. Статический stateless-сервис: всё состояние —
    /// на диске. Атомарность через temp ВНЕ Assets → swap внутрь Assets (старое → скрытый
    /// сосед, новое → на место, старое удаляется; сбой — откат), так что State/&lt;id&gt;
    /// в любой момент целый-старый или целый-новый. Пути deploy могут содержать подпапки;
    /// файлы копируются байтами.
    ///
    /// Пишет deploy.json/preserve.json сам (generic-механика) — страница не участвует.
    /// </summary>
    public static class PluginDeployer
    {
        public readonly struct Result
        {
            public readonly bool   Ok;
            public readonly string Message;
            public Result(bool ok, string message) { Ok = ok; Message = message; }
            public static Result Fail(string m) => new Result(false, m);
            public static Result Done(string m) => new Result(true, m);
        }

        // ----------------------------------------------------------------- deploy

        /// <summary>Включение: есть Preserve → распаковать сейв, иначе развернуть дефолт.</summary>
        public static Result Deploy(PluginManifest m)
        {
            if (m == null) return Result.Fail("нет манифеста");

            var issues = Validate(m);
            if (issues.Count > 0)
                return Result.Fail($"плагин '{m.Id}' невалиден: {string.Join("; ", issues)}");

            return HasPreserve(m.Id) ? DeployFromPreserve(m) : DeployFromPlugins(m);
        }

        private static Result DeployFromPlugins(PluginManifest m)
        {
            var tmp = FreshTemp(m.Id);
            var files = new List<string>();
            try
            {
                var srcDir = PluginRoots.SourceDir(m.Id);
                foreach (var entry in m.Deploy ?? Enumerable.Empty<DeployEntry>())
                {
                    var from = SafeRelative(entry?.From, out var fromError);
                    if (from == null) return Fail(tmp, $"deploy.from «{entry?.From}»: {fromError}");
                    var to = SafeRelative(entry.To, out var toError);
                    if (to == null) return Fail(tmp, $"deploy.to «{entry.To}»: {toError}");

                    var src = Path.Combine(srcDir, from);
                    if (!File.Exists(src))
                        return Fail(tmp, $"истоковый файл не найден: {entry.From}");
                    CopyFile(src, Path.Combine(tmp, to));
                    files.Add(to);
                }

                WriteDescriptor(tmp, new DeployDescriptor
                {
                    PluginGuid = m.Guid, DeployedVersion = m.Version, Files = files,
                });

                SwapIntoState(m.Id, tmp);
                return Result.Done($"развёрнут «{m.Id}» (дефолт v{m.Version})");
            }
            catch (System.Exception ex) { return Fail(tmp, ex.Message); }
        }

        private static Result DeployFromPreserve(PluginManifest m)
        {
            var preDir = NexusPaths.PreserveDir(m.Id);
            var tmp = FreshTemp(m.Id);
            try
            {
                // рекурсивно: подпапки сохраняются, инертность снимается только с имени файла
                foreach (var rel in RelativeFiles(preDir))
                {
                    if (rel == "preserve.json") continue;
                    CopyFile(Path.Combine(preDir, rel), Path.Combine(tmp, MapName(rel, PackRegime.ToLive)));
                }

                // переносим служебный дескриптор сейва в deploy.json
                var desc = JsonIo.Load<DeployDescriptor>(Path.Combine(preDir, "preserve.json"))
                           ?? new DeployDescriptor { PluginGuid = m.Guid, DeployedVersion = m.Version };
                if (string.IsNullOrEmpty(desc.PluginGuid)) desc.PluginGuid = m.Guid;
                desc.Files = RelativeFiles(tmp).Where(n => n != "deploy.json").ToList();
                WriteDescriptor(tmp, desc);

                SwapIntoState(m.Id, tmp);
                DeleteDir(preDir);   // сейв распакован в State — старый снапшот больше не нужен
                return Result.Done($"восстановлен сейв «{m.Id}» (из v{desc.DeployedVersion})");
            }
            catch (System.Exception ex) { return Fail(tmp, ex.Message); }
        }

        // --------------------------------------------------------------- undeploy

        /// <summary>Выключение: упаковать State в инертный Preserve, затем выселить State.</summary>
        public static Result Undeploy(string id)
        {
            var stateDir = NexusPaths.StateDir(id);
            if (!Directory.Exists(stateDir)) return Result.Done($"«{id}» уже не развёрнут");

            var desc = JsonIo.Load<DeployDescriptor>(Path.Combine(stateDir, "deploy.json"))
                       ?? new DeployDescriptor { PluginGuid = KnownGuid(id) };

            var ptmp = FreshTemp(id + ".preserve");
            try
            {
                // рекурсивно, с относительными путями; инертность — только к имени файла
                foreach (var rel in RelativeFiles(stateDir))
                {
                    if (rel == "deploy.json") continue;           // дескриптор пересоздадим
                    CopyFile(Path.Combine(stateDir, rel), Path.Combine(ptmp, MapName(rel, PackRegime.ToInert)));
                }
                JsonIo.Save(Path.Combine(ptmp, "preserve.json"), desc);

                // swap в Preserve (вне Assets — чистый System.IO): старый сейв в сторону, новый на место
                SwapDir(ptmp, NexusPaths.PreserveDir(id));

                // выселяем State (внутри Assets; Unity увидит удаление на общем Refresh после Apply)
                EvictState(id);
                return Result.Done($"выключен «{id}» (сейв упакован в Preserve)");
            }
            catch (System.Exception ex) { return Fail(ptmp, ex.Message); }
        }

        // ---------------------------------------------------------------- restore

        /// <summary>Restore: снести Preserve + State и развернуть чистый дефолт из Plugins.</summary>
        public static Result RestoreDefault(PluginManifest m)
        {
            if (m == null) return Result.Fail("нет манифеста");

            var issues = Validate(m);
            if (issues.Count > 0)
                return Result.Fail($"плагин '{m.Id}' невалиден: {string.Join("; ", issues)}");

            // State подменяется атомарно; сейв удаляется осознанно (диалог выше), но только после
            // успешной развёртки — при сбое у человека остаются и старый State, и его сейв
            var r = DeployFromPlugins(m);
            if (!r.Ok) return r;
            DeleteDir(NexusPaths.PreserveDir(m.Id));
            return Result.Done($"сброшен к дефолту «{m.Id}» (v{m.Version})");
        }

        /// <summary>Очистка осиротевшего состояния (манифеста нет).</summary>
        public static Result CleanOrphan(string id)
        {
            try { EvictState(id); }
            catch (System.Exception ex) { return Result.Fail($"не удалось удалить State «{id}»: {ex.Message}"); }
            DeleteDir(NexusPaths.PreserveDir(id));
            return Result.Done($"удалено осиротевшее состояние «{id}»");
        }

        // -------------------------------------------------------------- validate

        /// <summary>Проверки перед развёрткой: маппинг полон/инертен, в Plugins нет
        /// живых импортируемых файлов.</summary>
        public static List<string> Validate(PluginManifest m)
        {
            var issues = new List<string>();
            var dir = PluginRoots.SourceDir(m.Id);

            foreach (var entry in m.Deploy ?? Enumerable.Empty<DeployEntry>())
            {
                if (entry == null) { issues.Add("пустая запись deploy"); continue; }

                var from = SafeRelative(entry.From, out var fromError);
                if (from == null) issues.Add($"deploy.from «{entry.From}»: {fromError}");
                var to = SafeRelative(entry.To, out var toError);
                if (to == null) issues.Add($"deploy.to «{entry.To}»: {toError}");
                else if (to == "deploy.json") issues.Add("deploy.to «deploy.json» занят служебным дескриптором");
                if (from == null) continue;

                if (!PackRegime.IsInert(from))
                    issues.Add($"исток '{entry.From}' не инертный (.txt)");
                if (!File.Exists(Path.Combine(dir, from)))
                    issues.Add($"истоковый файл отсутствует: {entry.From}");
            }

            // рекурсивно: живой файл в подпапке плагина так же «давит» на проект
            foreach (var rel in RelativeFiles(dir))
            {
                if (rel == "manifest.json") continue;
                if (PackRegime.IsImportable(Path.GetFileName(rel)))
                    issues.Add($"в Plugins живой импортируемый файл: {rel} (должен быть .txt)");
            }

            return issues;
        }

        /// <summary>Версия, из которой развёрнут сейв в Preserve (для проверки протухания); null если нет.</summary>
        public static string PreservedVersion(string id)
            => JsonIo.Load<DeployDescriptor>(Path.Combine(NexusPaths.PreserveDir(id), "preserve.json"))?.DeployedVersion;

        public static bool HasPreserve(string id)
            => File.Exists(Path.Combine(NexusPaths.PreserveDir(id), "preserve.json"));

        /// <summary>Есть ли непустая папка персональных настроек (для показа кнопки очистки).</summary>
        public static bool HasUserSettings(string id)
        {
            var dir = NexusPaths.UserDir(id);
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).GetEnumerator().MoveNext();
        }

        /// <summary>Затереть персональные настройки/кэш страницы (UserSettings/&lt;id&gt;).
        /// Намеренно отдельно от Restore — тот префы НЕ трогает; это единственный путь их снести.</summary>
        public static Result ClearUserSettings(string id)
        {
            var dir = NexusPaths.UserDir(id);
            if (!Directory.Exists(dir)) return Result.Done($"у «{id}» нет персональных настроек");
            DeleteDir(dir);
            return Result.Done($"очищены персональные настройки «{id}»");
        }

        // --------------------------------------------------------------- helpers

        private static string FreshTemp(string key)
        {
            var tmp = Path.Combine(NexusPaths.TempRoot, key);
            DeleteDir(tmp);
            Directory.CreateDirectory(tmp);
            return tmp;
        }

        private static void WriteDescriptor(string dir, DeployDescriptor desc)
            => JsonIo.Save(Path.Combine(dir, "deploy.json"), desc);

        // temp(вне Assets) → State/<id> (внутри Assets). Старый State сперва переименовывается в
        // скрытую соседнюю папку (Unity такие не импортирует), новый встаёт на его место, старый
        // удаляется. Сбой переноса — откат: старый возвращается, State остаётся целым-старым.
        // .meta самой папки State/<id> остаётся на месте — guid папки не меняется.
        private static void SwapIntoState(string id, string tmp)
        {
            Directory.CreateDirectory(NexusPaths.StateRoot);
            SwapDir(tmp, NexusPaths.StateDir(id));
        }

        /// <summary>Атомарная подмена папки <paramref name="target"/> готовой <paramref name="fresh"/>:
        /// старое → скрытый сосед «.&lt;имя&gt;.old~», новое → на место, старое удаляется; при сбое — откат.</summary>
        private static void SwapDir(string fresh, string target)
        {
            var parent = Directory.GetParent(target)!.FullName;
            Directory.CreateDirectory(parent);
            var old = OldDir(target);
            DeleteDir(old);   // хвост прошлого сбоя

            var hadOld = Directory.Exists(target);
            if (hadOld) Directory.Move(target, old);

            try
            {
                Directory.Move(fresh, target);
            }
            catch
            {
                if (hadOld && !Directory.Exists(target))
                {
                    try { Directory.Move(old, target); }
                    catch (System.Exception rollback) { NexusDiagnostics.Error($"откат подмены {target}", rollback); }
                }
                throw;
            }

            TryDeleteDir(old);
        }

        // выселить State/<id>: сперва в сторону (быстро и целиком), затем удалить; .meta папки — тоже,
        // чтобы Unity на Refresh не ругался на осиротевший meta. Без AssetDatabase.DeleteAsset —
        // Apply и так заканчивается одним Refresh, который увидит удаление.
        private static void EvictState(string id)
        {
            var stateDir = NexusPaths.StateDir(id);
            if (Directory.Exists(stateDir))
            {
                var old = OldDir(stateDir);
                DeleteDir(old);
                Directory.Move(stateDir, old);
                TryDeleteDir(old);
            }

            var meta = stateDir + ".meta";
            if (File.Exists(meta)) File.Delete(meta);
        }

        private static string OldDir(string target)
            => Path.Combine(Directory.GetParent(target)!.FullName, "." + Path.GetFileName(target) + ".old~");

        private static void TryDeleteDir(string dir)
        {
            try { DeleteDir(dir); }
            catch (System.Exception ex) { NexusDiagnostics.Swallowed("удаление " + dir + " (уберётся при следующей подмене)", ex); }
        }

        /// <summary>
        /// Относительный путь из манифеста → нормализованный («a/b.cs»); null + причина, если путь
        /// выводит за пределы папки: «..», абсолютный путь, корень диска, пустой сегмент.
        /// </summary>
        public static string SafeRelative(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path)) { error = "пустой путь"; return null; }

            var p = path.Trim().Replace('\\', '/');
            if (p.StartsWith("/") || p.IndexOf(':') >= 0 || Path.IsPathRooted(p))
            {
                error = "абсолютный путь или корень диска запрещён — нужен путь относительно папки плагина";
                return null;
            }

            var parts = p.Split('/');
            var clean = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") { error = "«..» запрещён — путь не должен выходить из папки"; return null; }
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    error = $"недопустимые символы в «{part}»";
                    return null;
                }
                clean.Add(part);
            }

            if (clean.Count == 0) { error = "пустой путь"; return null; }
            return string.Join("/", clean);
        }

        /// <summary>Все файлы папки рекурсивно — относительными путями через «/»; нет папки — пусто.</summary>
        private static List<string> RelativeFiles(string dir)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;

            var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                list.Add(Path.GetFullPath(f).Substring(root.Length + 1).Replace('\\', '/'));
            list.Sort(System.StringComparer.Ordinal);
            return list;
        }

        // правило упаковки (ToLive/ToInert) — только к имени файла, подпапки сохраняются
        private static string MapName(string relative, System.Func<string, string> map)
        {
            var slash = relative.LastIndexOf('/');
            return slash < 0 ? map(relative) : relative.Substring(0, slash + 1) + map(relative.Substring(slash + 1));
        }

        // копия байтами: BOM, окончания строк и бинарные файлы (иконки) переносятся как есть
        private static void CopyFile(string src, string dst)
        {
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, dst, overwrite: true);
        }

        // guid плагина по манифесту в его исходной папке; неизвестен — пусто (не id: это разные ключи)
        private static string KnownGuid(string id)
        {
            var manifest = Path.Combine(PluginRoots.SourceDir(id), "manifest.json");
            return File.Exists(manifest) ? ManifestIo.Load(manifest)?.Guid : null;
        }

        private static void DeleteDir(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        private static Result Fail(string tmp, string message)
        {
            DeleteDir(tmp);
            return Result.Fail(message);
        }
    }
}
