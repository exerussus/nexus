namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Раздел сайдбара, в котором показывается страница. Числовые значения
    /// стабильны — на них могут опираться сортировка и сериализация, поэтому
    /// не переупорядочивать.
    /// </summary>
    public enum PageCategory
    {
        Infrastructure = 0,
        Game           = 1,
    }
}
