namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Тон сообщения статуса, которое страница шлёт через Context.SetStatus.
    /// Значения стабильны — не переупорядочивать; расширять только добавлением в конец.
    /// </summary>
    public enum StatusKind
    {
        Info    = 0,   // нейтрально (по умолчанию)
        Warning = 1,   // предупреждение
        Error   = 2,   // ошибка/сбой
        Ok      = 3,   // успех/готово
    }
}
