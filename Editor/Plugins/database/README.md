# DB

Servicing-страница: инспектор игровой базы и авто-генерация её контракта.

## Что делает

- Показывает все базы из статического `DataBase` со статусом загрузки.
- По каждой: `Ping`, `Select`, `Open`, `Initialize`, `Collect`.
- Слот для внешнего `ScriptableObject` (`IInitializable` / `ICollector`).
- Авто-ген: скан `IDataBase` с `[DataBase("alias")]`, генерация контракта и
  создание недостающих `.asset`.

## Внешний след

Создаётся **вне** Nexus, в проекте, и **не** стирается при отключении:

- `Assets/DataBase/Configs/DataBaseGen.json` — конфиг проекта (в гит).
- `Assets/DataBase/Scripts/Generated/DataBase.Generated.cs` — контракт для игры.
- `Assets/DataBase/Resources/*.asset` — базы.

> Отключение страницы безопасно: сгенерированный код и ассеты остаются — их
> потребляет сама игра, а не страница.

## Настройки

Личные тоглы (авто-регистрация, авто-создание, скан по фокусу, full-auto, логи,
предупреждения) — персональные, лежат в `UserSettings`, чистятся кнопкой
*Clear cache* в Manage. Пути и форма генерации — проектные, в `DataBaseGen.json`.
