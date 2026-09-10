# Logs

Демонстрационная страница Nexus и заодно проверка **markdown-рендера** в окне *Manage*.

## Что показывает

- статус через `Context.SetStatus` (включая `StatusKind.Ok`)
- подписку и публикацию события на шине `Context.Bus`
- загрузку своего UXML через `Context.LoadDeployedAsset`
- реакцию на фокус окна (`OnFocus` / `OnUnfocus`)

## Порядок интеграции

1. включить в Manage и нажать Apply
2. открыть рабочее окно и выбрать Logs
3. кликнуть по строке плагина, чтобы увидеть этот README

## Пример кода

```
public override VisualElement BuildUI()
{
    return new Label("Logs");
}
```

> Иконка вкладки берётся из `page_logo.png`, текст — из `README.md`.
> Оба читает хаб из `Plugins/logs/`; в `State/` они не деплоятся.

Подробнее — [документация Nexus](https://example.com/nexus).

---

Поддерживаемый markdown описан в скилле Nexus.
