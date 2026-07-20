namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Контекст, который хаб выдаёт странице — единственная санкционированная «рука
    /// наружу». Через него страница шлёт статус, берёт персональные пути и шину.
    /// Достучаться до другой страницы или до внутренностей движка отсюда нельзя.
    /// </summary>
    public interface IPageContext
    {
        /// <summary>Шина общих событий между страницами.</summary>
        IPageMessageBus Bus { get; }

        /// <summary>Тема: цвета по семантическим токенам (вид следует за палитрой хаба).</summary>
        IPageTheme Theme { get; }

        /// <summary>Фабрика типовых элементов (кнопки/бейджи) в едином стиле — по ролям.</summary>
        IPageUi Ui { get; }

        /// <summary>Сообщить статус пользователю (показывается в тулбаре окна).</summary>
        void SetStatus(string text, StatusKind kind = StatusKind.Info);

        /// <summary>Путь к ПЕРСОНАЛЬНОМУ файлу настроек страницы
        /// (UserSettings/Exerussus.Nexus/&lt;id&gt;/&lt;file&gt;; git-ignored, переживает restore).</summary>
        string GetUserConfigPath(string file);

        /// <summary>Asset-путь к СОБСТВЕННОМУ развёрнутому файлу страницы (uxml/uss/иконка)
        /// по имени относительно её корня. Страница передаёт ТОЛЬКО своё имя файла —
        /// директорию (`State/&lt;id&gt;/`) знает хаб (инвариант #7: раскладкой владеет хаб,
        /// id переназначаем). НЕ для записи — это развёрнутая копия, её перетрёт redeploy.</summary>
        string GetDeployedAssetPath(string relativeName);

        /// <summary>Загрузить собственный развёрнутый ассет страницы по относительному
        /// имени, не зная, где хаб его разместил. Напр.
        /// <c>Context.LoadDeployedAsset&lt;VisualTreeAsset&gt;("DataBasePage.uxml")</c>.</summary>
        T LoadDeployedAsset<T>(string relativeName) where T : UnityEngine.Object;

        /// <summary>Ключ для SessionState/EditorPrefs, заскоупленный на эту страницу.</summary>
        string GetSessionKey(string sub);
    }
}
