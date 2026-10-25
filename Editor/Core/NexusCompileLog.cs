using System;
using System.Collections.Generic;
using Exerussus.Nexus.Abstractions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;

namespace Exerussus.Nexus.Core
{
    /// <summary>Итог последней компиляции скриптов.</summary>
    public sealed class NexusCompileResult
    {
        /// <summary>Номер компиляции в сессии редактора (растёт с каждой).</summary>
        public int Generation;

        /// <summary>UTC, ticks.</summary>
        public long Started;
        public long Finished;
        public bool Running;
        public int ErrorCount;
        public int WarningCount;
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<string> Assemblies = new List<string>();
    }

    /// <summary>
    /// Сообщения компилятора для CLI — компонент Core с явным владельцем (этот класс). Итог последней компиляции
    /// лежит в SessionState: успешная компиляция кончается перезагрузкой домена, и итог должен её пережить.
    /// </summary>
    /// <remarks>Работает, только пока запущен сервер CLI: выключен — ни подписок, ни записи в SessionState.</remarks>
    public static class NexusCompileLog
    {
        private const string StoreKey = "Exerussus.Nexus.Cli.Compile";
        private const int MaxMessages = 200;
        private const int MaxMessageChars = 2000;

        private static NexusCompileResult _current;
        private static bool _active;

        /// <summary>Итог последней (или идущей) компиляции; null — в этой сессии CLI компиляций не было.</summary>
        public static NexusCompileResult Last => _current ??= Load();

        public static int Generation => Last?.Generation ?? 0;

        internal static void Activate()
        {
            if (_active) return;
            _current = Load();
            CompilationPipeline.compilationStarted += OnStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssembly;
            CompilationPipeline.compilationFinished += OnFinished;
            _active = true;
        }

        internal static void Deactivate(bool reloading)
        {
            if (!_active)
            {
                if (!reloading) SessionState.EraseString(StoreKey);
                return;
            }

            _active = false;
            CompilationPipeline.compilationStarted -= OnStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssembly;
            CompilationPipeline.compilationFinished -= OnFinished;

            if (reloading) Save();
            else SessionState.EraseString(StoreKey);
            _current = null;
        }

        private static void OnStarted(object context)
        {
            _current = new NexusCompileResult
            {
                Generation = (Last?.Generation ?? 0) + 1,
                Started = DateTime.UtcNow.Ticks,
                Running = true,
            };
            Save();
        }

        private static void OnAssembly(string assembly, CompilerMessage[] messages)
        {
            var r = _current ??= new NexusCompileResult { Generation = 1, Started = DateTime.UtcNow.Ticks, Running = true };
            r.Assemblies.Add(System.IO.Path.GetFileNameWithoutExtension(assembly ?? string.Empty));
            if (messages == null) return;

            foreach (var m in messages)
            {
                if (m.type == CompilerMessageType.Error)
                {
                    r.ErrorCount++;
                    if (r.Errors.Count < MaxMessages) r.Errors.Add(Cut(m.message));
                }
                else if (m.type == CompilerMessageType.Warning)
                {
                    r.WarningCount++;
                    if (r.Warnings.Count < MaxMessages) r.Warnings.Add(Cut(m.message));
                }
            }
        }

        private static void OnFinished(object context)
        {
            var r = _current;
            if (r == null) return;
            r.Running = false;
            r.Finished = DateTime.UtcNow.Ticks;
            Save();
        }

        private static void Save()
        {
            try
            {
                if (_current == null) return;
                SessionState.SetString(StoreKey, JsonConvert.SerializeObject(_current, Formatting.None));
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: сохранение итога компиляции", e);
            }
        }

        private static NexusCompileResult Load()
        {
            try
            {
                var raw = SessionState.GetString(StoreKey, string.Empty);
                return string.IsNullOrEmpty(raw) ? null : JsonConvert.DeserializeObject<NexusCompileResult>(raw);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: чтение итога компиляции", e);
                return null;
            }
        }

        private static string Cut(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", string.Empty);
            return s.Length <= MaxMessageChars ? s : s.Substring(0, MaxMessageChars) + "…";
        }
    }
}
