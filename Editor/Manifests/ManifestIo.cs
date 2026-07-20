namespace Exerussus.Nexus.Manifests
{
    /// <summary>
    /// Чтение/запись манифеста плагина. Тонкая обёртка над <see cref="JsonIo"/>,
    /// чтобы у вызывающего кода (дискавери) был типизированный вход. Сама по себе —
    /// статический stateless-сервис, без удерживаемого состояния.
    /// </summary>
    public static class ManifestIo
    {
        /// <summary>Прочитать манифест плагина; null при ошибке/отсутствии.</summary>
        public static PluginManifest Load(string absolutePath)
            => JsonIo.Load<PluginManifest>(absolutePath);

        /// <summary>Записать манифест атомарно, сохранив неизвестные поля.</summary>
        public static bool Save(string absolutePath, PluginManifest manifest)
            => JsonIo.Save(absolutePath, manifest);
    }
}
