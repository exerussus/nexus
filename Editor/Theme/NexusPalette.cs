using System.Collections.Generic;
using Exerussus.Nexus.Abstractions;
using UnityEngine;

namespace Exerussus.Nexus.Theme
{
    /// <summary>
    /// Палитра — ДАННЫЕ: отображение семантических токенов в цвета И полный набор
    /// цветов на каждую РОЛЬ (с явными состояниями). Сменив палитру, меняем вид везде,
    /// кто спрашивает по токену или роли. Добавление палитры — это данные (тот же
    /// словарь), а не код.
    /// </summary>
    public sealed class NexusPalette
    {
        public string Name { get; }
        private readonly Dictionary<NexusToken, Color> _map;
        private readonly Dictionary<NexusRole, NexusRoleColors> _roles;

        public NexusPalette(string name,
                            Dictionary<NexusToken, Color> map,
                            Dictionary<NexusRole, NexusRoleColors> roles)
        {
            Name   = name;
            _map   = map   ?? new Dictionary<NexusToken, Color>();
            _roles = roles ?? new Dictionary<NexusRole, NexusRoleColors>();
        }

        /// <summary>Цвет токена; пропуск виден как маджента (а не молча чёрный).</summary>
        public Color Get(NexusToken token)
            => _map.TryGetValue(token, out var c) ? c : Color.magenta;

        /// <summary>Набор цветов роли; пропуск виден как маджента-набор.</summary>
        public NexusRoleColors Role(NexusRole role)
            => _roles.TryGetValue(role, out var c) ? c : Magenta();

        // ---- встроенные палитры ----

        /// <summary>Дефолтная мягкая тёмная палитра.</summary>
        public static NexusPalette SoftDark()
        {
            var white = new Color(0.96f, 0.97f, 0.99f);
            var dark  = new Color(0.10f, 0.10f, 0.12f);
            var dim   = new Color(0.62f, 0.62f, 0.66f);

            var map = new Dictionary<NexusToken, Color>
            {
                { NexusToken.BgHard,     new Color(0.16f, 0.16f, 0.18f) },
                { NexusToken.BgSoft,     new Color(0.21f, 0.21f, 0.24f) },
                { NexusToken.BgRaised,   new Color(0.26f, 0.26f, 0.30f) },
                { NexusToken.TextNormal, new Color(0.86f, 0.86f, 0.88f) },
                { NexusToken.TextDim,    dim },
                { NexusToken.Border,     new Color(0.30f, 0.30f, 0.34f) },
                { NexusToken.Accent,     new Color(0.40f, 0.62f, 0.92f) },
                { NexusToken.Selection,  new Color(0.26f, 0.30f, 0.40f) },
                { NexusToken.Hover,      new Color(0.30f, 0.31f, 0.36f) },
                { NexusToken.Pressed,    new Color(0.22f, 0.25f, 0.33f) },
                { NexusToken.Ok,         new Color(0.30f, 0.62f, 0.40f) },
                { NexusToken.Warning,    new Color(0.85f, 0.70f, 0.35f) },
                { NexusToken.Error,      new Color(0.85f, 0.42f, 0.40f) },
                { NexusToken.Muted,      new Color(0.40f, 0.42f, 0.48f) },
                { NexusToken.Orphan,     new Color(0.72f, 0.46f, 0.24f) },

                { NexusToken.SurfaceHover,  new Color(0.31f, 0.31f, 0.36f) },
                { NexusToken.Divider,       new Color(1f, 1f, 1f, 0.12f) },
                { NexusToken.AccentHover,   new Color(0.52f, 0.72f, 1.00f) },
                { NexusToken.AccentPressed, new Color(0.30f, 0.48f, 0.74f) },
                { NexusToken.AccentSoft,    new Color(0.40f, 0.62f, 0.92f, 0.14f) },
                { NexusToken.OnAccent,      new Color(0.97f, 0.98f, 1.00f) },
                { NexusToken.OkSoft,        new Color(0.30f, 0.62f, 0.40f, 0.14f) },
                { NexusToken.WarningSoft,   new Color(0.85f, 0.70f, 0.35f, 0.14f) },
                { NexusToken.ErrorSoft,     new Color(0.85f, 0.42f, 0.40f, 0.16f) },
                { NexusToken.TextFaint,     new Color(0.46f, 0.46f, 0.50f) },
                { NexusToken.Focus,         new Color(0.45f, 0.66f, 0.96f) },
            };

            var roles = new Dictionary<NexusRole, NexusRoleColors>
            {
                { NexusRole.Primary, Solid(new Color(0.30f, 0.50f, 0.85f), white) },
                { NexusRole.Common,  Solid(new Color(0.30f, 0.31f, 0.36f), new Color(0.86f, 0.86f, 0.88f)) },
                { NexusRole.Alarm,   Solid(new Color(0.78f, 0.34f, 0.32f), white) },
                { NexusRole.Success, Solid(new Color(0.30f, 0.60f, 0.40f), white) },
                { NexusRole.Warning, Solid(new Color(0.82f, 0.66f, 0.32f), dark) },
                { NexusRole.Info,    Solid(new Color(0.30f, 0.58f, 0.70f), white) },
                { NexusRole.Muted,   Solid(new Color(0.34f, 0.35f, 0.40f), dim) },
                { NexusRole.Ghost,   Ghost(new Color(0.45f, 0.65f, 0.95f)) },
            };

            return new NexusPalette("Soft Dark", map, roles);
        }

