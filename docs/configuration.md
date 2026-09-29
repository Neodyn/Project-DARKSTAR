# Configuration reference

*Looking for a step-by-step guide instead of a field reference? See the full manual: **[manual-en.md](manual-en.md)** (English) / **[manual-de.md](manual-de.md)** (Deutsch).*

D.A.R.K.S.T.A.R. reads its settings from three JSON files next to the executable, all of which are created automatically with sensible defaults on first run if missing:

- **`config.json`** — connection, radios, speech, and behavior settings (this document's main focus)
- **`phrases.json`** — fixed question/answer pairs
- **`vocabulary.json`** — transcription hint words

All three are validated on load (out-of-range or missing values produce a `WARNING` in the log, but never block startup) and are always backed up to a `Backup/` subfolder before any automatic edit. When new fields are added in an update, existing files are merged with the new defaults automatically — your existing values are never overwritten, and you'll see a log line listing what was added.

The GUI editor (`Darkstar.Gui`) provides a graphical front-end for all of this and is generally the easier way to change these files.

## `config.json`

### Logging

| Field | Default | Description |
|---|---|---|
| `LoggingEnabled` | `true` | Master kill switch. When `false`, nothing is logged at all (console or file) — saves I/O in performance-critical deployments. |
| `DebugLogging` | `false` | Records verbose/raw messages (every UDP packet, raw TCP JSON, volume readings) to the console **and** the log file. Off, they are not written — the UDP path alone produced about 123 MB of string garbage per hour per talking pilot and wrote the same again to disk, for output nobody was reading. Nothing is lost when it matters: the last 200 verbose lines are kept in memory and written to the log file automatically whenever an error is logged, so a failure always arrives with its run-up. |
| `LogRetentionDays` | `30` | Log files older than this are deleted. `0` disables the age rule. |
| `LogRetentionMaxMb` | `200` | `logs\` is trimmed oldest-first until it fits. `0` disables the size rule. The three newest files are never deleted, so the log being written to cannot be removed by a budget that is too small. |

Both folders are pruned at startup and once an hour, by age first and then by size; `FileRetention.Decide` is a pure function of the file list so the arithmetic is tested with numbers rather than by filling a disk. Only `*.wav` in `recordings\` and `*.log` in `logs\` are ever considered — `Backup\` and `grpc-dumps\` exist precisely because you might want them later, and are left alone.

### Discord notifications (optional)

| Field | Default | Description |
|---|---|---|
| `DiscordEnabled` | `false` | Master switch. Notifications are entirely opt-in — nothing is sent unless this is `true`. |
| `DiscordWebhookUrl` | `""` | Webhook URL for bot start/stop and SRS connection loss/recovery notifications. Only used when `DiscordEnabled` is `true`. Create one in Discord under a channel's *Settings → Integrations → Webhooks → New Webhook*. |

### SRS connection

| Field | Default | Description |
|---|---|---|
| `SrsHost` | `"127.0.0.1"` | SRS server hostname/IP. |
| `SrsPort` | `5002` | SRS server TCP/UDP port. |
| `ClientName` | `"DARKSTAR"` | Name the bot registers as in the SRS client list. |
| `ExternalAwacsPassword` | `""` | Only needed if the server requires `EXTERNAL_AWACS_MODE` with a password. |

### Radios

| Field | Default | Description |
|---|---|---|
| `FrequencyHz` | `251000000` | Legacy single-radio frequency in Hz (251.000 MHz). Only used if `Radios` is empty. |
| `Modulation` | `"AM"` | Legacy single-radio modulation (`"AM"` or `"FM"`). Only used if `Radios` is empty. |
| `Radios` | `[]` | List of radios to monitor simultaneously. Each entry: `FrequencyHz`, `Modulation`, optional `Keyword`, optional `Callsign`, optional `Voice`. Takes priority over the legacy fields above once it has entries. |
| `Radios[].Voice` | `""` | TTS voice for this radio's replies. Empty falls back to the global `VoiceName`. |

Each radio gets its own independent hotword detector, recording buffer, and reply channel — activity on one frequency never affects another. `Keyword`, `Callsign` and `Voice` per radio are optional; when left empty, a radio falls back to the global `VoskKeyword`/`BotCallsign`/`VoiceName` below. This lets you run, for example, an AWACS frequency answering to "Overlord" and a tanker frequency answering to "Texaco" at the same time, in two different voices:

```json
"Radios": [
  { "FrequencyHz": 251000000, "Modulation": "AM", "Keyword": "Overlord", "Callsign": "Overlord", "Voice": "Microsoft Hazel Desktop" },
  { "FrequencyHz": 127500000, "Modulation": "AM", "Keyword": "Texaco",   "Callsign": "Texaco",   "Voice": "Microsoft David Desktop" }
]
```

The voice is resolved once at startup and travels with the radio that answers, so it applies to tactical replies, ATIS, the standby acknowledgement and fixed phrases alike. The startup log names each radio's voice, which is the only warning you get: a name this machine doesn't have fails silently, with no audio and no error on the radio.

### Bot identity & callsign parsing

| Field | Default | Description |
|---|---|---|
| `BotCallsign` | `"Overlord"` | Callsign the bot uses to identify itself in replies (global default; overridable per radio, see above). |
| `Radios[].AnswerTacticalRequests` | `null` | Whether this radio answers bogey dope, picture, threat check and the threat circle. `null` follows `DcsIntelEnabled`; `true`/`false` decide for this radio alone. The global switch stays the master — a radio can narrow what the bot does, never widen it. |
| `Radios[].AnswerAirfieldRequests` | `null` | The same for "runway in use" and ATIS, against `DcsAirfieldEnabled`. Setting one radio to airfield-only and another to tactical-only is how you get a separate tower frequency. |
| `PlayerNameCallsignSeparator` | `"\|"` | Separator used to parse a pilot's callsign out of their SRS player name. With the default `"\|"`, a player named `Enfield 1-1 | neodym` is addressed as `Enfield 1-1` — everything after the separator (typically the player's real name/handle) is ignored. If the separator isn't found, `/`, `\`, `:`, `;` and `~` are tried as well, and failing that the full name is used. `-` is never treated as a separator, since it belongs to flight numbers like `1-1`. Squadron tags in brackets (`[ISAF] Mobius 1`, `Mobius 1 (VF-1)`) are stripped, so the bot doesn't read them out. |

### Coalition security

| Field | Default | Description |
|---|---|---|
| `Coalition` | `2` | The bot's own coalition: `0` = Spectator, `1` = Red, `2` = Blue. |
| `RestrictToOwnCoalition` | `false` | When `true`, requests from the opposing coalition (looked up from the SRS client list) are ignored entirely — no reply is sent at all, matching realistic radio security. Requests from an unknown/spectator coalition are still answered normally. |

### Known-phrase restriction

| Field | Default | Description |
|---|---|---|
| `RestrictToKnownPhrases` | `true` | When `true`, the bot **only** answers phrases defined as triggers in `phrases.json`; everything else gets `FallbackResponse`. When `false`, unmatched input falls through to a freely generated Gemini reply instead. |
| `FallbackResponse` | `"Sorry, cannot answer that for you."` | Fixed reply used when `RestrictToKnownPhrases` is active and nothing in `phrases.json` matches. |

### Radio check

Answered on **every** radio, before the per-radio role gating and before both classifiers — a tower, an AWACS and a tanker all answer a radio check, and it needs no mission data at all, which is the whole point when you are trying to find out whether anything works. A `radio check` entry in `phrases.json` still wins, so an upgrade never changes wording somebody chose themselves; the default `phrases.json` no longer creates that entry.

| Field | Default | Description |
|---|---|---|
| `RadioCheckEnabled` | `true` | Master switch. |
| `RadioCheckTriggers` | `["radio check", "comm check", "how do you read", "how do you hear me"]` | Matched on word boundaries, like every other trigger list. |
| `RadioCheckReply` | `"Loud and clear."` | No mission data consulted — nothing is claimed about radar. |
| `RadioCheckReplyWithContact` | `"Loud and clear, contact."` | The caller's SRS name was matched to a unit in the mission. |
| `RadioCheckReplyNoContact` | `"Loud and clear, but no radar contact on you."` | It was not — which is the failure that otherwise silently turns every BRAA call into a bullseye call. |

A failed or slow lookup answers with `RadioCheckReply`, never `RadioCheckReplyNoContact`: a pilot whose radio works should not be told the bot cannot see them because DCS-gRPC happened to time out. The lookup is one unit query — no contacts, no declination — so the reply is not delayed by the sensor side of the mission being slow.

### Hotword detection

| Field | Default | Description |
|---|---|---|
| `VoskModelPath` | `""` | Path to an unpacked [Vosk model](https://alphacephei.com/vosk/models) folder. This is the primary (and effectively only supported) wake-word engine. |
| `VoskKeyword` | `"computer"` | Global default keyword the continuously transcribed text is checked against. Overridable per radio via `Radios[].Keyword`. |
| `VoskKeywordVariants` | `[]` | Further spellings that also count as the wake word. The hotword runs through a small English model, and on a server where most pilots are not native English speakers "Overlord" arrives as `over lord` — which fails the whole-word match on the space alone, so the bot stays silent with nothing to show why. Listing what the model really produces fixes that without touching the model. |
| `Radios[].KeywordVariants` | `[]` | The same, per radio. **Variants follow the word, not the radio:** a radio using the global wake word inherits the global variants, while a radio with its own `Keyword` starts from an empty list — otherwise a tanker on "Texaco" would quietly begin answering to a variant configured for the AWACS. |

Every accepted variant also raises the false-trigger rate, so the list is meant to be measured rather than guessed. `Darkstar.exe --test-hotword recordings --suggest-variants` reads the transcripts the model produced on the recordings that were supposed to trigger and didn't, proposes the candidates within an edit distance of the wake word (measured with spaces removed, so a word split in two scores 0), and flags any candidate that also appears on `_silence_` recordings instead of recommending it. `HotwordVariants` in `Darkstar.Core` holds both the resolution rule and the suggestion logic, both as pure functions so the arithmetic is tested rather than trusted.

Wake words and trigger phrases must **never** go into `vocabulary.json` — that list makes the transcriber snap anything that merely sounds like an entry onto its exact spelling, turning unintelligible audio into a command nobody gave. Pronunciation variants belong here.
| `HotwordAudioFilter` | `"LowPass"` | How 48 kHz radio audio is reduced to the 16 kHz Vosk wants. `LowPass` filters before discarding samples, so content above 8 kHz cannot fold down into the speech range and be misheard (measured: fold-over 60–79 dB down, against 5–22 dB for the old path). `"Average"` is that old path, kept only for comparing the two on the same recordings. See [manual-en.md, chapter 12](manual-en.md#12-wake-word-accuracy). |
| `HotwordAutoGain` | `false` | Amplifies quiet pilots before the wake word is looked for. Off by default: automatic gain also lifts background noise, and lifted noise is what produces wake words nobody said. Never affects the audio that is transcribed or saved. |
| `SaveRecordings` | `false` | Writes every transmission to `recordings\` as a WAV, tagged `_hit_` or `_missed_`, so accuracy can be measured with `Darkstar.exe --test-hotword recordings --compare`. Roughly 100 KB per second of speech, pruned by the two fields below. |
| `RecordingRetentionDays` | `7` | Recordings older than this are deleted. `0` disables the age rule. |
| `RecordingRetentionMaxMb` | `500` | `recordings\` is trimmed oldest-first until it fits. `0` disables the size rule. |
| `HotwordEnergyThreshold` | `2000` | Volume threshold (0–32767) for the placeholder energy-based detector, only used as a last-resort fallback if `VoskModelPath` is left empty. Does not recognize actual words. |
| `HotwordConsecutiveFramesNeeded` | `5` | Consecutive "loud" 20ms frames needed before the placeholder detector fires. Only relevant to the fallback above. |
| `SilenceFramesToStopRecording` | `50` | Consecutive "silent" 20ms frames (1 second at the default) after the hotword that end the recording. |
| `PreRollSeconds` | `2.0` | Seconds of audio always buffered before a hotword match and prepended to the recording, so the start of the actual message isn't cut off while detection is still triggering. |

### Speech-to-text & reply generation (Google Gemini)

| Field | Default | Description |
|---|---|---|
| `GeminiApiKey` | `""` | API key from [Google AI Studio](https://aistudio.google.com/apikey). Required — without it, transcription and reply generation do nothing. |
| `GeminiModel` | `"gemini-3.5-flash-lite"` | Model used for the combined transcription + reply call. Flash-Lite variants generally have a higher free-tier daily quota than the larger Flash models. |
| `GeminiFallbackModel` | `""` | Optional second model tried if `GeminiModel` keeps failing after all retries (e.g. quota exhausted). Leave empty to disable. |
| `GeminiMaxRetries` | `2` | Retry attempts per model after a transient error (429/500/502/503/504) before giving up. |
| `GeminiRetryDelayMs` | `1000` | Delay in milliseconds between retry attempts. |

### Reply audio

| Field | Default | Description |
|---|---|---|
| `ExternalAudioExePath` | `C:\Program Files\DCS-SimpleRadio-Standalone\ExternalAudio\DCS-SR-ExternalAudio.exe` | Path to the SRS tool used to transmit replies — it lives in the `ExternalAudio` folder of your SRS installation. The installer detects the real location and writes it here; **Detect SRS installation** on CH1 does the same at any time, and startup validation names the path it found when the configured one is missing. List available TTS voices with `DCS-SR-ExternalAudio.exe --help`. |
| `VoiceName` | `""` | TTS voice for any radio that doesn't set its own, e.g. `"Microsoft David Desktop"`. Empty means no `--voice` flag is passed at all, so ExternalAudio picks. |

**Which voices exist** is not a question about Windows but about ExternalAudio: it speaks through SAPI5, and the modern "natural" Windows 11 voices (Aria, Guy, Ryan…) are OneCore voices that SAPI5 usually cannot see. The authoritative list is what the tool itself prints, which `TtsVoices` in `Darkstar.Core` reads for the **List available voices** button on CH2 and CH3. Note that the tool prints that list from its *argument-error* handler rather than from a dedicated switch, so it is obtained by calling it with `--help` — a non-zero exit code and a page of usage text are therefore the normal, expected case there.

**Azure and Google voices** work today through `ExternalAudioExtraArgs` — `--azureCredentials="KEY;region"` or `--googleCredentials="path\to.json"` — with the provider's own voice name in `VoiceName` or per radio (e.g. `en-US-AndrewNeural`, `en-US-Wavenet-D`). They sound far better than anything installed locally, cost money per character, and never appear in the button's list, which asks this machine. `--gender=male` and `--culture=en-GB` are the alternative when you'd rather request a kind of voice than a specific one — safer on machines whose voice list you don't know.
| `ExternalAudioExtraArgs` | `""` | Extra command-line arguments appended to every transmission, for options this bot doesn't set itself. Whether your SRS build has a speaking-rate flag (and what it's called) depends on its version — check `DCS-SR-ExternalAudio.exe --help`, then put it here, e.g. `--speed=-1`. A wrong flag surfaces as an `[ExternalAudio]` error in the log. |

### "Standby" acknowledgement for slow replies

Transcription plus reply generation can take a few seconds. To keep the pilot from wondering whether the bot heard them at all, the bot can send a short acknowledgement first and follow up with the real reply once it's ready.

| Field | Default | Description |
|---|---|---|
| `AckEnabled` | `false` | Master switch. Off by default — it costs one extra transmission per slow request, which briefly occupies the frequency. |
| `AckAfterSeconds` | `4.0` | How long the bot may stay silent, measured **from the moment the wake word was detected**. If the real reply is ready sooner, no acknowledgement is sent at all. If the pilot is still transmitting when the time is up, the acknowledgement goes out right after their transmission ends — the bot never talks over them. |
| `AckMessage` | `"{pilot}, this is {callsign}, message received, standby."` | Text to speak. `{pilot}` = the requesting pilot's callsign (parsed from their SRS player name via `PlayerNameCallsignSeparator`), `{callsign}` = the replying radio's own callsign. If the pilot's name is unknown, `"{pilot}, "` is dropped automatically. |

### Rate limit per pilot

| Field | Default | Description |
|---|---|---|
| `RateLimitMaxRequests` | `6` | Requests one pilot may make inside the window. `0` or less disables the limit. |
| `RateLimitWindowSeconds` | `120` | Length of the sliding window. |
| `RateLimitReply` | `"{pilot}, standby, working other traffic."` | Spoken once per limited stretch. `{pilot}` and `{seconds}` are substituted; the wait is rounded up, since being refused again at 4.6 seconds after being told "5" reads as a broken bot. |

`RateLimiter` in `Darkstar.Core`, checked in `BotService` after the coalition check and **before** the Gemini call — the coalition check refuses more cheaply still, and the Gemini call is what the limit exists to protect, along with the frequency the reply would occupy.

Three properties are worth knowing because each one is a failure mode avoided rather than a feature:

- **A sliding window, not a cooldown.** A fixed pause after each request punishes the normal case — a pilot asking two or three things in quick succession and then flying for ten minutes.
- **Refused transmissions are not counted.** Counting them would move the window forward on every retry, so a pilot who keeps calling could never get back in. A limit that turns into a ban is not what was configured.
- **The refusal is spoken once, then the bot goes quiet.** Repeating it would occupy exactly the frequency being protected. Both the spoken and the silent case are logged, so a pilot complaining they were ignored leaves a trace. Going quiet without ever saying anything (an empty `RateLimitReply`) is available but a bad idea: a bot that silently stops answering is indistinguishable from a broken one, and the pilot's natural response is to transmit more.

Pilots with no recent requests are forgotten from the watchdog loop, so a server left running does not keep a record for every name that ever connected.

The acknowledgement is per radio and uses that radio's own callsign, so a tanker frequency acknowledges as "Texaco" while the AWACS frequency acknowledges as "Overlord". Acknowledgement and reply can never overlap: transmissions on one radio are serialized.

### DCS-gRPC (optional)

| Field | Default | Description |
|---|---|---|
| `DcsGrpcEnabled` | `false` | Master switch for the DCS-gRPC connection — required for the tactical replies below, the GUI's connectivity test and the mission data explorer. |
| `DcsGrpcAddress` | `"http://127.0.0.1:50051"` | Address of the DCS-gRPC server. Must include the scheme. |
| `DcsGrpcApiKey` | `""` | DCS-gRPC 0.7.x has no authentication of its own — leave empty unless you run it behind a proxy that requires a key. |

Setting up the DCS-gRPC server itself is described in the [manual](manual-en.md#8-live-mission-data-via-dcs-grpc).

### Tactical replies from live mission data

With `DcsIntelEnabled`, the bot answers tactical requests from the running mission instead of from `phrases.json` or Gemini. These replies take priority over both, because they are actually true for the current mission. Everything else keeps working exactly as before.

| Request | Trigger examples | Example reply |
|---|---|---|
| Bogey dope | "Overlord, bogey dope" | *"Bogey, bearing zero niner zero, 35 miles, 22 thousand, hot, group of two."* |
| Picture | "Overlord, picture" | *"Picture: two groups. Lead group, bullseye two seven zero for 40, 25 thousand, two contacts. Second group, ..."* |
| Threat check | "Overlord, threat check" | *"Nearest contact zero niner zero at 35 miles, 22 thousand."* |

Bearings are read digit by digit ("zero niner zero") so TTS doesn't turn `090` into "ninety". Adding the word "bullseye" to a request forces the bullseye format even when BRAA would be possible.

| Field | Default | Description |
|---|---|---|
| `DcsIntelEnabled` | `false` | Master switch. Needs `DcsGrpcEnabled` as well. |
| `DcsIntelContactSource` | `"AwacsThenMissionData"` | Where contacts may come from: `AwacsThenMissionData` (sensors, mission data as fallback), `AwacsOnly` (**god's eye off** — strictly what the unit detects), `MissionDataOnly` (always god's eye, unit name ignored). Anything else falls back to the default with a warning. |
| `DcsIntelAwacsUnitName` | `""` | Mission-editor name of the unit whose sensors decide what may be reported (typically an **AI** AWACS). Empty = god's eye, unless the mode forbids it. |
| `DcsIntelMaxRangeNm` | `120` | Contacts further away than this are not reported. `0` disables the limit. Measured from the pilot's aircraft, or from the bullseye if the pilot couldn't be identified. |
| `DcsIntelMaxGroups` | `3` | How many groups a picture call reports before summarizing the rest as "N further groups not reported". |
| `DcsIntelMagneticBearings` | `true` | Report magnetic bearings (what the pilot reads on their instruments) instead of true bearings, using the map's declination. |
| `DcsIntelIncludeHelicopters` | `true` | Include hostile helicopters, not just fixed-wing aircraft. |
| `DcsIntelSayContactType` | `true` | Name the aircraft/helicopter type ("type MiG-29"). DCS variant suffixes are stripped before speaking (`F-16C_50` → `F-16C`); a picture group flying several types is announced as "mixed, lead &lt;type&gt;". |
| `DcsIntelSlowSpeech` | `true` | Paces the numbers so they stay intelligible: a comma between the digits of a bearing (every TTS engine pauses at a comma instead of running "zeroninerzero" together) and ranges/altitudes/counts spelled out as words ("thirty five miles" instead of a rattled-off "35"). Turn off for terser, faster phrasing. |
| `DcsIntelTimeoutSeconds` | `5` | Per-call timeout for the DCS-gRPC queries behind a reply. |
| `DcsIntelBogeyDopeTriggers` | `["bogey dope", "bogie dope", "bogey dobe", "boogie dope", "nearest bandit", "closest contact"]` | Matched case-insensitively anywhere in the transcription. The deliberate misspellings catch common speech-recognition errors. |
| `DcsIntelPictureTriggers` | `["picture", "request picture", "say picture"]` | |
| `DcsIntelThreatTriggers` | `["threat check", "any threats", "threats"]` | |
| `DcsIntelBullseyeTriggers` | `["bullseye"]` | Forces the bullseye format instead of BRAA. |
| `DcsIntelAlphaCheckTriggers` | `["alpha check", "position check", "say my position"]` | Reports the caller's *own* position from the bullseye. Answered before the contact query, so it still works when the sensor source is unusable. |
| `DcsIntelNoPositionReply` | `"Negative, no radar contact on you."` | Alpha check when the caller cannot be matched to a unit. |

### Friendly positions ("where is Springfield 2-1?")

| Field | Default | Description |
|---|---|---|
| `DcsIntelFriendlyPositionEnabled` | `false` | Master switch. Also needs `DcsIntelEnabled`. |
| `DcsIntelFriendlyPositionTriggers` | `["where is", "where's", "position of", "say position of", "posit on", "locate"]` | The text following the phrase is taken as the aircraft being asked about. |
| `DcsIntelFriendlyNotFoundReply` | `"Negative, no contact on {pilot}."` | `{pilot}` is the name as the caller said it. |
| `DcsIntelFriendlyNoNameReply` | `"Say again, which aircraft?"` | The request named nobody, including when it used a pronoun. |
| `DcsIntelFriendlyNoCoalitionReply` | `"Negative, unable to identify your coalition."` | Refusal when the caller's side is unknown. |
| `DcsIntelFriendlySayHeading` | `true` | Include the friendly's own heading. |
| `Radios[].AnswerFriendlyPositionRequests` | `null` | Three-way per-radio role, like the other two. |

**Off by default deliberately.** A bot that reads out any player's position on request changes how a PvP server plays, which is the server owner's decision rather than a default to hand them. Three properties are not configurable, and `FriendlyPosition` in `Darkstar.Core` documents why each one exists:

- **Human players only.** The query is `CoalitionService.GetPlayerUnits`; AI units are never pulled in, because a candidate list of every AI on the coalition makes misidentification likely — and on this request that means confidently reporting the wrong position.
- **The caller's own coalition only**, which is what the query is scoped to.
- **An unknown sender coalition is refused.** Elsewhere `ResolveFriendlyCoalition` falls back to the bot's own side for an unknown sender, which is harmless when the answer concerns the enemy. Here it would let a spectator ask where the bot's side is, so the guard runs before any player is looked up.

**The target is found by position in the sentence, not by scanning it.** A request like this names two pilots, and `PilotNames.FindMatchInTranscript` deliberately refuses to choose when more than one candidate is named — the safeguard that stopped a wingman being reported as their flight lead. So `TriggerMatcher.FindMatchWithTail` returns what follows the trigger, and only that is searched. Nothing was loosened. The practical consequence is that the name must come after the phrase, and that pronouns are treated as naming nobody.

The reply is built by `BuildFriendlyPosition`, measuring from the caller's aircraft when known and the bullseye otherwise. The friendly's heading is included (half of what a rejoin needs); aspect never is, since it describes a contact closing on you and is meaningless about a wingman.
| `DcsIntelNoContactsReply` | `"Picture clean."` | Used when nothing matches the filters. |
| `UnintelligibleReply` | `"Say again, your last was unreadable."` | What the bot says when nothing intelligible came out of the transmission. Without it the bot would fall through to the phrase list or a guessed transcript and answer a request nobody made. |
| `WrongChannelReply` | `"Contact {callsign} on {frequency}."` | Said when a request arrives on a radio that doesn't handle it while exactly one other radio does — a handoff rather than a refusal. `{callsign}` and `{frequency}` are filled in from that radio; the frequency is read digit by digit ("two five one decimal zero"). Empty means no handoff: the transmission falls through to `phrases.json`. With two radios serving the same request there is no single right answer, so none is given. |
| `DcsIntelUnavailableReply` | `"Negative, no tactical data available at this time."` | Used when the mission data could not be read at all. |
| `DcsAirfieldEnabled` | `false` | Answers "runway in use" and ATIS calls from live weather and runway data. Needs `DcsGrpcEnabled`. The runway part additionally needs `evalEnabled = true` on the DCS-gRPC server, since runway headings are only reachable through `Eval`; without it the weather still works and the runway is reported as unknown. The only Lua the bot ever runs is a fixed, read-only snippet built into `DcsAirfieldService`, cached per mission — nothing a pilot says reaches it. See [manual-en.md, chapter 8.4](manual-en.md#84-runway-in-use-and-atis). |
| `DcsAirfieldRunwayTriggers` | `["runway in use", "active runway", "runway request", "which runway"]` | Phrases that ask for the runway only. |
| `DcsAirfieldAtisTriggers` | `["atis", "weather", "airfield information", "field conditions"]` | Phrases that ask for the full report. Checked before the runway triggers, so a call containing both gets the fuller answer. |
| `DcsAirfieldPressureUnit` | `"Both"` | `"Both"` reads QNH in hectopascals and then the altimeter setting in inches; `"Hectopascals"` or `"InchesHg"` for one only. |
| `DcsAirfieldAtFieldNm` | `5` | How close to an airfield's centre counts as being **at** it. Inside this the pilot's own position decides which airfield the request is about, even if the transcript contained something that looked like another airfield's name — which is what makes *"active runway for Punch 1-1"* work from the ramp without anyone pronouncing the airfield well enough for a transcriber. |
| `DcsAirfieldMaxDistanceNm` | `60` | How far the nearest airfield may be before the bot asks which one instead of assuming. |
| `DcsAirfieldUnknownReply` | `"Say the airfield you want conditions for."` | When no airfield was named and the pilot couldn't be located. |
| `DcsAirfieldUnavailableReply` | `"Negative, no airfield data available at this time."` | When the airfield data couldn't be read at all. |

**Where the contacts come from:** `DcsIntelContactSource` decides this, together with `DcsIntelAwacsUnitName`.

| Mode | AWACS sensors | God's eye | "Sensors see nothing" means |
|---|---|---|---|
| `AwacsThenMissionData` (default) | used when a unit is named | used as fallback | fall back to mission data |
| `AwacsOnly` | required | **never** | a clean picture is reported |
| `MissionDataOnly` | ignored | always | — |

In the default mode the fallback also triggers on an *empty* detection table, not just on an error: DCS only fills that table for **AI-controlled** units, so a player-flown AWACS always returns nothing, and "sees nothing right now" can't be told apart from "can never see anything" over the API. The trade-off is that while the AWACS detects nothing, you silently get god's-eye information.

`AwacsOnly` removes that trade-off in the other direction: the bot reports exactly what the unit detects, and an empty table is answered as "picture clean". Use it with an **AI** AWACS — a player-flown one will always look blind. If the sensor call fails outright, or no unit is named, the bot answers `DcsIntelUnavailableReply` ("no tactical data") rather than claiming an empty sky, and a threat circle skips that sweep instead of implying the airspace is clear.

Every `[Intel]` log line names the source actually used (`source=AWACS '<name>' sensors` or `source=mission data (god's eye)`), and the GUI's *"Try it without flying"* button shows the same — the quickest way to check which one you are really running on.

**How the pilot is located:** the bot matches the requester's SRS name against the units from `GetPlayerUnits`. Those two names are typed in different places and rarely agree character for character, so matching is tolerant — see `PilotNames` in `Darkstar.Core`. The rules are tried strictest first, and every one is applied across all units before the next is considered, so a weak rule can never beat a strong one:

| Rule | Matches |
|---|---|
| `ExactRaw` | The DCS field equals the full SRS name, character for character. |
| `CanonicalFull` | Equal once case, spaces, hyphens, underscores and squadron tags are ignored — `[ISAF] Mobius 1-1`, `MOBIUS 11` and `mobius_1_1` are one pilot. |
| `CanonicalHandle` | The part behind the separator matches (usually the DCS player name). |
| `CanonicalCallsign` | The part in front of the separator matches the unit's callsign or name. |
| `UniqueSubstring` | Last resort for a handle that differs only by a suffix (`Bernhard` against `Bernhard_S`). Applies **only** to the DCS player name, never to the unit's callsign — that field holds the *flight's* callsign, shared by every aircraft in it, so a partial match there would hand a wingman's call to the flight lead. Refused outright when more than one player would qualify. |

On a match, bearings are given from the pilot's own aircraft (BRAA including aspect: hot / flanking / beaming / cold). If no match is found, the reply falls back to the bullseye format rather than failing — and, since that fallback is silent on the radio, the log says which rule matched or why none did (`DebugLogging`, `[Intel] Pilot "…"`).

### Threat circle (standing watch)

A pilot asks for a threat circle and the bot keeps watching a circle of that radius **around the pilot's own aircraft** — it moves with them — warning them on the same frequency as soon as a hostile aircraft or helicopter enters it.

| On the radio | What happens |
|---|---|
| *"Overlord, threat circle forty miles"* | Circle armed. Reply: *"Threat circle active, forty miles."* (plus how many contacts are already inside). |
| *"Overlord, threat circle"* | Same, with `DcsIntelThreatCircleDefaultRadiusNm`. |
| *(a hostile enters)* | *"Enfield 1-1, this is Overlord… Threat, bearing zero, niner, five, twenty two miles, twenty two thousand, hot, type MiG-29."* |
| *"Overlord, cancel threat circle"* | *"Threat circle cancelled."* |

The radius may be spoken as digits or as words ("forty", "twenty five", "one hundred"). Each contact is announced **once per circle** — a bandit loitering at the edge can't turn into a stream of warnings. A circle ends when the pilot cancels it, when its time limit runs out, or when the pilot leaves the mission (slot change, logout, shot down).

| Field | Default | Description |
|---|---|---|
| `DcsIntelThreatCircleEnabled` | `true` | Master switch for the standing watch (needs `DcsIntelEnabled`). |
| `DcsIntelThreatCircleDefaultRadiusNm` | `40` | Radius used when the pilot names none. |
| `DcsIntelThreatCircleMaxRadiusNm` | `150` | Upper limit for a requested radius. |
| `DcsIntelThreatCirclePollSeconds` | `15` | Seconds between sweeps. Every sweep costs one set of mission queries **per active circle** — don't set it too low on a busy server. |
| `DcsIntelThreatCircleDurationMinutes` | `30` | A circle expires automatically after this long. |
| `DcsIntelThreatCircleMaxActive` | `8` | How many pilots can have a circle at the same time. |
| `DcsIntelThreatCircleMaxAlertsPerSweep` | `2` | Warnings transmitted per circle and sweep; the rest follow on later sweeps instead of occupying the frequency all at once. |
| `DcsIntelThreatCircleTriggers` | `["threat circle", "threat ring", "set threat circle"]` | Start phrases. |
| `DcsIntelThreatCircleCancelTriggers` | `["cancel threat circle", "stop threat circle", "threat circle off", "cancel threat ring"]` | Cancel phrases, checked before the start phrases. |
| `DcsIntelThreatCircleNoPilotReply` | `"Unable to set the threat circle, cannot locate your aircraft."` | The circle follows the pilot, so it can't be armed without finding their aircraft. |
| `DcsIntelThreatCircleBusyReply` | `"Unable, too many threat circles active at the moment."` | |
| `DcsIntelThreatCircleCancelledReply` | `"Threat circle cancelled."` | |
| `DcsIntelThreatCircleNoneActiveReply` | `"No threat circle active for you."` | |

## `phrases.json`

A list of fixed trigger/response pairs. If a trigger substring (case-insensitive) is found anywhere in the transcribed text, the matching response is used verbatim instead of a Gemini-generated reply — the transcription call still always happens (the recognized text is needed to match against), only the *reply* is replaced.

```json
[
  { "Trigger": "radio check",   "Response": "Radio check, loud and clear, five by five." },
  { "Trigger": "say again",     "Response": "Copy, say again your last transmission." },
  { "Trigger": "status",        "Response": "All systems nominal." },
  { "Trigger": "check in",      "Response": "Copy your check-in." },
  { "Trigger": "request weather","Response": "Weather is clear, visibility unrestricted." },
  { "Trigger": "request rtb",   "Response": "Copy, cleared to RTB." }
]
```

First match wins. Empty or duplicate triggers are flagged as warnings on load. See `RestrictToKnownPhrases` above to control what happens when nothing matches.

## `vocabulary.json`

A plain list of terms passed to Gemini as a transcription hint alongside the audio — it doesn't change what the bot can talk about, it only improves recognition accuracy for words that aren't common English (aircraft types, callsigns, unit names, jargon):

```json
["Viggen", "Overlord", "Bullseye", "Texaco", "Enfield"]
```

## Backups

Every automatic edit to any of the three files (schema merges, GUI saves) is preceded by a timestamped backup copy written to a `Backup/` subfolder next to the file — nothing is ever overwritten without a recoverable copy.
