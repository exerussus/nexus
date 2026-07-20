# API / Backend

Страница генерирует C#-клиент к REST-API из его OpenAPI-спеки. Модели и обёртки эндпоинтов штампует внешний генератор (openapi-generator), а транспорт — твой `RestClient` на UnityWebRequest + UniTask, который работает на всех платформах, включая WebGL. CLI пишет во временный staging (`Temp/`), а в `Assets` движок деплоит только `.cs` — мусор генератора (docs, openapi.yaml, служебные папки) в проект не попадает. Сама генерация — вызов внешнего CLI, поэтому сначала нужно подготовить окружение (раздел 1): большинство ошибок первого запуска — из-за пропущенной подготовки.

## Nexus: обслуживающая (servicing) страница

Продукт страницы живёт **вне** Nexus и нужен игре напрямую. Внешний футпринт:

- `Assets/Api/` — рантайм-клиент и все версии (Generated, link.xml, asmdef)
- `Tools/openapi-templates/` — шаблон api.mustache
- `ProjectSettings/ApiCodegen.json` — конфиг и профили
- `ProjectSettings/ApiCodegen.Snapshots/` и `ApiCodegen.Mirrors/` — снапшоты генераций
- `Temp/ApiCodegenStaging/` — staging CLI (мусорный, чистится сам)

Disable и Restore плагина эти файлы **не трогают** — сносятся они только явно, кнопками страницы («Полная очистка» на вкладке Generate, «Удалить профиль» на вкладке Profiles).

Движок кодогена задеплоен вместе со страницей в `Assets/NexusStates/api-backend/` (State коммитится в git), поэтому **CI работает только при задеплоенной странице** — это осознанное решение. Команды CI:

```
unity -batchmode -quit -projectPath . -executeMethod Exerussus.Nexus.Pages.ApiBackend.ApiCodegenBatch.Generate
unity -batchmode -quit -projectPath . -executeMethod Exerussus.Nexus.Pages.ApiBackend.ApiCodegenBatch.CheckDrift
```

Коды выхода: 0 — ок; 1 — генерация какого-то профиля не удалась; 2 — спека недоступна; 3 — есть ломающий дрейф (`CheckDrift` — гейт на PR).

---

## Профили (версии API)

Профиль = одна версия API, **полностью самодостаточная**: своя спека, свой генератор (команда/аргументы/шаблон), свой packageName и своя папка `Assets/Api/<обёртка>` с `Generated/`, `link.xml` и asmdef внутри. Сервисы на старой версии и на новой живут рядом как разные профили. Удаление профиля умеет сносить и его папку — версия убирается целиком, не задевая остальные. CI всегда гоняет все профили.

Обёртка версии **обязательна**: все сгенерённые типы (и API, и модели) вкладываются пост-обработкой в `static partial class` (ApiV10, ApiV20…), под-неймспейсы `.Model`/`.Api`/`.Client` объединяются в packageName версии. Обращение: `ApiV10.User`, `ApiV10.AuthApi` при `using <packageName версии>`. packageName по умолчанию выводится из обёртки целиком: ApiV10 → `ProjectApi.Generated.ApiV10` — при уникальных обёртках у каждой версии гарантированно своя сборка, поэтому и link.xml у каждой свой.

> **Внимание.** Классы-обёртки и packageName у профилей должны быть уникальны (обёртка = папка версии, packageName = имя asmdef). Вкладка Profiles подсвечивает дубли красным.

## 1. Подготовка окружения (инструменты на машине)

### 1.1. Java (JDK 17)

openapi-generator написан на Java и без неё не запустится — это причина ошибки «"java" не является командой». Поставь JDK 17 (Temurin). На Windows:

```
winget install EclipseAdoptium.Temurin.17.JDK
```

Проверь в новом терминале:

```
java -version
```

