using System;
using System.IO;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Контекст одной страницы. Статус уходит в коллбэк окна; тема — через
    /// IPageTheme (страница не знает про сборку Theme); персональные пути — в
    /// UserSettings/Exerussus.Nexus/&lt;id&gt;; ключи сессии заскоуплены на id.
    /// </summary>
    public sealed class NexusPageContext : IPageContext
    {
        private readonly string _id;
        private readonly Action<string, StatusKind> _status;

        public IPageMessageBus Bus   { get; }
        public IPageTheme      Theme { get; }
        public IPageUi         Ui    { get; }

        public NexusPageContext(string id, IPageMessageBus bus, IPageTheme theme, IPageUi ui, Action<string, StatusKind> status)
        {
            _id     = id;
            Bus     = bus;
            Theme   = theme;
            Ui      = ui;
            _status = status;
        }

        public void SetStatus(string text, StatusKind kind = StatusKind.Info)
            => _status?.Invoke(text, kind);

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
