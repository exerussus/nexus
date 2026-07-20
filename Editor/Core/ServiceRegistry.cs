using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Реестр сервисов. Статический stateless-сервис: каждый вызов сканирует заново.
    ///
    /// Два источника: манифесты на диске (декларация, level-1) и атрибуты [Service]
    /// в скомпилированных сборках (истина, level-2). Для сервисов рефлексия типов
    /// допустима — они не выселяются (либо есть в компиляции, либо нет), так что
    /// тут она не «дискавери выключенного», а чтение присутствующего контракта.
    /// </summary>
    public static class ServiceRegistry
    {
        public static List<ServiceInfo> Discover()
        {
            var byId = new Dictionary<string, ServiceInfo>(StringComparer.Ordinal);

            // level-1: манифесты (declared)
            if (Directory.Exists(NexusPaths.ServicesRoot))
                foreach (var dir in Directory.EnumerateDirectories(NexusPaths.ServicesRoot))
                {
                    var manifest = JsonIo.Load<ServiceManifest>(
                        Path.Combine(dir, "service-manifest.json"));
                    if (manifest?.ServiceId == null) continue;

                    byId[manifest.ServiceId] = new ServiceInfo
                    {
                        ServiceId       = manifest.ServiceId,
                        DeclaredVersion = manifest.Version,
                    };
                }

            // level-2: код (truth) — заполняем CodeVersion/TypeName, добавляем code-only
            foreach (var (id, version, typeName) in ScanCode())
            {
                if (!byId.TryGetValue(id, out var info))
                    byId[id] = info = new ServiceInfo { ServiceId = id };
                info.CodeVersion = version;
                info.TypeName    = typeName;
            }

            return new List<ServiceInfo>(byId.Values);
        }

        public static ServiceInfo Find(string serviceId)
        {
            foreach (var s in Discover())
                if (string.Equals(s.ServiceId, serviceId, StringComparison.Ordinal))
                    return s;
            return null;
        }

        // перебор [Service]-атрибутов в загруженных сборках (с защитой от битых сборок)
        private static IEnumerable<(string id, string version, string typeName)> ScanCode()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    var attr = t.GetCustomAttribute<ServiceAttribute>(inherit: false);
                    if (attr?.Id == null) continue;
                    yield return (attr.Id, attr.Version, t.FullName);
                }
            }
        }
    }
}
