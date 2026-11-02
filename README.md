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
nexus page.list | page.open world [--focus]     страницы окна Nexus; показать (по id или имени, без фокуса)
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
`<проект>/NexusTemp/` (Unity не импортирует и не чистит; внутри `.gitignore` со `*`). Канал `NexusTemp/cli` ведёт
демон по стандарту **efc/1** (файловый канал: репозиторий efc, `SPEC.md`). Клиенты демон кладёт рядом:

```
<проект>/NexusTemp/efc --channel <проект>/NexusTemp/cli call refresh      клиент efc (Linux, статический)
<проект>/NexusTemp/efc --channel <проект>/NexusTemp/cli --no-wait call tests.run   →  efc wait <id>
<проект>/NexusTemp/efc --channel <проект>/NexusTemp/cli batch steps.json  пакет с шагами (ниже)
python3 <проект>/NexusTemp/nexus-fs status                                  прежний клиент, тоже работает
```

- Вызов — файл: `cli/requests/<id>.json` → `cli/active/` (принят, фаза задания) → `cli/done/` (итог).
  Демон забирает вызов сразу (опрос 20 раз в секунду, по порядку id), выполняет тем же путём, что HTTP API.
  Повтор id не выполняется (и после перезапуска демона), брошенный запрос (старше своего срока + 60 с) — тоже.
- Паспорт `cli/channel.json`: команды редактора (из `bridge.hello`) и демона, лимиты, `howToStart`, контрольная
  сумма клиента `efc`, `instance` = `nexusd@<машина>:<путь проекта>`. Чужой паспорт (проект скопирован с другой
  машины) — канал не обслуживается, пульс `degraded`; удалите `NexusTemp/cli/channel.json` — демон подхватит канал.
- Итог по efc: `ok`, `exitCode`, `error`, `reason` (`expired`, `cancelled`, `server-restarted`, `busy`, `unknown-command`,
  …), `data` (данные команды), `expires`; для nexus-fs 5 — прежние `result`, `project`, `job`.
- **Корзина вместо удаления.** Агенту в VM удалять в подключённой папке часто нельзя. Прочитанный итог клиент
  удаляет, а если нельзя — переносит в `cli/trash/`; невзятый запрос отзывает только переносом (тот же rename, что
  у демона, — гонки нет). Корзину чистит демон: команда `trash.clean [--dry]` (без редактора, агенту можно без
  вопросов; клиенты шлют её сами при 50+ файлах) и раз в сутки — файлы старше суток.
- **Отмена** — `efc cancel <id>` (`cli/cancel/<id>.json`): ещё в очереди или ждёт редактор — итог 7; идёт задание —
  демон шлёт `job.cancel`, итог 7 по его завершении; команда уже выполнилась — обычный итог с `cancelIgnored`.
- **Пакеты с шагами** ведёт сам демон: каждый шаг — обычный вызов (ждёт задание, переживает перезагрузку домена).
  `${шаг.путь}` — из `data` прошлых шагов (и `exitCode`, `output`, `ok`), `expect` (`exit`, `output`, `notOutput`,
  `data`), `always` (уборка после ошибки или отмены), `onError` и `timeout` у шага; итог — `items[]` по шагам.
  `lock: true` (`efc --lock`) — пока пакет идёт, чужие меняющие команды получают отказ (efc — код 6 и `busyWith`,
  nexus-fs и HTTP — код 1, как от задачи); читающие команды, `trash.clean` и `--force` проходят. Задачи
  (`task.run`, ниже) — как прежде, их ведёт Unity.

```json
{ "batch": [
  { "id": "build", "command": "refresh" },
  { "id": "play",  "command": "play.start", "args": ["--clear-logs"] },
  { "id": "errs",  "command": "logs", "args": ["--errors", "--since", "${play.logSince}"], "expect": { "data": { "rows": 0 } } },
  { "id": "stop",  "command": "play.stop", "always": true } ] }
```

- Пульс `cli/alive.json` пишет демон раз в 2 с: `state` по efc (`ready`; `busy` — редактор перезагружает домен,
  вызовы принимаются; `degraded` — редактор завис или закрыт, канал чужой), в `detail` — `editor` (`ready`,
  `reloading`, `stalled`, `closed`), `phase`, `silent`, `lockedBy`. Замер — не отвечает сам демон (код 13).
- `ping` в файловом канале отвечает демон (без редактора); `help` — команды редактора и демона.
- Редактор встал — клиент печатает диагноз (`nexus-fs diag`, `efc diag` — работают и без ответа редактора):
  - сторож — часть демона (окна и фокус — Windows). Пульс главного потока — `ping` раз в 2 с; молчит дольше 10 с —
    `cli/stall.json`: фаза, отвечает ли окно Unity (отвечает — почти наверняка модальное окно), на переднем ли
    оно плане, окна процесса с заголовками, ошибки и хвост `Editor.log`;
  - будильник: Unity в фоне не продолжает работу после перезагрузки домена, пока процесс не станет активным
    приложением. Сторож будит её, если окно отвечает, Unity в фоне, нет диалога и вы ≥ 1,5 с не трогаете мышь и
    клавиатуру (тронули посреди пробуждения — фокус вам сразу). Режимы:
    - `quiet` (по умолчанию) — по шагам, пока пульс не оживёт: `window` — Nexus держит в редакторе пустое окно
      1×1 за экраном (без панели задач), демон активирует его — главное окно не двигается; `quiet` — главное окно
      без разворачивания, сразу под остальными; `focus` — крайний случай. Ожил — фокус сразу обратно;
    - `focus` — сразу жёстко: Unity на пару секунд на передний план, затем фокус возвращается;
    - `soft` — сообщение «приложение активно» без смены фокуса (Unity его мало, оставлен для сравнения);
    - `off` — только диагноз.

    Обновление пакета, которое Unity в фоне не применила (новая версия уже на диске, работает старый код), сервер
    CLI замечает сам за ≤ 30 с и перекомпилирует; перезагрузку домена доводит будильник.

    Настройка — `cli.wake off|soft|quiet|focus` или кнопка «будить» рядом с «CLI» в Manage; попытки по шагам —
    в `cli/wake.json` и в `nexus-fs diag`;
  - метки домена `cli/domain.json` (`reloading → loaded → ready`, `failed`) и `cli/startup-error.txt` — видно,
    где оборвалась перезагрузка.
- nexus-fs переводит пути проекта в выводе в локальные (`--no-map` — нет); efc — артефакты по корням паспорта.
- Выключили CLI — `NexusTemp/cli` удаляется; `NexusTemp/screenshots` и `NexusTemp/tests` (артефакты) остаются.
- Клиенты `nexus-fs` 1.0 и 5 работают и с этим демоном (5 удаляет прочитанное сам — в VM без права удаления
  итоги остаются до `expires`); `nexus-fs` 6 — корзина вместо удаления; 7 — шаги будильника в `diag`.

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
