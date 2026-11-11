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
- **Локализация** — app-core 6.3+ (base table + module packages), sub-tabs:
  - **Таблица** — all tables as one list (`LocWorkspace`): keys × languages, plural forms, package column and filter, overridden keys (⇅ with the chain per slot), usage counts; a new key goes to the chosen package, an edit — to the file of the effective value, «Пакет» moves a key; rename shows the usages and replaces them (tables saved at once; without a usage count it asks first — one project pass or the key only; keys assembled by a code prefix are warned about), delete lists them; «Сохранить» writes only changed files and refuses files changed on disk;
  - **Пакеты** — catalog in apply order (base, masks or the project's `ILocPackageProvider`), keys and language coverage per file, parse errors with lines, read-only and mod flags, runtime loading mode (`Auto` / `Project`), new package;
  - **Языки и окончания** — language names, plural rule, fallback, order (first = primary), add/remove, plural preview — across all files;
  - **Употребления** — one project pass (`LocUsages`: `^KEY`, `L.X`, usage patterns without `^`, providers; `Assets/` and embedded packages holding loc tables; C# comments skipped; in YAML assets `^KEY` counts only as a whole field value): where a key is used (click opens file:line), keys used but missing from the tables (add to a package), unused keys, overridden keys, files the pass skipped (over 32 MB, unreadable). «Used / unknown» is one rule from AppCore (`LocUsageIndex.IsUsed / Unknown / Unused`) for the table, this tab, the CLI and the pre-build check. A cancelled pass keeps the previous count;
  - **Патчи** — Play Mode: layers in apply order (packages marked), overridden values, history of any key (`Loc.History`); outside Play Mode: check a patch file against the tables;
  - **Настройки** — `LocalizationSettings` inspector, generate `L` constants, bake the runtime package list, project key check, import of the old positional JSON.
  - **CLI** — `loc.packages`, `loc.find <text> [--lang]`, `loc.usages <key>`, `loc.check [--unused]` (exit 1 on unknown keys or parse errors), `loc.rename <old> <new> [--dry] [--keys-only]` (key input is normalized like in the table: `LocUsages.NormalizeKey`; prefix warnings and skipped files are warnings, not failures).
- **Консоль** — AppDeck settings (app-core 4.1+): activation, hotkey, window, log capacity, metrics and mini-HUD, Preloaded Assets status, player-prefs reset. No compile reference to the Deck assembly — fields go through `SerializedObject`, so the plugin still builds with app-core 4.0.
- **Сборка** — sub-tabs:
  - **Билд** — profile, version bump, output folder, run, history;
  - **Пайплайн** — per-profile pre/post steps (`BuildStep<T>` + `[BuildStep]` from app-core `Exerussus.AppCore.Editor.Build`; steps are discovered in every assembly, so a package can ship its own), incl. Zip (name template), version.json, S3 upload via rclone and PlayFlow Cloud upload (zip → build → optional server restart; personal keys in UserSettings);
  - **BuildInfo** — current values, stamp settings, version overlay, migration of the old deployment.
  - **CLI** — the same build without the window: `app.build [--profile name|guid] [--dev] [--clean] [--allow-dirty] [--save]`
    starts the selected (or named) profile with its pipeline and answers at once; `app.build.wait [--timeout 1800]` waits
    for the result (version, hash, size, artifacts; failure — exit 1); `app.version [--bump patch|minor|major] [--set X.Y.Z]`
    shows or changes the version in every carrier. `app.build` refuses on Play, compile errors, dirty scenes and an
    uncommitted tree (`--allow-dirty`), never opens a dialog. One task: `task.run --steps "app.build; app.build.wait"`.

## Visual language

Soft cards on `BgSoft` with a `Divider` outline, recessed inputs, soft status washes with a
status dot; `Accent` only for active/interactive. Tabs and sub-tabs come from `IPageUi.Tabs`
and `IPageUi.Segmented`. All colours are theme tokens.
