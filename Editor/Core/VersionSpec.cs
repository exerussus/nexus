namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Версии и диапазоны Nexus. Версия — "major.minor". Диапазон требования —
    /// тоже "major.minMinor", но трактуется как ПРАВИЛО: мажор точный, минор снизу.
    /// Пример: требование "2.1" удовлетворяется сервисом "2.4" (2==2, 4&gt;=1),
    /// но не "3.0" (мажор сменился) и не "2.0" (минор ниже).
    /// </summary>
    public readonly struct SemVer
    {
        public readonly int Major;
        public readonly int Minor;
        public SemVer(int major, int minor) { Major = major; Minor = minor; }

        public static bool TryParse(string s, out SemVer v)
        {
            v = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Trim().Split('.');
            if (parts.Length < 1) return false;
            if (!int.TryParse(parts[0], out var major)) return false;
            var minor = 0;
            if (parts.Length >= 2 && !int.TryParse(parts[1], out minor)) return false;
            v = new SemVer(major, minor);
            return true;
        }

        public override string ToString() => $"{Major}.{Minor}";
    }

    public readonly struct VersionRange
    {
        public readonly int Major;     // должен совпасть точно
        public readonly int MinMinor;  // минор не ниже
        public VersionRange(int major, int minMinor) { Major = major; MinMinor = minMinor; }

        public static bool TryParse(string s, out VersionRange r)
        {
            r = default;
            if (!SemVer.TryParse(s, out var v)) return false;
            r = new VersionRange(v.Major, v.Minor);
            return true;
        }

        public bool Satisfies(SemVer v) => v.Major == Major && v.Minor >= MinMinor;

        public override string ToString() => $"{Major}.{MinMinor}+ (мажор {Major})";
    }

    /// <summary>
    /// Версия UPM-пакета по semver 2.0: «major.minor.patch[-pre.release][+build]» (недостающие
    /// minor/patch — нули). Сравнение по правилам semver: числа по порядку, затем pre-release —
    /// релиз старше любой своей pre-release версии, идентификаторы сравниваются по точкам (числовые
    /// — как числа и младше буквенных, буквенные — ординально), более длинный набор старше. Build
    /// в сравнении не участвует. Для проверки минимальной версии пакета (<c>packageRequires[].version</c>).
    /// </summary>
    public readonly struct PackageVersion
    {
        public readonly long     Major;
        public readonly long     Minor;
        public readonly long     Patch;
        public readonly string[] Pre;   // пусто — релиз

        private PackageVersion(long major, long minor, long patch, string[] pre)
        {
            Major = major; Minor = minor; Patch = patch; Pre = pre ?? System.Array.Empty<string>();
        }

        public static bool TryParse(string s, out PackageVersion v)
        {
            v = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var text = s.Trim();
            if (text.StartsWith("v") || text.StartsWith("V")) text = text.Substring(1);

            var plus = text.IndexOf('+');
            if (plus >= 0) text = text.Substring(0, plus);

            string[] pre = null;
            var dash = text.IndexOf('-');
            if (dash >= 0)
            {
                var tail = text.Substring(dash + 1);
                text = text.Substring(0, dash);
                if (tail.Length == 0) return false;
                pre = tail.Split('.');
                foreach (var id in pre)
                    if (id.Length == 0) return false;
            }

            var parts = text.Split('.');
            if (parts.Length < 1 || parts.Length > 3) return false;
            var nums = new long[3];
            for (var i = 0; i < parts.Length; i++)
                if (!long.TryParse(parts[i], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out nums[i]))
                    return false;

            v = new PackageVersion(nums[0], nums[1], nums[2], pre);
            return true;
        }

        public int CompareTo(PackageVersion other)
        {
            var c = Major.CompareTo(other.Major);
            if (c != 0) return c;
            if ((c = Minor.CompareTo(other.Minor)) != 0) return c;
            if ((c = Patch.CompareTo(other.Patch)) != 0) return c;

            var a = Pre ?? System.Array.Empty<string>();
            var b = other.Pre ?? System.Array.Empty<string>();
            if (a.Length == 0 || b.Length == 0) return b.Length.CompareTo(a.Length);   // релиз старше pre-release

            for (var i = 0; i < a.Length && i < b.Length; i++)
            {
                var an = long.TryParse(a[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ai);
                var bn = long.TryParse(b[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bi);
                if (an && bn) c = ai.CompareTo(bi);
                else if (an) c = -1;
                else if (bn) c = 1;
                else c = string.CompareOrdinal(a[i], b[i]);
                if (c != 0) return c < 0 ? -1 : 1;
            }

            return a.Length.CompareTo(b.Length);
        }

        public override string ToString()
            => Pre == null || Pre.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{string.Join(".", Pre)}";
    }
}
