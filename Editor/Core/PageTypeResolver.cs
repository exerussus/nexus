using System;
using Exerussus.Nexus.Abstractions;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Резолвит ИЗВЕСТНЫЙ typeName из манифеста в <see cref="Type"/>. Это резолюция
    /// указателя, а НЕ дискавери: мы не перебираем сборки, чтобы «найти страницы»
    /// (это делает <see cref="PluginDiscovery"/> по манифестам) — мы ищем конкретный,
    /// уже названный тип, чтобы его инстанцировать. Статический stateless-сервис.
    /// </summary>
    public static class PageTypeResolver
    {
        public static Type Resolve(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            var direct = Type.GetType(typeName);
            if (direct != null) return direct;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeName, throwOnError: false);
                if (t != null) return t;
            }

            // не нашли ни в одной загруженной сборке — обычно значит «код ещё не скомпилирован»
            NexusDiagnostics.Trace("Резолв типа",
                $"тип '{typeName}' не найден ни в одной загруженной сборке");
            return null;
        }
    }
}
