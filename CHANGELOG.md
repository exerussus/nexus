# Changelog

Формат — [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/), версии — [SemVer](https://semver.org/lang/ru/).

## [0.2.0]

### Добавлено
- Корни плагинов по маркеру `nexus-plugins.json`: папка с маркером в Assets или в любом пакете
  становится корнем плагинов. Поиск — только кнопкой «Найти корни плагинов» в Manage; найденное
  хранится в `ProjectSettings/Nexus.json` (`discoveredRoots`, asset-пути, коммитится).
- Manage: секция «Корни плагинов» — встроенный, ручные и найденные корни, пропавшие и дубли.
- `NexusPaths.AssetToAbsolute`; `ToAssetPath` понимает пакеты из кэша (иконки плагинов из git-пакетов).
- `CHANGELOG.md`, README движка.

### Изменено
- Nexus устанавливается как UPM-пакет: зависимость `com.unity.nuget.newtonsoft-json` объявлена
  в `package.json` (раньше Newtonsoft должен был быть в проекте сам). Если в проекте лежит
  Newtonsoft.Json.dll (NuGet и т.п.) — удалите его, иначе будет две сборки с одним именем.
- App 2.1: вариант «Текущий профиль» на вкладке Сборка собирает по активному Build Profile (сцены
  из его переопределения) и больше не переключает профиль на голую платформу.
- Workspace 1.1: из asmdef убрана ссылка на несуществующий asmdef `Newtonsoft.Json`
  (dll подхватывается авто-ссылкой).

## [0.1.0]
- Первая версия: хаб страниц-плагинов, сервисы, тема, страницы App 2.0, Workspace и др.
