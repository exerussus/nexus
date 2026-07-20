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
}
