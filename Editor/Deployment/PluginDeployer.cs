using System.Collections.Generic;
using System.IO;
using System.Linq;
using Exerussus.Nexus.Manifests;
using UnityEditor;

namespace Exerussus.Nexus.Deployment
{
    /// <summary>
    /// Файловые механики развёртки. Статический stateless-сервис: всё состояние —
    /// на диске. Атомарность через temp ВНЕ Assets → swap внутрь Assets, так что
    /// State/&lt;id&gt; в любой момент целый-старый или целый-новый.
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
                foreach (var entry in m.Deploy ?? Enumerable.Empty<DeployEntry>())
                {
                    var src = Path.Combine(PluginRoots.SourceDir(m.Id), entry.From);
                    if (!File.Exists(src))
                        return Fail(tmp, $"истоковый файл не найден: {entry.From}");
                    File.WriteAllText(Path.Combine(tmp, entry.To), File.ReadAllText(src));
                    files.Add(entry.To);
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
                foreach (var inert in Directory.EnumerateFiles(preDir))
                {
                    var name = Path.GetFileName(inert);
                    if (name == "preserve.json") continue;
                    File.WriteAllText(Path.Combine(tmp, PackRegime.ToLive(name)), File.ReadAllText(inert));
                }

                // переносим служебный дескриптор сейва в deploy.json
                var desc = JsonIo.Load<DeployDescriptor>(Path.Combine(preDir, "preserve.json"))
                           ?? new DeployDescriptor { PluginGuid = m.Guid, DeployedVersion = m.Version };
                desc.Files = Directory.EnumerateFiles(tmp).Select(f => Path.GetFileName(f))
                                      .Where(n => n != "deploy.json").ToList();
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
                       ?? new DeployDescriptor { PluginGuid = id };

            var ptmp = FreshTemp(id + ".preserve");
            try
            {
                foreach (var file in Directory.EnumerateFiles(stateDir))
                {
                    var name = Path.GetFileName(file);
                    if (name == "deploy.json") continue;           // дескриптор пересоздадим
                    File.WriteAllText(Path.Combine(ptmp, PackRegime.ToInert(name)), File.ReadAllText(file));
                }
                JsonIo.Save(Path.Combine(ptmp, "preserve.json"), desc);

                // swap в Preserve (вне Assets — чистый System.IO)
                var preDir = NexusPaths.PreserveDir(id);
                DeleteDir(preDir);
                Directory.CreateDirectory(Directory.GetParent(preDir)!.FullName);
                Directory.Move(ptmp, preDir);

                // выселяем State (внутри Assets — через AssetDatabase, чистит .meta + рекомпил)
                DeleteStateAsset(id);
                return Result.Done($"выключен «{id}» (сейв упакован в Preserve)");
            }
            catch (System.Exception ex) { return Fail(ptmp, ex.Message); }
        }

        // ---------------------------------------------------------------- restore

        /// <summary>Restore: снести Preserve + State и развернуть чистый дефолт из Plugins.</summary>
        public static Result RestoreDefault(PluginManifest m)
        {
            if (m == null) return Result.Fail("нет манифеста");
            DeleteDir(NexusPaths.PreserveDir(m.Id));   // сейв удаляется осознанно (диалог выше)
            DeleteStateAsset(m.Id);
            var r = DeployFromPlugins(m);
            return r.Ok ? Result.Done($"сброшен к дефолту «{m.Id}» (v{m.Version})") : r;
        }

        /// <summary>Очистка осиротевшего состояния (манифеста нет).</summary>
        public static Result CleanOrphan(string id)
        {
            DeleteStateAsset(id);
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
                if (!PackRegime.IsInert(entry.From))
                    issues.Add($"исток '{entry.From}' не инертный (.txt)");
                if (!File.Exists(Path.Combine(dir, entry.From)))
                    issues.Add($"истоковый файл отсутствует: {entry.From}");
            }

            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(f);
                    if (name == "manifest.json") continue;
                    if (PackRegime.IsImportable(name))
                        issues.Add($"в Plugins живой импортируемый файл: {name} (должен быть .txt)");
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

        // temp(вне Assets) → State/<id> (внутри Assets); старый State выселяем через AssetDatabase
        private static void SwapIntoState(string id, string tmp)
        {
            DeleteStateAsset(id);
            Directory.CreateDirectory(NexusPaths.StateRoot);
            Directory.Move(tmp, NexusPaths.StateDir(id));
        }

        private static void DeleteStateAsset(string id)
        {
            if (Directory.Exists(NexusPaths.StateDir(id)))
                AssetDatabase.DeleteAsset(NexusPaths.StateAssetDir(id));
            // подстраховка, если по какой-то причине осталась голая папка
            DeleteDir(NexusPaths.StateDir(id));
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
