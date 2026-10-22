using System.Globalization;
using System.IO;
using System.Text;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Exerussus.Nexus.Theme
{
    /// <summary>
    /// Генерация USS-переменных палитр из их C#-описания — один источник правды. Файлы лежат в
    /// пакете (Editor/Theme/Uss/&lt;Palette&gt;.uss) и пересобираются пунктом меню, когда палитра
    /// поменялась (пакет должен быть локальным — git-пакет только для чтения).
    /// </summary>
    public static class NexusThemeUss
    {
        /// <summary>Имя переменной токена: BgHard → --nx-bg-hard.</summary>
        public static string Var(NexusToken token) => NexusClass.Var(token);

        /// <summary>Имя переменной роли: --nx-role-primary-hover.</summary>
        public static string Var(NexusRole role, string part) => "--nx-role-" + Kebab(role.ToString()) + "-" + part;

        public static string Build(NexusPalette palette)
        {
            var sb = new StringBuilder();
            sb.Append("/* Сгенерировано из NexusPalette «").Append(palette.Name).Append("» — не править руками.\n");
            sb.Append("   Exerussus/Nexus/Dev/Regenerate theme USS. */\n\n");
            sb.Append('.').Append(NexusTheme.RootClass).Append(" {\n");

            foreach (NexusToken token in System.Enum.GetValues(typeof(NexusToken)))
                sb.Append("    ").Append(Var(token)).Append(": ").Append(Css(palette.Get(token))).Append(";\n");

            sb.Append('\n');
            foreach (NexusRole role in System.Enum.GetValues(typeof(NexusRole)))
            {
                var c = palette.Role(role);
                sb.Append("    ").Append(Var(role, "bg")).Append(": ").Append(Css(c.Bg)).Append(";\n");
                sb.Append("    ").Append(Var(role, "fg")).Append(": ").Append(Css(c.Fg)).Append(";\n");
                sb.Append("    ").Append(Var(role, "hover")).Append(": ").Append(Css(c.Hover)).Append(";\n");
                sb.Append("    ").Append(Var(role, "pressed")).Append(": ").Append(Css(c.Pressed)).Append(";\n");
                sb.Append("    ").Append(Var(role, "border")).Append(": ").Append(Css(c.Border)).Append(";\n");
                sb.Append("    ").Append(Var(role, "disabled")).Append(": ").Append(Css(c.Disabled)).Append(";\n");
            }

            var m = palette.Metrics;
            sb.Append('\n');
            sb.Append("    --nx-r-s: ").Append(Px(m.RadiusS)).Append(";\n");
            sb.Append("    --nx-r-m: ").Append(Px(m.RadiusM)).Append(";\n");
            sb.Append("    --nx-r-l: ").Append(Px(m.RadiusL)).Append(";\n");
            sb.Append("    --nx-title-spacing: ").Append(m.UpperTitles ? "1.5px" : "0px").Append(";\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>
        /// Классы-токены (<see cref="NexusClass"/>): цвет текста, фона и рамки каждым токеном.
        /// От палитры не зависят — ссылаются на её переменные.
        /// </summary>
        public static string BuildTokenClasses()
        {
            var sb = new StringBuilder();
            sb.Append("/* Сгенерировано из NexusToken — не править руками. Exerussus/Nexus/Dev/Regenerate theme USS.\n");
            sb.Append("   Классы-токены: nx-fg--<token> (текст), nx-bg--<token> (фон), nx-bd--<token> (рамка). */\n");

            foreach (NexusToken token in System.Enum.GetValues(typeof(NexusToken)))
            {
                var v = Var(token);
                sb.Append('\n');
                sb.Append('.').Append(NexusClass.Fg(token)).Append(" { color: var(").Append(v).Append("); }\n");
                sb.Append('.').Append(NexusClass.Bg(token)).Append(" { background-color: var(").Append(v).Append("); }\n");
                sb.Append('.').Append(NexusClass.Border(token)).Append(" { border-color: var(").Append(v).Append("); }\n");
            }

            return sb.ToString();
        }

        [MenuItem("Exerussus/Nexus/Dev/Regenerate theme USS")]
        private static void Regenerate()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NexusThemeUss).Assembly);
            if (info == null || info.source == PackageSource.Git || info.source == PackageSource.Registry)
            {
                Debug.LogWarning("[Nexus] Пакет только для чтения — USS палитр генерируется в локальной копии пакета (file: или embedded).");
                return;
            }

            var dir = Path.Combine(info.resolvedPath, "Editor", "Theme", "Uss");
            Directory.CreateDirectory(dir);

            foreach (var palette in NexusPalettes.All)
            {
                if (string.IsNullOrEmpty(palette.UssName)) continue;
                File.WriteAllText(Path.Combine(dir, palette.UssName + ".uss"), Build(palette), new UTF8Encoding(false));
            }

            File.WriteAllText(Path.Combine(dir, "NexusTokens.uss"), BuildTokenClasses(), new UTF8Encoding(false));

            AssetDatabase.Refresh();
            Debug.Log($"[Nexus] USS палитр пересобран: {dir}");
        }

        public static string Css(Color c)
        {
            var r = Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f);
            var g = Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f);
            var b = Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f);
            if (c.a >= 0.999f) return $"#{r:X2}{g:X2}{b:X2}";
            return "rgba(" + r + ", " + g + ", " + b + ", " + c.a.ToString("0.###", CultureInfo.InvariantCulture) + ")";
        }

        private static string Px(float v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "px";

        private static string Kebab(string name)
        {
            var sb = new StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if (char.IsUpper(ch) && i > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(ch));
            }

            return sb.ToString();
        }
    }
}
