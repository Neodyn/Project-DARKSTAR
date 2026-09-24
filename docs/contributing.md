# Contributing to D.A.R.K.S.T.A.R.

Thanks for your interest in contributing! This is a small hobby project, so the bar for contributing is low, but a few conventions keep the codebase consistent.

## Ground rules

- **All source code, comments, and log/console strings must be in English.** No exceptions — this keeps the project approachable for contributors and users who don't speak German (or whichever language a given contributor is most comfortable in). Discuss issues/PRs in whatever language you like; the code itself stays English-only.
- **Keep `Darkstar.Core` UI-agnostic.** Anything used by both the bot service and the GUI (config, phrases, vocabulary, logging, backups, the DCS-gRPC tester) belongs in `Darkstar.Core` and must not depend on WPF, Blazor, or console-specific APIs.
- **Never break `config.json`/`phrases.json`/`vocabulary.json` backward compatibility silently.** Adding a new field to `AppConfig` is fine and self-heals existing installs (see `MergeMissingFields` in `AppConfig.cs`); renaming or repurposing an existing field without a migration path is not.
- **Validate, don't crash.** Bad or out-of-range config values should produce a `WARNING` in the log (see `ValidateValues` in `AppConfig.cs`) rather than throwing. The bot should degrade gracefully wherever practical (e.g. missing Vosk model → falls back to an energy-threshold placeholder detector with a clear warning).
- **Back up before overwriting.** Any code path that writes `config.json`, `phrases.json`, or `vocabulary.json` must call `BackupUtils.BackupBeforeWrite` first.

## Project layout

See the [README](../README.md#project-structure) for the folder layout. In short:

- **`Darkstar`** (root) — the bot itself: SRS connection, audio pipeline, hotword detection, Gemini calls, reply construction. Runs as a console app or Windows Service.
- **`Darkstar.Core`** — shared library: `AppConfig`, `PhraseBook`, `VocabularyBook`, `Logger`, `BackupUtils`, `DcsGrpcTester`, `DcsGrpcExplorer`, `DcsIntelService`, `WindowsServiceManager`. Referenced by both `Darkstar` and `Darkstar.Gui`.
- **`Darkstar.Gui`** — Blazor Hybrid (WPF host + WebView2) configuration editor. Panels live under `Pages/`.

## Setting up a dev environment

Run `setup-dev-environment.ps1` from the repo root — it checks/installs the .NET 8 SDK, the required Visual Studio 2022 workloads (`.NET desktop development`, `ASP.NET and web development`), the WebView2 Runtime, and a Vosk model, then does a trial `dotnet restore`/`build` of the whole solution. It's safe to re-run any time; every step skips itself if already satisfied.

```powershell
.\setup-dev-environment.ps1 -ProjectRoot "C:\path\to\this\repo"
```

## Making changes

1. Open `Darkstar.sln` in Visual Studio 2022 (don't open an individual `.csproj` directly — see the note in `setup-dev-environment.ps1`'s parameter docs about why the exact folder layout matters).
2. Set `Darkstar` (and/or `Darkstar.Gui`) as the startup project.
3. Build and test locally against a real or test SRS server before opening a PR.
4. If you touch a `.razor` file in `Darkstar.Gui`, keep the explicit `@namespace` directive at the top of the file — it's a required workaround for a known Blazor Hybrid + WPF namespace-derivation bug ([dotnet/maui#5861](https://github.com/dotnet/maui/issues/5861)), not leftover cruft.
5. If you add a new `AppConfig` field, also add a validation check in `ValidateValues` and document it in [configuration.md](configuration.md).

## Pull requests

- Keep PRs focused on one change/feature at a time.
- Describe what changed and why in the PR description; screenshots are appreciated for GUI changes.
- Update [configuration.md](configuration.md) and/or [changelog.md](changelog.md) alongside any user-facing change.

## Reporting issues

Please include your `config.json` (with `GeminiApiKey`, `DiscordWebhookUrl`, and `DcsGrpcApiKey` redacted) and the relevant excerpt from `logs/` when reporting a bug — most issues are much faster to diagnose with the actual log output than a description alone.
