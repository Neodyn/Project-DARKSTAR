# D.A.R.K.S.T.A.R. — Complete manual

*Deutsche Fassung: **[handbuch-de.md](handbuch-de.md)***

**D**igital **A**ssistant for **R**adio **K**eyword-activated **S**peech **T**ranscription **A**nd **R**esponse — a voice-controlled radio assistant for [DCS World](https://www.digitalcombatsimulator.com/) that joins a [DCS-SimpleRadio-Standalone](https://github.com/ciribob/DCS-SimpleRadio-Standalone) (SRS) server as an external AWACS-mode client.

Pilots key up on a monitored frequency, say the wake word, ask something over the radio — and the bot transcribes it, works out an answer, and transmits it back in a synthetic voice.

This manual covers everything: what you need, how to install it, every setting, how to use it in the air, and what to do when something doesn't work.

---

## Table of contents

1. [How it works](#1-how-it-works)
2. [What you need](#2-what-you-need)
3. [Installation](#3-installation)
4. [First start](#4-first-start)
5. [Configuration reference](#5-configuration-reference)
6. [The configuration editor (GUI)](#6-the-configuration-editor-gui)
7. [Using the bot on the radio](#7-using-the-bot-on-the-radio)
8. [Live mission data via DCS-gRPC](#8-live-mission-data-via-dcs-grpc)
9. [Running as a Windows Service](#9-running-as-a-windows-service)
10. [Files, logs and backups](#10-files-logs-and-backups)
11. [Troubleshooting](#11-troubleshooting)
12. [Costs, limits and privacy](#12-costs-limits-and-privacy)

---

## 1. How it works

One request travels through the bot like this:

```
Pilot transmits on 251.000
        │
        ▼
SRS server  ──UDP (Opus)──▶  Bot decodes audio
        │
        ▼
Vosk transcribes continuously, locally, offline
        │   wake word found ("Overlord")?
        ▼
Recording starts (including ~2 s of buffered audio from before the wake word)
        │   silence, or no more packets
        ▼
Recording ends
        │
        ├─▶ Tactical request ("bogey dope")?     → answer from live mission data (DCS-gRPC)
        ├─▶ Known phrase from phrases.json?      → fixed answer
        └─▶ otherwise                            → Google Gemini transcribes and answers
        │
        ▼
Reply text → DCS-SR-ExternalAudio.exe → Windows TTS → transmitted on the same frequency
```

Two things are worth knowing up front:

- **Wake-word detection runs entirely on your machine.** Vosk transcribes everything it hears locally and checks the text for the configured keyword. No audio leaves your PC for this step, there is no account and no per-request cost.
- **Only the actual question goes to the cloud**, and only when the wake word triggered — and only if the answer isn't already covered by a fixed phrase or by live mission data.

Each configured radio is fully independent: its own wake word, its own callsign, its own recording state. Two pilots can talk to two different frequencies at the same time without interfering with each other.

---

## 2. What you need

### Mandatory

| Requirement | Notes |
|---|---|
| **Windows** | The bot uses `DCS-SR-ExternalAudio.exe` and Windows TTS voices for its replies. |
| **.NET 8** | The **Desktop Runtime** covers both the bot and the GUI. Needed to *run* it. The installer brings it along; from source you need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). |
| **A running SRS server** | The bot connects as an external AWACS-mode client. It does not have to run on the same machine, but `DCS-SR-ExternalAudio.exe` currently transmits to `127.0.0.1` (see [Troubleshooting](#11-troubleshooting)). |
| **`DCS-SR-ExternalAudio.exe`** | Ships with the SRS **server** install. Used for every reply. |
| **A Vosk speech model** | Offline, free, no account. [Download list](https://alphacephei.com/vosk/models) — see the table below for which one to pick. |
| **A Google Gemini API key** | Free to create at [Google AI Studio](https://aistudio.google.com/apikey). Only needed for transcription and freely generated replies; the free tier is enough for testing and small groups. |
| **Visual C++ Redistributable (x64)** | Vosk's native `libvosk.dll` needs it. The installer and the dev-setup script both handle this. |

### Optional

| Optional | What it adds |
|---|---|
| **WebView2 Runtime** | Required by the GUI configuration editor (preinstalled on current Windows versions; the installer adds it if missing). |
| **DCS-gRPC server** | Enables the tactical replies ("bogey dope", "picture", "threat check") and the mission data explorer. See [chapter 8](#8-live-mission-data-via-dcs-grpc). |
| **A Discord webhook** | Start/stop and connection-loss notifications. |
| **Visual Studio 2022** | Only for building from source, with the workloads *.NET desktop development* and *ASP.NET and web development*. |

### Which Vosk model?

Recognition quality is the single biggest factor in how well the bot works. The small model is fast but weak; if the bot mishears wake words or triggers on the wrong ones, this is almost always the reason.

| Size | Model | Disk | Recommendation |
|---|---|---|---|
| Small | `vosk-model-small-en-us-0.15` | ~40 MB | Only for a first smoke test. Noticeably error-prone. |
| **Standard** | `vosk-model-en-us-0.22` | ~1.8 GB | **Recommended.** Clearly better wake-word accuracy, still fast enough. |
| Large | `vosk-model-en-us-0.42-gigaspeech` | ~2.3 GB | Best accuracy, higher CPU and RAM usage. |

The dev-setup script can download any of them for you (`-VoskModelSize Standard`).

---

## 3. Installation

### Option A: with the installer (recommended for a server)

The Inno Setup installer puts the bot, the GUI and a Vosk model onto a machine and installs **every** runtime dependency both need — the target machine only has to be Windows.

1. Run `Setup.exe`.
2. Pick the components (bot, GUI, Vosk model).
3. Optionally tick *"Install and start as a Windows Service"* — you can also do this later and more comfortably from the GUI (see [chapter 9](#9-running-as-a-windows-service)).

The installer checks each dependency and skips whatever is already present: .NET 8 Desktop Runtime, Visual C++ Redistributable, WebView2 Runtime.

Building the installer yourself is described in [building-the-installer.md](building-the-installer.md).

### Building the installer yourself

From the repository root, in PowerShell:

```powershell
.\build-installer.ps1
```

This checks the prerequisites (.NET 8 SDK, Inno Setup 6), publishes the bot and the GUI, downloads
the three dependency installers and a Vosk model, and compiles everything into
`installer\output\DARKSTAR-Setup-1.0.exe`. Copy that one file to the target machine and run it —
nothing else has to be prepared there.

```powershell
.\build-installer.ps1 -VoskModelSize Standard -Version 1.1   # recommended model, ~1.8 GB installer
.\build-installer.ps1 -Slim                                  # slim installer, no model bundled
.\build-installer.ps1 -DryRun                                # only check prerequisites
```

Everything shipped is built in **Release**; the script never uses Debug output. `-Slim` (same as
`-SkipVoskModel`) leaves the speech model out entirely, which turns a ~1.8 GB installer into a few
MB — useful when the target machine already has a model, or when you distribute it separately. The
file is then named `DARKSTAR-Setup-<version>-slim.exe`, and `VoskModelPath` has to be filled in by
hand after installing; without it the bot falls back to a placeholder that recognizes no words.

Re-running is safe: each step skips itself when its result is already there. Delete
`installer\vosk-model\` to bundle a different model size. More options are in
[building-the-installer.md](building-the-installer.md).


### Option B: from source

1. Clone the repository.
2. Run the setup script from an **elevated** PowerShell — it checks and installs the .NET 8 SDK, the required Visual Studio workloads, the Visual C++ Redistributable, the WebView2 Runtime and a Vosk model, verifies the project structure, and finishes with a trial build:

   ```powershell
   .\setup-dev-environment.ps1 -ProjectRoot "C:\path\to\repo" -VoskModelSize Standard
   ```

   | Parameter | Default | Purpose |
   |---|---|---|
   | `-ProjectRoot` | `K:\Darkstar\BOT_SRS` | Repository folder. |
   | `-VoskModelSize` | `Small` | `Small`, `Standard` or `Large` (see the table above). |
   | `-VoskModelUrl` | — | Download a different model instead; overrides `-VoskModelSize`. |
   | `-SkipWebView2` | off | Skip the WebView2 check. |
   | `-SkipVoskModel` | off | Skip the model download. |
   | `-SkipVCRedist` | off | Skip the Visual C++ Redistributable check. |

   The script is safe to re-run at any time — every step skips itself when it is already satisfied. An existing Vosk model is never replaced, even if you ask for a different size (delete the folder first if you want to switch).

3. Build and run:

   ```
   dotnet run --project Darkstar.csproj
   ```

4. For a production copy, publish it framework-dependent:

   ```
   dotnet publish Darkstar.csproj -c Release -r win-x64 --self-contained false -o publish\bot
   dotnet publish Darkstar.Gui\Darkstar.Gui.csproj -c Release -r win-x64 --self-contained false -o publish\gui
   ```

### Folder layout

```
Darkstar-Project/
├── Darkstar.sln                  Visual Studio solution
├── Darkstar.csproj               The bot (console app / Windows Service) → Darkstar.exe
├── Program.cs, BotService.cs, SrsConnection.cs, GeminiClient.cs, ...
├── Darkstar.Core/                Shared library: config, phrases, logging, backups,
│                                 DCS-gRPC access, Windows Service management
├── Darkstar.Gui/                 Configuration editor → Darkstar.ConfigEditor.exe
├── installer/                    Inno Setup script
└── setup-dev-environment.ps1     Dev machine bootstrap
```

**Important:** `Darkstar.csproj` sits in the repository root, *not* in a subfolder of its own — its project references point at `Darkstar.Core\` relative to itself. Moving it into a subfolder breaks the solution.

---

## 4. First start

1. **Start the bot once without a `config.json`.** It writes a `config.json` with default values next to the executable and exits, so you can review the settings before it connects to anything.

2. **Fill in the essentials** — either by editing `config.json` directly or, more comfortably, in the GUI (`Darkstar.ConfigEditor.exe`):

   | Setting | Meaning |
   |---|---|
   | `SrsHost`, `SrsPort` | Your SRS server (default `127.0.0.1:5002`). |
   | `Radios` | The frequencies to monitor, each with wake word and callsign. |
   | `VoskModelPath` | Folder of the unpacked Vosk model. |
   | `GeminiApiKey` | Your API key. |
   | `ExternalAudioExePath` | Path to `DCS-SR-ExternalAudio.exe` in the SRS server folder. |
   | `Coalition` | `2` = Blue, `1` = Red, `0` = Spectator. |

3. **Start the bot again.** The log should show, in order: phrases loaded, wake word active with the radio list, "Connecting to SRS server …", "Connected. Waiting for hotword…".

4. **Check it in DCS:** the bot appears in the SRS client list under `ClientName` (default `DARKSTAR`). Key up on a monitored frequency and say the wake word plus a question.

A quick way to verify the reply path on its own, without flying, is a manual test transmission:

```
cd "C:\Program Files\DCS-SimpleRadio-Standalone\Server"
DCS-SR-ExternalAudio.exe --text="Radio check, loud and clear." --freqs=251.000 --modulations=AM --coalition=2 --port=5002 --name="TEST"
```

If you hear that in DCS, TTS and SRS transmission both work, and anything still broken is on the recognition side.

---

## 5. Configuration reference

The bot reads three JSON files next to its executable. All three are created with sensible defaults if missing, validated on load (bad values produce a `WARNING` but never block startup), automatically extended with new fields after an update, and **always backed up to `Backup\` before any automatic change**.

> A full reference also lives in [configuration.md](configuration.md); this chapter is the same content in reading order.

### 5.1 `config.json`

#### Logging

| Field | Default | Description |
|---|---|---|
| `LoggingEnabled` | `true` | Master kill switch. `false` = nothing is logged at all. |
| `DebugLogging` | `false` | Verbose console output (UDP packets, raw JSON, volume readings). The log *file* always has full detail regardless. |

#### SRS connection

| Field | Default | Description |
|---|---|---|
| `SrsHost` | `"127.0.0.1"` | SRS server address. |
| `SrsPort` | `5002` | SRS server port. |
| `ClientName` | `"DARKSTAR"` | Name shown in the SRS client list. |
| `ExternalAwacsPassword` | `""` | Only if the server requires `EXTERNAL_AWACS_MODE` with a password. |

#### Radios

| Field | Default | Description |
|---|---|---|
| `Radios` | `[]` | The radios to monitor simultaneously. Each entry: `FrequencyHz`, `Modulation` (`"AM"`/`"FM"`), optional `Keyword`, optional `Callsign`. |
| `FrequencyHz` | `251000000` | Legacy single radio in Hz, only used while `Radios` is empty. |
| `Modulation` | `"AM"` | Legacy single radio modulation. |

```json
"Radios": [
  { "FrequencyHz": 251000000, "Modulation": "AM", "Keyword": "Overlord", "Callsign": "Overlord" },
  { "FrequencyHz": 127500000, "Modulation": "AM", "Keyword": "Texaco",   "Callsign": "Texaco" }
]
```

Each radio reacts **only** to its own wake word. Empty `Keyword`/`Callsign` fall back to the global `VoskKeyword`/`BotCallsign`.

#### Bot identity

| Field | Default | Description |
|---|---|---|
| `BotCallsign` | `"Overlord"` | Callsign the bot identifies with (global default, per radio overridable). |
| `PlayerNameCallsignSeparator` | `"\|"` | Cuts the pilot's callsign out of their SRS name: `Enfield 1-1 \| neodym` → the bot says "Enfield 1-1". If the separator isn't present, the full name is used. |

Hyphens are replaced by spaces before speech, so "1-1" is spoken as "one one" and not "eleven".

#### Coalition security

| Field | Default | Description |
|---|---|---|
| `Coalition` | `2` | The bot's own side: `0` = Spectator, `1` = Red, `2` = Blue. |
| `RestrictToOwnCoalition` | `false` | `true` = requests from the opposing coalition are ignored entirely (no reply at all). Unknown/spectator senders are still answered. |

#### Wake word and recording

| Field | Default | Description |
|---|---|---|
| `VoskModelPath` | `""` | Folder of the unpacked Vosk model. Without it the bot falls back to a volume-based placeholder that recognizes no words at all. |
| `VoskKeyword` | `"computer"` | Global wake word, used by radios that don't set their own. |
| `SilenceFramesToStopRecording` | `50` | Silent 20 ms frames that end a recording (50 = 1 second). |
| `PreRollSeconds` | `2.0` | Seconds of audio kept before the wake word and prepended to the recording, so the start of the message isn't lost. |
| `HotwordEnergyThreshold` | `2000` | Only for the placeholder detector (no Vosk model). |
| `HotwordConsecutiveFramesNeeded` | `5` | Only for the placeholder detector. |

The keyword is matched as a **whole word**, case-insensitively.

#### Speech-to-text and replies (Google Gemini)

| Field | Default | Description |
|---|---|---|
| `GeminiApiKey` | `""` | API key. Without it there is no transcription. |
| `GeminiModel` | `"gemini-3.5-flash-lite"` | Model for the combined transcription + reply call. Flash-Lite variants have the highest free-tier quota. |
| `GeminiFallbackModel` | `""` | Optional second model, tried when the first keeps failing (e.g. quota exhausted). |
| `GeminiMaxRetries` | `2` | Retries per model on transient errors (429/500/502/503/504). |
| `GeminiRetryDelayMs` | `1000` | Delay between retries. |

Transcription and reply are one single API call — that halves the quota usage compared to separate calls.

#### Fixed phrases

| Field | Default | Description |
|---|---|---|
| `RestrictToKnownPhrases` | `true` | `true` = the bot answers **only** triggers from `phrases.json`, everything else gets `FallbackResponse`. `false` = anything unmatched is answered freely by Gemini. |
| `FallbackResponse` | `"Sorry, cannot answer that for you."` | The reply used when nothing matches. |

#### Reply audio

| Field | Default | Description |
|---|---|---|
| `ExternalAudioExePath` | `C:\Program Files\DCS-SimpleRadio-Standalone\Server\DCS-SR-ExternalAudio.exe` | The SRS tool used for transmitting. |
| `VoiceName` | `""` | Windows TTS voice, e.g. `"Microsoft David Desktop"`. Empty = default voice. `DCS-SR-ExternalAudio.exe --help` lists the options. |
| `ExternalAudioExtraArgs` | `""` | Extra arguments appended to every transmission, for options this bot doesn't set itself. Whether your SRS build has a speaking-rate flag, and what it's called, depends on its version — check `--help`, then put it here (e.g. `--speed=-1`). |

#### "Standby" acknowledgement for slow replies

| Field | Default | Description |
|---|---|---|
| `AckEnabled` | `false` | Master switch for the holding message. |
| `AckAfterSeconds` | `4.0` | How long the bot may stay silent, counted **from the wake word**. If the real reply is ready sooner, nothing is sent. |
| `AckMessage` | `"{pilot}, this is {callsign}, message received, standby."` | `{pilot}` = the pilot's callsign, `{callsign}` = this radio's callsign. If the pilot is unknown, `"{pilot}, "` is dropped automatically. |

The bot never talks over a pilot: if the time expires while they are still transmitting, the acknowledgement goes out right after they stop. Acknowledgement and reply can never overlap.

#### DCS-gRPC connection

| Field | Default | Description |
|---|---|---|
| `DcsGrpcEnabled` | `false` | Master switch for everything that reads live mission data. |
| `DcsGrpcAddress` | `"http://127.0.0.1:50051"` | Address of the DCS-gRPC server, including the scheme. |
| `DcsGrpcApiKey` | `""` | DCS-gRPC 0.7.x has no authentication of its own — leave this empty unless you run it behind a proxy that requires a key. |

#### Tactical replies

| Field | Default | Description |
|---|---|---|
| `DcsIntelEnabled` | `false` | Master switch. Also needs `DcsGrpcEnabled`. |
| `DcsIntelContactSource` | `"AwacsThenMissionData"` | `AwacsThenMissionData`, `AwacsOnly` (god's eye off) or `MissionDataOnly`. See [chapter 8.3](#83-turning-on-the-tactical-replies). |
| `DcsIntelAwacsUnitName` | `""` | Mission-editor name of the unit whose sensors decide what may be reported. Empty = god's eye, unless the mode forbids it. |
| `DcsIntelMaxRangeNm` | `120` | Contacts further out are not reported. `0` = no limit. |
| `DcsIntelMaxGroups` | `3` | Groups reported per picture call before the rest is summarized. |
| `DcsIntelMagneticBearings` | `true` | Magnetic instead of true bearings (what the pilot reads on their instruments). |
| `DcsIntelIncludeHelicopters` | `true` | Include hostile helicopters. |
| `DcsIntelSayContactType` | `true` | Name the aircraft/helicopter type. DCS variant suffixes are stripped (`F-16C_50` → `F-16C`); a group flying several types is called "mixed, lead <type>". |
| `DcsIntelSlowSpeech` | `true` | Paces the numbers: comma between the digits of a bearing, ranges/altitudes/counts spelled out as words. Off = terser, faster phrasing. |
| `DcsIntelTimeoutSeconds` | `5` | Timeout per mission-data query. |
| `DcsIntelBogeyDopeTriggers` | `["bogey dope", "bogie dope", "bogey dobe", "boogie dope", "nearest bandit", "closest contact"]` | Matched case-insensitively anywhere in the transcription. The deliberate misspellings catch common recognition errors. |
| `DcsIntelPictureTriggers` | `["picture", "request picture", "say picture"]` | |
| `DcsIntelThreatTriggers` | `["threat check", "any threats", "threats"]` | |
| `DcsIntelBullseyeTriggers` | `["bullseye"]` | Forces the bullseye format instead of BRAA. |
| `DcsIntelNoContactsReply` | `"Picture clean."` | When nothing matches the filters. |
| `DcsIntelUnavailableReply` | `"Negative, no tactical data available at this time."` | When the mission data can't be read at all. |

#### Threat circle (standing watch)

| Field | Default | Description |
|---|---|---|
| `DcsIntelThreatCircleEnabled` | `true` | Master switch (needs `DcsIntelEnabled`). |
| `DcsIntelThreatCircleDefaultRadiusNm` | `40` | Radius when the pilot names none. |
| `DcsIntelThreatCircleMaxRadiusNm` | `150` | Upper limit for a requested radius. |
| `DcsIntelThreatCirclePollSeconds` | `15` | Seconds between sweeps. Each sweep costs one set of mission queries per active circle. |
| `DcsIntelThreatCircleDurationMinutes` | `30` | Automatic expiry. |
| `DcsIntelThreatCircleMaxActive` | `8` | Circles running at the same time. |
| `DcsIntelThreatCircleMaxAlertsPerSweep` | `2` | Warnings per circle and sweep; the rest follow later. |
| `DcsIntelThreatCircleTriggers` | `["threat circle", "threat ring", "set threat circle"]` | Start phrases. |
| `DcsIntelThreatCircleCancelTriggers` | `["cancel threat circle", "stop threat circle", "threat circle off", "cancel threat ring"]` | Cancel phrases. |
| `DcsIntelThreatCircleNoPilotReply` | `"Unable to set the threat circle, cannot locate your aircraft."` | |
| `DcsIntelThreatCircleBusyReply` | `"Unable, too many threat circles active at the moment."` | |
| `DcsIntelThreatCircleCancelledReply` | `"Threat circle cancelled."` | |
| `DcsIntelThreatCircleNoneActiveReply` | `"No threat circle active for you."` | |


#### Discord notifications

| Field | Default | Description |
|---|---|---|
| `DiscordEnabled` | `false` | Master switch, off by default. |
| `DiscordWebhookUrl` | `""` | Webhook for start/stop and SRS connection loss/recovery. Create it in Discord under *Channel settings → Integrations → Webhooks*. |

### 5.2 `phrases.json`

Fixed trigger/answer pairs. If a trigger appears anywhere in the transcribed text (case-insensitive), its answer is used verbatim instead of a generated one. The first match wins.

```json
[
  { "Trigger": "radio check", "Response": "Radio check, loud and clear, five by five." },
  { "Trigger": "status",      "Response": "All systems nominal." },
  { "Trigger": "request rtb", "Response": "Copy, cleared to RTB." }
]
```

The transcription call still happens either way — the recognized text is what the trigger is matched against; only the *reply* is replaced.

### 5.3 `vocabulary.json`

A plain list of terms passed to Gemini as a hint. It doesn't change what the bot can talk about, it just improves recognition of words that aren't ordinary English:

```json
["Viggen", "Overlord", "Bullseye", "Texaco", "Enfield"]
```

---

## 6. The configuration editor (GUI)

`Darkstar.ConfigEditor.exe` edits all three files graphically — and uses exactly the same code the bot itself uses to read and write them, so nothing can drift apart. It finds the bot's config folder automatically by searching its own folder, the folders above it, and their `bin\` subfolders.

The bot does **not** need to be stopped to look at settings, but changed settings only take effect when the bot is restarted.

| Channel | Contents |
|---|---|
| **CH1 Connection** | Config folder, SRS host/port, client name, EAM password, coalition, coalition restriction. |
| **CH2 Radios** | The radio list: frequency, modulation, per-radio wake word and callsign, add/remove. |
| **CH3 Speech** | Gemini key/model/retries, TTS voice, pre-roll, Vosk model folder, global wake word, silence frames, the standby acknowledgement, and the placeholder detector's settings. |
| **CH4 Phrases** | The trigger/answer table plus `RestrictToKnownPhrases` and the fallback reply. |
| **CH5 Vocabulary** | The hint word list as chips. |
| **CH6 Discord** | Master switch and webhook URL. |
| **CH7 Logging** | Logging switches, resolved log folder, and a live tail of the newest log file. |
| **CH8 DCS-gRPC** | Connection test, tactical-reply settings with a test button, and the mission data explorer. |
| **CH9 Service** | Install, start, stop and cleanly remove the Windows Service. |

Bottom bar: **Discard (reload from disk)** and **Save changes**. Every save makes a timestamped backup first.

Details on CH8 and CH9 are in the next two chapters; a full walkthrough is in [gui.md](gui.md).

---

## 7. Using the bot on the radio

### The basic pattern

1. Select a monitored frequency in SRS.
2. Key up, say the **wake word** of that radio, then your request, in one transmission:
   *"Overlord, radio check."*
3. Release the PTT. The bot recognizes the end of the transmission from the silence (or from the packets stopping).
4. It answers on the same frequency: *"Enfield 1-1, this is Overlord… Radio check, loud and clear, five by five."*

The wake word may be anywhere in the sentence — the recording keeps ~2 seconds of what came before it, so nothing gets cut off if you trigger it mid-sentence.

### What determines the answer

The bot decides in this order:

1. **Tactical request** (bogey dope / picture / threat check) → answered from the running mission via DCS-gRPC. Takes priority because it's the only source that is actually *true*.
2. **Known phrase** from `phrases.json` → the fixed answer.
3. **Otherwise** → depends on `RestrictToKnownPhrases`: either the fallback reply, or a freely generated Gemini answer.

### Tactical calls

| Request | Example reply |
|---|---|
| *"Overlord, bogey dope"* | *"Bogey, bearing zero, niner, zero, thirty five miles, twenty two thousand, hot, group of two, type MiG-29."* |
| *"Overlord, picture"* | *"Picture: two groups. Lead group, bullseye two, seven, zero, for forty miles, twenty five thousand, two contacts, Su-27. …"* |
| *"Overlord, threat check"* | *"Nearest contact zero, niner, zero at thirty five miles, twenty two thousand."* |
| *"Overlord, bogey dope bullseye"* | Same as bogey dope, but positions given from the bullseye. |

Bearings are spoken digit by digit ("zero niner zero"), because TTS would otherwise read `090` as "ninety". With `DcsIntelSlowSpeech` (on by default) there is a comma between the digits and the other numbers are spelled out as words, which keeps the voice from rushing them. Aircraft and helicopter types are announced when `DcsIntelSayContactType` is on. Aspect follows standard brevity: **hot** (nose on), **flanking**, **beaming**, **cold** (running away).

For BRAA from your own aircraft, the bot has to find *your* aircraft: it matches your SRS name against the DCS player names. If that fails — for example because your SRS name is nothing like your DCS name — it automatically switches to the bullseye format instead of refusing the request.

### Threat circle: a standing watch

Instead of asking again and again, a pilot can arm a watch around their own aircraft:

| On the radio | What happens |
|---|---|
| *"Overlord, threat circle forty miles"* | *"Threat circle active, forty miles."* — plus how many contacts are already inside. |
| *"Overlord, threat circle"* | Same, using the configured default radius. |
| *(a hostile enters the circle)* | *"Enfield 1-1, this is Overlord… Threat, bearing zero, niner, five, twenty two miles, twenty two thousand, hot, type MiG-29."* |
| *"Overlord, cancel threat circle"* | *"Threat circle cancelled."* |

The circle **moves with the aircraft** — it is always centred on where the pilot is now, not where they were when they asked. The radius can be spoken as digits or words ("forty", "twenty five", "one hundred").

Each contact is announced **once per circle**: a bandit that leaves and comes back does not trigger a second warning, so a contact loitering at the edge can't turn into a stream of calls. If several hostiles enter at once, the nearest ones are called first and the rest follow on the next sweeps rather than occupying the frequency all at once.

A circle ends when the pilot cancels it, after the configured time limit, or when the pilot leaves the mission (slot change, logout, shot down). Warnings wait for a pilot who is currently transmitting — but only briefly, since a late threat call is worse than a slightly overlapping one.


### Multiple radios

Every radio answers only to its own wake word and with its own callsign. A tanker frequency can run as "Texaco" while the AWACS frequency runs as "Overlord", both at the same time, each with its own conversation.

### Things that are normal

- **The bot doesn't hear itself.** While it transmits a reply, that radio is muted for itself — otherwise it would answer its own answer forever.
- **A reply takes a moment.** Transcription plus generation is typically 2–5 seconds. The standby acknowledgement (`AckEnabled`) exists to bridge exactly that gap.
- **It only reacts after the wake word.** Everything else on the frequency is ignored — but it *is* being transcribed locally the whole time, which is how the wake word is found.

---

## 8. Live mission data via DCS-gRPC

DCS-gRPC is a separate, free server component that exposes a running mission over the network. The bot uses it for the tactical replies and for the mission data explorer in the GUI.

### 8.1 Installing DCS-gRPC (on the DCS side)

Per the [official documentation](https://github.com/DCS-gRPC/rust-server):

1. Download the release archive and extract it into your DCS **Saved Games** folder (typically `C:\Users\<name>\Saved Games\DCS` or `…\DCS.openbeta_server`). Afterwards you have `Scripts\DCS-gRPC\`, `Mods\Tech\DCS-gRPC\` and `Scripts\Hooks\DCS-gRPC.lua` there.

2. Add this line to `MissionScripting.lua` in the DCS install folder (`…\DCS World\Scripts\MissionScripting.lua`), directly after `dofile('Scripts/ScriptingSystem.lua')`:

   ```lua
   dofile(lfs.writedir()..[[Scripts\DCS-gRPC\grpc-mission.lua]])
   ```

3. Create `Saved Games\DCS\Config\dcs-grpc.lua` so the server starts with every mission:

   ```lua
   autostart = true
   host = "127.0.0.1"   -- "0.0.0.0" if the bot runs on a different machine
   port = 50051
   ```

4. Start DCS and load a mission. To verify, look for `GRPC` entries in `Logs\dcs.log`, or for a `Logs\grpc.log` file.

> **Note:** DCS-gRPC 0.7.x has no authentication of its own. If the host listens on `0.0.0.0`, anyone who can reach that port can read mission data — restrict it with your firewall.

### 8.2 Connecting the bot

1. GUI → **CH8 DCS-gRPC** → switch **Enable DCS-gRPC** on, check address and port.
2. **Test connection.** A successful test also proves that a mission is actually running, because it asks for the mission's in-game time.

### 8.3 Turning on the tactical replies

Still on CH8, under **Tactical replies**:

- Switch **Answer tactical requests from mission data** on.
- **Contact source** and **AWACS unit name:** together they decide where contacts may come from — see below.
- Adjust range, group count and the trigger phrases as you like.
- **"Try it without flying"** shows the exact sentence the bot would speak, plus where the data came from — nothing is transmitted.

#### Where the contacts come from

Two settings decide this: **Contact source** (`DcsIntelContactSource`) and the **AWACS unit name**.

| Mode | AWACS sensors | God's eye | "Sensors see nothing" means |
|---|---|---|---|
| **AWACS sensors, mission data as fallback** (default) | used when a unit is named | used as fallback | fall back to mission data |
| **AWACS sensors only** | required | **never** | a clean picture is reported |
| **Mission data only** | ignored | always | — |

In the **default** mode the bot asks the unit's sensors first and uses plain mission data whenever they deliver nothing — on an error, a missing unit, *and* on an empty detection table. That last case matters: DCS only fills the detection table for **AI-controlled** units, so a player-flown AWACS always returns an empty list, and over the API "currently sees nothing" is indistinguishable from "can never see anything". Taking it at face value would make the bot call "picture clean" while ten bandits are inbound. The price of that safety is that while your AWACS detects nothing, you silently get god's eye.

**AWACS sensors only** turns that off: what the unit detects is what gets reported, and an empty table is answered honestly as "picture clean". This is the realistic setting — but only with an **AI** AWACS, since a player-flown one will always look blind. If the sensor call fails, or no unit is named, the bot says "no tactical data" instead of pretending the sky is empty, and a threat circle skips that sweep rather than implying the airspace is clear.

**Mission data only** ignores the unit name entirely and always reports every hostile aircraft.

Which source was actually used is in every `[Intel]` log line (`source=AWACS 'Overlord-1' sensors` or `source=mission data (god's eye)`), and *"Try it without flying"* shows the same — the quickest way to check you aren't silently running on the fallback.

### 8.4 The mission data explorer

Below the tactical settings, the explorer shows what a running mission exposes — as raw JSON, with 38 queries across mission, time, world, coalition, players, units, weather and live event streams. It is strictly read-only: only `Get`/`Stream` calls are made, nothing in the mission is changed, so it is safe to use on a live server with players on it.

- **Run snapshot** runs every query that needs no input in one go.
- Names found in results (units, groups, airbases) are offered as autocomplete in the name fields, so you can drill down from *Groups* → *Units of a group* → *Detected targets*.
- Results can be copied or saved to `grpc-dumps\` in the config folder.

This is the tool to use when you want to know what else could be built on top of the mission data.

---

## 9. Running as a Windows Service

As a service the bot runs in the background and after every reboot, with nobody logged in.

The comfortable way is GUI → **CH9 SERVICE**:

- The panel shows the live state, the start type and **which executable is actually registered** — with a warning when that differs from the configured path, which is how a stale installation gets spotted.
- **Install**: the executable path is pre-filled with `Darkstar.exe` next to the config folder. Optionally with delayed auto-start (so the network and the SRS server are up first) and optionally started right away.
- **Stop and remove**: asks for confirmation, stops the service, waits until it has really stopped, and only then deletes it. That order matters — deleting a running service only marks it for deletion and leaves a ghost entry behind until the next reboot.
- **Start / Stop** for the installed service.

Every operation that changes a service asks for administrator rights via UAC; the editor itself does not need to run elevated. Reading the status needs no rights at all.

Two things worth knowing:

- **The service runs the executable you registered**, and reads `config.json` from *that* folder. If you keep a second copy of the bot somewhere for testing, make sure you're editing the config the service actually uses — the panel shows the registered path for exactly this reason.
- **A service has no console window.** Use the log files under `logs\` to see what it's doing.

Alternatively the installer can register the service during installation, or you can do it by hand with `sc.exe`.

---

## 10. Files, logs and backups

Everything lives next to the bot's executable:

| Path | Contents |
|---|---|
| `config.json` | All settings. |
| `phrases.json` | Fixed question/answer pairs. |
| `vocabulary.json` | Transcription hints. |
| `logs\` | Timestamped log files, always with full detail. |
| `Backup\` | Automatic timestamped copies made before any automatic change. |
| `grpc-dumps\` | JSON results saved from the mission data explorer. |

The log is the first place to look when something is off. A healthy startup looks roughly like this:

```
N fixed reply phrase(s) loaded from phrases.json.
Wake word detection active (Vosk, offline), 2 radio(s):
  251.000 MHz (AM): wake word "Overlord", callsign "Overlord"
  127.500 MHz (AM): wake word "Texaco", callsign "Texaco"
Connecting to SRS server 127.0.0.1:5002, monitoring: ...
Connected. Waiting for hotword...
```

And during a request:

```
[Hotword Detected] 251.000 MHz: starting recording (sender: "Enfield 1-1 | neodym" -> callsign "Enfield 1-1", ...)
[Recording finished] 251.000 MHz: 96000 bytes of PCM, transcribing...
[STT] "overlord bogey dope"
[Intel] 251.000 MHz: BogeyDope request answered from mission data (source=..., contacts=3, reference=unit 'Enfield-1-1')
[Reply] 251.000 MHz: "Enfield 1-1, this is Overlord... Bogey, bearing ..."
```

The `[STT]` lines are the most useful of all: they show what the bot actually *understood*, which answers most "why did it do that?" questions immediately.

---

## 11. Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| **Bot doesn't appear in the SRS client list** | Wrong `SrsHost`/`SrsPort`, server not running, or firewall. Check the log for the connection line. |
| **Bot reacts to nothing** | `VoskModelPath` empty or wrong (the log says so at startup), wrong frequency/modulation, or the wake word isn't being recognized — check the `[STT]` lines. |
| **Wake word is only recognized sometimes** | Model too weak. Switch to `Standard` (`-VoskModelSize Standard`) and delete the old model folder first. This is by far the most common cause. |
| **A radio reacts to the wrong wake word** | Almost always a mis-transcription by a weak model — check `[STT]`. Keywords are matched as whole words, so a longer word containing the keyword won't trigger it. |
| **No reply, log shows a Gemini error** | Key missing/invalid, or quota exhausted. Set `GeminiFallbackModel`, or wait for the quota to reset. |
| **Reply is generated but never heard** | `ExternalAudioExePath` wrong, or TTS voice not installed. Test it manually (see [chapter 4](#4-first-start)). |
| **Bot answers its own replies** | Should be impossible — the radio is self-muted while transmitting. If it happens anyway, please report it with the log. |
| **Numbers are rattled off / hard to understand** | Switch `DcsIntelSlowSpeech` on (GUI: CH8 → *Slow, clearly spoken numbers*). If the voice itself is too fast overall, try a different `VoiceName`, or a speaking-rate flag via `ExternalAudioExtraArgs` if your SRS version supports one. |
| **Reply arrives very late** | Normal for 2–5 s. Turn on the standby acknowledgement so pilots know they were heard. |
| **Threat circle warnings never arrive** | Check the log for `[ThreatCircle]` lines: they show every sweep result. Most often the pilot's aircraft can't be matched to their SRS name (the circle needs it as its centre), or the circle already expired. |
| **Tactical requests say "no tactical data"** | DCS-gRPC not running, no mission loaded, wrong address, or `DcsGrpcEnabled` off. Test it on CH8. |
| **Tactical replies always use bullseye instead of BRAA** | Your SRS name couldn't be matched to a DCS player name. Make the two similar, or use the separator convention (`CALLSIGN 1-1 \| handle`). |
| **"Picture clean" although enemies are up** | The configured AWACS unit is player-flown or doesn't exist, plus a range limit that's too tight. Clear `DcsIntelAwacsUnitName` for a test, or raise `DcsIntelMaxRangeNm`. |
| **Service won't start** | Usually a path problem: check the registered executable on CH9, and that `config.json` sits in *that* folder. |
| **Service can't be removed** | Something still holds a handle on it (services.msc, Task Manager). Close both and retry; a reboot always clears it. |
| **GUI shows "config.json exists but has invalid JSON"** | Deliberate: the editor refuses to overwrite a broken file with defaults. Fix the syntax (the log names the position) and reload. |
| **Transmission to a remote SRS server doesn't arrive** | `DCS-SR-ExternalAudio.exe` is currently called without a server parameter, so it transmits to `127.0.0.1`. With a remote SRS server the replies stay local. Check `--help` for the right parameter name and ask for it to be wired in. |

Still stuck? The log file plus the `[STT]` lines around the failure usually explain it, and are what to include in a bug report (with `GeminiApiKey`, `DiscordWebhookUrl` and `DcsGrpcApiKey` redacted).

---

## 12. Costs, limits and privacy

- **What costs money:** nothing, in normal use. Vosk, SRS and DCS-gRPC are free and local; the Gemini free tier is enough for testing and small groups. Heavy use can exceed the free daily quota — that's what `GeminiFallbackModel` is for.
- **What leaves your machine:** only the recorded question, and only after the wake word triggered, and only when it isn't answered by a fixed phrase or by mission data. Continuous listening for the wake word happens entirely locally.
- **What is logged:** the recognized text of every processed request lands in the log file. If that matters in your group, say so — or turn logging off with `LoggingEnabled`.
- **Limits worth knowing:** replies use Windows TTS, so quality depends on the installed voices. Vosk works best on English speech. The tactical replies are only as good as the mission data DCS exposes.

---

## Further reading

- [README.md](../README.md) — project overview
- [configuration.md](configuration.md) — configuration reference
- [gui.md](gui.md) — the configuration editor in detail
- [contributing.md](contributing.md) — development notes
- [building-the-installer.md](building-the-installer.md) — building the installer
- [changelog.md](changelog.md) — what changed

License: GPL-3.0 — see [LICENSE](../LICENSE).
