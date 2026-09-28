using System;
using System.IO;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Контекст одной страницы. Статус уходит в журнал ядра (<see cref="NexusStatusLog"/>)
    /// с именем страницы как источником; окно показывает журнал. Тема — через
    /// IPageTheme (страница не знает про сборку Theme); персональные пути — в
    /// UserSettings/Exerussus.Nexus/&lt;id&gt;; ключи сессии заскоуплены на id.
    /// </summary>
    public sealed class NexusPageContext : IPageContext
    {
        private readonly string _id;
        private readonly string _source;   // человекочитаемое имя страницы для журнала

        public IPageMessageBus Bus   { get; }
        public IPageTheme      Theme { get; }
        public IPageUi         Ui    { get; }

        public NexusPageContext(string id, string source, IPageMessageBus bus, IPageTheme theme, IPageUi ui)
        {
            _id     = id;
            _source = string.IsNullOrEmpty(source) ? id : source;
            Bus     = bus;
            Theme   = theme;
            Ui      = ui;
        }

        /// <summary>Первая строка — коротко (тулбар), остальное — подробности (окно сообщений).</summary>
        public void SetStatus(string text, StatusKind kind = StatusKind.Info)
            => NexusStatusLog.Add(_source, text, kind);

        public string GetUserConfigPath(string file)
            => Path.Combine(NexusPaths.UserConfigDir(_id), file);

        public string GetDeployedAssetPath(string relativeName)
            => NexusPaths.StateAssetPath(_id, relativeName);

        // UnityEngine.Object — полная квалификация: с using System здесь Object неоднозначен
        public T LoadDeployedAsset<T>(string relativeName) where T : UnityEngine.Object
            => AssetDatabase.LoadAssetAtPath<T>(GetDeployedAssetPath(relativeName));

        public string GetSessionKey(string sub)
            => $"Exerussus.Nexus.page.{_id}.{sub}";
    }
}
