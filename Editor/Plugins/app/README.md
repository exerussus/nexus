# Nexus plugin: `app`

Servicing page that scaffolds, edits & generates the App feature. Reclassified
replacement of the old Project Hub / `navigation` plugin (new id, new guid — not an
in-place evolution).

## What kind of page this is

**Servicing (обслуживающая).** The files it produces live **outside** Nexus (in
`Assets/App/...`) and are consumed by the game directly, so disabling/evicting this
plugin **never** deletes them. The page owns that footprint explicitly — see the
**External files** tab (status with full paths + cleanup limited to regenerable outputs).

## Tabs

- **Editor** — page/popup id registry (source of truth = `NavigationData`), affixes,
  Apply (writes ids, NavigationSettings entries, regenerates `Navigation.uss`, migrates
  USS classes across UXML on rename/delete, renames page asset files/folders).
- **Graph** — navigation graph of the active scene (added separately).
- **Generation** — code-gen (`NavTargets.cs` = PageId/PopupId consts), page/popup templates,
  and **App scaffold** (below). Element names are not generated: elements marked `generate-link`
  are picked up by the controller generator (uxml context menu → Create Page/Popup Controller),
  which emits typed fields straight into the controller.
- **Hooks** — action-hook library (UserSettings) inserting `display:none` hooks into `mainContainer`.
- **Nav classes** — standard nav classes (`to-back-page__navigation`) into `Navigation.uss`.
- **External files** — footprint status + cleanup of regenerable outputs only.

## App scaffold (new)

Idempotent generation of the default project footprint — creates only what is missing,
never overwrites authored files:

- `Containers/{AppStarter,Default,LoadingScreen}.uxml` — a filled `mainContainer` + the
  shared `AppStyle` via `<Style>`.
- `Styles/AppStyle.uss` — common style, empty, injected into every container, user-extendable.
- `UIToolkit/AppTheme.tss` — `@import unity-theme://default`, empty.
- `UIToolkit/App Settings.asset` — `PanelSettings` (scale-with-screen 1920×1080), `themeStyleSheet` → `AppTheme.tss`.
- `Resources/UISoundLibrary.asset` — empty default UI sound library.
- `Resources/NavigationData.asset` — the page/popup id registry (replaces the old `db` DataBase).
- `Resources/NavigationSettings.asset` — routing config (className → target + Kind).
  (Existing projects keep whatever name is already in `app.config.json`.)

## Paths config (committed)

All paths/names are read from a **project** config in git space:
`Assets/App/app.config.json` (created with defaults on first use, editable, hot-reloaded
on the page's Refresh). Defaults match the layout above. See `AppPaths.cs`.

## External footprint (survives disable)

- `Resources/NavigationData.asset`, `Resources/NavigationSettings.asset` — config (authored data).
- `Styles/Navigation.uss`, `Navigation/Generated/NavTargets.cs`
  — generated (regenerable). **Only these two are removable from External files.**
- Containers, AppStyle/AppTheme/App Settings, UISoundLibrary, `Pages/**`, `Popups/**` — authored, status-only.

## Inside the plugin (wiped on disable, packed into Preserve)

Page code (`AppPage*.cs`), `AppPaths.cs`, the controller generator
(`AppControllerGenerator.cs`), the page asmdef, and the page style `AppPage.uss`
(loaded via `Context.LoadDeployedAsset`, never by hardcoded path).

## Personal data

- Hooks library → `UserSettings/Exerussus.Nexus/app/hooks.txt` (via `Context.GetUserConfigPath`;
  covered by **Clear settings**, survives restore/disable).
- Dirty flag → `SessionState` via `Context.GetSessionKey`.

## Package dependencies (`manifest.json` → `packageRequires`)

UPM git packages installed two-phase on Apply:

- `com.exerussus.di` — `https://github.com/exerussus/DI.git` @ `1.0.2`
- `com.exerussus.signals` — `https://github.com/exerussus/signals.git` @ `1.0.0`
- `com.exerussus.payloads` — `https://github.com/exerussus/payloads.git` @ `1.0.0`
- `com.exerussus.app-core` — `https://github.com/exerussus/app-core.git` @ `REPLACE_WITH_TAG`
  (set the real git tag; `com.cysharp.unitask` comes in transitively via app-core).

## Assembly references (`Exerussus.Nexus.Pages.App.asmdef`, Editor-only)

`Exerussus.Nexus.Abstractions`, `Exerussus.AppCore`. (`db` and `app.abstractions` removed —
audio/input go through `SoundAdapter`/`InputAdapter`.)

Namespaces consumed from app-core 2.0: `Exerussus.AppCore` (`AppRunner`),
`Exerussus.AppCore.Navigation` (`NavigationData`, `NavigationSettings`, `PageId`, `PopupId`),
`Exerussus.AppCore.Views` (`AppPage`, `AppPopup`, controllers), `Exerussus.AppCore.Audio`
(`UISoundLibrary`).

## Companion runtime types (live in app-core, not in this plugin)

- `NavigationData` — id registry SO + standalone `[PagesDropdown]`/`[PopupsDropdown]` attributes.
- `NavigationIdDrawer` — dedicated Odin drawer for those attributes (app-core editor asmdef).

## UI language note

UI strings are English (comments Russian), kept from the original page for consistency.
