# Nexus plugin: `app`

Servicing page for AppCore (`com.exerussus.app-core` 4.x). Everything it produces lives in
`Assets/App` — a folder owned entirely by AppCore (fixed paths in `AppCorePaths`) — and is
consumed by the game directly, so disabling this plugin **never** deletes it.

The former standalone `build` page is merged in as the **Сборка** tab; BuildInfo itself is now
part of AppCore (no separate deployment).

## Tabs

- **Editor** — id registry for pages, popups and fragments (source of truth: the single
  `Assets/App/Settings/NavigationSettings.asset`), class affixes, Apply (ids + entries,
  `Navigation.uss`, `NavTargets.cs`, USS class migration across uxml, view folder renames).
- **Generation** — `NavTargets.cs` (PageId / PopupId / FragmentId), canonical views per id
  (uxml + controller with generated `.Elements.cs`), idempotent `Assets/App` scaffold.
- **Nav classes** — standard navigation classes (`to-back-page__navigation`) and the full class list.
- **External** — footprint status with full paths; cleanup limited to regenerable outputs.
- **Text** — character sets, source scanning, font chain, bake + coverage report (logic in AppCore).
- **Локализация** — app-core 4.4+, sub-tabs:
  - **Таблица** — base `.loc` table: keys × languages, plural forms per row (±), project scan (usages, keys missing from the table, unused keys), add/rename/delete;
  - **Языки и окончания** — language names, plural rule, fallback, order (first = primary), add/remove, plural preview;
  - **Патчи** — Play Mode: layers in apply order (with rejected counts), overridden values, history of any key (`Loc.History`); outside Play Mode: check a patch file against the base;
  - **Настройки** — `LocalizationSettings` inspector, generate `L` constants, project key check, import of the old positional JSON.
- **Консоль** — AppDeck settings (app-core 4.1+): activation, hotkey, window, log capacity, metrics and mini-HUD, Preloaded Assets status, player-prefs reset. No compile reference to the Deck assembly — fields go through `SerializedObject`, so the plugin still builds with app-core 4.0.
- **Сборка** — sub-tabs:
  - **Билд** — profile, version bump, output folder, run, history;
  - **Пайплайн** — per-profile pre/post steps (`BuildStep<T>` + `[BuildStep]` from app-core `Exerussus.AppCore.Editor.Build`; steps are discovered in every assembly, so a package can ship its own), incl. Zip (name template), version.json, S3 upload via rclone and PlayFlow Cloud upload (zip → build → optional server restart; personal keys in UserSettings);
  - **BuildInfo** — current values, stamp settings, version overlay, migration of the old deployment.

## Visual language

Soft cards on `BgSoft` with a `Divider` outline, recessed inputs, soft status washes with a
status dot; `Accent` only for active/interactive. Tabs and sub-tabs come from `IPageUi.Tabs`
and `IPageUi.Segmented`. All colours are theme tokens.