> **КРИТИЧНО:** после установки Java обязательно полностью перезапусти Unity (и Unity Hub). Редактор наследует переменную PATH в момент старта; пока он запущен, про свежую Java он не знает, и генерация будет падать с «java не команда», хотя в терминале `java -version` уже работает. Это самая частая причина затыка.

### 1.2. openapi-generator-cli

Это npm-обёртка, которая сама качает нужный .jar генератора. Нужен установленный Node.js, затем глобально поставь CLI:

```
npm install -g @openapitools/openapi-generator-cli
```

Проверь (в первый раз он докачает версию генератора):

```
openapi-generator-cli version
```

## 2. Подготовка Unity-проекта (пакеты и файлы)

### 2.1. Newtonsoft.Json

RestClient сериализует через Newtonsoft. Поставь пакет: Window → Package Manager → плюс слева → «Add package by name» → введи:

```
com.unity.nuget.newtonsoft-json
```

### 2.2. UniTask

В реестре Unity его нет, ставится из git. Package Manager → «Add package from git URL» → вставь:

```
https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask
```

### 2.3. Рантайм-клиент

Положи `RestClient.cs`, `RestClientOptions.cs`, `ApiException.cs` в `Assets/Api/Runtime/` (кнопка развёртывания на вкладке Generate — сырьё лежит в деплое страницы, `Templates/`). Свою asmdef заводить не нужно: если её нет, движок при генерации сам создаст `ProjectApi.asmdef` (ссылка на UniTask; Newtonsoft — precompiled, цепляется автоматически). Без сборки у рантайма версии не смогли бы сослаться на RestClient — Assembly-CSharp из asmdef недостижим. Клиент один на все версии.

### 2.4. Asmdef и link.xml версий — автоматически

Заводить их руками не нужно: при генерации движок сам создаёт в папке версии `Assets/Api/<обёртка>/` недостающие link.xml (сборка версии + Newtonsoft, preserve all) и asmdef (имя = packageName версии; ссылки: рантайм-сборка + UniTask). asmdef лежит в **корне** версии, а не в Generated — регенерация заменяет только Generated, так что твои правки ссылок в asmdef живут; отсутствующую ссылку на рантайм движок дочинит сам, не трогая остальное. IL2CPP собирает все link.xml под Assets, так что per-version файл легален.

### 2.5. Шаблон api.mustache

Положи api.mustache **вне** Assets — в `Tools/openapi-templates/csharp/` (кнопка развёртывания на вкладке Generate). Это дефолт для всех профилей; отдельный профиль может указать свой путь в поле «Кастомный шаблон» — например, слепок шаблона под старую версию CLI.

## 3. Настройка (вкладки Profiles и Config)

Все параметры привязаны к профилю. На вкладке Profiles — жизненный цикл версии: имя, спека (URL/файл + health-check), класс-обёртка («Определить из спеки» вытащит версию из путей: api/v1.0 → ApiV10). На вкладке Config — генератор выбранного профиля: команда CLI, доп. аргументы, шаблон, packageName, срезка префиксов; там же — выведенные пути версии и предпросмотр команды. Ключевая строка «Доп. аргументы» — одной строкой:

```
--global-property models,apis,modelDocs=false,apiDocs=false,modelTests=false,apiTests=false,supportingFiles=OpenAPIDateConverter.cs:FileParameter.cs --additional-properties=library=httpclient,targetFramework=netstandard2.1,validatable=false
```

> Внутри списков через запятую **не должно быть пробелов** — иначе «Found unexpected parameters». Пробелы есть только между разными флагами (перед `--additional-properties`).

packageName в этой строке нет намеренно — движок подставляет его сам (из поля профиля или выводит из обёртки). Пути версии (Generated, link.xml, asmdef) не редактируются — они выводятся из обёртки, чтобы папка версии была самодостаточной. Обёртка типов в команду CLI не входит — её делает пост-обработка после деплоя (в CI так же, движок общий).

## 4. Запуск и использование

