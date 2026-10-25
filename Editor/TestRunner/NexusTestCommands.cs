using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using Exerussus.Nexus.Deployment;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Exerussus.Nexus.TestRunner
{
    /// <summary>
    /// Тесты через CLI: <c>tests.run</c>, <c>tests.list</c>. Сборка компилируется, только если в проекте есть
    /// com.unity.test-framework — иначе команд нет («off means off» на уровне кода).
    /// </summary>
    /// <remarks>
    /// Запуск — задание: PlayMode-тесты перезагружают домен, поэтому обработчик результатов регистрируется заново
    /// на каждой загрузке, пока идёт задание тестов.
    /// </remarks>
    [InitializeOnLoad]
    internal static class NexusTestCommands
    {
        private const string Kind = "tests";
        private const string ListKind = "tests.list";
        private const int MaxFailures = 20;

        private static Callbacks _callbacks;
        private static TestRunnerApi _api;

        static NexusTestCommands()
        {
            NexusCliJobs.Register(Kind, Step);
            NexusCliJobs.Register(ListKind, _ => { });

            // результаты PlayMode приходят после перезагрузки домена — обработчик нужен и в новом домене
            if (NexusCliServer.ActiveInSession && NexusCliJobs.Running(Kind) != null) RegisterCallbacks();
        }

        private static TestRunnerApi Api
        {
            get
            {
                if (_api != null) return _api;
                _api = ScriptableObject.CreateInstance<TestRunnerApi>();
                _api.hideFlags = HideFlags.HideAndDontSave;
                return _api;
            }
        }

        [NexusCommand("tests.run", "Запустить тесты (EditMode по умолчанию) и дождаться итога; упавшие — код 1",
            Usage = "tests.run [--mode edit|play|all] [--filter <regex по полному имени>] [--test <полное имя,…>] [--assembly a,b] [--category c] [--save|--discard] [--timeout 1800]")]
        private static int Run(NexusCommandContext ctx)
        {
            if (EditorApplication.isPlaying) throw new NexusCommandException("Идёт Play — остановите: play.stop", NexusCommandContext.Exit.Failed);
            var running = NexusCliJobs.Running("refresh", "compile", "play.start", "play.stop", Kind);
            if (running != null) throw new NexusCommandException($"Идёт задание {running.Id} ({running.Kind}) — job.status {running.Id}", NexusCommandContext.Exit.Failed);
            if (EditorUtility.scriptCompilationFailed) throw new NexusCommandException("Ошибки компиляции — compile.errors", NexusCommandContext.Exit.Failed);

            // иначе Test Framework откроет модальный «сохранить сцены?» и заблокирует редактор
            NexusSceneCommands.HandleDirty(ctx.Flag("save"), ctx.Flag("discard"), revert: true);

            var filter = new Filter { testMode = ParseMode(ctx.Get("mode")) };
            var group = ctx.Get("filter");
            if (!string.IsNullOrEmpty(group)) filter.groupNames = new[] { group };
            var tests = ctx.GetList("test");
            if (tests.Count > 0) filter.testNames = tests.ToArray();
            var assemblies = ctx.GetList("assembly");
            if (assemblies.Count > 0) filter.assemblyNames = assemblies.ToArray();
            var categories = ctx.GetList("category");
            if (categories.Count > 0) filter.categoryNames = categories.ToArray();

            var job = NexusCliJobs.Start(Kind, ctx.GetInt("timeout", 1800), ctx);
            job.Set("mode", filter.testMode.ToString());
            NexusCliJobs.SetPhase(job, "подготовка");

            RegisterCallbacks();
            try
            {
                Api.Execute(new ExecutionSettings(filter));
            }
            catch (Exception e)
            {
                NexusCliJobs.Finish(job, NexusCommandContext.Exit.Failed, "✗ Тесты не запустились: " + e.Message + "\n");
                UnregisterCallbacks();
            }

            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("tests.list", "Список тестов (полные имена)", Usage = "tests.list [--mode edit|play] [--filter <подстрока>] [--max 200]", ReadOnly = true)]
        private static int List(NexusCommandContext ctx)
        {
            var mode = ParseMode(ctx.Get("mode"));
            if (mode == (TestMode.EditMode | TestMode.PlayMode)) throw new NexusCommandException("--mode edit или play");
            var filter = ctx.Get("filter");
            var max = Math.Max(1, ctx.GetInt("max", 200));
            var job = NexusCliJobs.Start(ListKind, 60, ctx);
            NexusCliJobs.SetPhase(job, "сбор списка");

            Api.RetrieveTestList(mode, root =>
            {
                var names = new List<string>();
                Collect(root, names);
                if (!string.IsNullOrEmpty(filter)) names.RemoveAll(n => n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0);
                var sb = new StringBuilder();
                for (var i = 0; i < names.Count && i < max; i++) sb.Append(names[i]).Append('\n');
                if (names.Count > max) sb.Append($"… ещё {names.Count - max} (--max)\n");
                sb.Append($"— тестов: {names.Count}\n");
                NexusCliJobs.Finish(job, NexusCommandContext.Exit.Ok, sb.ToString(), new { total = names.Count, tests = names.Count > max ? names.GetRange(0, max) : names });
            });
            return NexusCommandContext.Exit.Ok;
        }

        private static void Collect(ITestAdaptor node, List<string> names)
        {
            if (node == null) return;
            if (!node.HasChildren && !node.IsSuite)
            {
                names.Add(node.FullName);
                return;
            }

            if (node.Children == null) return;
            foreach (var c in node.Children) Collect(c, names);
        }

        private static TestMode ParseMode(string mode)
        {
            switch ((mode ?? "edit").ToLowerInvariant())
            {
                case "edit": case "editmode": return TestMode.EditMode;
                case "play": case "playmode": return TestMode.PlayMode;
                case "all": case "both": return TestMode.EditMode | TestMode.PlayMode;
                default: throw new NexusCommandException("--mode edit, play или all");
            }
        }

        private static void Step(NexusCliJob job)
        {
            // итог приходит в обработчик; здесь — только срок (его проверяет ядро)
        }

        // ---------------------------------------------------------------- результаты

        private static void RegisterCallbacks()
        {
            if (_callbacks != null) return;
            _callbacks = new Callbacks();
            TestRunnerApi.RegisterTestCallback(_callbacks);
        }

        private static void UnregisterCallbacks()
        {
            if (_callbacks == null) return;
            TestRunnerApi.UnregisterTestCallback(_callbacks);
            _callbacks = null;
        }

        private sealed class Callbacks : IErrorCallbacks
        {
            private int _total;
            private int _done;
            private int _failed;

            public void RunStarted(ITestAdaptor testsToRun)
            {
                var job = NexusCliJobs.Running(Kind);
                if (job == null) return;
                _total = testsToRun != null ? testsToRun.TestCaseCount : 0;
                _done = 0;
                _failed = 0;
                job.Set("total", _total.ToString(CultureInfo.InvariantCulture));
                NexusCliJobs.SetPhase(job, $"тесты: 0/{_total}");
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result == null || result.HasChildren) return;
                var job = NexusCliJobs.Running(Kind);
                if (job == null) return;
                if (_total == 0) int.TryParse(job.Get("total", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out _total);
                _done++;
                if (result.TestStatus == TestStatus.Failed) _failed++;
                NexusCliJobs.SetPhase(job, $"тесты: {_done}/{_total}" + (_failed > 0 ? $", упало {_failed}" : string.Empty));
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                var job = NexusCliJobs.Running(Kind);
                UnregisterCallbacks();
                if (job == null || result == null) return;

                var failures = new List<ITestResultAdaptor>();
                CollectFailures(result, failures);

                var passed = result.PassCount;
                var failed = result.FailCount;
                var skipped = result.SkipCount;
                var inconclusive = result.InconclusiveCount;
                var total = passed + failed + skipped + inconclusive;

                string xml = null;
                try
                {
                    var dir = NexusTempPaths.Ensure(NexusTempPaths.Tests);
                    xml = Path.Combine(dir, job.Id + ".xml").Replace('\\', '/');
                    TestRunnerApi.SaveResultToFile(result, xml);
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Swallowed("CLI: запись результатов тестов", e);
                    xml = null;
                }

                var sb = new StringBuilder();
                if (total == 0) sb.Append("✗ Ни одного теста не найдено (проверьте --filter/--assembly/--mode)\n");
                else if (failed == 0) sb.Append($"✓ Тесты: {passed} прошли" + (skipped > 0 ? $", пропущено {skipped}" : string.Empty) + $" за {NexusCliTextShim.Seconds(result.Duration)}\n");
                else sb.Append($"✗ Тесты: упало {failed} из {total} (прошли {passed}" + (skipped > 0 ? $", пропущено {skipped}" : string.Empty) + ")\n");

                var list = new List<object>();
                for (var i = 0; i < failures.Count && i < MaxFailures; i++)
                {
                    var f = failures[i];
                    var message = Cut(f.Message, 400);
                    var stack = Head(f.StackTrace, 3);
                    sb.Append("  ✗ ").Append(f.FullName).Append('\n');
                    if (message.Length > 0) sb.Append("    ").Append(message.Replace("\n", "\n    ")).Append('\n');
                    if (stack.Length > 0) sb.Append(stack).Append('\n');
                    list.Add(new { name = f.FullName, message, stack });
                }

                if (failures.Count > MaxFailures) sb.Append($"  … ещё {failures.Count - MaxFailures} упавших\n");
                if (xml != null) sb.Append("отчёт: ").Append(xml).Append('\n');

                var exit = total == 0 || failed > 0 ? NexusCommandContext.Exit.Failed : NexusCommandContext.Exit.Ok;
                NexusCliJobs.Finish(job, exit, sb.ToString(), new { total, passed, failed, skipped, inconclusive, duration = result.Duration, failures = list, report = xml });
            }

            public void OnError(string message)
            {
                var job = NexusCliJobs.Running(Kind);
                UnregisterCallbacks();
                if (job != null) NexusCliJobs.Finish(job, NexusCommandContext.Exit.Failed, "✗ Тесты прерваны: " + message + "\n");
            }

            private static void CollectFailures(ITestResultAdaptor node, List<ITestResultAdaptor> into)
            {
                if (node == null) return;
                if (!node.HasChildren)
                {
                    if (node.TestStatus == TestStatus.Failed) into.Add(node);
                    return;
                }

                foreach (var c in node.Children) CollectFailures(c, into);
            }
        }

        private static string Cut(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", string.Empty).Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        private static string Head(string stack, int lines)
        {
            if (string.IsNullOrEmpty(stack)) return string.Empty;
            var sb = new StringBuilder();
            var n = 0;
            foreach (var raw in stack.Replace("\r", string.Empty).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (n++ >= lines) break;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("    ").Append(line);
            }

            return sb.ToString();
        }

        private static class NexusCliTextShim
        {
            public static string Seconds(double s) => s.ToString(s < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " с";
        }
    }
}