        /// <summary>Холодная тёмная альтернатива (демонстрирует подмену палитры).</summary>
        public static NexusPalette Graphite()
        {
            var white = new Color(0.95f, 0.96f, 0.97f);
            var dark  = new Color(0.08f, 0.09f, 0.10f);
            var dim   = new Color(0.58f, 0.60f, 0.63f);

            var map = new Dictionary<NexusToken, Color>
            {
                { NexusToken.BgHard,     new Color(0.11f, 0.12f, 0.13f) },
                { NexusToken.BgSoft,     new Color(0.15f, 0.16f, 0.18f) },
                { NexusToken.BgRaised,   new Color(0.20f, 0.21f, 0.23f) },
                { NexusToken.TextNormal, new Color(0.88f, 0.89f, 0.90f) },
                { NexusToken.TextDim,    dim },
                { NexusToken.Border,     new Color(0.24f, 0.25f, 0.27f) },
                { NexusToken.Accent,     new Color(0.36f, 0.74f, 0.70f) },
                { NexusToken.Selection,  new Color(0.20f, 0.32f, 0.32f) },
                { NexusToken.Hover,      new Color(0.24f, 0.25f, 0.28f) },
                { NexusToken.Pressed,    new Color(0.17f, 0.22f, 0.22f) },
                { NexusToken.Ok,         new Color(0.32f, 0.64f, 0.52f) },
                { NexusToken.Warning,    new Color(0.83f, 0.68f, 0.34f) },
                { NexusToken.Error,      new Color(0.83f, 0.40f, 0.40f) },
                { NexusToken.Muted,      new Color(0.36f, 0.38f, 0.41f) },
                { NexusToken.Orphan,     new Color(0.70f, 0.45f, 0.26f) },

                { NexusToken.SurfaceHover,  new Color(0.25f, 0.26f, 0.29f) },
                { NexusToken.Divider,       new Color(1f, 1f, 1f, 0.10f) },
                { NexusToken.AccentHover,   new Color(0.46f, 0.84f, 0.80f) },
                { NexusToken.AccentPressed, new Color(0.24f, 0.56f, 0.52f) },
                { NexusToken.AccentSoft,    new Color(0.36f, 0.74f, 0.70f, 0.14f) },
                { NexusToken.OnAccent,      new Color(0.05f, 0.10f, 0.10f) },
                { NexusToken.OkSoft,        new Color(0.32f, 0.64f, 0.52f, 0.14f) },
                { NexusToken.WarningSoft,   new Color(0.83f, 0.68f, 0.34f, 0.14f) },
                { NexusToken.ErrorSoft,     new Color(0.83f, 0.40f, 0.40f, 0.16f) },
                { NexusToken.TextFaint,     new Color(0.44f, 0.46f, 0.49f) },
                { NexusToken.Focus,         new Color(0.40f, 0.78f, 0.72f) },
            };

            var roles = new Dictionary<NexusRole, NexusRoleColors>
            {
                { NexusRole.Primary, Solid(new Color(0.26f, 0.62f, 0.58f), white) },
                { NexusRole.Common,  Solid(new Color(0.24f, 0.25f, 0.28f), new Color(0.88f, 0.89f, 0.90f)) },
                { NexusRole.Alarm,   Solid(new Color(0.74f, 0.32f, 0.32f), white) },
                { NexusRole.Success, Solid(new Color(0.30f, 0.62f, 0.50f), white) },
                { NexusRole.Warning, Solid(new Color(0.80f, 0.64f, 0.30f), dark) },
                { NexusRole.Info,    Solid(new Color(0.32f, 0.56f, 0.62f), white) },
                { NexusRole.Muted,   Solid(new Color(0.30f, 0.31f, 0.34f), dim) },
                { NexusRole.Ghost,   Ghost(new Color(0.40f, 0.78f, 0.72f)) },
            };

            return new NexusPalette("Graphite", map, roles);
        }

        // ---- построение наборов ролей (состояния заданы явно, но выводятся из базы) ----

        private static NexusRoleColors Solid(Color bg, Color fg) => new NexusRoleColors
        {
            Bg       = bg,
            Fg       = fg,
            Hover    = Lighten(bg, 0.08f),
            Pressed  = Darken(bg, 0.07f),
            Disabled = Desaturate(Darken(bg, 0.02f), 0.6f),
            Border   = Lighten(bg, 0.14f),
            Soft     = new Color(bg.r, bg.g, bg.b, 0.16f),
        };

        private static NexusRoleColors Ghost(Color accent) => new NexusRoleColors
        {
            Bg       = new Color(accent.r, accent.g, accent.b, 0f),
            Fg       = accent,
            Hover    = new Color(accent.r, accent.g, accent.b, 0.14f),
            Pressed  = new Color(accent.r, accent.g, accent.b, 0.24f),
            Disabled = new Color(accent.r, accent.g, accent.b, 0.30f),
            Border   = accent,
            Soft     = new Color(accent.r, accent.g, accent.b, 0.14f),
        };

        private static NexusRoleColors Magenta()
        {
            var m = Color.magenta;
            return new NexusRoleColors { Bg = m, Fg = m, Hover = m, Pressed = m, Disabled = m, Border = m, Soft = m };
        }

        private static Color Lighten(Color c, float a)
            => new Color(Mathf.Clamp01(c.r + a), Mathf.Clamp01(c.g + a), Mathf.Clamp01(c.b + a), c.a);

        private static Color Darken(Color c, float a) => Lighten(c, -a);

        private static Color Desaturate(Color c, float t)
        {
            var g = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
            return new Color(Mathf.Lerp(c.r, g, t), Mathf.Lerp(c.g, g, t), Mathf.Lerp(c.b, g, t), c.a);
        }
    }
}
