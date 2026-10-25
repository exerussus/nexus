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

### Nexus Daemon

Команды идут через **Nexus Daemon** (`nexusd`) — фоновый сервис, один на машину. Редактор с включённым CLI сам
запускает его (`Tools~/daemon/nexusd.dll`, нужен .NET 8 или новее; без него — dotnet из Unity 6). Демон:

- знает все проекты с включённым CLI (`nexus list`) и принимает вызов сразу — даже пока редактор перезагружает
  домен или спит в фоне; сам ждёт задания (refresh, Play, тесты) и отдаёт итог;
- ведёт файловый канал `NexusTemp/cli` для агентов без сети до localhost;
- сторожит главный поток редактора и будит его (см. «Файловый канал»);
- сам, без редактора, выполняет команды `dialog.*` — системные диалоги Windows, на которых встал редактор (см. ниже);
- слушает только `127.0.0.1` (порт 47800–47809), каждый запрос — с токеном из
  `%LOCALAPPDATA%\Exerussus\Nexus\tokens.json`; запросы из браузера отклоняются.

Прямой TCP к редактору мимо демона выключен (`nexus-cli.json` → `DirectTcp` — только для отладки).
CLI выключен — демон не запускается, файлов канала нет; выключен во всех проектах — демон выходит сам.
Состояние — `nexus daemon.status`, в подсказке «CLI» в Manage и в `nexus-fs diag`. Журнал —
`%LOCALAPPDATA%\Exerussus\Nexus\daemon.log`. HTTP API: `GET /v1/projects`, `POST /v1/calls`,
`GET /v1/calls/<id>?wait=20`, `GET /v1/projects/<id>/commands` (с `daemonCommands`), `GET /v1/projects/<id>/artifacts/<путь>`.

#### Системные диалоги редактора (`dialog.*`)

Модальное окно Windows (`#32770`: «Script Updating Consent», «Scene has been modified externally», «Save scene?»…)
останавливает главный поток Unity — и вместе с ним все команды CLI. Команды `dialog.*` выполняет **демон**, не
обращаясь к редактору, поэтому они отвечают и тогда, когда редактор висит.

- `dialog.list [--raw] [--no-shot]` — диалоги редактора: id (отпечаток окна, текста и кнопок — изменилось что-то,
  id другой), заголовок, текст, кнопки (`*` — по умолчанию), флажки, снимок `NexusTemp/dialogs/<id>.png`
  (PrintWindow, без экрана и фокуса; на заблокированном экране может выйти чёрным; хранятся сутки, не больше 30).
  `--raw` — все дочерние окна диалога и окна процесса (для разбора неизвестных диалогов). Ничего не нажимает.
- Кнопки видны, если они — окна Windows; у TaskDialog и окон, нарисованных самим редактором, — только заголовок
  и снимок.
- Модальное окно по-прежнему решает человек: демон его не закрывает.

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

### Файловый канал (агент без сети до редактора)

Агенту в VM (или в WSL) localhost недоступен — для него тот же CLI идёт через файлы проекта:
`<проект>/NexusTemp/` (Unity не импортирует и не чистит; внутри `.gitignore` со `*`). Канал ведёт демон.

```
python3 <проект>/NexusTemp/nexus-fs status
python3 <проект>/NexusTemp/nexus-fs refresh
python3 <проект>/NexusTemp/nexus-fs --no-wait tests.run   →  nexus-fs result <id>
```

- Вызов — файл: `cli/requests/<id>.json` → `cli/active/` (в работе, с фазой задания) → `cli/done/` (итог).
  Демон забирает вызов сразу (5 раз в секунду), выполняет тем же путём, что HTTP API.
- Сроки задаёт вызывающий: `--timeout` (предел выполнения), `--keep` (сколько хранить итог; без него клиент
  удаляет итог сам после чтения).
- Пульс `cli/alive.json` пишет демон раз в 2 с (`daemon: true`, `state` редактора: `ready`, `reloading`,
  `stalled`, `closed`; `phase`; `silent` — сколько молчит главный поток). Замер — не отвечает сам демон (код 13).
- Редактор встал — `nexus-fs` сам печатает диагноз (`nexus-fs diag` — полный, работает и без ответа редактора):
  - сторож — часть демона (окна и фокус — Windows). Пульс главного потока — `ping` раз в 2 с; молчит дольше 10 с —
    `cli/stall.json`: фаза, отвечает ли окно Unity (отвечает — почти наверняка модальное окно), на переднем ли
    оно плане, окна процесса с заголовками, ошибки и хвост `Editor.log`;
  - будильник: Unity в фоне не продолжает работу после перезагрузки домена, пока окно не в фокусе. Сторож будит
    её, если окно отвечает, Unity в фоне и нет диалога: `soft` (по умолчанию) — сообщение «приложение активно»
    без смены фокуса; `focus` — если мягкого мало, Unity на пару секунд на передний план, затем фокус возвращается;
    `off` — только диагноз. Настройка — `cli.wake off|soft|focus` или список рядом с «CLI» в Manage; попытки —
    в `cli/wake.json` и в `nexus-fs diag`;
  - метки домена `cli/domain.json` (`reloading → loaded → ready`, `failed`) и `cli/startup-error.txt` — видно,
    где оборвалась перезагрузка.
- Пути проекта в выводе переводятся в локальные (`--no-map` — нет): путь скриншота открывается сразу.
- Выключили CLI — `NexusTemp/cli` удаляется; `NexusTemp/screenshots` и `NexusTemp/tests` (артефакты) остаются.
- Клиент `nexus-fs` 1.0 работает и с демоном; `nexus-fs` 5 не ждёт перезагрузку сам — это делает демон.

### Задачи

Несколько команд одним вызовом: редактор ведёт шаги сам (и через перезагрузку домена), итог — отчёт по задаче
и по каждому шагу.

```
nexus task.run --steps "refresh; play.start --clear-logs; wait --seconds 2; screenshot; play.stop"
nexus task.run smoke --set scene=Main          сценарий ProjectSettings/Nexus/tasks/smoke.json
nexus task.run --list | tasks | task.status <id> | task.cancel <id>
```

```json
{
  "name": "smoke", "timeout": 900, "onError": "stop",
  "steps": [
    { "id": "build",  "command": "refresh" },
    { "id": "play",   "command": "play.start", "args": ["--clear-logs"] },
    { "id": "shot",   "command": "screenshot" },
    { "id": "errors", "command": "logs", "args": ["--errors", "--since", "${play.logSince}"],
      "expect": { "data": { "rows": 0 } } },
    { "id": "stop",   "command": "play.stop", "always": true }
  ]
}
```

- `onError` (задача или шаг): `stop` — после провала только шаги `always`; `continue` — дальше.
- `expect`: `exit`, `output` / `notOutput` (подстрока), `data` (поля данных команды).
- `${шаг.поле}` — из данных пройденного шага; `${args.ключ}` — из `--set`.
- Пока идёт задача, меняющие команды извне получают отказ; `--force` проходит (пометка в отчёте), читающие
  команды (`status`, `logs`, …) работают всегда. `"lock": false` — без блокировки.
- `wait --seconds N | --frames N | --idle` — служебный шаг.

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
