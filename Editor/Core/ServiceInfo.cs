namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Сведённая информация о сервисе. <see cref="CodeVersion"/> (из атрибута
    /// [Service]) — источник истины; <see cref="DeclaredVersion"/> (из манифеста) —
    /// кэш. Если они расходятся — манифест протух (<see cref="Stale"/>).
    /// </summary>
    public sealed class ServiceInfo
    {
        public string ServiceId       { get; set; }
        public string DeclaredVersion { get; set; }   // из service-manifest.json (может быть null)
        public string CodeVersion     { get; set; }   // из [Service] в скомпилированном коде (может быть null)
        public string TypeName        { get; set; }

        /// <summary>Версия для гейта: код (истина), иначе манифест.</summary>
        public string EffectiveVersion => CodeVersion ?? DeclaredVersion;

        /// <summary>Сервис реально присутствует в компиляции (код найден).</summary>
        public bool Present => CodeVersion != null;

        /// <summary>Манифест есть, код есть, но версии не совпали.</summary>
        public bool Stale =>
            !string.IsNullOrEmpty(DeclaredVersion) &&
            !string.IsNullOrEmpty(CodeVersion) &&
            DeclaredVersion != CodeVersion;
    }
}
