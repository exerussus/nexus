using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;

namespace Exerussus.Nexus.Core
{
    /// <summary>Персональные настройки вида (что скрыто из сайдбара). Хранятся одним
    /// файлом на УРОВНЕ Nexus, не в папке страницы — поэтому «Clear cache» их не трогает.</summary>
    public sealed class NexusViewConfig
    {
        public List<string> Hidden { get; set; } = new List<string>();
    }

    /// <summary>
    /// Видимость страниц в сайдбаре. Модерируется ТОЛЬКО Nexus (не страницами):
    /// сокрытая страница работает штатно (развёрнута, компилируется, доступна по шине),
    /// её просто нельзя выбрать в сайдбаре. Это личная настройка вида, поэтому лежит в
    /// UserSettings (git-ignored, на работу редактора не влияет) и не идёт в State.
    ///
    /// Явный владелец состояния — этот модуль (а не «сервис со скрытым кэшем»):
    /// карта скрытых грузится из файла, пишется при изменении, <see cref="Changed"/>
    /// даёт окнам обновиться вживую.
    /// </summary>
    public static class NexusView
    {
        public static event Action Changed;

        private static HashSet<string> _hidden;

        private static string ConfigPath => Path.Combine(NexusPaths.UserRoot, "nexus-view.json");

        /// <summary>Видна ли страница в сайдбаре (по умолчанию — да).</summary>
        public static bool IsVisible(string id)
        {
            Ensure();
            return !_hidden.Contains(id);
        }

        /// <summary>Задать видимость; пишет файл и уведомляет окна только при реальном изменении.</summary>
        public static void SetVisible(string id, bool visible)
        {
            Ensure();
            var changed = visible ? _hidden.Remove(id) : _hidden.Add(id);
            if (!changed) return;
            Save();
            Changed?.Invoke();
        }

        private static void Ensure()
        {
            if (_hidden != null) return;
            var cfg = JsonIo.Load<NexusViewConfig>(ConfigPath);
            _hidden = new HashSet<string>(cfg?.Hidden ?? new List<string>(), StringComparer.Ordinal);
        }

        private static void Save()
            => JsonIo.Save(ConfigPath, new NexusViewConfig
            {
                Hidden = _hidden.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            });
    }
}
