# Contributing to D.A.R.K.S.T.A.R.

Thanks for your interest in contributing! This is a small hobby project, so the bar for contributing is low, but a few conventions keep the codebase consistent.

## Ground rules

- **All source code, comments, and log/console strings must be in English.** No exceptions — this keeps the project approachable for contributors and users who don't speak German (or whichever language a given contributor is most comfortable in). Discuss issues/PRs in whatever language you like; the code itself stays English-only.
- **Keep `Darkstar.Core` UI-agnostic.** Anything used by both the bot service and the GUI (config, phrases, vocabulary, logging, backups, the DCS-gRPC tester) belongs in `Darkstar.Core` and must not depend on WPF, Blazor, or console-specific APIs.
- **Never break `config.json`/`phrases.json`/`vocabulary.json` backward compatibility silently.** Adding a new field to `AppConfig` is fine and self-heals existing installs (see `MergeMissingFields` in `AppConfig.cs`); renaming or repurposing an existing field without a migration path is not.
- **Validate, don't crash.** Bad or out-of-range config values should produce a `WARNING` in the log (see `ValidateValues` in `AppConfig.cs`) rather than throwing. The bot should degrade gracefully wherever practical (e.g. missing Vosk model → falls back to an energy-threshold placeholder detector with a clear warning).
- **Never commit anything the bot wrote while running.** `recordings\` holds the recorded voices of other players with their names in the file names, `logs\` holds transcripts and positions, and `config.json` holds the API keys in plain text. All of it is in `.gitignore`, and the test suite checks that it stays there — add a new output folder to both in the same change. Note that a `.gitignore` cannot help with the author name and e-mail in your commits: set a noreply address and turn on *Keep my email addresses private* on GitHub if you would rather not publish them.
- **Back up before overwriting.** Any code path that writes `config.json`, `phrases.json`, or `vocabulary.json` must call `BackupUtils.BackupBeforeWrite` first.

## Project layout

See the [README](../README.md#project-structure) for the folder layout. The three projects:

- **`Darkstar`** (root) — the bot itself: SRS connection, audio pipeline, hotword detection, Gemini calls, reply construction. Runs as a console app or Windows Service.
- **`Darkstar.Core`** — shared library, referenced by both the bot and the GUI. UI-agnostic by rule (see above).
- **`Darkstar.Gui`** — Blazor Hybrid (WPF host + WebView2) configuration editor. One panel per channel under `Pages/`; `Pages/Main.razor` holds the sidebar as data (`NavGroup`/`NavEntry`) rather than as markup, so adding a panel means adding one entry. Two rules the test suite enforces: a panel's **channel number never changes** (the documentation is full of "on CH2 Radios"), and CH8's three sidebar entries must match the three `Section*` constants in `DcsGrpcPanel` — a mismatch there shows a blank page and fails nowhere.
- **`Darkstar.Tests`** — the test suite. References `Darkstar.Core` only; see below.

### Where things live

The bot (repository root):

| File | What it does |
|---|---|
| `Program.cs` | Generic Host setup, console-vs-service hosting, unhandled-exception logging, CLI entry points. |
| `BotService.cs` | The `BackgroundService`: per-radio sessions, recording state machine, the order requests are classified in, transmitting. The biggest file, and the one to read first. |
| `SrsConnection.cs`, `SrsModels.cs`, `SrsAudioPacket.cs` | SRS external-AWACS protocol: TCP JSON sync, UDP voice packets. |
| `OpusCodec.cs` | Opus decode, one decoder per sender GUID (decoder state is per stream and not thread-safe). |
| `HotwordDetector.cs`, `VoskHotwordDetector.cs` | The detector interface, the Vosk implementation, and the energy-threshold fallback. |
| `HotwordTestRunner.cs` | `--test-hotword`: replays recordings through the real detector, compares audio paths, proposes wake-word variants. |
| `GeminiClient.cs` | Transcription + reply in one call, retries, fallback model, the prompt itself. |
| `ExternalAudioSender.cs` | Runs `DCS-SR-ExternalAudio.exe` to transmit, with the per-radio voice. |
| `SpeechServices.cs`, `WavUtils.cs` | Speech-service glue and PCM/WAV handling. |
| `DiscordNotifier.cs` | Optional webhook notifications. |

`Darkstar.Core` — configuration and infrastructure:

| File | What it does |
|---|---|
| `AppConfig.cs` | Every setting, plus `RadioConfig`, schema-merge, validation, and the vocabulary/trigger conflict check. |
| `PhraseBook.cs`, `VocabularyBook.cs` | `phrases.json` and `vocabulary.json`. |
| `Logger.cs` | Console + file logging, the debug ring buffer, throttled flush-to-disk. |
| `LogFiles.cs` | Picking the newest log by the timestamp in its **name** — see the comment in there about why not by modification time. |
| `FileRetention.cs` | Pruning `recordings\` and `logs\`. Pure decision function plus a best-effort driver. |
| `BackupUtils.cs` | The pre-write backup every config writer must call. |
| `SrsPaths.cs` | Finding `DCS-SR-ExternalAudio.exe`. Mirrored in Pascal by the installer; `srstest` asserts the two stay in sync. |
| `TtsVoices.cs` | Reading the available TTS voices out of ExternalAudio's `--help`. |
| `VoskModelCheck.cs` | Validating a Vosk model folder *before* the native call, which is uncatchable if it fails. |
| `AudioFrontEnd.cs` | The anti-alias filter, decimation and optional auto-gain feeding the detector. |
| `HotwordVariants.cs` | Accepting several spellings of a wake word, and proposing them from recordings. |
| `RateLimiter.cs` | Keeping one pilot from using the whole Gemini quota and the frequency. |
| `TowerPlan.cs` | Turning a mission's airfields into one tower radio each. Pure; the GUI applies the result. |
| `FrequencyAnnouncer.cs` | F10 markers and the startup message that tell pilots the generated frequencies. |
| `TuneInGreeting.cs` | Greeting a pilot who tunes onto a frequency — and, mostly, deciding when not to. |
| `WindowsServiceManager.cs` | Install/start/stop/remove the service, via PowerShell rather than parsing localized `sc.exe` output. |

`Darkstar.Core` — understanding and answering requests:

| File | What it does |
|---|---|
| `TriggerMatcher.cs` | Whole-phrase, word-boundary trigger matching. **Every** new trigger list goes through this, never `Contains`. |
| `PilotNames.cs` | Matching an SRS name to a DCS player, and speaking callsigns. Read the rule order before touching it. |
| `DcsIntelService.cs` | Tactical replies: bogey dope, picture, threat, alpha check, friendly positions. Also the bearing/range/altitude helpers. |
| `FriendlyPosition.cs` | "Where is Springfield 2-1?" — target extraction and the safety rules. |
| `RadioCheck.cs` | "Radio check", answered on every radio. |
| `ThreatCircleService.cs` | The standing watch and its sweep loop. |
| `DcsAirfieldService.cs`, `AirfieldReport.cs` | Runway in use and ATIS, including the runway-query Lua. |
| `RadioRoles.cs` | Which frequency serves which kind of request, and the handoff. |
| `DcsGrpcChannels.cs` | Shared `GrpcChannel` per address — a channel owns an HTTP/2 connection and is meant to be reused. |
| `DcsGrpcTester.cs`, `DcsGrpcExplorer.cs` | The connection test and the read-only mission-data browser. |

### Conventions worth knowing before you add a feature

- **Trigger phrases go through `TriggerMatcher`.** A bare `Contains` check is what once made the bot answer "bogey dope" to anything it couldn't make out.
- **Never put a trigger phrase in the default `vocabulary.json`.** That list tells the transcriber to snap similar-sounding audio onto its exact spelling, which manufactures commands nobody gave.
- **Never interpolate user input into the runway Lua.** `DcsAirfieldService.RunwayQueryLua` is a compile-time constant that asks for all airfields at once, precisely so nothing from a transmission can reach `CustomService.Eval`. `atistest` asserts that property.
- **Put decision logic in a pure function.** `FileRetention.Decide`, `HotwordVariants.Suggest`, `FriendlyPosition.CleanTarget` and the bearing helpers are all testable without DCS, SRS or Windows — which is the only reason they are tested at all.
- **A new capability that reveals information needs a reason to be on.** Friendly positions default to off and refuse a caller whose coalition is unknown; the comments in `FriendlyPosition.cs` explain why. Follow that pattern rather than adding an always-on feature.

## Tests

```powershell
dotnet run --project Darkstar.Tests
```

One command, a readable transcript, exit code 0 when every assertion held. No SRS server, no DCS, no Gemini key and no speech model are needed — which is the point: a test you can only run with a mission loaded is a test nobody runs.

| File | Covers |
|---|---|
| `Test.cs` | The whole harness: a counter, `Check`, `Eq`, and finding the repository root by walking up to `Darkstar.sln`. |
| `SrsAndVoskTests.cs` | Locating `DCS-SR-ExternalAudio.exe`, validating a Vosk model folder, the detector's wiring. |
| `AudioFrontEndTests.cs` | The anti-alias filter and decimation, measured against test tones. |
| `PilotNameTests.cs` | Matching an SRS name to a DCS pilot, rule by rule, including the cases it must refuse. |
| `AirfieldTests.cs` | Runway selection, ATIS units, the radio roles and the handoff. |
| `HousekeepingTests.cs` | Retention, alpha check, radio check, voices, wake-word variants, friendly positions, rate limiting, the installer's contents, and the documentation checks. |

**Read files through `ReadSource("relative/path")`, never by absolute path.** A test that hard-codes `/home/you/...` or `C:\Users\you\...` passes on your machine and throws on everybody else's, halfway through the run. `Program.cs` checks for that before any suite runs and stops with the file and line, because the alternative is a stack trace where an explanation belongs.

**There is no test framework on purpose.** Most of these assertions are about a spoken sentence, an arithmetic result, or whether a source file still says what a comment claims — none of which needs xUnit, a runner plugin or an attribute vocabulary. A dependency-free test project still builds in five years and can be read start to finish.

Three kinds of assertion appear, and the third is unusual enough to explain:

1. **Behaviour** — call a function, check the answer. Most of the suite.
2. **Numbers with an independent check** — one degree of latitude is 60 nautical miles by definition, so a range calculation can be verified without trusting the formula it was written from.
3. **Arrangement** — reading a source file as text to assert that a radio check is answered *before* the tactical classifier, that no user input can reach the runway Lua, that the coalition guard runs before any player lookup. Those properties live in the order of the code rather than in its behaviour, so there is nothing else to call. They are brittle by nature: if you move code and one fails, check whether the property still holds before re-anchoring the assertion.

The suite also checks the documentation against the code — every setting, every command-line option, every source file in the map above, and every internal cross-reference. Adding a setting without documenting it fails the build.

`Darkstar.Core` grants `InternalsVisibleTo("Darkstar.Tests")`, so helpers can stay `internal` instead of being made public purely to be tested.

## Continuous integration

`.github/workflows/build.yml` runs on every push and pull request:

- **`test`** — restores, builds the whole solution in Release, runs the suite. On `windows-latest`, because `Darkstar.Gui` is WPF and only builds there; a green tick on Linux would simply never have covered the GUI.
- **`bot-only`** — publishes the bot exactly as `build-installer.ps1` does, then checks the output contains nothing but the file types a running bot needs. That catches a NuGet package quietly dropping localized resource folders or XML documentation into what the installer packs, which nothing else would notice.

## Setting up a dev environment

Run `setup-dev-environment.ps1` from the repo root — it checks/installs the .NET 8 SDK, the required Visual Studio 2022 workloads (`.NET desktop development`, `ASP.NET and web development`), the WebView2 Runtime, and a Vosk model, then does a trial `dotnet restore`/`build` of the whole solution. It's safe to re-run any time; every step skips itself if already satisfied.

```powershell
.\setup-dev-environment.ps1 -ProjectRoot "C:\path\to\this\repo"
```

## Making changes

1. Open `Darkstar.sln` in Visual Studio 2022 (don't open an individual `.csproj` directly — see the note in `setup-dev-environment.ps1`'s parameter docs about why the exact folder layout matters).
2. Set `Darkstar` (and/or `Darkstar.Gui`) as the startup project.
3. Build and test locally against a real or test SRS server before opening a PR.
4. **Adding a project?** `Darkstar.csproj` is in the repository root, so anything you add lands underneath it. It excludes every `.cs` file in any subfolder (`<Compile Remove="*\**\*.cs" />`) precisely so that a new project does not get compiled into the bot — which fails with duplicate assembly attributes and "only one compilation unit can have top-level statements", errors that say nothing about the real cause. Don't weaken that exclusion; if the bot ever needs a subfolder of its own sources, add a matching `<Compile Include>` for it.
5. If you touch a `.razor` file in `Darkstar.Gui`, keep the explicit `@namespace` directive at the top of the file — it's a required workaround for a known Blazor Hybrid + WPF namespace-derivation bug ([dotnet/maui#5861](https://github.com/dotnet/maui/issues/5861)), not leftover cruft.
6. If you add a new `AppConfig` field, also add a validation check in `ValidateValues` and document it in [configuration.md](configuration.md) **and both manuals**. This is checked rather than trusted: the test suite reflects over `AppConfig` and `RadioConfig` and fails when a setting is missing from `manual-en.md`, `manual-de.md` or `configuration.md`. It does the same for the `--test-hotword` options, for the source-file map above, and for every internal cross-reference in the documentation — a renamed heading leaves its links dead, which is easy to do and invisible afterwards.
7. A new trigger list goes through `TriggerMatcher` and gets added to `AppConfig.FindVocabularyTriggerConflicts`, so a phrase that also sits in `vocabulary.json` is reported at startup.

## Pull requests

- Keep PRs focused on one change/feature at a time.
- Describe what changed and why in the PR description; screenshots are appreciated for GUI changes.
- Update [configuration.md](configuration.md), both manuals and [changelog.md](changelog.md) alongside any user-facing change. The documentation checks above will tell you if you missed a setting, but they cannot tell you the prose is right.
- Explain *why* in a comment wherever the obvious implementation would have been wrong. Much of this codebase's value is in those comments — the whole-word trigger matching, the anti-alias filter, the log-file-by-name rule and the coalition guard on friendly positions all look like over-engineering until you know what went wrong without them.

## Reporting issues

Please include your `config.json` (with `GeminiApiKey`, `DiscordWebhookUrl`, and `DcsGrpcApiKey` redacted) and the relevant excerpt from `logs/` when reporting a bug — most issues are much faster to diagnose with the actual log output than a description alone.
