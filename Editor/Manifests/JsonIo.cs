using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace Exerussus.Nexus.Manifests
{
    /// <summary>
    /// Единый JSON-ввод/вывод для всех текстовых документов Nexus (манифесты,
    /// deploy.json, preserve.json). Статический stateless-сервис.
    ///
    /// Настройки заданы один раз: camelCase известных полей, дословное сохранение
    /// неизвестных ключей (через <see cref="JsonExtensionData"/> в моделях),
    /// Indented + стабильный порядок — чтобы git-дифф появлялся только при реальном
    /// изменении смысла. Запись атомарна (temp → replace).
    /// </summary>
    public static class JsonIo
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting        = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver  = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy(processDictionaryKeys: false, overrideSpecifiedNames: false),
            },
        };

        public static T Load<T>(string absolutePath) where T : class
        {
            try
            {
                if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                    return null;
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(absolutePath), Settings);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] Не удалось прочитать '{absolutePath}': {ex.Message}");
                return null;
            }
        }

        public static bool Save<T>(string absolutePath, T value)
        {
            try
            {
                var dir = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(value, Settings);
                var tmp  = absolutePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(absolutePath)) File.Replace(tmp, absolutePath, null);
                else                           File.Move(tmp, absolutePath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] Не удалось записать '{absolutePath}': {ex.Message}");
                return false;
            }
        }
    }
}
