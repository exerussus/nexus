# Nexus

Движок редакторных фич Exerussus: разворачиваемые страницы-плагины, статические сервисы, тема.
Окно — `Exerussus/Nexus/Open`, управление плагинами — `Exerussus/Nexus/Manage`.

## Где Nexus ищет плагины

Плагин — папка `<id>/` с `manifest.json` и инертными исходниками (`*.cs.txt`, `*.uxml.txt`, …).
Папка, в которой лежат такие `<id>/`, — **корень плагинов**. Корни собираются из трёх источников
(по приоритету; при совпадении `id` побеждает первый):

1. **Встроенный** — `Editor/Plugins/` самого Nexus.
2. **Ручные пути** — Manage → «Корни плагинов» → «Add scan path…» (папка внутри проекта).
3. **По маркеру** — любая папка в `Assets` или в пакете (встроенном, git, registry), в которой лежит
   файл `nexus-plugins.json`:

   ```json
   { "schemaVersion": 1, "name": "Project Tools" }
   ```

### Маркер и поиск

- Поиск запускается **только вручную**: Manage → «Найти корни плагинов». Фоновых проходов нет,
  редактор не нагружается.
- Найденные корни пишутся в `ProjectSettings/Nexus.json` (поле `discoveredRoots`, asset-пути вида
  `Assets/…` или `Packages/<имя>/…`) — файл коммитится, поэтому коллеги получают корни вместе с git.
  Нажать кнопку нужно тому, кто добавил или переместил пакет с плагинами.
- Повторный поиск перезаписывает только найденные по маркеру корни; ручные пути не трогает.
- Корень, пропавший с диска, пропускается и подсвечивается в Manage до следующего поиска.
- Маркер — обычный json (TextAsset): в папках с `~` на конце Unity его не импортирует, и поиск
  его не увидит. Исходники плагинов и так инертны (`.txt`), прятать их в `~` не нужно.

## CLI

Команды в открытый редактор (Nexus → Manage → «CLI», по умолчанию включён). Клиент лежит в
`%LOCALAPPDATA%\Exerussus\Nexus\cli` — добавьте папку в PATH.

```
nexus list                                     открытые проекты
nexus --project MyGame help                    команды проекта
nexus --project MyGame analytics.views         представления отчёта
nexus --project MyGame analytics.report --open
nexus --project MyGame analytics.export --profile weekly
nexus --project MyGame --json analytics.export --format csv --from 2026-10-17
nexus --project MyGame analytics.query --view fights --group-by @build --aggs count,median:duration
nexus --project MyGame analytics.query --events "Shop.*" --type breakdown --group-by @event,@platform
```

Проект — `--project <имя папки|путь>` или текущая папка внутри проекта. Код выхода — код команды:
0 успех, 1 ошибка выполнения, 2 аргументы, 3 нет команды, 4 исключение, 5 таймаут, 6 редактор занят,
10 нет редактора, 11 проект не однозначен, 12 нет связи.

Своя команда страницы:

```csharp
[NexusCommand("mypage.do", "Что делает", Usage = "mypage.do <что> [--force]")]
private static int Do(NexusCommandContext ctx)
{
    var what = ctx.Arg(0) ?? throw new NexusCommandException("нужно <что>");
    ctx.Print("готово: " + what);
    ctx.Data = new { what };
    return NexusCommandContext.Exit.Ok;
}
```

Команда выполняется в главном потоке редактора, без окон и диалогов.
