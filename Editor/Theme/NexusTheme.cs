using System;
using Exerussus.Nexus.Abstractions;
using UnityEngine;

namespace Exerussus.Nexus.Theme
{
    /// <summary>
    /// Тема хаба: единственная активная палитра (выбор уровня редактора, явный
    /// владелец — этот модуль). Палитра подменяема (<see cref="NexusPalettes"/>),
    /// смена шлёт <see cref="Changed"/>, чтобы окна перерисовались; выбор персистит
    /// слой выше (UI ↔ Core), здесь — только активное значение.
    /// </summary>
    public static class NexusTheme
    {
        private static NexusPalette _active = NexusPalette.SoftDark();

        /// <summary>Палитра сменилась — окнам стоит перестроиться.</summary>
        public static event Action Changed;

        public static NexusPalette Active
        {
            get => _active;
            set
            {
                _active = value ?? NexusPalette.SoftDark();
                Changed?.Invoke();
            }
        }

        public static Color Get(NexusToken token) => _active.Get(token);

        public static NexusRoleColors GetRole(NexusRole role) => _active.Role(role);

        /// <summary>Адаптер темы для выдачи странице через Context (изоляция цела).</summary>
        public static IPageTheme PageTheme { get; } = new Provider();

        private sealed class Provider : IPageTheme
        {
            public Color Get(NexusToken token) => NexusTheme.Get(token);
            public NexusRoleColors GetRole(NexusRole role) => NexusTheme.GetRole(role);
        }
    }
}
