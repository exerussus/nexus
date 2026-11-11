using System;
using System.IO;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Обновление пакета Nexus, не применённое в фоне — компонент сервера CLI (владелец — <see cref="NexusCliServer"/>,
    /// вызывается из его проверки раз в 30 с; CLI выключен — не работает).
    /// <para>Package Manager кладёт новую версию git-пакета в новую папку кэша и удаляет старую, а перекомпиляцию
    /// Unity в фоне откладывает до фокуса: работает старый код, который смотрит в удалённую папку (демон не
    /// обновляется, «нет Tools~/daemon/nexusd.dll»). Локальный пакет (<c>file:</c>) меняется на месте — видна
    /// только другая версия в <c>package.json</c>.</para>
    /// <para>Заметили — один раз за домен <c>AssetDatabase.Refresh</c> и перекомпиляция (как <c>compile</c> из CLI);
    /// застрявшую в фоне перезагрузку домена доводит будильник демона. В Play, во время компиляции и импорта,
    /// пока идут задания и задачи CLI — ждём следующей проверки. Состояние — только поля текущего домена.</para>
    /// </summary>
    internal static class NexusPackageWatch
    {
        private static string _root;
        private static string _name;
        private static string _loaded;
        private static bool _requested;

        internal static void Check()
        {
            if (_requested) return;
            try
            {
                if (_loaded == null)
                {
                    // первая проверка домена: запомнить, какая версия загружена и откуда
                    _root = NexusPaths.EngineAbsRoot;
                    var own = Read(Path.Combine(_root, "package.json"));
                    _name = (string)own?["name"] ?? "com.exerussus.nexus";
                    _loaded = (string)own?["version"] ?? "";
                    return;
                }

                if (_loaded.Length == 0) return; // движок не пакетом (папка в Assets) — компилируется сам

                var (disk, where) = DiskVersion();
                if (disk == null || disk == _loaded) return;
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;

                // идёт задание или задача CLI (задача — тоже задание вида task): перезагрузка домена посреди них
                // оборвала бы шаг и исказила отчёт — откладываем до следующей проверки, когда всё закончится
                var busy = NexusCliJobs.Running();
                if (busy != null)
                {
                    NexusDiagnostics.Trace("CLI", $"обновление пакета ({_loaded} → {disk}) ждёт конца задания {busy.Id} ({busy.Kind})");
                    return;
                }

                _requested = true;
                // в консоль всегда (не только Verbose): человек должен видеть, почему редактор перекомпилировался
                Debug.Log($"Nexus: пакет обновлён на диске ({_loaded} → {disk}, {where}), Unity не применила его в фоне — перекомпилирую");
                AssetDatabase.Refresh();
                CompilationPipeline.RequestScriptCompilation();
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: проверка обновления пакета", e);
            }
        }

        /// <summary>Версия пакета на диске: в загруженной папке, а если её удалили — в новой папке кэша. null — не видно.</summary>
        private static (string version, string where) DiskVersion()
        {
            var own = Path.Combine(_root, "package.json");
            if (File.Exists(own)) return ((string)Read(own)?["version"], _root);

            // старую папку удалили; новая появится, когда Package Manager закончит — до тех пор ждём
            var cache = Path.Combine(NexusPaths.ProjectRoot, "Library", "PackageCache");
            if (!Directory.Exists(cache)) return (null, null);
            foreach (var dir in Directory.GetDirectories(cache, _name + "@*"))
            {
                var version = (string)Read(Path.Combine(dir, "package.json"))?["version"];
                if (version != null) return (version == _loaded ? version + " (новая папка)" : version, dir);
            }

            return (null, null);
        }

        private static JObject Read(string path)
        {
            try
            {
                return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null; // файл пишется прямо сейчас — проверим через 30 с
            }
        }
    }
}
