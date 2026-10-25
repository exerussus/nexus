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

### Управление редактором

Встроено в ядро и работает, пока включён CLI (выключен — ни подписок на лог, ни буфера, ни заданий).

```
nexus status                                   Play/Edit, компиляция, сцена, ошибки лога, задания
nexus refresh                                  Refresh → импорт → компиляция → reload; итог — ошибки компиляции
nexus compile [--clean]                        перекомпилировать скрипты
nexus compile.errors [--warnings]              ошибки последней компиляции
nexus logs --errors                            только ошибки (коротко: первая строка, повторы схлопнуты)
nexus logs --grep Analytics,Fault --last 20    по ключевым словам (любое из), --all — все сразу, --not — исключить
nexus logs --errors --stack 4 --since 1532     со стеком, только новые после записи #1532
nexus logs --play --errors                     с последнего входа в Play
nexus logs.clear [--console]
nexus play.start [--scene Main] [--clear-logs] [--fail-on-errors]
nexus play.stop | play.pause [on|off] | play.step [--frames 10]
nexus scene.list [--all] | scene.open Main [--save|--discard] | scene.save [--as …]
nexus screenshot                               Game с UI → путь к PNG (по умолчанию ≤1280 px, --full — как есть)
nexus screenshot scene | screenshot window --window Inspector
nexus tests.run [--mode edit|play|all] [--filter Analytics] → итог и упавшие тесты (код 1)
nexus tests.list [--mode play]
nexus menu "Assets/Reimport All" | menu --list "Window"
nexus assets.find "t:Prefab Ship" | asset.reimport <путь> | select <путь> | save
nexus jobs | job.status <id> | job.cancel <id>
```

Команды AppCore (страница App, app-core 6.1+): `app.create page|popup|fragment <id>`, `app.delete`, `app.views`,
`app.uxml`, `app.controller`, `app.page.set`, `app.generate`, `app.validate`, `app.runner*`, `app.deck` — см. README AppCore.

**Задания.** Долгие команды (`refresh`, `compile`, `play.start/stop`, `tests.*`, `screenshot` Game) идут
заданиями: клиент печатает этапы и ждёт итога, в том числе через перезагрузку домена — файл экземпляра в это
время помечен `reloading`, клиент ждёт, а не считает редактор закрытым. `--no-wait` — только id задания;
`--timeout <с>` — общий срок. Одновременно идёт одно задание, меняющее редактор (остальные — код 1 с id идущего).

**Вывод экономный.** Лог по умолчанию — последние 30 строк, первая строка сообщения ≤300 символов, без стека,
одинаковые схлопнуты (`×N`). В конце — номер последней записи: следующий вызов `--since N` вернёт только новое.

**Фон.** `play.start` включает `Application.runInBackground` на сессию Play (игра идёт без фокуса редактора;
`--no-background` — не включать). `screenshot window` читает пиксели экрана — окно должно быть видно.
В Edit Mode снимок Game без UI Toolkit (рендер камер) — для снимка с UI запустите Play.

Проект — `--project <имя папки|путь>` или текущая папка внутри проекта. Код выхода — код команды:
0 успех, 1 ошибка выполнения, 2 аргументы, 3 нет команды, 4 исключение, 5 таймаут (задание ещё идёт), 6 редактор занят,
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