Вкладка Generate → выбери профиль → «Сгенерировать профиль» (или «Сгенерировать все»). В логе — команда, код возврата CLI, деплой, обёртка и список изменённых файлов. Вкладка Drift показывает, что уехало в API выбранного профиля с момента его последней генерации (ломающие изменения — красным).

Вызов сгенеренного клиента из кода (типы — под обёрткой версии, namespace — её packageName):

```csharp
using ProjectApi.Generated.ApiV10;

var options = new RestClientOptions("https://petstore3.swagger.io/api/v3");
var client  = new RestClient(options);
var petApi  = new ApiV10.PetApi(client);

var pet = await petApi.GetPetByIdAsync(1);
Debug.Log($"{pet.Id}: {pet.Name}"); // pet — это ApiV10.Pet
```

Если обёртку в вызовах писать лень — добавь `using static ProjectApi.Generated.ApiV10.ApiV10;` и зови типы без префикса. Не забудь сослаться на asmdef версии из своей сборки. Базовый URL задаётся в RestClientOptions, а пути из спеки (`/pet/{petId}`) идут относительно него.

## 5. Если что-то пошло не так

**"java" не является командой** — Java не установлена или Unity запущена до её установки. Поставь JDK 17 и полностью перезапусти Unity (см. 1.1).

**Found unexpected parameters** — пробел внутри списка аргументов через запятую. Убери все пробелы внутри `--global-property` и `--additional-properties` (см. 3).

**CS0234: DataAnnotations does not exist** — модели тянут валидацию, которой нет в .NET Standard. Добавь `validatable=false` в `--additional-properties` — это убирает IValidatableObject и атрибуты `[Required]`.

**Не найден ...Client.OpenAPIDateConverter / FileParameter** — не сгенерены supporting-хелперы. Они уже в дефолтной строке аргументов (`supportingFiles=OpenAPIDateConverter.cs:FileParameter.cs`). При обёртке пост-обработка сама вложит конвертер рядом с моделями — ссылки резолвятся как соседи.

**В staging не найден вывод генератора (src/…)** — CLI отработал, но не создал `src/<packageName>`. Обычно это упавшая генерация (см. stderr в логе) или нестандартный шаблон, поменявший раскладку. Проверь код возврата CLI и staging-папку из предпросмотра команды.

**CS0101: одноимённые типы в двух версиях** — у двух профилей одна обёртка или один packageName — они пишут в одну папку/сборку. Вкладка Profiles подсвечивает дубли красным.

**Неправильный namespace в сгенеренном коде** — packageName взялся не тот. Он берётся из поля профиля (или выводится из обёртки), а не из строки аргументов. Если в старом конфиге остался packageName в «Доп. аргументах» — убери его оттуда. И помни: под-неймспейсы `.Model`/`.Api`/`.Client` штатно объединяются в packageName версии — это не ошибка.

**Загрузка файла (upload) не работает в рантайме** — RestClient шлёт тело как JSON, а файлы требуют multipart/form-data. File-эндпоинты компилятся, но не работают — не используй их, пока не добавишь multipart в RestClient.

**Папка Templates в деплое страницы не найдена** — деплой State неполный (кто-то снёс файлы руками). Nexus Manage → Restore плагина вернёт её; внешний футпринт при этом не трогается.

## 6. Ограничения

Не поддержано: загрузка файлов (multipart). Enum в query-параметрах сериализуется C#-именем (PascalCase), а не wire-значением — если бэк ждёт «available», а enum даёт «Available», передавай параметр строкой. csharp-генератор помечен Experimental — при апдейте CLI имена флагов и переменных шаблона стоит сверять и фиксировать версию (у профиля свой генератор — версии можно апдейтить по одной). Обёртка пост-обработкой рассчитана на стоковые модели генератора (ссылки через using-alias); если генератор начнёт эмитить полностью квалифицированные ссылки вида `global::{packageName}.Client.X` в коде — они сломаются, см. комментарий в OutputPostProcessor.
