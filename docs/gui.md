# Config Editor GUI (`Darkstar.Gui`)

A desktop configuration editor for `config.json`, `phrases.json`, and `vocabulary.json` — an alternative to hand-editing JSON, built as a **Blazor Hybrid** app: a native WPF window hosting a WebView2 pane that renders Razor components. This lets the UI reuse plain HTML/CSS (dark, AWACS/avionics-inspired styling) while calling straight into `Darkstar.Core` with no serialization boundary — the same `AppConfig`, `PhraseBook`, and `VocabularyBook` classes the bot service itself uses.

Build/run target: `Darkstar.ConfigEditor.exe` (assembly name of the `Darkstar.Gui` project), `net8.0-windows`, requires the WebView2 Runtime (installed automatically by `setup-dev-environment.ps1` or the Inno Setup installer if missing).

## Shared config with the bot

On startup, the GUI resolves the same `config.json`/`phrases.json`/`vocabulary.json` the bot service reads, by first checking its own folder, then walking up the ancestor directory chain, and at each level also searching any `bin\` subfolder recursively — so it finds the bot's actual output folder (e.g. `bin\Debug\net8.0\`) even though that's a sibling branch rather than a direct ancestor of the GUI's own build output. If no existing config is found anywhere, it falls back to its own folder and lets you create fresh files there.

The footer has two buttons:
- **Discard** — reverts all unsaved changes by reloading from disk.
- **Save** — writes `config.json`, `phrases.json`, and `vocabulary.json` back to disk, automatically backing up the previous version of each to `Backup/` first (see [configuration.md](configuration.md#backups)).

## Channels

The sidebar navigation is styled after a radio's channel selector (CH1–CH9), one channel per settings panel:

### CH1 — Connection
SRS server host/port, client name (as shown in the SRS client list), optional external AWACS mode (EAM) password, the path to `DCS-SR-ExternalAudio.exe`, the bot's own coalition (Blue/Red/Spectator, as three selectable buttons), and a toggle to restrict replies to the bot's own coalition.

A **"Detect SRS installation"** button below the path field looks the executable up instead of having it typed: it first tries to make sense of whatever is already in the field (a folder — the SRS directory or the `ExternalAudio` folder itself — is resolved to the executable inside it), then falls back to searching both `Program Files` folders, per-user install locations and every fixed drive for the layouts SRS is known to use. It reports what it found, says so when the configured path was already correct, and leaves the field untouched when there is nothing to find. The probing lives in `Darkstar.Core\SrsPaths.cs`, which the installer's own detection mirrors in Pascal script.

### CH2 — Radios
Manages the `Radios` list: each card shows one radio's frequency (Hz), modulation (AM/FM dropdown), and optional per-radio wake word and callsign — left as placeholders showing the current global default when empty. Radios can be added or removed freely. If the list is empty, a notice explains that the bot falls back to the legacy single-radio fields, which are also editable here in a separate card below the list.

### CH3 — Speech
All Gemini and hotword-detection settings: API key, primary and optional fallback model, retry count/delay, TTS voice name, pre-roll seconds, the Vosk model folder path, the global wake word (used by any radio that doesn't set its own), and silence-frame count to end a recording. A toggle plus two fields configure the "message received, standby" acknowledgement for slow replies (`AckEnabled` / `AckAfterSeconds` / `AckMessage`, with `{pilot}` and `{callsign}` placeholders). A **"Wake word accuracy"** card holds the audio preparation (a low-pass anti-alias filter before the 48 kHz → 16 kHz reduction, or the old three-sample average for comparison), the optional automatic gain for quiet pilots, and the switch that saves every transmission to `recordings\` — tagged by whether the wake word fired, which is what makes `Darkstar.exe --test-hotword recordings --compare` able to grade a change instead of leaving it to impressions (see [manual-en.md, chapter 12](manual-en.md#12-wake-word-accuracy)). A separate card holds the energy-threshold fallback detector's settings, used only when no Vosk model is configured.

### CH4 — Phrases
An editable table of trigger/response pairs (`phrases.json`), with a header toggle for `RestrictToKnownPhrases` and a field for the fallback response used when nothing matches. Rows can be added or removed individually.

### CH5 — Vocabulary
A chip-based editor for `vocabulary.json`: existing terms are shown as removable chips, and a text field (with an "Add" button and Enter-to-add) appends new ones, case-insensitively deduplicated. The panel notes that this list only affects transcription accuracy, not what the bot responds to (that's CH4).

### CH6 — Discord
A single toggle for `DiscordEnabled` and the webhook URL field, which is disabled (grayed out) while the toggle is off — reinforcing that the integration is entirely opt-in. Lists the three events that trigger a notification: bot started/connected, SRS connection lost/restored, and bot shutting down.

### CH7 — Logging
Toggles for `LoggingEnabled` and `DebugLogging`, a read-only display of the resolved log folder path, and a live-refreshable tail of the most recent log lines read directly from the current log file — so you can check that the bot (running separately) is behaving correctly without leaving the GUI.

### CH8 — DCS-gRPC
A toggle for `DcsGrpcEnabled`, fields for the server address and optional API key (both disabled while the toggle is off), and a **"Test connection"** button that calls `DcsGrpcTester.TestConnectionAsync` against `MissionService.GetScenarioCurrentTime` — confirming that a DCS-gRPC server is reachable and actively receiving data from a running mission. Everything below on this channel builds on that connection: the tactical replies, the threat circle and the mission data explorer (see [manual-en.md, chapter 8](manual-en.md#8-live-mission-data-via-dcs-grpc)).

#### Tactical replies

Between the connection test and the explorer sits the configuration for the bot's tactical replies ("bogey dope", "picture", "threat check", answered from live mission data — see [configuration.md](configuration.md#tactical-replies-from-live-mission-data)): the master switch, the contact source (AWACS sensors with or without a god's-eye fallback, or mission data only) with a warning when the chosen mode leaves no usable source, the AWACS unit name whose sensors gate what may be reported, range/group/timeout limits, toggles for magnetic bearings, helicopters and naming aircraft types, the trigger phrases (one comma-separated line per request type), and the two canned replies for "nothing found" and "no data".

A **threat circle** card configures the standing watch pilots can request by radio (radius defaults and limits, sweep interval, expiry, how many circles may run at once, how many warnings one sweep may transmit, and the start/cancel phrases).

A **"Try it without flying"** card runs a real request against the running mission and shows the exact sentence the bot would speak, plus a line saying where the data came from (AWACS sensors or mission data) and which reference point was used. It works with the values currently on screen, so settings can be tried before saving, and nothing is transmitted on SRS. Entering a pilot's SRS name gives BRAA from that pilot's aircraft; leaving it empty shows the bullseye variant.

#### Mission data explorer

Below the connection test, the **Mission data explorer** shows exactly which data a running mission exposes through DCS-gRPC, as raw JSON (every field, including empty/default values). It is strictly **read-only** — only `Get*`/`Is*`/`Stream*` calls are made; nothing that spawns units, sends messages, sets flags, kicks players or pauses/stops the mission is ever called, so it is safe to use on a live server.

- **Run snapshot** — runs every query that needs no input in one go (mission name/file/description, scenario and timer times, session ID, paused state, theatre, airbases, F10 marks, groups, player units, static objects, connected players, server state, and the bullseye of both Blue and Red) and combines them into one JSON document keyed by query ID. A failing call doesn't abort the snapshot; its entry contains the error instead (typically the `Hook` calls, which need DCS-gRPC's hook part running on the server).
- **Single query** — a picker grouped by category, each showing the underlying RPC and a short description. Only the inputs the selected query needs are shown:

| Category | Queries | Inputs |
|---|---|---|
| Mission | name, file, description, scenario start/current time, session ID, paused, trigger user flag | flag name (user flag only) |
| Time | model time, absolute time, time zero, real time | — |
| World | theatre, airbases/FARPs/carriers, F10 map marks | coalition (airbases) |
| Coalition | bullseye, groups, player-occupied units, static objects | coalition, category (groups) |
| Players & server | connected players, multiplayer?, is server?, ban list, ballistics count | — |
| Group & unit | units of a group, unit details, position, transform, descriptor, player in unit, radar status, detected targets | group or unit name |
| Environment | wind, wind with turbulence, temperature & pressure, magnetic declination | lat/lon/alt |
| Live streams | mission events, unit movements | listen duration, poll rate, category |

- **Name suggestions** — every `name` value found in any result (units, groups, airbases, players…) is collected and offered as autocomplete in the unit/group name fields, so you can drill down from *Groups* → *Units of a group* → *Unit details* / *Detected targets* without typing mission-editor names by hand.
- **Take position from unit** — for the environment queries: looks up the entered unit's current position and fills in latitude/longitude/altitude.
- **Live streams** listen for a configurable number of seconds (max 120) or 500 messages, whichever comes first, and can be cancelled at any time. Each recorded message carries a `receivedAfterSeconds` timestamp.
- **Copy JSON** copies the full result to the clipboard; **Save to file** writes it to `grpc-dumps/<timestamp>_<query>.json` in the config folder. Very large results are truncated in the on-screen view only (the copied/saved JSON is always complete).

The explorer is built against the `RurouniJones.Dcs.Grpc` 0.7.1 bindings. A server running a newer DCS-gRPC version still answers these calls; RPCs added in later versions appear once the NuGet package is updated.

### CH9 — Windows Service
Installs, removes, starts and stops the bot's Windows Service, so it runs in the background and after every reboot without anyone logging in.

The panel shows the live state (not installed / running / stopped), the display name, the start type, and **which executable the service is actually registered to run** — with a warning when that differs from the path configured below it, which is how a stale installation pointing at an old folder gets spotted. On startup it checks the known service names (`Darkstar` and the installer's `D.A.R.K.S.T.A.R.`) and preselects whichever one exists.

- **Install** registers the executable (pre-filled as `Darkstar.exe` next to the resolved config folder, since the service reads the config from its own folder), optionally with delayed auto-start so the network and the SRS server are up first, and optionally starts it right away.
- **Stop and remove** asks for confirmation, then stops the service, waits until it has really reached "Stopped", and only then deletes it — deleting a running service would just mark it for deletion and leave a ghost entry behind until the next reboot.
- **Start / Stop** control the installed service without removing it.

Every operation that changes a service needs administrator rights, so Windows shows a UAC prompt each time; the editor itself does not have to run elevated. Reading the status needs no elevation. If the UAC prompt is dismissed, the panel says so instead of failing silently. The output of the elevated commands is shown in a details box when something goes wrong.

Internally this goes through PowerShell (`Get-Service` / `New-Service` / `Start-Service`) rather than parsing `sc.exe` output, because `sc.exe` localizes its field labels — screen-scraping it would break on a German Windows.

## Known build requirements

Building `Darkstar.Gui` has a few non-obvious requirements, all already applied in this repo but worth knowing if something breaks after an update:

- The project must use the **`Microsoft.NET.Sdk.Razor`** SDK (not the plain `Microsoft.NET.Sdk`) — otherwise `.razor` files aren't compiled at all, surfacing as missing-type errors for `BlazorWebView`/`RootComponent` at runtime.
- `Microsoft.AspNetCore.Components.WebView.Wpf` must be version **`8.0.100`** (not `8.0.10`, which doesn't exist) — and no explicit `Microsoft.Web.WebView2` version should be pinned; let it resolve transitively to avoid a version mismatch.
- Every `.razor` file needs an explicit `@namespace` directive as its first line (`Darkstar.Gui` for root-level components, `Darkstar.Gui.Pages` for panels) — a documented workaround for a Blazor Hybrid + WPF namespace-derivation bug ([dotnet/maui#5861](https://github.com/dotnet/maui/issues/5861)) that otherwise produces "Cannot find the type 'local:Main'" or similar errors.
- WPF-specific files (`App.xaml(.cs)`, `MainWindow.xaml(.cs)`) must live inside the `Darkstar.Gui` project folder, not the bot's console-app folder, which has no WPF support.

See [contributing.md](contributing.md) for the general dev-environment setup.
