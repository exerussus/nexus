# Nexus plugin: `build`

Servicing page: build by profile, bump the game version, run pre-/post-process pipelines and deploy **BuildInfo** — a runtime ScriptableObject with the version and commit hash.

## What kind of page this is

**Servicing (обслуживающая).** Its external footprint — the BuildInfo deployment (`Assets/Plugins/BuildInfo` by default) — is consumed by the game, so disabling the page **never** deletes it. Removal is an explicit, confirmed action on the **BuildInfo** tab and touches only the files the page created.

## Tabs

- **Сборка** — version (`Major +1` / `Minor +1` / `Patch +1`, optional Android versionCode / iOS buildNumber bump), profile picker, Development / Clean build, output path template, **Собрать**, build history.
- **Пайплайн** — per-profile pre- and post-process steps: order, on/off, per-step settings.
- **BuildInfo** — deploy / update / move / remove the BuildInfo footprint; prefix, postfix, hash length, `-dirty` marker, resource name.

## Build flow

1. Activate the chosen profile. If that switches the platform, the request is kept in `SessionState` and the build **continues automatically** after the domain reload.
2. Pre-process steps (in pipeline order).
3. `BuildPipeline.BuildPlayer` with the profile (or the current platform when no profile asset is chosen).
4. Post-process steps — only after a **successful** build.
5. Optionally re-activate the previously active profile. Result goes to the personal history.

Output path template tokens: `{profile}` `{platform}` `{product}` (also `{version}` `{hash}` `{date}`, but those create a new folder per build and defeat Unity's incremental build cache — the page warns). Default: `Builds/{profile}/{product}` — a stable folder; the version goes into the zip name, and the zip lands next to it in `Builds/{profile}/`. Old configs with `Builds/{profile}/{version}` are migrated automatically.

## BuildInfo

- Committed asset is **empty** (mode `editor-play-mode`).
- **Any** build (page, File → Build, CI) stamps version, hash, prefix/postfix, profile before building and reverts afterwards — also after a failed build or an editor crash.
- In editor **Play Mode** version and hash are served on the fly (never written to the asset) and cleared on exit.
- Runtime access: `Exerussus.Builds.BuildInfo.Current` (lazy `Resources.Load`), `FullVersion` = `prefix + version + postfix + "+" + hash (+ "-dirty")`.
- The runtime assembly `Exerussus.BuildInfo` is `autoReferenced`; other asmdefs reference it by name.

## Adding a step

Create a class in this plugin:

```
[BuildStep("my-step", "My step", BuildStage.Post, Description = "…")]
internal sealed class MyStep : BuildStep<MyStep.Settings>
{
    [Serializable] internal sealed class Settings { public bool flag = true; }
    protected override void Execute(BuildStepContext ctx, Settings s) { /* throw on failure */ }
}
```

The page discovers it via `TypeCache` and appends it (disabled) to every profile pipeline. Add the file to `manifest.json` → `deploy`.

## Built-in steps

- **zip** (post) — `<product>_<version>_<hash>.zip` (without git: `<product>_<version>.zip`). Excludes `*_BurstDebugInformation_DoNotShip` and `*_BackUpThisFolder_ButDontShipItWithYourGame` by default. Written to a temp file and renamed. Single-file builds (`.apk`/`.aab`) are zipped as the file.

## Data

- Shared config: `<BuildInfo root>/Editor/build-config.json` (committed). Before the first deploy — a personal draft in `UserSettings`.
- Personal: `UserSettings/Exerussus.Nexus/build/prefs.json`, `history.json`.

## Caveats

- Zip does not keep unix permissions — macOS/Linux binaries may need `chmod +x` after unpacking.
- `-dirty` detection needs `git` in `PATH`; without it the hash is read from `.git` directly and the tree is treated as clean.
- If a build profile overrides Player Settings, the version shown is the one Unity reports for the active profile.
