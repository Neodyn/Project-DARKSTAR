# Config Editor GUI (`Darkstar.Gui`)

A desktop editor for `config.json`, `phrases.json` and `vocabulary.json` — an alternative to
hand-editing JSON.

Built as a **Blazor Hybrid** app: a WPF window hosting a WebView2 pane that renders Razor
components. That lets the interface reuse plain HTML/CSS while calling straight into
`Darkstar.Core` — the same `AppConfig`, `PhraseBook` and `VocabularyBook` classes the bot itself
runs on, with no serialization boundary between them.

- **Executable:** `Darkstar.ConfigEditor.exe`, `net8.0-windows`
- **Needs:** the WebView2 Runtime (installed by `setup-dev-environment.ps1` or the installer)
- **Title bar** carries the build: `1.2 (built 2026-10-08 14:33 UTC)`, or `0.0.0-dev` for one built
  from source. Small and grey on purpose — reference information, not a headline, but always
  visible, because the question it answers only comes up when something is already wrong.

## Shared config with the bot

On startup the editor looks for the same files the bot service reads:

1. its own folder,
2. every folder above it,
3. any `bin\` subfolder of those, searched recursively.

Step 3 is what finds the bot's real output folder (`bin\Debug\net8.0\`), which is a sibling branch
rather than an ancestor. If nothing is found anywhere, it falls back to its own folder and offers
to create fresh files there.

The footer has two buttons:

- **Discard** — reverts every unsaved change by reloading from disk.
- **Save** — writes all three files back, backing the previous version of each up to `Backup/`
  first (see [configuration.md](configuration.md#backups)).

## The sidebar

Styled after a radio's channel selector, one channel per settings panel. The channels are grouped
by the question you are answering rather than by their number:

| Group | Channels |
|---|---|
| **SERVER** | CH1 Connection, CH9 Service |
| **RADIO & SPEECH** | CH2 Radios, CH3 Speech, CH4 Phrases, CH5 Vocabulary |
| **MISSION DATA — CH8** | Server & test, Replies, Mission explorer |
| **MONITORING** | CH6 Discord, CH7 Logging |

Two things about that table are deliberate:

- **A panel never changes its number.** Around ninety places in this documentation say "on CH2
  Radios" — so panels may be regrouped and reordered, but CH7 is the log today and tomorrow.
- **CH8 is three entries rather than one.** It had grown into a thousand-line page holding the
  connection, every reply setting, two test buttons and a 38-query explorer; no amount of grouping
  in the sidebar helps a page nobody can find anything on. All three are still CH8.

An entry is marked **off** when the thing it configures is switched off in the configuration. That
means "you turned this off", never "this is unreachable right now" — a connection that cannot be
reached is a different problem, reported on the panel itself.

---

## CH1 — Connection

| Setting | Notes |
|---|---|
| SRS host and port | |
| Client name | As it appears in the SRS client list. |
| EAM password | Only if the server requires external AWACS mode with one. |
| `DCS-SR-ExternalAudio.exe` | With a **Detect SRS installation** button, see below. |
| Callsign separator | How a pilot's callsign is cut out of their SRS name. The hint spells out the fallbacks and why `-` is never one. |
| Own coalition | Blue / Red / Spectator, as three buttons. |
| Restrict to own coalition | Ignore the opposing side entirely. |

**Detect SRS installation** looks the executable up instead of having it typed:

1. It first makes sense of whatever is already in the field — a folder, whether the SRS directory
   or the `ExternalAudio` folder itself, is resolved to the executable inside it.
2. Failing that, it searches both `Program Files` folders, per-user install locations and every
   fixed drive for the layouts SRS is known to use.

It reports what it found, says so when the configured path was already correct, and leaves the
field untouched when there is nothing to find. The probing lives in `Darkstar.Core\SrsPaths.cs`,
which the installer mirrors in Pascal script.

## CH2 — Radios

Manages the `Radios` list. Each radio is a card:

| Field | Notes |
|---|---|
| Frequency, modulation | Hz, and AM/FM. |
| Wake word, callsign | Per radio. Empty shows the global default as a placeholder. |
| Three role switches | **Default / On / Off** for tactical requests, airfield requests and friendly positions, with a line underneath stating what this radio ends up answering. |
| Also accept as the wake word | Extra spellings for how the recognizer really hears it. |
| Voice | So the AWACS, the tanker and the tower sound like three people. Empty falls back to the global voice. |

**Why friendly positions are their own role** rather than part of the tactical one: it is a
different kind of decision. The tactical replies are about the enemy, that one is about your own
people — so a server can put it on a single squadron frequency and nowhere else.

A separate tower frequency is made right here: airfield on and tactical off, the reverse on the
AWACS frequency. The buttons are greyed out when a feature is off globally, since a radio can
narrow what the bot does but never widen it.

**The wake-word variants field** is what a non-English-speaking crew needs: a German speaker's
"Overlord" often arrives as `over lord`, and the space alone defeats the whole-word match. The
hint changes with the radio — one using the global wake word inherits the global variants, one
with its own does not, because variants follow the word rather than the radio. Both say the list
should come from a `--suggest-variants` run rather than from guesswork, since every variant also
raises the false-trigger rate.

**List available voices** (a card above the list) runs `DCS-SR-ExternalAudio.exe --help` and shows
what this machine really has, as chips and as autocomplete in every voice field. Deliberately an
autocomplete and not a dropdown: with Azure or Google configured the valid names are the
provider's, and a picker would make exactly the best-sounding option impossible to enter. The
panel also says why that list is shorter than the Windows voice settings suggest — ExternalAudio
speaks through SAPI5, which cannot see Windows' modern "natural" voices.

Below the role switches sits `WrongChannelReply`, the sentence that sends a pilot to the frequency
that does serve their request. The hint states the rule that makes it safe: it is used only when
**exactly one** other radio serves the request, and emptying the field switches the handoff off.

### One tower per airfield

Reads the airfield list from the mission loaded right now and adds a radio per airfield. The base
frequency, step and callsign suffix are editable next to the button.

- It leads with a **warning card** rather than a hint: the generated frequencies are *invented* —
  DCS-gRPC does not report an airfield's real one — so the F10 announcement is what makes them
  findable at all.
- The card also says why the wake word stays the global one instead of becoming the airfield's
  name, since that is the first thing somebody would otherwise "improve".
- Generated radios are added **unsaved**, so they can be reviewed before Save changes.
- A second button removes the towers that look generated, which is what a map change needs since
  generating is additive. It names how many it found, refuses when the callsign suffix is empty
  (no fingerprint left worth trusting), and leaves hand-built radios alone.

### Announce the frequencies in game

The other half of that feature: the master toggle, one toggle each for the two halves (the durable
F10 marker per airfield, the transient on-screen message at startup) and how long that message
stays up. It says what each half is for, warns when DCS-gRPC is off — writing into a mission needs
it — and notes that the announcement runs after the SRS connection and off the startup path.

This card was added late: the four `AnnounceFrequencies*` settings were documented in three files
and editable only in `config.json`, while the tower card pointed at CH8 for them, where they had
never been.

### Greet a pilot who tunes in

A toggle, the sentence with its three placeholders, and the minimum seconds between two greetings
on one frequency.

It carries a warning card for the same reason the tower generator does — the warning is the
feature's most important documentation. SRS has no unicast, so the greeting is heard by everybody
on the frequency rather than by the pilot who caused it, which is why it ships switched off. The
card says what the throttles guarantee (once per client per frequency for the whole session; one
greeting for a flight of four checking in together) and when to switch it off again.

If the radio list is empty, a notice explains that the bot falls back to the legacy single-radio
fields, editable in their own card below.

## CH3 — Speech

Everything about hearing and answering:

| Card | Holds |
|---|---|
| Gemini | API key, primary and optional fallback model, retry count and delay. |
| Voice | The global TTS voice, with the same **List available voices** button as CH2, a note that a per-radio voice overrides it, and the Azure/Google credentials hint on the extra-arguments field. |
| Detection | Pre-roll seconds, Vosk model folder, global wake word and its variants, silence frames to end a recording. |
| Standby acknowledgement | `AckEnabled` / `AckAfterSeconds` / `AckMessage`, with `{pilot}` and `{callsign}`. |
| Rate limit | Requests per pilot per sliding window, and what to say when somebody goes over. |
| Wake word accuracy | See below. |
| Fallback detector | The energy-threshold settings, used only when no Vosk model is configured. |

The rate-limit card states its reasoning in the panel rather than in a tooltip: one pilot
repeating the wake word can use up the Gemini quota and hold the frequency at the same time, and
neither failure looks like the pilot who caused it.

**Wake word accuracy** holds the audio preparation (a low-pass anti-alias filter before the
48 kHz → 16 kHz reduction, or the old three-sample average for comparison), the optional automatic
gain for quiet pilots, and the switch that saves every transmission to `recordings\` — tagged by
whether the wake word fired. That tagging is what lets
`Darkstar.exe --test-hotword recordings --compare` grade a change instead of leaving it to
impressions (see [manual-en.md, chapter 12](manual-en.md#12-wake-word-accuracy)). The two
retention limits for that folder sit next to it.

## CH4 — Phrases

An editable table of trigger/response pairs (`phrases.json`), with:

- a header toggle for `RestrictToKnownPhrases`,
- the fallback response used when nothing matches, and
- `UnintelligibleReply` beside it, for the different case of a transmission that held nothing
  intelligible at all. Asking for a repeat is honest where the fallback would pretend the question
  had been understood.

A **Radio check** card below holds its own toggle, the trigger list and three replies: one for
when no mission data was consulted, one for when the caller was matched to a unit, and one for
when they were not.

It sits on this channel rather than CH8 because it is the same kind of thing as the table above
it, and because it works without DCS-gRPC. The card says so, and also that a `radio check` row in
the table takes priority over all of it.

## CH5 — Vocabulary

A chip editor for `vocabulary.json`: existing terms are removable chips, a text field (with an Add
button and Enter-to-add) appends new ones, deduplicated case-insensitively. The panel notes that
this list only affects transcription accuracy, not what the bot responds to — that is CH4.

Any term that is **also a trigger phrase** is drawn in red above a warning card. The two lists
must stay separate because speech recognition snaps anything that merely sounds like a vocabulary
term onto its exact spelling: a command phrase here turns an unclear transmission into that
command, and the bot answers it instead of asking for a repeat. The check is
`AppConfig.FindVocabularyTriggerConflicts`, and the bot logs the same warning at startup.

## CH6 — Discord

A toggle for `DiscordEnabled` and the webhook URL, which stays greyed out while the toggle is off
— the integration is entirely opt-in.

The panel lists the five events that send a message:

| Event | |
|---|---|
| Started and connected | With the frequencies it monitors. |
| SRS connection lost / restored | Once per outage, not once per five-second retry. |
| Shutting down | |
| Start failed | With the reason. |
| Stopped unexpectedly | With the error. |

The last two are the ones that matter on a server nobody is watching: a bot that never came up is
otherwise indistinguishable from a quiet frequency.

## CH7 — Logging

- Toggles for `LoggingEnabled` and `DebugLogging`. The latter is off by default because per-packet
  logging is expensive — and an error dumps the last 200 verbose lines by itself, so turning it on
  is only needed to watch something live.
- The resolved log folder and its two retention limits (`LogRetentionDays` / `LogRetentionMaxMb`),
  with a note that the three newest files are never deleted; the one being written is among them.
- The file currently being shown, and a tail of its last 40 lines read straight from disk — so a
  bot running as a service can be watched without leaving the editor. A **Live** switch re-reads
  every two seconds, which is as fresh as it gets: the bot forces its log to disk about once a
  second.

**The file is picked by the timestamp in its name**, not by its modification time. That matters
more than it sounds. Windows does not update a file's size or modification time while a handle is
open on it, so the log being written looks older — and 0 bytes long — next to a finished one from
an earlier run. Sorting by modification time therefore showed the *previous* run's log until the
bot was stopped. See `LogFiles.PickNewest` in `Darkstar.Core`.

## CH8 — DCS-gRPC

Three sidebar entries, one component:

| Entry | Holds |
|---|---|
| **Server & test** | The connection and the test button. |
| **Replies** | Everything answered from mission data. |
| **Mission explorer** | The read-only query browser. |

The split is driven by `DcsGrpcPanel.SectionServer` / `SectionReplies` / `SectionExplorer`. The
sidebar names a section by constant rather than by string, and the test suite checks that the
declared sections, the blocks rendering them and the sidebar entries are the same three — a
section named in one place and not the other fails silently otherwise, as a blank page or as
settings nothing can reach.

### Server & test

A toggle for `DcsGrpcEnabled`, the server address and optional API key (both disabled while the
toggle is off), and a **Test connection** button.

The test calls `DcsGrpcTester.TestConnectionAsync` against `MissionService.GetScenarioCurrentTime`
— which confirms both that a DCS-gRPC server is reachable and that it is receiving data from a
running mission. Everything else on this channel builds on that connection (see
[manual-en.md, chapter 8](manual-en.md#8-live-mission-data-via-dcs-grpc)).

### Replies

Everything the bot answers from the running mission.

**Tactical replies** ("bogey dope", "picture", "threat check" — see
[configuration.md](configuration.md#tactical-replies-from-live-mission-data)):

- the master switch;
- the contact source — AWACS sensors with or without a god's-eye fallback, or mission data only —
  with a warning when the chosen mode leaves no usable source;
- the AWACS unit name whose sensors gate what may be reported;
- range, group and timeout limits;
- toggles for magnetic bearings, helicopters and naming aircraft types;
- the trigger phrases, one comma-separated line per request type, including **alpha check** (a
  pilot's own position) and **bullseye**, which is not a request of its own but a word added to
  one;
- the canned replies for "nothing found", "no data" and "caller not found".

**Airfield** turns on "runway in use" and ATIS: the trigger phrases, whether the altimeter is read
in hectopascals, inches or both, the two distances that let a pilot's own position decide the
airfield instead of its spoken name, the two fallback replies, and a **Show reply** test that
prints the sentence plus which airfield was used, the wind, and why that runway won.

> The panel states that runway headings need `evalEnabled = true` on the DCS-gRPC server — they
> come from `Airbase.getRunways()`, reachable only through `Eval`. Without it the weather still
> works and the runway is reported as unknown. Taxiways are not available from DCS at all.

**Friendly positions** turns on "where is Springfield 2-1?": the master switch, the trigger
phrases, the three refusal replies (pilot not found, nobody named, caller's coalition unknown) and
whether the friendly's heading is included.

It leads with a warning card rather than a hint, because this is the one feature whose default
says something about the server rather than about convenience: it is off, only human players on
the caller's own side are ever reported, and a caller whose coalition cannot be determined is
refused outright.

**Threat circle** configures the standing watch pilots can request by radio: radius defaults and
limits, sweep interval, expiry, how many circles may run at once, how many warnings one sweep may
transmit, the start and cancel phrases, and the four spoken replies (cancelled, nothing was
running, the caller's aircraft could not be found, too many circles active).

**Try it without flying** runs a real request against the running mission and shows the exact
sentence the bot would speak, plus where the data came from and which reference point was used. It
uses the values currently on screen, so settings can be tried before saving, and nothing is
transmitted on SRS. Entering a pilot's SRS name gives BRAA from that pilot's aircraft; leaving it
empty shows the bullseye variant.

### Mission data explorer

Shows exactly which data a running mission exposes through DCS-gRPC, as raw JSON — every field,
including empty and default values.

It is strictly **read-only**: only `Get*`, `Is*` and `Stream*` calls are made. Nothing that spawns
units, sends messages, sets flags, kicks players or pauses the mission is ever called, so it is
safe to use on a live server.

**Run snapshot** runs every query that needs no input in one go — mission name, file and
description, scenario and timer times, session ID, paused state, theatre, airbases, F10 marks,
groups, player units, static objects, connected players, server state, and the bullseye of both
coalitions — and combines them into one JSON document keyed by query ID. A failing call does not
abort the snapshot; its entry carries the error instead, typically the `Hook` calls, which need
DCS-gRPC's hook part running on the server.

**Single query** is a picker grouped by category, each showing the underlying RPC and a short
description. Only the inputs the selected query needs are shown:

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

Four things make it usable rather than merely complete:

- **Name suggestions** — every `name` value found in any result is collected and offered as
  autocomplete in the unit and group fields, so you can drill down from *Groups* → *Units of a
  group* → *Unit details* without typing mission-editor names by hand.
- **Take position from unit** — for the environment queries: looks up that unit's position and
  fills in latitude, longitude and altitude.
- **Live streams** listen for a configurable number of seconds (max 120) or 500 messages,
  whichever comes first, and can be cancelled. Each message carries a `receivedAfterSeconds` mark.
- **Copy JSON** / **Save to file** — the latter writes `grpc-dumps/<timestamp>_<query>.json` in the
  config folder. Large results are truncated in the on-screen view only; what is copied or saved
  is always complete.

Built against the `RurouniJones.Dcs.Grpc` 0.7.1 bindings. A server running a newer DCS-gRPC still
answers these calls; RPCs added later appear once the package is updated.

## CH9 — Windows Service

Installs, removes, starts and stops the bot's Windows Service, so it runs in the background and
after every reboot without anyone logging in.

The panel shows the live state (not installed / running / stopped), the display name, the start
type, and **which executable the service is actually registered to run** — with a warning when
that differs from the path configured below it, which is how a stale installation pointing at an
old folder gets spotted. On startup it checks the known service names (`Darkstar` and the
installer's `D.A.R.K.S.T.A.R.`) and preselects whichever exists.

| Button | What it does |
|---|---|
| **Install** | Registers the executable — pre-filled as `Darkstar.exe` next to the resolved config folder, since the service reads the config from its own folder — optionally with delayed auto-start so the network and SRS are up first, and optionally starts it right away. |
| **Stop and remove** | Asks for confirmation, stops the service, waits until it has really reached "Stopped", and only then deletes it. Deleting a running service merely marks it for deletion and leaves a ghost entry until the next reboot. |
| **Start / Stop** | Controls the installed service without removing it. |

Every operation that changes a service needs administrator rights, so Windows shows a UAC prompt
each time; the editor itself does not have to run elevated, and reading the status needs no
elevation. A dismissed prompt is reported rather than failing silently, and the output of the
elevated commands is shown in a details box when something goes wrong.

Internally this goes through PowerShell (`Get-Service` / `New-Service` / `Start-Service`) rather
than parsing `sc.exe` output, because `sc.exe` localizes its field labels — screen-scraping it
would break on a German Windows.

## Known build requirements

All already applied in this repo, but worth knowing if something breaks after an update:

- **The `Microsoft.NET.Sdk.Razor` SDK**, not the plain `Microsoft.NET.Sdk`. Otherwise `.razor`
  files are not compiled at all, which surfaces as missing-type errors for
  `BlazorWebView`/`RootComponent` at runtime.
- **`Microsoft.AspNetCore.Components.WebView.Wpf` version `8.0.100`** (not `8.0.10`, which does not
  exist), and no pinned `Microsoft.Web.WebView2` version — let it resolve transitively to avoid a
  mismatch.
- **An explicit `@namespace` directive** as the first line of every `.razor` file (`Darkstar.Gui`
  for root components, `Darkstar.Gui.Pages` for panels). A documented workaround for a Blazor
  Hybrid + WPF namespace-derivation bug ([dotnet/maui#5861](https://github.com/dotnet/maui/issues/5861))
  that otherwise produces "Cannot find the type 'local:Main'".
- **WPF files in the GUI project folder** (`App.xaml(.cs)`, `MainWindow.xaml(.cs)`), not in the
  bot's console-app folder, which has no WPF support.

See [contributing.md](contributing.md) for the general dev-environment setup.
