using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Exerussus.Nexus.Manifests
{
    /// <summary>
    /// Маркер корня плагинов — файл <see cref="FileName"/> в папке, дочерние папки которой
    /// устроены как Plugins/ (&lt;id&gt;/manifest.json). Позволяет хранить плагины где угодно
    /// (в Assets, во встроенном или git-пакете) без ручной настройки путей: хаб находит
    /// маркеры по кнопке «Найти корни плагинов» и запоминает их в ProjectSettings/Nexus.json.
    /// </summary>
    /// <remarks>
    /// Маркер — обычный json (импортируется как TextAsset), поэтому его видит AssetDatabase.
    /// В папках с «~» на конце (их Unity не импортирует) маркер не найдётся.
    /// </remarks>
    public sealed class PluginRootMarker
    {
        public const string FileName = "nexus-plugins.json";

        public int    SchemaVersion { get; set; } = 1;
        public string Name          { get; set; }   // человекочитаемое имя набора плагинов
        public string Description   { get; set; }   // опционально

        /// <summary>Все неизвестные поля — сохраняются и пишутся обратно дословно.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; }
    }
}
