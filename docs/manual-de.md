# D.A.R.K.S.T.A.R. — Vollständiges Handbuch

*English version: **[manual-en.md](manual-en.md)***

**D**igital **A**ssistant for **R**adio **K**eyword-activated **S**peech **T**ranscription **A**nd **R**esponse — ein sprachgesteuerter Funk-Assistent für [DCS World](https://www.digitalcombatsimulator.com/), der sich als External-AWACS-Client mit einem [DCS-SimpleRadio-Standalone](https://github.com/ciribob/DCS-SimpleRadio-Standalone)-Server (SRS) verbindet.

Der Pilot drückt auf einer überwachten Frequenz die Sendetaste, sagt das Hotword und stellt eine Frage — der Bot transkribiert sie, ermittelt eine Antwort und funkt sie mit synthetischer Stimme zurück.

Dieses Handbuch deckt alles ab: was du brauchst, wie du es installierst, jede Einstellung, die Bedienung im Flug und was zu tun ist, wenn etwas nicht läuft.

---

## Inhalt

1. [Wie es funktioniert](#1-wie-es-funktioniert)
2. [Was du brauchst](#2-was-du-brauchst)
3. [Installation](#3-installation)
4. [Erster Start](#4-erster-start)
5. [Konfigurationsreferenz](#5-konfigurationsreferenz)
6. [Der Konfigurationseditor (GUI)](#6-der-konfigurationseditor-gui)
7. [Bedienung am Funk](#7-bedienung-am-funk)
8. [Live-Missionsdaten über DCS-gRPC](#8-live-missionsdaten-über-dcs-grpc)
9. [Betrieb als Windows-Dienst](#9-betrieb-als-windows-dienst)
10. [Dateien, Logs und Backups](#10-dateien-logs-und-backups)
11. [Fehlersuche](#11-fehlersuche)
12. [Hotword-Genauigkeit](#12-hotword-genauigkeit)
13. [Kosten, Grenzen und Datenschutz](#13-kosten-grenzen-und-datenschutz)

---

## 1. Wie es funktioniert

Eine Anfrage durchläuft den Bot so:

```
Pilot sendet auf 251.000
        │
        ▼
SRS-Server  ──UDP (Opus)──▶  Bot dekodiert das Audio
        │
        ▼
Vosk transkribiert laufend, lokal, offline
        │   Hotword erkannt („Overlord“)?
        ▼
Aufnahme startet (inklusive ~2 s gepufferter Audio von VOR dem Hotword)
        │   Stille oder keine Pakete mehr
        ▼
Aufnahme endet
        │
        ├─▶ Taktische Anfrage („bogey dope“)?   → Antwort aus Live-Missionsdaten (DCS-gRPC)
        ├─▶ Bekannte Phrase aus phrases.json?   → feste Antwort
        └─▶ sonst                               → Google Gemini transkribiert und antwortet
        │
        ▼
Antworttext → DCS-SR-ExternalAudio.exe → Windows-TTS → Aussendung auf derselben Frequenz
```

Zwei Punkte vorweg:

- **Die Hotword-Erkennung läuft vollständig auf deinem Rechner.** Vosk transkribiert lokal alles Gehörte und prüft den Text auf das konfigurierte Schlüsselwort. Für diesen Schritt verlässt kein Audio deinen PC, es braucht keinen Account und kostet nichts pro Anfrage.
- **In die Cloud geht nur die eigentliche Frage** — und auch das nur, wenn das Hotword ausgelöst hat und die Antwort nicht schon durch eine feste Phrase oder durch Missionsdaten abgedeckt ist.

Jedes konfigurierte Radio ist vollständig eigenständig: eigenes Hotword, eigenes Rufzeichen, eigener Aufnahmezustand. Zwei Piloten können gleichzeitig auf zwei Frequenzen sprechen, ohne sich gegenseitig zu stören.

---

## 2. Was du brauchst

### Zwingend

| Voraussetzung | Anmerkung |
|---|---|
| **Windows** | Der Bot nutzt `DCS-SR-ExternalAudio.exe` und Windows-TTS-Stimmen für seine Antworten. |
| **.NET 8** | Die **Desktop Runtime** deckt Bot und GUI ab. Nötig zum *Ausführen*. Der Installer bringt sie mit; aus dem Quellcode brauchst du das [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). |
| **Laufender SRS-Server** | Der Bot verbindet sich als External-AWACS-Client. Er muss nicht auf derselben Maschine laufen — allerdings sendet `DCS-SR-ExternalAudio.exe` derzeit an `127.0.0.1` (siehe [Fehlersuche](#11-fehlersuche)). |
| **`DCS-SR-ExternalAudio.exe`** | Teil von DCS-SimpleRadio-Standalone, im Unterordner `ExternalAudio`. Wird für jede Antwort verwendet. |
| **Vosk-Sprachmodell** | Offline, kostenlos, ohne Account. [Übersicht](https://alphacephei.com/vosk/models) — welches, siehe Tabelle unten. |
| **Google-Gemini-API-Key** | Kostenlos bei [Google AI Studio](https://aistudio.google.com/apikey). Nur für Transkription und frei formulierte Antworten nötig; die kostenlose Stufe reicht für Tests und kleine Gruppen. |
| **Visual C++ Redistributable (x64)** | Wird von Vosks nativer `libvosk.dll` benötigt. Installer und Setup-Skript erledigen das. |

### Optional

| Optional | Wofür |
|---|---|
| **WebView2 Runtime** | Für den GUI-Konfigurationseditor (auf aktuellen Windows-Versionen vorinstalliert; der Installer ergänzt sie bei Bedarf). |
| **DCS-gRPC-Server** | Ermöglicht die taktischen Antworten („bogey dope“, „picture“, „threat check“) und den Missionsdaten-Explorer. Siehe [Kapitel 8](#8-live-missionsdaten-über-dcs-grpc). |
| **Discord-Webhook** | Benachrichtigungen bei Start/Stopp und Verbindungsverlust. |
| **Visual Studio 2022** | Nur zum Bauen aus dem Quellcode, mit den Workloads *.NET-Desktopentwicklung* und *ASP.NET und Webentwicklung*. |

### Welches Vosk-Modell?

Die Erkennungsqualität ist der mit Abstand wichtigste Faktor dafür, wie gut der Bot funktioniert. Das kleine Modell ist schnell, aber schwach — wenn Hotwords nicht oder falsch erkannt werden, liegt es fast immer daran.

| Größe | Modell | Platzbedarf | Empfehlung |
|---|---|---|---|
| Small | `vosk-model-small-en-us-0.15` | ~40 MB | Nur für einen ersten Funktionstest. Spürbar fehleranfällig. |
| **Standard** | `vosk-model-en-us-0.22` | ~1,8 GB | **Empfohlen.** Deutlich zuverlässigere Hotword-Erkennung, immer noch schnell genug. |
| Large | `vosk-model-en-us-0.42-gigaspeech` | ~2,3 GB | Beste Genauigkeit, höherer CPU- und RAM-Bedarf. |

Das Setup-Skript lädt jedes davon auf Wunsch herunter (`-VoskModelSize Standard`).

---

## 3. Installation

### Der normale Weg: der Installer von der Releases-Seite

1. Lade die aktuelle **`DARKSTAR-Setup-<Version>.exe`** von der **Releases**-Seite des Repositorys herunter.
2. Führe sie aus (sie fragt nach Administratorrechten — sie installiert System-Runtimes).
3. Installationsart wählen:

   | Installationsart | Was installiert wird | Wann |
   |---|---|---|
   | **Bot + Config GUI (recommended)** | Alles. | Der Bot läuft auf einem Rechner, an dem du sitzt. |
   | **Bot only (headless server)** | Bot und Sprachmodell, kein Editor. | **Ein DCS-Server.** Es wird nichts mit Fenster installiert, und die **WebView2-Runtime entfällt ebenfalls** — sie wird nur gebraucht, um die Oberfläche des Editors zu zeichnen. |
   | **Custom** | Komponenten selbst auswählen. | |

   Die Komponenten hinter diesen Arten:

   | Komponente | Was es ist |
   |---|---|
   | Bot service | Der Bot selbst. Wird immer installiert. |
   | Config GUI | Der grafische Konfigurationseditor. Empfohlen. |
   | Vosk model | Das Offline-Sprachmodell. Nur im vollen Installer, nicht in der `-slim`-Variante. |

   Auf einer Bot-only-Maschine werden die Einstellungen mit einem Texteditor in `config.json` gepflegt — oder im Editor auf dem eigenen PC, auf eine Kopie gerichtet. Die **.NET-8-*Desktop*-Runtime** wird in jedem Fall installiert, auch bei Bot only: sie ist das eine Paket, das sowohl die Anforderung des Bots als auch die des Editors abdeckt, und die kleinere zu nehmen hieße, die andere Hälfte später nachzuinstallieren.

4. Optional *„Install and start as a Windows Service"* ankreuzen. Das geht auch später und bequemer über die GUI — siehe [Kapitel 9](#9-betrieb-als-windows-dienst).

Der Installer bringt alle Laufzeitabhängigkeiten mit und installiert nur, was tatsächlich fehlt: die **.NET 8 Desktop Runtime**, das **Visual C++ Redistributable** (für Vosk) und die **WebView2 Runtime** (für die GUI).

Außerdem legt er eine `config.json` mit den beiden Pfaden an, die er selbst ermitteln kann — beide müssen damit nicht mehr von Hand eingetragen werden:

- **`VoskModelPath`** — wohin er das Sprachmodell gerade installiert hat.
- **`ExternalAudioExePath`** — wo **deine** SRS-Installation liegt. Er fragt zuerst in der Registry nach, wo sich DCS-SimpleRadio-Standalone eingetragen hat, und sucht, falls dort nichts steht, die üblichen Installationsorte ab (beide `Program Files`-Ordner, Benutzerinstallationen sowie Laufwerkswurzel, `Program Files` und `Games` jedes Laufwerks) — zuerst nach `ExternalAudio\DCS-SR-ExternalAudio.exe`, dann nach den älteren Varianten direkt im Ordner und unter `Server\`.

Eine vorhandene `config.json` wird nie überschrieben, eine Reparatur oder ein Update lässt deine Einstellungen also unangetastet. Wird nichts gefunden (SRS noch nicht installiert oder an einem ungewöhnlichen Ort), bleibt das Feld auf seinem Standardwert und lässt sich später mit dem Knopf **Detect SRS installation** auf **CH1 Connection** füllen.

Es gibt zwei Varianten: Die volle enthält das Sprachmodell, die **`-slim`**-Variante nicht und ist wenige MB statt bis zu ~2 GB groß — für Rechner, auf denen bereits ein Modell liegt. Dann muss `VoskModelPath` hinterher von Hand gesetzt werden.

### Eine bestehende Installation aktualisieren

Die neue `Setup.exe` einfach über die alte laufen lassen — vorher deinstallieren ist nicht nötig. Sie erkennt die installierte Version und verhält sich entsprechend:

| Situation | Was passiert |
|---|---|
| **Neuere Version** | Normales Update. Installationsordner und die früher gewählten Komponenten/Aufgaben werden übernommen, also Weiter, Weiter, Fertig. |
| **Gleiche Version nochmal** | Reparatur-Installation. Dasselbe, nur ohne Versionswechsel. |
| **Ältere über eine neuere Version** | Sie sagt es und fragt nach. Ein Nein bricht ab, ohne irgendetwas zu ändern. |

Was ein Update erhält und selbst erledigt:

- **`config.json`, `phrases.json` und `vocabulary.json` werden nie überschrieben.** Neue Einstellungen aus dem Update ergänzt der Bot beim nächsten Start selbst in deiner vorhandenen `config.json` — mit ihren Standardwerten und nach einer Sicherung in `Backup\`.
- **Der Windows-Dienst wird vor dem ersten Dateikopieren gestoppt und danach wieder gestartet.** Ohne das würde das Update an einer gesperrten `Darkstar.exe` scheitern, denn ein laufender Dienst hält seine eigene Programmdatei offen. Lässt sich der Dienst nicht wieder starten, sagt der Installer das, statt dich raten zu lassen.
- **Eine auf einen alten Pfad zeigende Dienstregistrierung wird repariert** — die vorhandene Registrierung wird entfernt und neu angelegt.
- **Der Konfigurationseditor wird über den Windows-Neustart-Manager behandelt**: Ist er offen, bietet das Setup an, ihn zu schließen, statt an einer gesperrten Datei zu scheitern.

Welcher der drei Fälle vorliegt, steht auf der Seite „Ready to Install", bevor irgendetwas geschrieben wird.

Die Versionsnummer kommt aus dem Build: `.\build-installer.ps1 -Version 1.2` schreibt `1.2` in den Dateinamen, in die Dateieigenschaften der Setup.exe und in den Deinstallationseintrag, den der *nächste* Installer wieder ausliest. Ein Build ohne eigene Versionsnummer ist vom vorherigen nicht zu unterscheiden — also bei jedem Mal hochzählen.


Nach der Installation geht es weiter mit [Kapitel 4](#4-erster-start).

### Den Installer selbst bauen

Nur nötig, um eine neue `Setup.exe` zu erzeugen (für ein Release oder mit einem anderen Sprachmodell). In PowerShell, im Wurzelverzeichnis des Repositorys:

```powershell
.\build-installer.ps1                                        # voller Installer, kleines Modell
.\build-installer.ps1 -VoskModelSize Standard -Version 1.1   # empfohlenes Modell, ~1,8 GB
.\build-installer.ps1 -Slim                                  # schlanker Installer, ohne Modell
```

Das Skript prüft die Voraussetzungen, veröffentlicht beide Projekte **im Release-Modus**, holt die Abhängigkeits-Installer und das Modell und kompiliert alles nach `installer\output\`. Einzelheiten und alle Optionen: [building-the-installer.md](building-the-installer.md).

### Aus dem Quellcode betreiben

Für die Arbeit am Code, nicht für den reinen Betrieb. Das Setup-Skript installiert .NET 8 SDK, die Visual-Studio-Workloads, die Runtimes und ein Sprachmodell und macht einen Testbuild:

```powershell
.\setup-dev-environment.ps1 -ProjectRoot "C:\pfad\zum\repo" -VoskModelSize Standard
dotnet run --project Darkstar.csproj
```

Seine Parameter und der Projektaufbau stehen in [contributing.md](contributing.md).

---

## 4. Erster Start

1. **Konfigurationseditor öffnen.** Er liegt im Startmenü als *D.A.R.K.S.T.A.R. Config Editor* oder als `Darkstar.ConfigEditor.exe` im Installationsordner. Die `config.json` des Bots findet er selbst.

   *(Du betreibst es aus dem Quellcode? Dann den Bot einmal starten — er legt eine `config.json` neben der EXE an und beendet sich, damit du sie prüfen kannst, bevor sich etwas verbindet.)*

2. **Das Nötigste eintragen:**

   | Einstellung | Kanal in der GUI | Bedeutung |
   |---|---|---|
   | `SrsHost`, `SrsPort` | CH1 Connection | Dein SRS-Server (Standard `127.0.0.1:5002`). |
   | `Coalition` | CH1 Connection | `2` = Blau, `1` = Rot, `0` = Zuschauer. |
   | `ExternalAudioExePath` | CH1 Connection | Pfad zu `DCS-SR-ExternalAudio.exe` im Ordner `ExternalAudio` der SRS-Installation. Wird normalerweise schon vom Installer eingetragen; sonst findet ihn **Detect SRS installation**. |
   | `Radios` | CH2 Radios | Die zu überwachenden Frequenzen, je mit Hotword und Rufzeichen. |
   | `GeminiApiKey` | CH3 Speech | Dein API-Key. |
   | `VoskModelPath` | CH3 Speech | Vom Installer bereits gesetzt; von Hand nur nach einer `-slim`-Installation oder einem Quellcode-Build nötig. |

   Danach **Save changes** — von jeder Datei wird vorher automatisch eine Sicherung angelegt.

3. **Bot starten:** entweder auf **CH9 Service** als Windows-Dienst registrieren (läuft im Hintergrund und nach jedem Neustart), oder zum Testen einfach `Darkstar.exe` mit sichtbarem Konsolenfenster ausführen.

4. **Log prüfen.** Ein gesunder Start zeigt der Reihe nach: geladene Phrasen, aktive Hotword-Erkennung mit deiner Radioliste, „Connecting to SRS server …", „Connected. Waiting for hotword…".

5. **In DCS prüfen:** Der Bot erscheint in der SRS-Clientliste unter `ClientName` (Standard `DARKSTAR`). Auf einer überwachten Frequenz senden, Hotword plus Frage sprechen.

Den Antwortweg kannst du auch ohne Flug einzeln testen:

```
cd "C:\Program Files\DCS-SimpleRadio-Standalone\ExternalAudio"
DCS-SR-ExternalAudio.exe --text="Radio check, loud and clear." --freqs=251.000 --modulations=AM --coalition=2 --port=5002 --name="TEST"
```

Hörst du das in DCS, funktionieren TTS und SRS-Aussendung — alles Verbleibende liegt dann auf der Erkennungsseite.

---

## 5. Konfigurationsreferenz

Der Bot liest drei JSON-Dateien neben seiner EXE. Alle drei werden bei Bedarf mit sinnvollen Vorgaben angelegt, beim Laden geprüft (schlechte Werte erzeugen eine `WARNING`, blockieren aber nie den Start), nach Updates automatisch um neue Felder ergänzt und **vor jeder automatischen Änderung nach `Backup\` gesichert**.

> Eine Referenz gibt es auch in [configuration.md](configuration.md); dieses Kapitel enthält dieselben Inhalte in Lesereihenfolge.

### 5.1 `config.json`

#### Logging

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `LoggingEnabled` | `true` | Hauptschalter. `false` = es wird überhaupt nichts protokolliert. |
| `DebugLogging` | `false` | Zeichnet ausführliche Ausgaben (UDP-Pakete, rohes JSON, Pegelwerte) in Konsole **und** Datei auf. Standardmäßig aus, weil teuer — aber die letzten 200 ausführlichen Zeilen liegen im Speicher und werden bei einem Fehler automatisch ins Log geschrieben, ein Fehlschlag kommt also weiterhin mit seiner Vorgeschichte. |
| `LogRetentionDays` | `30` | Logdateien, die älter sind, werden gelöscht. `0` = für immer behalten. |
| `LogRetentionMaxMb` | `200` | Hält `logs\` unter dieser Größe, älteste zuerst. `0` = kein Limit. Die drei neuesten Logs werden nie gelöscht — darunter das, in das gerade geschrieben wird. |

#### SRS-Verbindung

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `SrsHost` | `"127.0.0.1"` | Adresse des SRS-Servers. |
| `SrsPort` | `5002` | Port des SRS-Servers. |
| `ClientName` | `"DARKSTAR"` | Name in der SRS-Clientliste. |
| `ExternalAwacsPassword` | `""` | Nur nötig, wenn der Server `EXTERNAL_AWACS_MODE` mit Passwort verlangt. |

#### Radios

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `Radios` | `[]` | Die gleichzeitig überwachten Radios. Je Eintrag: `FrequencyHz`, `Modulation` (`"AM"`/`"FM"`), optional `Keyword`, optional `Callsign`, optional `Voice`. |
| `Radios[].Voice` | `""` | TTS-Stimme für die Antworten dieses Radios. Leer = die globale `VoiceName`. Damit klingen mehrere Radios nach mehreren Personen. |
| `Radios[].KeywordVariants` | `[]` | Weitere Schreibweisen für das Hotword dieses Radios. Ein Radio mit eigenem `Keyword` erbt die globalen Varianten **nicht**. |
| `TowerPlanBaseMHz` | `133.000` | Niedrigste Frequenz des erzeugten Tower-Plans. Diese Frequenzen sind **erfunden** — DCS-gRPC liefert die echte nicht. |
| `TowerPlanStepMHz` | `0.500` | Abstand zwischen den erzeugten Tower-Frequenzen. |
| `TowerPlanModulation` | `"AM"` | Modulation der erzeugten Tower. |
| `TowerPlanCallsignSuffix` | `"Tower"` | Wird an den Flugplatznamen angehängt, z. B. „Batumi Tower". Leer = nur der Name. |
| `AnnounceFrequenciesEnabled` | `true` | Schreibt die Frequenzen beim Start in die Mission — die einzige Stelle, an der Piloten die erzeugten erfahren. |
| `AnnounceFrequenciesMarkers` | `true` | Ein F10-Marker pro Flugplatz. Die dauerhafte Hälfte. |
| `AnnounceFrequenciesMessage` | `true` | Eine einmalige Bildschirmmeldung beim Start. |
| `AnnounceFrequenciesMessageSeconds` | `20` | Wie lange diese Meldung stehen bleibt. |
| `TuneInGreetingEnabled` | `false` | Begrüßt einen Piloten, der eine der Frequenzen des Bots einstellt. Standardmäßig aus — SRS kennt kein Unicast, es hören also alle auf dieser Frequenz mit. |
| `TuneInGreetingText` | `"{pilot}, {callsign}. {tactical} Say my callsign to be heard."` | Was gesagt wird. `{pilot}`, `{callsign}` und `{tactical}` werden eingesetzt; ein leerer Platzhalter hinterlässt keine Lücke. |
| `TuneInGreetingGapSeconds` | `90` | Mindestabstand zwischen zwei Begrüßungen auf einer Frequenz, damit ein gemeinsam einbuchender Flight eine hört. |
| `FrequencyHz` | `251000000` | Altes Einzelradio in Hz, nur verwendet solange `Radios` leer ist. |
| `Modulation` | `"AM"` | Modulation des alten Einzelradios. |

```json
"Radios": [
  { "FrequencyHz": 251000000, "Modulation": "AM", "Keyword": "Overlord", "Callsign": "Overlord" },
  { "FrequencyHz": 127500000, "Modulation": "AM", "Keyword": "Texaco",   "Callsign": "Texaco" }
]
```

Jedes Radio reagiert **ausschließlich** auf sein eigenes Hotword. Leere `Keyword`/`Callsign` fallen auf die globalen Werte `VoskKeyword`/`BotCallsign` zurück.

#### Identität des Bots

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `BotCallsign` | `"Overlord"` | Rufzeichen, mit dem sich der Bot meldet (global, pro Radio überschreibbar). |
| `PlayerNameCallsignSeparator` | `"\|"` | Schneidet das Rufzeichen aus dem SRS-Namen: `Enfield 1-1 \| neodym` → der Bot sagt „Enfield 1-1“. `/`, `\`, `:`, `;`, `~` gehen auch; `-` nie (das gehört zu `1-1`). Squadron-Tags wie `[ISAF]` werden entfernt und nie vorgelesen. |
| `Radios[].AnswerTacticalRequests` | `null` | Ob dieses Radio taktische Anfragen beantwortet. `null` = dem globalen Schalter folgen. |
| `Radios[].AnswerAirfieldRequests` | `null` | Dasselbe für Bahn-/ATIS-Anfragen. So entsteht eine Tower-Frequenz. |

Vor der Sprachausgabe werden Bindestriche durch Leerzeichen ersetzt, damit „1-1“ als „one one“ und nicht als „eleven“ gesprochen wird.

#### Koalitionssicherheit

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `Coalition` | `2` | Eigene Seite: `0` = Zuschauer, `1` = Rot, `2` = Blau. |
| `RestrictToOwnCoalition` | `false` | `true` = Anfragen der gegnerischen Koalition werden vollständig ignoriert (gar keine Antwort). Unbekannte/Zuschauer werden weiterhin beantwortet. |

#### Hotword und Aufnahme

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `VoskModelPath` | `""` | Ordner des entpackten Vosk-Modells. Ohne ihn greift ein reiner Lautstärke-Platzhalter, der keine Wörter erkennt. |
| `VoskKeyword` | `"computer"` | Globales Hotword für Radios ohne eigenes. |
| `VoskKeywordVariants` | `[]` | Weitere Schreibweisen, die ebenfalls als Hotword gelten — dafür, wie der Erkenner es wirklich hört, z. B. `["over lord"]` für `"Overlord"`. Aus einer Messung füllen, nicht raten: siehe [12.5](#125-akzent-akzeptieren-wie-das-wort-wirklich-ankommt). |
| `HotwordAudioFilter` | `"LowPass"` | Audio-Aufbereitung vor Vosk. `"Average"` ist der alte Weg, nur zum Vergleich — siehe [Kapitel 12](#12-hotword-genauigkeit). |
| `HotwordAutoGain` | `false` | Leise Piloten für die Erkennung verstärken. Standardmäßig aus; kann Fehlauslösungen verursachen. |
| `SaveRecordings` | `false` | Jede Aussendung nach `recordings\` speichern, um die Genauigkeit zu messen. |
| `RecordingRetentionDays` | `7` | Aufnahmen, die älter sind, werden gelöscht. `0` = für immer behalten. |
| `RecordingRetentionMaxMb` | `500` | Hält `recordings\` unter dieser Größe, älteste zuerst. `0` = kein Limit. |
| `SilenceFramesToStopRecording` | `50` | Stille 20-ms-Frames, die eine Aufnahme beenden (50 = 1 Sekunde). |
| `PreRollSeconds` | `2.0` | Sekunden Audio, die vor dem Hotword gepuffert und der Aufnahme vorangestellt werden, damit der Anfang nicht fehlt. |
| `HotwordEnergyThreshold` | `2000` | Nur für den Platzhalter-Detektor (kein Vosk-Modell). |
| `HotwordConsecutiveFramesNeeded` | `5` | Nur für den Platzhalter-Detektor. |

Das Schlüsselwort wird als **ganzes Wort** verglichen, Groß-/Kleinschreibung spielt keine Rolle.

#### Transkription und Antworten (Google Gemini)

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `GeminiApiKey` | `""` | API-Key. Ohne ihn gibt es keine Transkription. |
| `GeminiModel` | `"gemini-3.5-flash-lite"` | Modell für den kombinierten Transkriptions- und Antwortaufruf. Flash-Lite-Varianten haben das höchste Freikontingent. |
| `GeminiFallbackModel` | `""` | Optionales Zweitmodell, wenn das erste dauerhaft scheitert (z. B. Kontingent erschöpft). |
| `GeminiMaxRetries` | `2` | Wiederholungen pro Modell bei vorübergehenden Fehlern (429/500/502/503/504). |
| `GeminiRetryDelayMs` | `1000` | Wartezeit zwischen Wiederholungen. |

Transkription und Antwort sind ein einziger API-Aufruf — das halbiert den Kontingentverbrauch gegenüber getrennten Aufrufen.

#### Feste Phrasen

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `RestrictToKnownPhrases` | `true` | `true` = der Bot beantwortet **nur** Trigger aus `phrases.json`, alles andere bekommt `FallbackResponse`. `false` = Unbekanntes beantwortet Gemini frei. |
| `FallbackResponse` | `"Sorry, cannot answer that for you."` | Antwort, wenn nichts passt. |

#### Antwort-Audio

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `ExternalAudioExePath` | `C:\Program Files\DCS-SimpleRadio-Standalone\ExternalAudio\DCS-SR-ExternalAudio.exe` | Das SRS-Werkzeug für die Aussendung. Wird vom Installer aus der tatsächlichen SRS-Installation übernommen; **Detect SRS installation** auf CH1 sucht ihn jederzeit erneut. |
| `VoiceName` | `""` | TTS-Stimme für jedes Radio, das keine eigene setzt, z. B. `"Microsoft David Desktop"`. Leer = was ExternalAudio selbst wählt. **List available voices** auf CH2 oder CH3 drücken, oder `DCS-SR-ExternalAudio.exe --help` aufrufen. Siehe [7 → Verschiedene Stimmen](#verschiedene-stimmen-pro-radio). |
| `ExternalAudioExtraArgs` | `""` | Zusätzliche Argumente für jede Aussendung, für Optionen, die der Bot nicht selbst setzt. Ob deine SRS-Version einen Parameter fürs Sprechtempo hat und wie er heißt, hängt von der Version ab — in `--help` nachsehen und hier eintragen (z. B. `--speed=-1`). |

#### Zwischenansage bei langsamer Antwort

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `AckEnabled` | `false` | Hauptschalter für die Zwischenansage. |
| `AckAfterSeconds` | `4.0` | Wie lange der Bot schweigen darf, gezählt **ab dem Hotword**. Ist die echte Antwort früher fertig, wird nichts gesendet. |
| `AckMessage` | `"{pilot}, this is {callsign}, message received, standby."` | `{pilot}` = Rufzeichen des Piloten, `{callsign}` = Rufzeichen dieses Radios. Ist der Pilot unbekannt, entfällt `"{pilot}, "` automatisch. |

#### Rate-Limit pro Pilot

| Feld | Standard | Beschreibung |
|---|---|---|
| `RateLimitMaxRequests` | `6` | Anfragen, die ein Pilot pro Fenster stellen darf. `0` schaltet das Limit ab. |
| `RateLimitWindowSeconds` | `120` | Länge des gleitenden Fensters. |
| `RateLimitReply` | `"{pilot}, standby, working other traffic."` | Wird **einmal** gesagt, wenn ein Pilot darüber liegt. `{pilot}` und `{seconds}` werden eingesetzt. Leer = nichts sagen, nicht empfohlen. |

Jede Aussendung kostet einen Gemini-Aufruf und belegt die Frequenz, solange die Antwort gesprochen wird. Ein Pilot, der das Weckwort in Dauerschleife sagt — aus Langeweile, aus Ärger über eine Fehlerkennung, oder mit klemmender Sendetaste, die ihm Cockpitgeräusch einspeist — kann damit das Kontingent für alle verbrauchen und gleichzeitig den Kanal blockieren. Keiner der beiden Ausfälle sieht nach dem Piloten aus, der ihn verursacht hat.

Es ist ein **gleitendes Fenster**, keine Pause nach jedem Aufruf: drei Fragen in kurzer Folge und danach zehn ruhige Minuten werden nie gebremst. Eine abgelehnte Aussendung wird **nicht mitgezählt**, ein Pilot kann seine eigene Wartezeit also nicht durch weitere Versuche verlängern — ein Limit, das zur Sperre wird, war nicht gemeint. Die Ablehnung wird einmal gesprochen, danach schweigt der Bot, weil Wiederholen genau die Frequenz belegen würde, die das Limit schützt. Beide Fälle stehen im Log, damit eine Beschwerde über „ignoriert werden" eine Spur hat.


Der Bot funkt nie dazwischen: Läuft die Zeit ab, während der Pilot noch sendet, geht die Ansage direkt nach dessen Übertragung raus. Ansage und Antwort können sich nie überlappen.

#### DCS-gRPC-Verbindung

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `DcsGrpcEnabled` | `false` | Hauptschalter für alles, was Live-Missionsdaten liest. |
| `DcsGrpcAddress` | `"http://127.0.0.1:50051"` | Adresse des DCS-gRPC-Servers, inklusive Schema. |
| `DcsGrpcApiKey` | `""` | DCS-gRPC 0.7.x hat keine eigene Authentifizierung — leer lassen, außer du betreibst es hinter einem Proxy, der einen Schlüssel verlangt. |

#### Taktische Antworten

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `DcsIntelEnabled` | `false` | Hauptschalter. Braucht zusätzlich `DcsGrpcEnabled`. |
| `DcsIntelContactSource` | `"AwacsThenMissionData"` | `AwacsThenMissionData`, `AwacsOnly` (God's Eye aus) oder `MissionDataOnly`. Siehe [Kapitel 8.3](#83-taktische-antworten-aktivieren). |
| `DcsIntelAwacsUnitName` | `""` | Missionseditor-Name der Einheit, deren Sensoren bestimmen, was gemeldet werden darf. Leer = God's Eye, sofern der Modus es erlaubt. |
| `DcsIntelMaxRangeNm` | `120` | Weiter entfernte Kontakte werden nicht gemeldet. `0` = keine Grenze. |
| `DcsIntelMaxGroups` | `3` | Gruppen pro Picture-Meldung, bevor der Rest zusammengefasst wird. |
| `DcsIntelMagneticBearings` | `true` | Magnetische statt rechtweisende Peilungen (das, was der Pilot am Instrument abliest). |
| `DcsIntelIncludeHelicopters` | `true` | Feindliche Hubschrauber einbeziehen. |
| `DcsIntelSayContactType` | `true` | Flugzeug- bzw. Helikoptertyp ansagen. DCS-Variantensuffixe werden entfernt (`F-16C_50` → `F-16C`); eine Gruppe mit mehreren Typen wird als „mixed, lead <Typ>“ gemeldet. |
| `DcsIntelSlowSpeech` | `true` | Bremst die Zahlen aus: Komma zwischen den Ziffern einer Peilung, Entfernungen/Höhen/Anzahlen als Wörter ausgeschrieben. Aus = knappere, schnellere Formulierung. |
| `DcsIntelTimeoutSeconds` | `5` | Zeitlimit je Missionsdaten-Abfrage. |
| `DcsIntelBogeyDopeTriggers` | `["bogey dope", "bogie dope", "bogey dobe", "boogie dope", "nearest bandit", "closest contact"]` | Werden ohne Rücksicht auf Groß-/Kleinschreibung irgendwo im Transkript gesucht. Die absichtlichen Verhörer fangen typische Erkennungsfehler ab. |
| `DcsIntelPictureTriggers` | `["picture", "request picture", "say picture"]` | |
| `DcsIntelThreatTriggers` | `["threat check", "any threats", "threats"]` | |
| `DcsIntelBullseyeTriggers` | `["bullseye"]` | Erzwingt das Bullseye-Format statt BRAA. |
| `DcsIntelAlphaCheckTriggers` | `["alpha check", "position check", "say my position"]` | Gibt die *eigene* Position des Anrufers vom Bullseye aus zurück. |
| `DcsIntelNoPositionReply` | `"Negative, no radar contact on you."` | Alpha Check, wenn der Anrufer keiner Einheit zugeordnet werden kann — meist ein Namensunterschied. |
| `DcsIntelFriendlyPositionEnabled` | `false` | Einem Piloten sagen, wo ein anderer **menschlicher Spieler** seiner Seite ist. Standardmäßig aus — siehe [7 → Wo ist jemand](#wo-ist-jemand). |
| `DcsIntelFriendlyPositionTriggers` | `["where is", "where's", "position of", "say position of", "posit on", "locate"]` | Was auf die Phrase folgt, gilt als das gefragte Flugzeug. |
| `DcsIntelFriendlyNotFoundReply` | `"Negative, no contact on {pilot}."` | Genannter Pilot nicht gefunden. `{pilot}` ist der Name, wie er gesagt wurde. |
| `DcsIntelFriendlyNoNameReply` | `"Say again, which aircraft?"` | Die Anfrage nannte niemanden („where is he?"). |
| `DcsIntelFriendlyNoCoalitionReply` | `"Negative, unable to identify your coalition."` | Die Seite des Anrufers war nicht bestimmbar, die Anfrage wird abgelehnt. |
| `DcsIntelFriendlySayHeading` | `true` | Den Kurs des Freundes mitansagen — die halbe Information für einen Rejoin. |
| `Radios[].AnswerFriendlyPositionRequests` | `null` | Dreiwertig wie die anderen zwei Rollen: `null` folgt dem globalen Schalter. |
| `DcsIntelNoContactsReply` | `"Picture clean."` | Wenn nichts auf die Filter passt. |
| `UnintelligibleReply` | `"Say again, your last was unreadable."` | Wird gesagt, wenn nichts Verständliches transkribiert wurde — statt eine Anfrage zu erraten. |
| `WrongChannelReply` | `"Contact {callsign} on {frequency}."` | Weiterleitung, wenn eine Anfrage auf einem Radio landet, das sie nicht bedient. Leer = keine Weiterleitung. |
| `DcsIntelUnavailableReply` | `"Negative, no tactical data available at this time."` | Wenn die Missionsdaten gar nicht lesbar sind. |
| `DcsAirfieldEnabled` | `false` | „runway in use"/ATIS beantworten. Für den Bahn-Teil `evalEnabled = true` am DCS-gRPC-Server nötig — siehe [8.4](#84-bahn-in-benutzung-und-atis). |
| `DcsAirfieldRunwayTriggers` | siehe [8.4](#84-bahn-in-benutzung-und-atis) | Phrasen nur für die Bahn. |
| `DcsAirfieldAtisTriggers` | siehe [8.4](#84-bahn-in-benutzung-und-atis) | Phrasen für den vollen Bericht. Wird zuerst geprüft. |
| `DcsAirfieldPressureUnit` | `"Both"` | `Both` / `Hectopascals` / `InchesHg`. |
| `DcsAirfieldAtFieldNm` | `5` | Innerhalb dieser Entfernung entscheidet die Position des Piloten den Platz — kein Name nötig. |
| `DcsAirfieldMaxDistanceNm` | `60` | Darüber fragt der Bot nach, welcher Platz gemeint ist. |
| `DcsAirfieldUnknownReply` | `"Say again the airfield, unable to identify."` | Aus Position und Name ließ sich kein Platz ermitteln. |
| `DcsAirfieldUnavailableReply` | `"Negative, no airfield data available at this time."` | Die Flugplatzdaten waren gar nicht lesbar — meist `evalEnabled = false` auf dem DCS-gRPC-Server, siehe [8.4](#84-bahn-in-benutzung-und-atis). |

#### Threat Circle (stehende Überwachung)

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `DcsIntelThreatCircleEnabled` | `true` | Hauptschalter (braucht `DcsIntelEnabled`). |
| `DcsIntelThreatCircleDefaultRadiusNm` | `40` | Radius, wenn der Pilot keinen nennt. |
| `DcsIntelThreatCircleMaxRadiusNm` | `150` | Obergrenze für einen angeforderten Radius. |
| `DcsIntelThreatCirclePollSeconds` | `15` | Sekunden zwischen zwei Durchläufen. Jeder Durchlauf kostet einen Satz Missionsabfragen pro aktivem Kreis. |
| `DcsIntelThreatCircleDurationMinutes` | `30` | Automatisches Ende. |
| `DcsIntelThreatCircleMaxActive` | `8` | Gleichzeitig laufende Kreise. |
| `DcsIntelThreatCircleMaxAlertsPerSweep` | `2` | Warnungen pro Kreis und Durchlauf; der Rest folgt später. |
| `DcsIntelThreatCircleTriggers` | `["threat circle", "threat ring", "set threat circle"]` | Startphrasen. |
| `DcsIntelThreatCircleCancelTriggers` | `["cancel threat circle", "stop threat circle", "threat circle off", "cancel threat ring"]` | Abbruchphrasen. |
| `DcsIntelThreatCircleNoPilotReply` | `"Unable to set the threat circle, cannot locate your aircraft."` | |
| `DcsIntelThreatCircleBusyReply` | `"Unable, too many threat circles active at the moment."` | |
| `DcsIntelThreatCircleCancelledReply` | `"Threat circle cancelled."` | |
| `DcsIntelThreatCircleNoneActiveReply` | `"No threat circle active for you."` | |


#### Discord-Benachrichtigungen

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `DiscordEnabled` | `false` | Hauptschalter, standardmäßig aus. |
| `DiscordWebhookUrl` | `""` | Webhook für die fünf Statusmeldungen: Start (mit den überwachten Frequenzen), Herunterfahren, fehlgeschlagener Start mit Begründung, unerwarteter Abbruch mit Fehler sowie Verbindungsverlust und -wiederkehr. In Discord unter *Kanaleinstellungen → Integrationen → Webhooks* anlegen. |

### 5.2 `phrases.json`

Feste Trigger-/Antwortpaare. Taucht ein Trigger irgendwo im transkribierten Text auf (unabhängig von Groß-/Kleinschreibung), wird seine Antwort wörtlich verwendet statt einer generierten. Der erste Treffer gewinnt.

```json
[
  { "Trigger": "radio check", "Response": "Radio check, loud and clear, five by five." },
  { "Trigger": "status",      "Response": "All systems nominal." },
  { "Trigger": "request rtb", "Response": "Copy, cleared to RTB." }
]
```

Der Transkriptionsaufruf findet trotzdem statt — der erkannte Text ist ja die Grundlage für den Trigger-Vergleich; ersetzt wird nur die *Antwort*.

**Ein Eintrag `radio check` wird nicht mehr standardmäßig angelegt**, weil der Bot das inzwischen selbst beantwortet — und dabei sagen kann, ob er dich auf dem Radar hat, was eine feste Phrase nicht wissen kann. Ein vorhandener Eintrag in deiner `phrases.json` hat weiterhin Vorrang, ein Update ändert also niemals einen Wortlaut, den du selbst gewählt hast. Lösche den Eintrag, wenn du das eingebaute Verhalten möchtest.

#### Wo ist jemand

*„Overlord, Punch 1-1, where is Springfield 2-1?"* → *„Springfield 2 1, bearing 040, 25 miles, 18 thousand, heading 090."*

**Standardmäßig aus** (`DcsIntelFriendlyPositionEnabled`, CH8) — und das ist eine Entscheidung über deinen Server, keine Einstellung zum Durchklicken. Ein Bot, der auf Zuruf jede Spielerposition verrät, verändert, wie ein PvP-Server gespielt wird. Die Funktion lässt sich auf CH2 außerdem auf bestimmte Frequenzen beschränken, genau wie die Tower- und AWACS-Rollen — ein Staffelkanal, der nur das beantwortet, ist ein sinnvoller Aufbau.

Drei Grenzen sind eingebaut und nicht konfigurierbar:

| | |
|---|---|
| **Nur menschliche Spieler** | KI-Wingmen werden nicht gemeldet. Sie stehen nicht in der Liste, die DCS für spielerbesetzte Einheiten liefert, und alle KI-Einheiten der Koalition hereinzuholen würde die Kandidatenliste so vergrößern, dass Verwechslungen wahrscheinlich werden — was bei *dieser* Anfrage bedeutet, jemandem voller Überzeugung die falsche Position zu geben. |
| **Nur die eigene Koalition** | Abgefragt wird die Seite des Anrufers. Nach der Gegenseite zu fragen ist nicht möglich. |
| **Ein Anrufer ohne bekannte Koalition bekommt nichts** | Überall sonst im Bot fällt eine unbekannte Senderkoalition auf die eigene Seite des Bots zurück — harmlos, solange die Antwort den Feind betrifft. Hier könnte damit jemand aus dem Zuschauerslot fragen, wo deine Spieler sind. Diese eine Anfrage lehnt deshalb ab. |

Gemessen wird vom Flugzeug des Anrufers, wenn es gefunden wird, sonst vom Bullseye — derselbe Rückfall wie beim Bogey Dope:

| Situation | Antwort |
|---|---|
| Anrufer geortet | *„Springfield 2 1, bearing 040, 25 miles, 18 thousand, heading 090."* |
| Anrufer nicht geortet | *„Springfield 2 1, bullseye 270, 40 miles, 18 thousand, heading 090."* |
| Genannter Pilot nicht gefunden | *„Negative, no contact on Springfield 2 1."* |
| Niemand genannt | *„Say again, which aircraft?"* |

Der **Kurs** des Freundes ist dabei, weil seine Position nur die Hälfte eines Rejoins ist; abschaltbar. **Aspect wird nie gegeben** — hot, cold, flanking und beaming beschreiben, ob ein Kontakt auf dich zufliegt. Das ist eine Frage über einen Feind und über einen Wingman sinnlos.

#### Warum der Name hinter der Phrase stehen muss

So eine Anfrage nennt zwei Piloten, den Anrufer und das Ziel — und der Bot weigert sich absichtlich zu wählen, wenn in einer Aussendung mehr als ein Pilot genannt wird. Genau diese Absicherung verhindert, dass ein Wingman als sein Rottenführer gemeldet wird. Das Ziel wird deshalb aus dem Text **nach** der Triggerphrase genommen, und dort gibt es genau eine Antwort.

Praktisch heißt das: die Formulierung zählt. *„where is Springfield 2-1"* funktioniert, *„Springfield 2-1, where is he"* nicht. Pronomen werden als „niemand genannt" erkannt, die zweite Variante fragt also *„say again, which aircraft?"* statt „kein Kontakt auf he" zu melden.

Wird ein Pilot nicht gefunden, wird der Name zurückgelesen — das sagt dir, dass es ein Namensproblem war und nicht ein fehlendes Flugzeug. Derselbe Unterschied zwischen SRS- und DCS-Namen, der auch aus BRAA-Angaben Bullseye-Angaben macht.

### Radio Check

| Feld | Standard | Beschreibung |
|---|---|---|
| `RadioCheckEnabled` | `true` | Beantwortet „radio check“ auf **jedem** Funkkanal, unabhängig davon, wofür dieser konfiguriert ist. |
| `RadioCheckTriggers` | `["radio check", "comm check", "how do you read", "how do you hear me"]` | Als ganze Wörter verglichen. |
| `RadioCheckReply` | `"Loud and clear."` | Ohne DCS-gRPC — über Radar wird nichts behauptet. |
| `RadioCheckReplyWithContact` | `"Loud and clear, contact."` | Mit DCS-gRPC, wenn der SRS-Name einer Einheit in der Mission zugeordnet werden konnte. |
| `RadioCheckReplyNoContact` | `"Loud and clear, but no radar contact on you."` | Mit DCS-gRPC, wenn nicht. |

### 5.3 `vocabulary.json`

Eine schlichte Liste von Begriffen, die Gemini als Hinweis mitbekommt. Sie ändert nicht, worüber der Bot sprechen kann, sondern verbessert die Erkennung von Wörtern, die kein gewöhnliches Englisch sind:

```json
["Viggen", "Overlord", "Texaco", "Enfield", "Batumi"]
```

> **Niemals eine Auslösephrase hier eintragen.** Der Transkription wird gesagt, alles, was auch nur *klingt wie* ein Begriff aus dieser Liste, genau so zu schreiben — das ist der Zweck der Hinweise, und es passiert mit unverständlichem Audio genauso. Eine Kommandophrase in dieser Liste verwandelt damit jedes Gemurmel in dieses Kommando, und weil taktische Anfragen vor Phrasen und Gemini beantwortet werden, antwortet der Bot überzeugt auf eine Anfrage, die niemand gestellt hat.
>
> `"Bogey Dope"` war einer der Standardeinträge — genau so wurde das gefunden: Der Bot antwortete mit einem Bogey Dope, sobald er eine Aussendung nicht verstand. Der Eintrag ist entfernt, und der Bot warnt jetzt beim Start — und der Konfigurationseditor markiert die betroffenen Chips rot — wenn ein Vokabeleintrag zugleich eine Auslösephrase ist. Hier gehören Eigennamen hin: Rufzeichen, Flugzeugtypen, Kartennamen.

---

## 6. Der Konfigurationseditor (GUI)

`Darkstar.ConfigEditor.exe` bearbeitet alle drei Dateien grafisch — und benutzt dafür exakt denselben Code wie der Bot, sodass nichts auseinanderlaufen kann. Den Konfigurationsordner findet er automatisch, indem er seinen eigenen Ordner, die darüberliegenden Ordner und deren `bin\`-Unterordner durchsucht.

Zum Ansehen muss der Bot nicht gestoppt werden; geänderte Einstellungen greifen aber erst nach einem Neustart des Bots.

Die Kanäle sind danach gruppiert, was man gerade einrichtet, nicht nach ihrer Nummer — **SERVER** (CH1, CH9), **RADIO & SPEECH** (CH2–CH5), **MISSION DATA — CH8** (drei Einträge) und **MONITORING** (CH6, CH7). Die Nummer eines Panels ändert sich nie, alles was dieses Handbuch über CH2 oder CH7 sagt, zeigt also weiterhin an dieselbe Stelle. Ein Eintrag mit der Markierung *off* ist in der Konfiguration ausgeschaltet — das heißt nicht, dass etwas nicht erreichbar wäre.

| Kanal | Inhalt |
|---|---|
| **CH1 Connection** | Konfigurationsordner, SRS-Host/-Port, Clientname, EAM-Passwort, der Pfad zu `DCS-SR-ExternalAudio.exe` samt Knopf **Detect SRS installation**, Koalition, Koalitionsbeschränkung. |
| **CH2 Radios** | Die Radioliste: Frequenz, Modulation, Hotword, akzeptierte Hotword-Schreibweisen, Rufzeichen und Stimme je Radio, die drei Rollenschalter (Taktik / Flugplatz / Positionen von Freunden), ein Knopf **List available voices**, hinzufügen/entfernen. Darunter der Verweistext auf die richtige Frequenz, der Tower-Generator mit der Karte **Announce the frequencies in game** und die Begrüßung beim Aufschalten. |
| **CH3 Speech** | Gemini-Key/-Modell/-Wiederholungen, die globale TTS-Stimme samt **List available voices**, Pre-Roll, Vosk-Modellordner, globales Hotword und seine akzeptierten Schreibweisen, Stille-Frames, die Zwischenansage, das Rate-Limit pro Pilot, die Karte zur Hotword-Genauigkeit samt Aufnahmen-Aufräumgrenzen und die Werte des Platzhalter-Detektors. |
| **CH4 Phrases** | Die Trigger-/Antworttabelle plus `RestrictToKnownPhrases`, die Fallback-Antwort und die Radio-Check-Karte. |
| **CH5 Vocabulary** | Die Hinweiswortliste als Chips, wobei alles, was auch eine Triggerphrase ist, rot markiert wird. |
| **CH6 Discord** | Hauptschalter und Webhook-URL. |
| **CH7 Logging** | Log-Schalter, ermittelter Log-Ordner, Aufräumgrenzen für Logs und eine Live-Ansicht der neuesten Logzeilen. |
| **CH8 DCS-gRPC** | Drei Einträge in der Seitenleiste: **Server & test** (Verbindung und Testknopf), **Replies** (taktische Antworten inklusive Alpha Check mit eigenem Testknopf, die Karte für Positionen von Freunden, der Threat Circle und die Flugplatz-Karte) und **Mission explorer**. |
| **CH9 Service** | Windows-Dienst installieren, starten, stoppen und sauber entfernen. |

Fußzeile: **Discard (reload from disk)** und **Save changes**. Vor jedem Speichern wird eine Sicherungskopie mit Zeitstempel angelegt.

Einzelheiten zu CH8 und CH9 stehen in den nächsten beiden Kapiteln, eine vollständige Beschreibung in [gui.md](gui.md).

---

## 7. Bedienung am Funk

### Der Grundablauf

1. In SRS eine überwachte Frequenz wählen.
2. Sendetaste drücken, das **Hotword** dieses Radios sagen, dann die Anfrage — alles in einer Übertragung:
   *„Overlord, radio check.“*
3. Sendetaste loslassen. Das Ende erkennt der Bot an der Stille (oder daran, dass keine Pakete mehr kommen).
4. Er antwortet auf derselben Frequenz: *„Enfield 1-1, this is Overlord… Radio check, loud and clear, five by five.“*

Das Hotword darf irgendwo im Satz stehen — die Aufnahme enthält rund zwei Sekunden von davor, es geht also nichts verloren, wenn du mitten im Satz auslöst.

### Was die Antwort bestimmt

Der Bot entscheidet in dieser Reihenfolge:

1. **Taktische Anfrage** (bogey dope / picture / threat check) → Antwort aus der laufenden Mission über DCS-gRPC. Hat Vorrang, weil das die einzige Quelle ist, die tatsächlich *stimmt*.
2. **Bekannte Phrase** aus `phrases.json` → die feste Antwort.
3. **Sonst** → je nach `RestrictToKnownPhrases`: entweder die Fallback-Antwort oder eine frei formulierte Gemini-Antwort.

### Taktische Anfragen

| Anfrage | Beispielantwort |
|---|---|
| *„Overlord, bogey dope“* | *„Bogey, bearing zero, niner, zero, thirty five miles, twenty two thousand, hot, group of two, type MiG-29.“* |
| *„Overlord, picture“* | *„Picture: two groups. Lead group, bullseye two, seven, zero, for forty miles, twenty five thousand, two contacts, Su-27. …“* |
| *„Overlord, threat check“* | *„Nearest contact zero, niner, zero at thirty five miles, twenty two thousand.“* |
| *„Overlord, bogey dope bullseye“* | Wie bogey dope, aber Positionen relativ zum Bullseye. |
| *„Overlord, Punch 1-1, alpha check bullseye“* | *„Alpha check, bullseye zero, one, zero, one hundred twenty two miles, twenty two thousand.“* |

Der **Alpha Check** ist der Sonderfall: Er meldet die *eigene* Position, nicht die des Gegners — damit ein Pilot prüfen kann, ob seine Navigation noch mit der aller anderen übereinstimmt. Er wird vor der Kontaktabfrage beantwortet und funktioniert daher auch dann, wenn die Sensorquelle unbrauchbar ist — genau der Moment, in dem man am meisten wissen will, ob der Bot einen überhaupt sieht. Lässt sich der Name keiner Einheit zuordnen, kommt `DcsIntelNoPositionReply` statt einer Position aus dem Nichts.

Peilungen werden ziffernweise gesprochen („zero niner zero“), weil die TTS `090` sonst als „ninety“ liest. Mit `DcsIntelSlowSpeech` (standardmäßig an) steht zwischen den Ziffern ein Komma und die übrigen Zahlen werden als Wörter ausgeschrieben — das hält die Stimme davon ab, sie herunterzurattern. Flugzeug- und Helikoptertypen werden angesagt, wenn `DcsIntelSayContactType` an ist. Der Aspect folgt der üblichen Brevity: **hot** (fliegt auf dich zu), **flanking**, **beaming**, **cold** (fliegt weg).

Für BRAA vom eigenen Flugzeug aus muss der Bot *dein* Flugzeug finden: Er gleicht deinen SRS-Namen mit den DCS-Spielernamen ab. Klappt das nicht — etwa weil dein SRS-Name ganz anders lautet als dein DCS-Name — wechselt er automatisch aufs Bullseye-Format, statt die Anfrage abzulehnen.

### Radio Check

*„Overlord, radio check“* → *„Loud and clear.“*

Wird auf **jedem** Funkkanal beantwortet, egal wofür dieser auf CH2 konfiguriert ist — ein Tower, ein AWACS und ein Tanker beantworten alle einen Radio Check; einen abzulehnen, weil dieser Kanal „nur für Flugplatz-Anfragen“ ist, wäre absurd. Und es braucht überhaupt keine Missionsdaten: Genau darum geht es, wenn man herausfinden will, ob überhaupt etwas funktioniert.

Mit aktiviertem DCS-gRPC sagt die Antwort etwas mehr:

| Antwort | Was sie bedeutet |
|---|---|
| *„Loud and clear.“* | Der Funk funktioniert. Missionsdaten wurden nicht abgefragt. |
| *„Loud and clear, contact.“* | Der Funk funktioniert **und** dein SRS-Name wurde deinem Flugzeug zugeordnet — BRAA-Angaben werden von dort gemessen. |
| *„Loud and clear, but no radar contact on you.“* | Der Funk funktioniert, aber dein Name konnte nicht zugeordnet werden. Taktische Anfragen fallen aufs Bullseye zurück. Namen anpassen, dann verschwindet das. |

Die dritte Antwort ist die wichtige: Ein Namensunterschied ist sonst unsichtbar und macht stillschweigend aus jeder BRAA-Angabe eine Bullseye-Angabe. Ist DCS-gRPC nur langsam oder gerade weg, kommt das schlichte *„Loud and clear.“* — einem funktionierenden Funkgerät soll nie gesagt werden, der Bot sehe dich nicht, wenn das gar nichts mit dem Funk zu tun hat.

### Flugplatz-Anfragen

| Anfrage | Beispielantwort |
|---|---|
| *„Overlord, Batumi, runway in use"* | *„Batumi, runway in use one three, wind one three zero at one niner knots."* |
| *„Overlord, Kobuleti ATIS"* | *„Kobuleti information, wind two one zero at eight knots, temperature one five, QNH one zero one three, altimeter two niner niner two, runway in use two five."* |
| *„Overlord, runway in use"* (ohne Platznamen) | Der Flugplatz, der deinem Flugzeug am nächsten liegt. |

Die Bahn ist das Ende mit dem meisten Gegenwind, aus dem echten Missionswetter. Für den Bahn-Teil braucht es `evalEnabled = true` am DCS-gRPC-Server — siehe [Kapitel 8.4](#84-bahn-in-benutzung-und-atis). Rollanweisungen sind nicht möglich: DCS gibt Rollwege gar nicht heraus.

### Threat Circle: die stehende Überwachung

Statt immer wieder nachzufragen, kann ein Pilot eine Überwachung um sein eigenes Flugzeug scharfschalten:

| Am Funk | Was passiert |
|---|---|
| *„Overlord, threat circle forty miles“* | *„Threat circle active, forty miles.“* — dazu, wie viele Kontakte schon drin sind. |
| *„Overlord, threat circle“* | Dasselbe mit dem konfigurierten Standardradius. |
| *(ein Feind dringt ein)* | *„Enfield 1-1, this is Overlord… Threat, bearing zero, niner, five, twenty two miles, twenty two thousand, hot, type MiG-29.“* |
| *„Overlord, cancel threat circle“* | *„Threat circle cancelled.“* |

Der Kreis **fliegt mit** — sein Mittelpunkt ist immer dort, wo der Pilot gerade ist, nicht dort, wo er bei der Anforderung war. Der Radius darf als Ziffern oder als Wort gesprochen werden („forty“, „twenty five“, „one hundred“).

Jeder Kontakt wird **einmal pro Kreis** gemeldet: Ein Bandit, der den Kreis verlässt und zurückkommt, löst keine zweite Warnung aus — so wird aus einem Kontakt am Rand keine Dauerbeschallung. Dringen mehrere gleichzeitig ein, kommen die nächstgelegenen zuerst und der Rest in den folgenden Durchläufen, statt die Frequenz auf einen Schlag zu belegen.

Ein Kreis endet per Sprachkommando, nach dem konfigurierten Zeitlimit oder wenn der Pilot die Mission verlässt (Slotwechsel, Logout, Abschuss). Warnungen warten kurz, wenn der Pilot gerade sendet — aber nur kurz, denn eine verspätete Bedrohungsmeldung ist schlimmer als eine leicht überlappende.


### Nach der Position eines Freundes fragen

*„Overlord, where is Springfield 2-1?"* → *„Springfield 2 1, bullseye two, seven, zero, for forty miles, twenty two thousand, heading zero, niner, zero."*

**Standardmäßig aus** (`DcsIntelFriendlyPositionEnabled`) — und dieser Standard ist eine Aussage über deinen Server, keine Einstellung zum Durchklicken; die Begründung steht in [Kapitel 5.1](#51-configjson). Was die Funktion tut, wenn sie an ist:

- Gefunden werden ausschließlich **menschliche Spieler**. Ein KI-Flight ist niemand, nach dem jemand fragt.
- Nur die **eigene Koalition**. Ein Anrufer, dessen Koalition SRS nicht bestimmen kann, wird abgelehnt statt geraten (`DcsIntelFriendlyUnknownCoalitionReply`).
- Der **Name muss hinter der Auslösephrase stehen**: *„where is Springfield 2-1"* funktioniert, *„Springfield 2-1, where are you"* nicht — der Bot nimmt, was nach der Phrase kommt, als Namen. Pronomen benennen niemanden, *„where is he?"* wird deshalb mit `DcsIntelFriendlyNoNameReply` beantwortet und nicht mit der Suche nach einem Piloten namens „he".
- Die Frage nach **sich selbst** wird als Alpha Check beantwortet, nicht mit „bearing zero zero zero, zero miles".
- Ein Pilot, der gerade nicht in der Mission ist, ergibt `DcsIntelFriendlyNotFoundReply`. Der Bot meldet nie eine Position, die er nicht messen konnte.

Wie jede andere taktische Antwort lässt sich auch diese auf bestimmte Frequenzen beschränken (CH2 → *friendly positions*) — so landet sie auf einem Staffelkanal und sonst nirgends.

### Wenn der Bot beschäftigt ist

Zwei Dinge können eine Antwort anders klingen lassen als erwartet, beide mit Absicht.

**„Message received, standby."** Dauern Transkription und Antwort länger als `AckAfterSeconds` (gemessen ab dem Hotword), sagt der Bot das, statt dich im Unklaren zu lassen, ob er dich gehört hat. Die eigentliche Antwort kommt, sobald sie fertig ist; ist sie rechtzeitig da, hörst du die Zwischenmeldung nie. Sie wird mit dem Rufzeichen des jeweiligen Radios gesendet und funkt nie jemandem dazwischen, der gerade sendet.

**„Standby, working other traffic."** Das ist das Limit pro Pilot (`RateLimitMaxRequests` je `RateLimitWindowSeconds`, standardmäßig 6 in 2 Minuten). Es existiert, weil jede Aussendung einen Gemini-Aufruf kostet und die Frequenz belegt, solange die Antwort gesprochen wird: ein einzelner Pilot — gelangweilt, genervt vom Missverstandenwerden oder mit klemmender Sendetaste und Cockpitlärm — könnte sonst das Kontingent für alle aufbrauchen und gleichzeitig den Kanal blockieren.

Drei Details sind dabei wichtig:

- Es ist ein **gleitendes Fenster**, keine Abklingzeit: drei Fragen kurz hintereinander und danach zehn Minuten Fliegen lassen dich nie warten.
- **Abgelehnte Aussendungen werden nicht gezählt.** Erneutes Versuchen schiebt das Fenster nicht nach vorn — ein Limit, das zur Sperre wird, war nicht das, was eingestellt wurde.
- Die Ablehnung wird **einmal gesagt und danach geschwiegen**. Sie zu wiederholen würde genau die Frequenz belegen, die sie schützt. Beides steht im Log: ein Pilot, der sagt, er sei ignoriert worden, lässt sich damit prüfen statt glauben oder bezweifeln.

Und zuletzt: war in der Aussendung nichts Verständliches, kommt die Bitte um Wiederholung statt einer geratenen Antwort.

### Mehrere Radios

Jedes Radio antwortet nur auf sein eigenes Hotword und mit seinem eigenen Rufzeichen. Eine Tankerfrequenz kann als „Texaco“ laufen, während die AWACS-Frequenz gleichzeitig als „Overlord“ arbeitet — jede mit eigenem Gesprächsverlauf.

### Verschiedene Stimmen pro Radio

Jedes Radio kann auch **seine eigene Stimme** haben (`Radios[].Voice`, oder das Feld auf CH2). Bleibt es leer, nimmt das Radio die globale `VoiceName` von CH3; ist auch die leer, wählt ExternalAudio selbst. Bei drei konfigurierten Radios ist das der Unterschied zwischen einem Bot auf drei Frequenzen und drei Personen im Funkkreis:

```json
"Radios": [
  { "FrequencyHz": 251000000, "Modulation": "AM", "Callsign": "Overlord", "Voice": "Microsoft Hazel Desktop" },
  { "FrequencyHz": 127500000, "Modulation": "AM", "Callsign": "Texaco",   "Voice": "Microsoft David Desktop" },
  { "FrequencyHz": 133000000, "Modulation": "AM", "Callsign": "Tower",    "Voice": "Microsoft Zira Desktop" }
]
```

**Was du wirklich hast**, zeigt der Knopf **List available voices** auf CH2 (oder CH3): Er ruft `DCS-SR-ExternalAudio.exe --help` auf und zeigt das Ergebnis. Diese Liste ist die einzige, die zählt — und sie ist meist kürzer als erwartet:

> ExternalAudio spricht über die ältere SAPI5-Schnittstelle von Windows. Die modernen „natürlichen“ Stimmen von Windows 11 — Aria, Guy, Ryan und die anderen — sind OneCore-Stimmen, und SAPI5 sieht sie in der Regel nicht. Wenn eine Stimme in den Windows-Einstellungen auftaucht, aber nicht in dieser Liste, ist das der Grund und kein Konfigurationsfehler. Ein englisches Standard-Windows hat meist *David* und *Zira* (en-US); *Hazel*, *George* und *Susan* (en-GB) kommen mit dem britischen Sprachpaket.

Ein nicht vorhandener Name schlägt **stumm** fehl — die Aussendung passiert einfach nicht. Zwei Dinge machen das sichtbar: Das Startup-Log nennt die Stimme jedes Radios, und bei einer Beschwerde des Tools erscheint eine `[ExternalAudio:ERR]`-Zeile. Wenn ein Radio nach einem Stimmenwechsel verstummt, dort zuerst nachsehen.

#### Deutlich bessere Stimmen: Azure oder Google

Lokal installierte Stimmen klingen nach Sprachcomputer. ExternalAudio kann auch über Azure AI Speech oder Google Cloud Text-to-Speech sprechen, deren neuronale Stimmen nach einem Menschen klingen. Beide brauchen ein Konto und kosten pro Zeichen — und beide funktionieren heute schon über `ExternalAudioExtraArgs` (CH3):

```json
"ExternalAudioExtraArgs": "--azureCredentials=\"DEIN_KEY;westeurope\"",
"VoiceName": "en-US-AndrewNeural"
```

Bei Google entsprechend `--googleCredentials="C:\pfad\credentials.json"` mit einem Namen wie `en-US-Wavenet-D`. Die Zugangsdaten gelten für alle Radios, der Stimmenname bleibt pro Radio — ein Cloud-Konto bringt dir also drei wirklich unterschiedlich klingende Controller.

Diese Namen erscheinen **nie** unter **List available voices** — dieser Knopf fragt *diesen Rechner*, und der kennt sie nicht. Von Hand eintippen.

#### Eine Art Stimme statt eines Namens verlangen

Auch über `ExternalAudioExtraArgs`: `--gender=male`, `--culture=en-GB`. ExternalAudio nimmt dann irgendeine passende Stimme. Vorzuziehen, wenn der Bot auf Rechner kommt, deren Stimmenliste du nicht kennst — ein dort nicht existierender Name schlägt fehl, eine Geschlechtsangabe nicht.


### Ein Tower pro Flugplatz

Eine Kaukasus-Mission hat ein Dutzend Flugplätze oder mehr. Statt für jeden einen Radio-Eintrag zu tippen, auf CH2 den Knopf **Generate from the running mission** drücken: er liest die Flugplatzliste aus der gerade geladenen Mission und legt pro Platz ein Radio an — Rufzeichen `<Flugplatz> Tower`, Flugplatz-Anfragen an, Taktik aus. Vorhandene Radios bleiben, ihre Frequenzen werden übersprungen statt doppelt belegt.

```
133.000 MHz (AM): wake word "Overlord", callsign "Batumi Tower",   answers: airfield
133.500 MHz (AM): wake word "Overlord", callsign "Kobuleti Tower", answers: airfield
134.000 MHz (AM): wake word "Overlord", callsign "Kutaisi Tower",  answers: airfield
```

Zwei Dinge daran sind bewusst so, und beide betreffen Fehlerfälle, nicht Geschmack.

**Die Frequenzen sind erfunden.** DCS-gRPC liefert die echte Funkfrequenz eines Flugplatzes nicht — die `Airbase`-Daten enthalten Name, Rufzeichen, Koalition und Position, sonst nichts. Diese Frequenzen stehen in der Missionsdatei und erreichen die Skriptumgebung nie. Ein echter Verlust ist das nicht, weil der Bot über SRS sendet und nicht über das DCS-eigene ATIS, er braucht also ohnehin einen eigenen Plan — aber es heißt, dass **deine Piloten diese Zahlen nirgends nachlesen können**, und dafür ist der nächste Abschnitt da. Wähle mit `TowerPlanBaseMHz` und `TowerPlanStepMHz` einen Bereich, den deine Mission nicht anderweitig nutzt.

**Das Hotword bleibt das globale.** Es liegt nahe, jeden Tower auf seinen Flugplatznamen hören zu lassen. Tu es nicht: „Kobuleti" und „Senaki-Kolkhi" durch ein kleines englisches Sprachmodell, gesprochen von einem Nicht-Muttersprachler, ist genau der Fehlerfall, für den [Kapitel 12.5](#125-akzent-akzeptieren-wie-das-wort-wirklich-ankommt) existiert — und als *Hotword* heißt ein Fehlschlag, dass der Bot überhaupt nicht reagiert, ohne jede Spur im Log. Die **Frequenz** identifiziert den Platz. Der Pilot sagt ein Wort, das er aussprechen kann, und der Bot antwortet als „Batumi Tower".

**Das Erzeugen ist additiv**, vorhandene Plätze behalten also ihre Frequenzen und nichts kollidiert. Beim Kartenwechsel zuerst **Remove N generated tower(s)** drücken — sonst stehen die Tower der alten Karte neben den neuen, und der Bot registriert Radios für Plätze, die es nicht mehr gibt. Als erzeugt gilt ein Radio, wenn alle vier Merkmale zutreffen: Rufzeichen endet auf den eingestellten Suffix, es beantwortet Flugplatz-Anfragen, sonst nichts, und es hat kein eigenes Hotword. Ein handgebautes Radio, auf das alle vier passen, ist davon nicht zu unterscheiden und verschwindet mit — darum nennt der Knopf, was er entfernen wird, und darum wird nichts geschrieben, bis du **Save changes** drückst. Ohne Suffix bleibt kein verlässliches Merkmal übrig, dann lehnt der Knopf ab und sagt es, statt zu raten.

Ist ein Tower pro Platz besser als einer für alle? Performance spielt kaum eine Rolle: das Sprachmodell wird einmal geladen und geteilt, und ein Detektor arbeitet nur, wenn auf *seiner* Frequenz Audio ankommt — stille Tower kosten ein paar MB Speicher und keine messbare CPU. Das eine echte Argument ist, dass Antworten pro Radio serialisiert sind: mit einem einzigen Tower warten zwei Piloten an verschiedenen Plätzen aufeinander, mit je einem nicht. Der Platz selbst wird in beiden Fällen aus der Position des Piloten ermittelt, beide Varianten sind also korrekt.

### Den Piloten die Frequenzen mitteilen

Weil die Tower-Frequenzen erfunden sind, stehen sie in keinem Briefing und auf keinem Kneeboard. Der Bot schreibt sie deshalb beim Start in die laufende Mission (`AnnounceFrequenciesEnabled`, standardmäßig an — **CH2 Radios → Announce the frequencies in game**, unter dem Tower-Generator):

- **Ein F10-Kartenmarker pro Flugplatz**, an dessen Position, mit dem zuständigen Tower, den nicht platzgebundenen Frequenzen — AWACS, Tanker — und dem Hotword. Das ist die dauerhafte Hälfte: bleibt die ganze Mission über stehen und ist jederzeit nachlesbar.
- **Eine einmalige Bildschirmmeldung** beim Start, für den, der schon fliegt. Sie nennt die nicht platzgebundenen Radios vollständig und fasst die Tower zusammen, weil ein Dutzend davon aus dem Bild scrollen würde.

Marker gehen nur an die eigene Koalition, sind schreibgeschützt und benutzen einen **festen ID-Bereich**: ein neu gestarteter Bot ersetzt seine Marker statt einen zweiten Satz anzulegen — ein Server, der den Bot an einem Abend dreimal neu startet, bekommt also nicht drei übereinanderliegende Marker pro Platz. Der gesamte Bereich wird vorher geleert, eine kleinere Mission hinterlässt damit keine Waisen.

Schlägt das fehl — keine Mission geladen, DCS-gRPC nicht erreichbar — steht es als `[Announce]` im Log und sonst passiert nichts. Der Bot läuft auch ohne; die Piloten müssen die Frequenzen dann nur anders erfahren.

### Einen Piloten begrüßen, der sich aufschaltet

Ein Pilot, der eine dieser erfundenen Frequenzen einstellt und nichts hört, kann einen funktionierenden Bot nicht von einem kaputten unterscheiden. Der Bot kann ihn deshalb begrüßen — mit dem Kanal, den er erreicht hat, und mit der Information, wo das taktische Radio liegt. Genau das kann ihm der Marker an seinem Startplatz nicht mehr sagen, sobald er in der Luft ist:

> *„Punch 1-1, Batumi Tower. Overlord is on two five one decimal zero. Say my callsign to be heard."*

Dafür wird DCS nichts gefragt. Jeder SRS-Client meldet, welche Frequenzen er eingestellt hat — im Sync und bei jedem Radio-Update. Der Bot hat diese Daten längst bekommen und weggeworfen. (Die eingestellte Frequenz eines Spielers existiert sonst nirgends: sie ist clientseitig, und weder die DCS-Scripting-Umgebung noch DCS-gRPC gibt sie heraus.)

Die Funktion ist **standardmäßig aus** (`TuneInGreetingEnabled`), und das ist der wichtige Teil dieses Abschnitts. SRS kennt kein Unicast: die Begrüßung hören *alle* auf der Frequenz, nicht der Pilot, der sie ausgelöst hat. Ein Bot, der aus Hilfsbereitschaft über einen BRAA-Call sendet, ist schlechter als einer, der schweigt — jede Regel hier ist deshalb eine Bremse:

- Jeder Client wird **einmal pro Frequenz für die ganze Sitzung** begrüßt — danach nie wieder, auch Stunden später nicht. Ein Pilot, der zwischen zwei Presets hin- und herschaltet, löst eine Begrüßung aus, nicht eine pro Umschaltung.
- `TuneInGreetingGapSeconds` (Standard 90) hält eine Frequenz still, auf der es gerade eine Begrüßung gab — ein **gemeinsam einbuchender Flight von vier hört eine**.
- Ein Pilot, der schon begrüßt wurde, wird *vor* dem Blick auf diesen Abstand abgewiesen. Ein erneutes Aufschalten kann den Abstand für den Nächsten also nicht nach hinten schieben.
- Die Begrüßung geht über denselben Weg wie jede Antwort, **wartet** also hinter dem, was das Radio gerade sagt.
- Ein Funkgerät, das der Pilot nicht eingeschaltet hat, ist keine eingestellte Frequenz: Slots, die SRS als deaktiviert meldet, und die 1-Hz-Platzhalterfrequenz, die dazugehört, werden ignoriert. Nur ein *geänderter* Frequenzsatz gilt als Ankunft, denn SRS schickt Radio-Updates auch für Lautstärke und Verschlüsselung.
- Die gegnerische Koalition wird nicht begrüßt, genauso wie sie nicht bedient wird (`RestrictToOwnCoalition`), und der eigene Client des Bots wird vorher aus der Liste genommen — sonst würde er sich selbst begrüßen.

`{tactical}` nennt die Radios, die taktische Anfragen beantworten, und nie den Kanal, auf dem der Pilot schon zuhört. Auf einem Server ohne DCS-gRPC gibt es keine, und der Satz kommt ohne sie aus. Alles steht als `[Greeting]` im Log, mit laufender Zählung.

Schalten Sie die Funktion ein, solange die erzeugten Tower-Frequenzen für Ihre Piloten neu sind. Schalten Sie sie aus, sobald sie sie kennen — ab dann ist sie nur eine Stimme mehr auf einer belegten Frequenz.

### Einer Frequenz eine Aufgabe geben

Standardmäßig beantwortet jedes Radio alles. Die Aufteilung ist der Weg zu einem Tower: auf **CH2 Radios** hat jedes Radio einen Dreifachschalter für **taktische Anfragen** (Bogey Dope, Picture, Threat Check, Threat Circle) und für **Flugplatz-Anfragen** (Bahn in Benutzung, ATIS).

| | Bedeutung |
|---|---|
| **Default** | Folgt dem globalen Schalter — so wie es sich vor dieser Einstellung überall verhielt. |
| **On** | Dieses Radio beantwortet sie. |
| **Off** | Dieses Radio nie, egal was global eingestellt ist. |

Ein Plan mit zwei Frequenzen sieht dann so aus:

| Frequenz | Hotword | Rufzeichen | Taktisch | Flugplatz |
|---|---|---|---|---|
| 251.000 | Overlord | Overlord | On | **Off** |
| 252.000 | Tower | Batumi Tower | **Off** | On |

Fragst du den Tower nach einem Bogey Dope, wirst du dorthin geschickt, wo du hättest anrufen sollen:

> *„Tower, bogey dope."*
> *„Punch 1-1, this is Batumi Tower… Contact Overlord on two five one decimal zero."*

Das passiert nur, wenn **genau ein** anderes Radio die Anfrage bedient. Sind zwei Tower konfiguriert, gibt es keine eindeutige Antwort — dann erfindet der Bot keine, und die Aussendung fällt wie jede andere auf `phrases.json` durch. `WrongChannelReply` leeren schaltet die Weiterleitung ganz ab.

Die globalen Schalter bleiben der Hauptschalter: ein Radio kann einschränken, nie erweitern. Ist `DcsAirfieldEnabled` aus, beantwortet kein Radio ATIS, egal wie sein eigener Schalter steht — der Konfigurationseditor gräut die Knöpfe aus und sagt es dazu. Phrasen und freie Antworten funktionieren immer auf jedem Radio.

Das Startlog nennt die Aufgabe jedes Radios — der schnellste Weg zu prüfen, ob der Plan stimmt:

```
Wake word detection active (Vosk, offline), 2 radio(s):
  251.000 MHz (AM): wake word "Overlord", callsign "Overlord", answers: tactical
  252.000 MHz (AM): wake word "Tower", callsign "Batumi Tower", answers: airfield
```

### Was normal ist

- **Der Bot hört sich nicht selbst.** Während er antwortet, ist dieses Radio für ihn stummgeschaltet — sonst würde er endlos auf seine eigene Antwort antworten.
- **Eine Antwort dauert einen Moment.** Transkription plus Formulierung braucht typischerweise 2–5 Sekunden. Genau dafür gibt es die Zwischenansage (`AckEnabled`).
- **Er reagiert erst nach dem Hotword.** Alles andere auf der Frequenz wird ignoriert — lokal transkribiert wird aber durchgehend, denn nur so lässt sich das Hotword finden.

---

## 8. Live-Missionsdaten über DCS-gRPC

DCS-gRPC ist eine separate, kostenlose Serverkomponente, die eine laufende Mission über das Netzwerk zugänglich macht. Der Bot nutzt sie für die taktischen Antworten und für den Missionsdaten-Explorer in der GUI.

### 8.1 DCS-gRPC installieren (auf DCS-Seite)

Nach der [offiziellen Dokumentation](https://github.com/DCS-gRPC/rust-server):

1. Release-Archiv herunterladen und in deinen DCS-**Saved-Games**-Ordner entpacken (typischerweise `C:\Users\<name>\Saved Games\DCS` bzw. `…\DCS.openbeta_server`). Danach liegen dort `Scripts\DCS-gRPC\`, `Mods\Tech\DCS-gRPC\` und `Scripts\Hooks\DCS-gRPC.lua`.

2. In der `MissionScripting.lua` im DCS-Installationsordner (`…\DCS World\Scripts\MissionScripting.lua`) direkt nach `dofile('Scripts/ScriptingSystem.lua')` diese Zeile ergänzen:

   ```lua
   dofile(lfs.writedir()..[[Scripts\DCS-gRPC\grpc-mission.lua]])
   ```

3. `Saved Games\DCS\Config\dcs-grpc.lua` anlegen, damit der Server mit jeder Mission startet:

   ```lua
   autostart = true
   host = "127.0.0.1"   -- "0.0.0.0", wenn der Bot auf einer anderen Maschine läuft
   port = 50051
   ```

4. DCS starten und eine Mission laden. Zur Kontrolle in `Logs\dcs.log` nach Einträgen mit `GRPC` suchen oder prüfen, ob eine Datei `Logs\grpc.log` existiert.

> **Hinweis:** DCS-gRPC 0.7.x hat keine eigene Authentifizierung. Lauscht der Host auf `0.0.0.0`, kann jeder, der den Port erreicht, Missionsdaten lesen — schränke das per Firewall ein.

### 8.2 Bot verbinden

1. GUI → **CH8 DCS-gRPC → Server & test** → **Enable DCS-gRPC** einschalten, Adresse und Port prüfen.
2. **Test connection.** Ein erfolgreicher Test belegt zugleich, dass tatsächlich eine Mission läuft, denn er fragt die Missionszeit ab.

### 8.3 Taktische Antworten aktivieren

Ebenfalls auf CH8, jetzt unter **Replies**:

- **Answer tactical requests from mission data** einschalten.
- **Contact source** und **AWACS unit name:** zusammen bestimmen sie, woher die Kontakte kommen dürfen — siehe unten.
- Reichweite, Gruppenzahl und Triggerphrasen nach Geschmack anpassen.
- **„Try it without flying“** zeigt den exakten Satz, den der Bot sagen würde, samt Datenquelle — gesendet wird dabei nichts.

Mit diesem Schalter kommen auch die Anfragen dazu, die **Personen** betreffen statt des Feindes. Beide werden vor der Kontaktabfrage beantwortet und funktionieren deshalb weiter, wenn die Sensorquelle unbrauchbar ist:

- **Alpha Check** — die eigene Position des Anrufers vom Bullseye aus. Mit den taktischen Antworten standardmäßig an; die Triggerphrasen stehen in derselben Karte.
- **Positionen von Freunden** — wo ein anderer menschlicher Spieler derselben Seite ist. Eigene Karte, und bewusst **aus**, auch wenn die taktischen Antworten an sind: siehe [7 → Wo ist jemand](#wo-ist-jemand) für die Begründung und für das, was abgelehnt wird.

Ein **Radio Check** braucht davon nichts und funktioniert ganz ohne DCS-gRPC, gewinnt mit Verbindung aber die Angabe, ob der Bot den Anrufer auf dem Schirm hat — der schnellste Weg, einen Namensunterschied zwischen SRS und DCS zu finden.

#### Woher die Kontakte kommen

Das entscheiden zwei Einstellungen: **Contact source** (`DcsIntelContactSource`) und der **AWACS-Einheitenname**.

| Modus | AWACS-Sensoren | God's Eye | „Sensoren sehen nichts“ heißt |
|---|---|---|---|
| **AWACS-Sensoren, Missionsdaten als Rückfall** (Vorgabe) | wenn eine Einheit eingetragen ist | als Rückfall | Rückfall auf Missionsdaten |
| **Nur AWACS-Sensoren** | zwingend | **nie** | saubere Lage wird gemeldet |
| **Nur Missionsdaten** | ignoriert | immer | — |

Im **Standardmodus** fragt der Bot zuerst die Sensoren der Einheit und nimmt die einfachen Missionsdaten, sobald diese nichts liefern — bei einem Fehler, bei fehlender Einheit *und* bei leerer Erfassungstabelle. Letzteres ist der springende Punkt: DCS füllt die Erfassungstabelle nur für **KI-gesteuerte** Einheiten, eine spielergeflogene AWACS liefert also immer eine leere Liste, und über die Schnittstelle ist „sieht gerade nichts“ nicht von „kann grundsätzlich nichts sehen“ unterscheidbar. Nähme der Bot das für bare Münze, meldete er „picture clean“, während zehn Bandits anfliegen. Der Preis dieser Absicherung: Solange deine AWACS nichts erfasst, bekommst du stillschweigend God's Eye.

**Nur AWACS-Sensoren** schaltet genau das ab: Gemeldet wird, was die Einheit erfasst, und eine leere Tabelle wird ehrlich als „picture clean“ beantwortet. Das ist die realistische Einstellung — aber nur mit einer **KI**-AWACS, denn eine spielergeflogene wirkt immer blind. Schlägt der Sensoraufruf fehl oder ist keine Einheit eingetragen, sagt der Bot „no tactical data“, statt einen leeren Himmel vorzutäuschen, und ein Threat Circle überspringt diesen Durchlauf, statt Entwarnung zu suggerieren.

**Nur Missionsdaten** ignoriert den Einheitennamen vollständig und meldet immer jedes feindliche Flugzeug.

Welche Quelle tatsächlich verwendet wurde, steht in jeder `[Intel]`-Logzeile (`source=AWACS 'Overlord-1' sensors` oder `source=mission data (god's eye)`), und *„Try it without flying“* zeigt dasselbe — die schnellste Kontrolle, dass du nicht unbemerkt im Rückfall läufst.

### 8.4 Bahn in Benutzung und ATIS

Weiter auf CH8 → **Replies**, unter **Answer "runway in use" and ATIS calls**. Sobald es an ist, kann ein Pilot fragen:

> *„Overlord, Batumi, runway in use."*
> *„Batumi, runway in use one three, wind one three zero at one niner knots."*

> *„Overlord, Kobuleti ATIS."*
> *„Kobuleti information, wind two one zero at eight knots, temperature one five, QNH one zero one three, altimeter two niner niner two, runway in use two five."*

#### Du musst den Flugplatz nicht aussprechen

Platznamen sind die schwächste Stelle an der ganzen Sache: „Mineralnye Vody", „Kobuleti", „Batumi" sind genau die Wörter, bei denen Spracherkennung daneben liegt — und eine Fehltranskription, die zufällig auf einen *anderen* Platz passt, ergibt eine überzeugt falsche Antwort.

Deshalb benutzt der Bot zuerst, **wo du bist**, und den Namen nur als Feinentscheidung. Auf dem Vorfeld von Batumi:

> *„Overlord, active runway for Punch 1-1."*
> *„Punch 1-1, this is Overlord… Batumi, runway in use one three, wind one three zero at one niner knots."*

Kein Platzname gesprochen. Zwei Dinge machen das möglich:

- **Innerhalb von `DcsAirfieldAtFieldNm` (Standard 5 NM) um den Platzmittelpunkt entscheidet deine Position** — geparkt, rollend oder in der Platzrunde. Ein verstümmeltes Wort, das nach einem anderen Platznamen aussah, wird ignoriert. Einen *anderen* Platz absichtlich zu nennen funktioniert weiterhin: er wird gemeldet, und das Log notiert, dass du woanders warst.
- **Das eigene Rufzeichen zu nennen identifiziert dich** — ein zweiter Weg, wenn SRS-Name und DCS-Name nicht zusammenpassen. „for Punch 1-1" und „for Punch one one" funktionieren beide; gesprochene Ziffern werden vor dem Vergleich zurück in Zahlen verwandelt, und zwei in einer Aussendung genannte Piloten werden abgelehnt statt geraten. Das hilft auch den taktischen Antworten: dieselbe Zuordnung entscheidet, ob ein Bogey Dope BRAA von deinem Flugzeug aus geben kann oder auf Bullseye zurückfallen muss.

In der Luft und weiter als `DcsAirfieldMaxDistanceNm` (60 NM) von allem entfernt, ohne genannten Platz, fragt der Bot nach — statt einen Platz hunderte Meilen weit weg zu melden, als wärst du dort.

Die Antwort nennt immer den verwendeten Platz, und die Logzeile sagt, wie er gewählt wurde (`the pilot is at it, 0.3 NM from the centre` / `named in the request` / `nearest to the pilot, 12 NM`) — eine falsche Wahl ist damit hörbar und nachvollziehbar statt stillschweigend.

#### Eine Einstellung am DCS-gRPC-Server

Wind, Temperatur und Druck haben eigene DCS-gRPC-Aufrufe. **Bahnausrichtungen nicht.** Die gibt es nur als `Airbase.getRunways()` in DCS selbst, und DCS-gRPC reicht das ausschließlich über sein `Eval` heraus — das **standardmäßig abgeschaltet** ist.

Also in der DCS-gRPC-Serverkonfiguration:

```lua
evalEnabled = true
```

Danach die Mission neu starten. Ohne das meldet der Bot weiterhin das Wetter und sagt *„runway unknown"* — und schreibt eine Logzeile, die genau diese Einstellung nennt, statt stillschweigend zu versagen.

Weil `Eval` beliebiges Lua ausführt, lohnt es zu wissen, was der Bot damit tatsächlich tut:

- Das Lua ist eine **Konstante im Quellcode** (`DcsAirfieldService.RunwayQueryLua`). Nichts, was ein Pilot sagt, und nichts aus einem Konfigurationsfeld wird je hineingeschrieben — der Schnipsel fragt *alle* Flugplätze auf einmal ab, genau damit kein Platzname eingesetzt werden muss.
- Er **liest nur**. Keine Einheit wird erzeugt, kein Flag gesetzt, keine Nachricht gesendet.
- Er läuft **einmal pro Mission**. Bahnen bewegen sich nicht, das Ergebnis wird bis zum Wechsel der DCS-Sitzung zwischengespeichert.

Ein Test prüft diese Eigenschaften bei jedem Build.

#### Wie die Bahn gewählt wird

Jede Bahn lässt sich von beiden Enden benutzen. Für jedes Ende berechnet der Bot die Windkomponente längs der Bahn und nimmt die mit dem meisten Gegenwind — das ist die Bedeutung von „runway in use". Echte Gleichstände (Windstille oder reiner Seitenwind) werden zuerst über den geringeren Seitenwind, dann über die größere Länge entschieden, damit die Antwort nicht von der Reihenfolge abhängt, in der DCS die Bahnen zufällig auflistet.

Die Bezeichnung ist der magnetische Kurs auf zehn gerundet, wobei 0 zu 36 wird. Liefert DCS einen eigenen Namen für die Bahn und stimmt der bis auf eins überein, wird dieser genommen — er steht so auf der Karte und im Kneeboard, samt `L`/`R`-Zusatz.

Peilungen folgen `DcsIntelMagneticBearings` wie überall sonst: der von DCS gemeldete Wind ist rechtweisend und wird für die Ansage nach magnetisch umgerechnet, sofern du das nicht abgeschaltet hast.

#### Einstellungen

| | |
|---|---|
| **Runway / ATIS triggers** | Die Auslösephrasen für beides. ATIS wird zuerst geprüft, ein Anruf mit beidem bekommt also die vollständigere Antwort. |
| **Altimeter setting** | `Both` liest QNH in Hektopascal und danach die Zoll-Einstellung — praktisch bei gemischten Flugzeugtypen. Oder eines von beiden. |
| **Try it without flying** | Führt eine echte Anfrage aus und zeigt den Satz samt verwendetem Flugplatz, Wind und Begründung der Bahnwahl. Es wird nichts gesendet. |

#### Was nicht möglich ist

**Rollwege.** DCS gibt sie nicht heraus — weder über gRPC noch über die eigene Scripting-API. Sie sind Teil des Terrainmodells. Bahnen und Parkplätze sind die Grenze dessen, was irgendein Werkzeug aus einer Mission lesen kann; Rollanweisungen müssten also je Flugplatz von Hand geschrieben werden.

### 8.5 Der Missionsdaten-Explorer

Unter den taktischen Einstellungen zeigt der Explorer, welche Daten eine laufende Mission liefert — als rohes JSON, mit 38 Abfragen zu Mission, Zeit, Welt, Koalition, Spielern, Einheiten, Wetter und Live-Ereignisströmen. Er liest ausschließlich: Es werden nur `Get`/`Stream`-Aufrufe gemacht, an der Mission ändert sich nichts. Er ist damit auch auf einem Server mit Spielern gefahrlos nutzbar.

- **Run snapshot** führt alle Abfragen ohne Eingabe auf einmal aus.
- Gefundene Namen (Einheiten, Gruppen, Flugplätze) werden in den Namensfeldern als Vorschläge angeboten, sodass du von *Groups* über *Units of a group* bis zu *Detected targets* durchklicken kannst.
- Ergebnisse lassen sich kopieren oder unter `grpc-dumps\` im Konfigurationsordner speichern.

Das ist das Werkzeug, wenn du wissen willst, was sich sonst noch aus den Missionsdaten bauen ließe.

---

## 9. Betrieb als Windows-Dienst

Als Dienst läuft der Bot im Hintergrund und nach jedem Neustart, ohne dass sich jemand anmelden muss.

Am bequemsten geht das über GUI → **CH9 SERVICE**:

- Das Panel zeigt den aktuellen Zustand, den Starttyp und **welche EXE tatsächlich registriert ist** — mit Warnung, falls das vom konfigurierten Pfad abweicht. So fällt eine veraltete Installation sofort auf.
- **Install**: Der EXE-Pfad ist mit `Darkstar.exe` neben dem Konfigurationsordner vorbelegt. Optional mit verzögertem Autostart (damit Netzwerk und SRS-Server vorher oben sind) und optional sofortigem Start.
- **Stop and remove**: fragt nach, stoppt den Dienst, wartet bis er wirklich gestoppt ist und löscht ihn erst dann. Diese Reihenfolge ist entscheidend — das Löschen eines laufenden Dienstes merkt ihn nur zum Löschen vor und hinterlässt bis zum nächsten Neustart eine Leiche.
- **Start / Stop** für den installierten Dienst.

Jede ändernde Aktion fragt per UAC nach Administratorrechten; der Editor selbst muss nicht erhöht laufen. Das Auslesen des Zustands braucht keine Rechte.

Zwei Dinge, die man wissen sollte:

- **Der Dienst führt genau die registrierte EXE aus** und liest die `config.json` aus *deren* Ordner. Wenn du zum Testen eine zweite Kopie des Bots hast, achte darauf, die Konfiguration zu bearbeiten, die der Dienst auch wirklich verwendet — genau dafür zeigt das Panel den registrierten Pfad an.
- **Ein Dienst hat kein Konsolenfenster.** Was er tut, steht in den Logdateien unter `logs\` — oder in der Live-Ansicht auf CH7 Logging, dieselben Daten ohne den Editor zu verlassen.
- **Das Log wird laufend geschrieben** und etwa einmal pro Sekunde auf die Platte gezwungen. Zeigt der Explorer die aktuelle Logdatei mit 0 Bytes an, aktualisiert Windows nur den Verzeichniseintrag einer offenen Datei nicht — der Inhalt ist da. Lies sie über CH7, mit einem Tail-Werkzeug (`Get-Content -Wait`) oder in einem Editor, statt der Größenspalte zu glauben.

Alternativ kann der Installer den Dienst während der Installation registrieren, oder du machst es von Hand mit `sc.exe`.

---

## 10. Dateien, Logs und Backups

Alles liegt neben der EXE des Bots:

| Pfad | Inhalt |
|---|---|
| `config.json` | Sämtliche Einstellungen. |
| `phrases.json` | Feste Frage-/Antwortpaare. |
| `vocabulary.json` | Erkennungshinweise. |
| `logs\` | Logdateien mit Zeitstempel, eine pro Start, benannt `darkstar_<Datum>_<Zeit>.log`. Ausführliche Paketdetails nur mit `DebugLogging`; bei einem Fehler kommen die letzten 200 ausführlichen Zeilen automatisch mit. |
| `recordings\` | Nur mit aktivem `SaveRecordings`. Eine WAV pro Aussendung, etwa 100 KB pro Sekunde Sprache. |
| `Backup\` | Automatische Sicherungskopien vor jeder automatischen Änderung. |
| `grpc-dumps\` | Gespeicherte JSON-Ergebnisse aus dem Missionsdaten-Explorer. |

**Keiner der beiden Ordner wächst unbegrenzt.** Beim Start und einmal pro Stunde löscht der Bot seine eigenen alten Dateien: Aufnahmen nach `RecordingRetentionDays` (7) oder sobald `recordings\` über `RecordingRetentionMaxMb` (500) liegt, Logs nach `LogRetentionDays` (30) bzw. `LogRetentionMaxMb` (200). Erst das Alter, dann die Größe: Was das Alterslimit überlebt, wird von den ältesten Dateien her gekürzt, bis der Ordner passt. Die drei neuesten Logdateien werden nie gelöscht, egal was die Limits sagen — ein versehentlich gesetztes 1-MB-Budget kann also nicht das Log löschen, in das gerade geschrieben wird. Ein Limit auf `0` schaltet es ab. Jede Löschung steht mit Begründung im Log:

```
[Retention] Removed 12 recording(s), freeing 340.5 MB (9 past the age limit, 3 over the size budget).
```

Angetastet wird sonst nichts — nur `*.wav` in `recordings\` und `*.log` in `logs\`. `Backup\` und `grpc-dumps\` bleiben absichtlich unberührt: Die gibt es ja gerade für den Fall, dass man sie später noch braucht.

Das Log ist die erste Anlaufstelle, wenn etwas klemmt. Ein gesunder Start sieht ungefähr so aus:

```
N fixed reply phrase(s) loaded from phrases.json.
Wake word detection active (Vosk, offline), 2 radio(s):
  251.000 MHz (AM): wake word "Overlord", callsign "Overlord"
  127.500 MHz (AM): wake word "Texaco", callsign "Texaco"
Connecting to SRS server 127.0.0.1:5002, monitoring: ...
Connected. Waiting for hotword...
```

Und während einer Anfrage:

```
[Hotword Detected] 251.000 MHz: starting recording (sender: "Enfield 1-1 | neodym" -> callsign "Enfield 1-1", ...)
[Recording finished] 251.000 MHz: 96000 bytes of PCM, transcribing...
[STT] "overlord bogey dope"
[Intel] 251.000 MHz: BogeyDope request answered from mission data (source=..., contacts=3, reference=unit 'Enfield-1-1')
[Reply] 251.000 MHz: "Enfield 1-1, this is Overlord... Bogey, bearing ..."
```

Am nützlichsten sind die `[STT]`-Zeilen: Sie zeigen, was der Bot tatsächlich *verstanden* hat, und beantworten die meisten „Warum hat er das gemacht?“-Fragen sofort.

### Alle Kennzeichen im Log

Jede Zeile trägt das Kennzeichen des Teils, aus dem sie stammt. Eine Startzeile sagt, wie eine Funktion eingestellt ist; die Zeilen während einer Anfrage sagen, was sie getan hat.

| Kennzeichen | Wird geschrieben bei | Wissenswert |
|---|---|---|
| `[SRS]` | Verbinden, verbunden, Verbindungsversuch fehlgeschlagen, neuer Versuch | Der Bot versucht es **alle 5 Sekunden, endlos**. Ein zurückkehrender Server wird ohne Neustart wieder aufgenommen. |
| `[SRS UDP]` | Die Sprachverbindung ist abgerissen oder die Empfangsschleife lief auf einen unerwarteten Fehler | Auch das ist nicht tödlich, es geht zurück über `[SRS]`. |
| `[Watchdog]` | Eine Aufnahme wurde beendet, weil keine weiteren Audiopakete kamen | Normal, wenn jemand abrupt aufhört zu senden statt in Stille auszulaufen. |
| `[Hotword]` | Beim Start, wenn die automatische Verstärkung an ist | Sagt, dass leise Piloten für die Erkennung verstärkt werden. |
| `[Hotword Detected]` | Das Hotword hat ausgelöst: Frequenz, SRS-Name, erkanntes Rufzeichen | Der Anfang jeder Anfrage. |
| `[Recording finished]` | Die Aussendung ist zu Ende, mit der Menge Audio | |
| `[STT]` | Was die Transkription verstanden hat — oder dass nichts Verständliches dabei war | Die mit Abstand nützlichste Zeile der Datei. |
| `[Phrase Match]` | Eine bekannte Auslösephrase hat die Antwort bestimmt, oder keine und es wurde die Fallback-Antwort benutzt | |
| `[Intel]` | Taktische Antworten: der Zustand beim Start, pro Anfrage Art, Auslöser und Datenquelle | Sagt auch, wenn `DcsIntelEnabled` an ist, während DCS-gRPC aus ist — das beantwortet diese Frage, bevor sie gestellt wird. |
| `[Airfield]` | Bahn in Benutzung und ATIS: Zustand beim Start, jede Anfrage, und gRPC-Fehler darunter | |
| `[RadioCheck]` | Ein Radio Check wurde beantwortet, mit der Phrase, die gepasst hat | |
| `[ThreatCircle]` | Ein Circle wurde aktiviert, abgebrochen oder lief ab; die Warnungen jedes Durchlaufs | |
| `[Radio]` | Eine Anfrage kam auf einer Frequenz an, die sie nicht bedient | Sagt, ob an die richtige Frequenz verwiesen wurde oder ob sie zu `phrases.json` durchgefallen ist. |
| `[Coalition Check]` | Beim Start, und immer wenn eine Aussendung der gegnerischen Koalition ignoriert wurde | |
| `[Rate limit]` | Ein Pilot ist über dem Limit, und wenn er erneut sendet, während er noch darüber ist | Der zweite Fall wird protokolliert, aber **nicht** beantwortet — das ist die Regel „einmal gesagt, dann still". |
| `[Ack]` | Die Standby-Zwischenmeldung wurde scharf geschaltet, gesendet oder konnte nicht gesendet werden | |
| `[Greeting]` | Ein Pilot hat sich aufgeschaltet und wurde begrüßt, mit laufender Zählung | |
| `[Announce]` | Wie viele F10-Marker gesetzt wurden, oder warum keine | |
| `[Reply]` | Der genaue Satz, der rausgegangen ist | Zusammen mit `[STT]` ist das das ganze Gespräch. |
| `[ExternalAudio]` | `DCS-SR-ExternalAudio.exe` hat sich mit einem Fehlercode beendet | Die erste Stelle, wenn der Bot im Log antwortet, aber nicht auf dem Funk. |
| `[Gemini]` | Ein API-Fehler mit der Angabe, was als Nächstes passiert; ein neuer Versuch; eine nicht lesbare Antwort | |
| `[Discord]` | Benachrichtigungen aktiv, oder warum sie trotz Aktivierung aus sind | |
| `[Retention]` | Alte Aufnahmen oder Logs wurden gelöscht, mit Begründung | |
| `[Error]` | Das Verarbeiten einer Aufnahme oder die Audiobehandlung ist fehlgeschlagen | Der Stacktrace steht dabei. |

### Wenn die SRS-Verbindung abreißt

Der Bot hört nicht auf. `[SRS UDP]` hält den Abriss fest, `[SRS]` versucht es alle fünf Sekunden erneut, bis der Server wieder da ist, und der erste erfolgreiche Versuch schreibt wieder `Connected.` — ohne Neustart, ohne Eingriff.

Mit Discord-Benachrichtigungen (`DiscordEnabled`) wird der Ausfall **einmal** gemeldet, nicht einmal pro Versuch, und die Rückkehr bekommt eine eigene Meldung. Über denselben Webhook laufen auch Start (mit den überwachten Frequenzen), Herunterfahren, ein fehlgeschlagener Start und ein unerwarteter Abbruch — das ist das, was einen als Windows-Dienst laufenden Bot überhaupt sichtbar macht.

Ein Reconnect löst **keine** Welle von Begrüßungen aus: der Bot weiß weiterhin, wen er schon begrüßt hat, und die Clientliste, die er zurückbekommt, entspricht der, die er hatte — nichts sieht nach einer Ankunft aus. Piloten, die *während* des Ausfalls gegangen sind, werden erst vergessen, wenn SRS ihren Disconnect meldet; ging diese Nachricht verloren, bleibt ihr Eintrag bis zum nächsten Neustart stehen, was ein paar Bytes kostet und sonst nichts.

---

## 11. Fehlersuche

| Symptom | Wahrscheinliche Ursache und Abhilfe |
|---|---|
| **Bot beendet sich gleich nach dem Start, im Log steht „the wake word model could not be loaded"** | Der Ordner in `VoskModelPath` fehlt oder enthält kein Vosk-Modell. Das Log nennt den Ordner und was zu tun ist; ein Modellordner enthält `am\`, `conf\`, `graph\` und `ivector\`. Nach einer `-slim`-Installation muss das Modell separat geladen werden. |
| **Bot erscheint nicht in der SRS-Clientliste** | Falscher `SrsHost`/`SrsPort`, Server läuft nicht, oder Firewall. Verbindungszeile im Log prüfen. |
| **Bot beantwortet die falsche Anfrage, wenn er dich nicht verstanden hat** | Eine Auslösephrase steht in `vocabulary.json` — siehe [5.3](#53-vocabularyjson). Der Bot warnt beim Start davor, der Konfigurationseditor markiert es rot. Die `[STT]`-Zeile im Log zeigt, was tatsächlich transkribiert wurde, die `[Intel]`-Zeile darunter nennt die Auslösephrase, die gegriffen hat. |
| **Bot reagiert auf gar nichts** | `VoskModelPath` leer oder falsch (das Log sagt es beim Start), falsche Frequenz/Modulation, oder das Hotword wird nicht erkannt — `[STT]`-Zeilen ansehen und [Kapitel 12](#12-hotword-genauigkeit). |
| **Hotword wird nur manchmal erkannt** | Modell zu schwach. Auf `Standard` wechseln (`-VoskModelSize Standard`), vorher den alten Modellordner löschen. Das ist mit Abstand die häufigste Ursache. Systematisch nachmessen: [Kapitel 12](#12-hotword-genauigkeit). |
| **Ein Radio reagiert auf das falsche Hotword** | Fast immer eine Fehltranskription des schwachen Modells — `[STT]` prüfen. Schlüsselwörter werden als ganze Wörter verglichen, ein längeres Wort löst also nicht aus. |
| **Keine Antwort, im Log ein Gemini-Fehler** | Key fehlt/ungültig oder Kontingent erschöpft. `GeminiFallbackModel` setzen oder auf die Zurücksetzung warten. |
| **Antwort wird erzeugt, aber nie gehört** | `ExternalAudioExePath` falsch oder TTS-Stimme nicht installiert. Auf CH1 **Detect SRS installation** drücken, dann eine Aussendung manuell testen (siehe [Kapitel 4](#4-erster-start)). Das Log nennt den gefundenen Pfad, wenn der eingetragene nicht existiert. |
| **Bot antwortet auf seine eigenen Antworten** | Sollte unmöglich sein — das Radio ist währenddessen selbst stummgeschaltet. Falls doch, bitte mit Log melden. |
| **Zahlen werden heruntergerattert / sind schwer verständlich** | `DcsIntelSlowSpeech` einschalten (GUI: CH8 → **Replies** → *Slow, clearly spoken numbers*). Ist die Stimme insgesamt zu schnell, eine andere `VoiceName` probieren oder — falls deine SRS-Version das kann — einen Tempo-Parameter über `ExternalAudioExtraArgs` setzen. |
| **Ein Radio ist nach dem Setzen einer Stimme verstummt** | Den Stimmennamen hat dieser Rechner nicht. **List available voices** auf CH2 drücken und einen Namen aus dieser Liste verwenden — die „natürlichen“ Windows-Stimmen sind meist nicht dabei. Das Startup-Log nennt die Stimme jedes Radios, `[ExternalAudio:ERR]`-Zeilen zeigen die Beschwerde des Tools. |
| **„Where is X" wird mit „say again, which aircraft?" beantwortet** | Der Name muss hinter der Triggerphrase stehen: *„where is Springfield 2-1"*, nicht *„Springfield 2-1, where is he"*. Siehe [7 → Wo ist jemand](#wo-ist-jemand). |
| **„Where is X" ergibt „unable to identify your coalition"** | Der Anrufer steht in der SRS-Clientliste nicht auf Rot oder Blau — typischerweise ein Zuschauerslot. Diese Anfrage lehnt ab statt eine Seite zu raten. |
| **„Where is X" findet nie einen KI-Wingman** | So gewollt: gemeldet werden nur menschliche Spieler. |
| **Der Bot antwortet „standby, working other traffic" und ignoriert mich danach** | Das Rate-Limit pro Pilot. Das Log nennt die Wartezeit (`[Rate limit]`). Auf CH3 `RateLimitMaxRequests` erhöhen oder `RateLimitWindowSeconds` senken, wenn es für deinen Server zu streng ist. |
| **Alle Radios sprechen mit derselben Stimme** | `Radios[].Voice` ist bei allen leer, sie fallen also auf die globale `VoiceName` zurück. Pro Radio auf CH2 setzen. |
| **Antwort kommt sehr spät** | 2–5 s sind normal. Zwischenansage aktivieren, damit Piloten wissen, dass sie gehört wurden. |
| **Threat-Circle-Warnungen kommen nie an** | Im Log nach `[ThreatCircle]`-Zeilen sehen, die jeden Durchlauf protokollieren. Meist lässt sich das Flugzeug des Piloten nicht seinem SRS-Namen zuordnen (der Kreis braucht es als Mittelpunkt), oder der Kreis ist bereits abgelaufen. |
| **Taktische Anfragen liefern „no tactical data“** | DCS-gRPC läuft nicht, keine Mission geladen, falsche Adresse, oder `DcsGrpcEnabled` aus. Auf CH8 → **Server & test** testen. |
| **Taktische Antworten nutzen immer Bullseye statt BRAA** | Dein SRS-Name ließ sich keinem DCS-Spielernamen zuordnen. Namen angleichen oder die Trennzeichen-Konvention nutzen (`CALLSIGN 1-1 \| handle`). Am schnellsten bestätigt: *„radio check“* rufen — *„no radar contact on you“* heißt genau das. |
| **`recordings\` oder `logs\` haben die Platte gefüllt** | Sollte nicht mehr passieren: Beide werden beim Start und stündlich aufgeräumt. Die `[Retention]`-Zeilen im Log prüfen, und dass `RecordingRetentionDays`/`RecordingRetentionMaxMb` nicht beide `0` sind. |
| **Die Antwort auf `radio check` ist nicht die auf CH4 eingestellte** | Eine Zeile `radio check` in der `phrases.json` hat absichtlich Vorrang vor der eingebauten Antwort. Zeile löschen, um das eingebaute Verhalten zu bekommen. |
| **„Picture clean“, obwohl Feinde in der Luft sind** | Die konfigurierte AWACS-Einheit ist spielergeflogen oder existiert nicht, dazu eine zu enge Reichweitengrenze. Zum Test `DcsIntelAwacsUnitName` leeren oder `DcsIntelMaxRangeNm` erhöhen. |
| **Dienst startet nicht** | Meist ein Pfadproblem: Auf CH9 die registrierte EXE prüfen und ob die `config.json` in *diesem* Ordner liegt. |
| **Dienst lässt sich nicht entfernen** | Etwas hält noch ein Handle darauf (services.msc, Task-Manager). Beides schließen und erneut versuchen; ein Neustart räumt es immer auf. |
| **GUI meldet „config.json exists but has invalid JSON“** | Absicht: Der Editor weigert sich, eine kaputte Datei mit Standardwerten zu überschreiben. Syntax korrigieren (das Log nennt die Stelle) und neu laden. |
| **Aussendung an einen entfernten SRS-Server kommt nicht an** | `DCS-SR-ExternalAudio.exe` wird derzeit ohne Serverparameter aufgerufen und sendet damit an `127.0.0.1`. Bei entferntem SRS-Server bleiben die Antworten lokal. Den richtigen Parameternamen in `--help` nachsehen und einbauen lassen. |

Kommst du nicht weiter: Die Logdatei samt den `[STT]`-Zeilen rund um den Fehler erklärt das meiste — und genau das gehört in einen Fehlerbericht (mit geschwärztem `GeminiApiKey`, `DiscordWebhookUrl` und `DcsGrpcApiKey`).

---

## 12. Hotword-Genauigkeit

Ein Hotword, das nicht anspringt — oder anspringt, obwohl niemand es gesagt hat — ist die häufigste Beschwerde über so einen Aufbau. Vier Dinge entscheiden darüber, in dieser Reihenfolge.

### 12.1 Die Modellgröße (mit Abstand der größte Effekt)

Das kleine Modell (~40 MB) ist ein Kompromiss für Rechner, die nichts übrig haben. Es verhört sich leicht, und jedes Verhören ist eine Gelegenheit, dein Schlüsselwort entweder zu überhören oder zu erfinden. Der Wechsel auf `Standard` (~1,8 GB) klärt die Sache meist:

```powershell
.\setup-dev-environment.ps1 -VoskModelSize Standard    # aus dem Quellcode
.\build-installer.ps1 -VoskModelSize Standard          # in einen neuen Installer
```

Vorher den alten Modellordner löschen, dann `VoskModelPath` auf den neuen zeigen lassen.

### 12.2 Audio-Aufbereitung (CH3 Speech → „Audio preparation")

SRS liefert 48 kHz, Vosk will 16 kHz. Der Weg dorthin bedeutet, zwei von drei Samples wegzuwerfen — und was im Original über 8 kHz liegt, verschwindet dabei nicht einfach. Es klappt als Spiegelbild in den hörbaren Bereich zurück: 10 kHz erscheint wieder bei 6 kHz, 11 kHz bei 5 kHz, 12 kHz bei 4 kHz. Genau dort werden Konsonanten unterschieden — deshalb äußert sich der Effekt als verhörte Wörter und nicht als hörbares Rauschen.

| Einstellung | Wirkung |
|---|---|
| **Low-pass** (Standard) | Filtert zuerst, sodass über 8 kHz nichts mehr übrig ist, das zurückklappen könnte. Gegen Testtöne gemessen liegen die Faltungsprodukte 60–79 dB darunter. |
| **Average** (alt) | Das frühere Verhalten: drei Samples mitteln, zwei wegwerfen. Gleich gemessen liegen die Faltungsprodukte nur 5–22 dB darunter, das meiste kommt also durch. |

`Average` ist für genau einen Zweck erhalten: den Vergleich beider Wege an deinen eigenen Aufnahmen (siehe 12.4). Sonst gibt es keinen Grund dafür.

### 12.3 Leise Piloten (optional)

**„Even out quiet pilots"** verstärkt Aussendungen, die zu leise für die Erkennung ankommen. Bewusst standardmäßig aus: jede automatische Verstärkung hebt auch Hintergrundgeräusche, und in den Sprachbereich gehobenes Rauschen ist genau das, was Hotwords erzeugt, die niemand gesagt hat. Wer es einschaltet, sollte danach auf Fehlauslösungen achten. Das transkribierte und gespeicherte Audio bleibt unberührt — es betrifft nur, was der Detektor hört.

### 12.4 Messen statt raten

**„Save every transmission to `recordings\`"** (CH3 Speech) einschalten und eine Weile normal fliegen. Der Bot schreibt eine WAV-Datei pro Aussendung, benannt nach dem, was passiert ist:

- `..._hit_...` — das Hotword hat ausgelöst.
- `..._missed_...` — jemand hat gesendet und es hat nicht ausgelöst. Das sind die interessanten, und ihretwegen gibt es das Ganze: ein Überhören hinterlässt sonst nirgends eine Spur.

Dann lässt man den Bot sich selbst bewerten:

```powershell
Darkstar.exe --test-hotword recordings --compare
```

Das schickt jede Aufnahme durch den echten Detektor, in denselben 20-ms-Blöcken wie im Betrieb, mit beiden Audio-Aufbereitungen nebeneinander — und zählt, wie viele wie erwartet herauskommen. Es verbindet sich mit nichts, läuft also auch bedenkenlos, während der Dienst aktiv ist.

```
file                                   expected  Average   LowPass
------------------------------------------------------------------
..._0.000MHz_missed_Enfield 1-1.wav    trigger   MISSED    ok
..._0.000MHz_hit_Springfield 2-1.wav   trigger   ok        ok

Average: 4 of 6 as expected (67%), 2 missed, 0 fired when they shouldn't.
LowPass: 6 of 6 as expected (100%), 0 missed, 0 fired when they shouldn't.
```

Alle Optionen — keine davon fasst `config.json` an:

| Option | Wirkung |
|---|---|
| `--verbose` | Zeigt, was Vosk tatsächlich transkribiert hat. Meist der Moment, in dem klar wird, was schiefging. |
| `--compare` | Lässt beide Audiowege laufen und stellt sie nebeneinander. |
| `--filter LowPass\|Average` | Nur einen der beiden Wege. |
| `--keyword <wort>` | Ein anderes Hotword. |
| `--variants <a,b,c>` | Andere akzeptierte Schreibweisen — eine Variantenliste ausprobieren, bevor man sie festschreibt (siehe [12.5](#125-akzent-akzeptieren-wie-das-wort-wirklich-ankommt)). |
| `--suggest-variants` | Schlägt Schreibweisen aus den verpassten Aufnahmen vor. Impliziert `--verbose` und endet immer mit 0. |
| `--model <ordner>` | Ein anderes Vosk-Modell — so vergleicht man Modellgrößen an den eigenen Aufnahmen. |
| `--autogain` | Wendet zusätzlich die optionale Verstärkung für leise Piloten an, um zu sehen, ob sie hier hilft. |

`Darkstar.exe --test-hotword` ohne Pfad nimmt den Ordner `recordings`. Exit-Code 0 heißt, alle Erwartungen aus den Dateinamen wurden erfüllt, 2 heißt, mindestens eine nicht — damit lässt sich das Werkzeug in einem Skript als Prüfung verwenden.

Eigene Fälle ergänzt man durch Umbenennen: eine Datei mit `_silence_` im Namen soll *nicht* auslösen. So lässt sich ein Satz Aufnahmen pflegen, der den Bot niemals wecken darf — Motorengeräusch, Funkverkehr anderer Piloten, die eigene Stimme des Bots.

> Aufnahmen kosten etwa 100 KB pro Sekunde Sprache. Sie werden automatisch aufgeräumt (siehe [Kapitel 10](#10-dateien-logs-und-backups)), die Einstellung sollte nach der Messung aber trotzdem wieder aus.

### 12.5 Akzent: akzeptieren, wie das Wort wirklich ankommt

Das ist der Abschnitt für einen Server, dessen Piloten überwiegend keine englischen Muttersprachler sind — also für die meisten deutschsprachigen Server.

Das Hotword läuft durch ein kleines **englisches** Modell. „Overlord" aus einem deutschen Mund kommt typischerweise als eines davon zurück:

```
over lord      ← der Killer: ein Leerzeichen, also trifft \bOverlord\b nie
oberlord
of a lord
```

Der erste Fall ist der wichtige. Der Bot hat das Wort im Grunde richtig gehört und trotzdem nichts getan, weil der Erkenner mitten hinein ein Leerzeichen gesetzt hat. Noch deutlicher zu sprechen hilft dagegen nicht.

Deshalb kann ein Radio **mehrere Schreibweisen** seines Hotwords akzeptieren — `VoskKeywordVariants` global, `Radios[].KeywordVariants` pro Radio, oder das Feld *„Also accept as the wake word"* auf CH2/CH3:

```json
"VoskKeyword": "Overlord",
"VoskKeywordVariants": ["over lord", "oberlord"]
```

Das ist **kein** Nachtrainieren des Modells. Das Modell hört weiterhin, was es hört; der Bot besteht nur nicht mehr auf einer einzigen Schreibweise davon. (Bei den Anfragen funktioniert das längst so — `DcsIntelBogeyDopeTriggers` enthält absichtlich `"bogie dope"` und `"bogey dobe"`.)

**Varianten gehören zum Wort, nicht zum Radio.** Ein Radio, das das globale Hotword nutzt, nutzt auch die globalen Varianten. Ein Radio mit eigenem `Keyword` beginnt mit einer leeren Liste — sonst würde ein Tanker auf „Texaco" stillschweigend auf „over lord" antworten.

#### Die Liste nicht raten — aus den Aufnahmen ablesen

Jede akzeptierte Variante erhöht auch die Fehltriggerrate. Die Liste soll deshalb aus einer Messung kommen, und der Bot ermittelt sie für dich:

```powershell
Darkstar.exe --test-hotword recordings --suggest-variants
```

Er nimmt die Aufnahmen, die auslösen sollten und es nicht taten, sammelt, was das Modell dort tatsächlich gehört hat, und schlägt die Schreibweisen vor, die deinem Hotword ähneln — sortiert nach Häufigkeit:

```
Variant suggestions from 7 missed recording(s), checked against 12 recording(s) where nobody called:

  phrase                       missed files  edits  note
  --------------------------------------------------------------------------
  over lord                    5             0
  oberlord                     2             1
  of a lord                    3             3      ALSO heard when nobody called - would cause false triggers

  For config.json (add to the radio's KeywordVariants, or VoskKeywordVariants globally):

    "VoskKeywordVariants": ["over lord", "oberlord"]
```

Zwei Dinge an dieser Ausgabe sind wichtig:

- **`edits` ist der Abstand zu deinem Hotword, ohne Leerzeichen gerechnet.** `over lord` erreicht 0 — es unterschied sich nur in der Wortgrenze. Genau deshalb ist es der häufigste Fehlschlag *und* die sicherste Variante.
- **Die Spalte `note` ist der eigentliche Zweck.** Ein Kandidat, der auch auf Aufnahmen auftaucht, bei denen niemand den Bot gerufen hat, wird aufgeführt, aber *nicht* empfohlen — und bleibt aus der fertigen Zeile heraus. Ihn zu akzeptieren würde Treffer gegen einen Bot eintauschen, der Leuten ins Wort fällt.

Danach denselben Lauf mit der neuen Liste wiederholen. Die verpassten Aufnahmen sollten jetzt Treffer sein — und deine `_silence_`-Aufnahmen sollten weiterhin still bleiben. **Diese zweite Zahl entscheidet, ob eine Variante es wert war**, also lohnt sich ein Satz `_silence_`-Dateien: Motorengeräusch, Funk anderer Piloten, normales Durcheinander.

> **Hotwords und Triggerphrasen niemals in die `vocabulary.json`.** Diese Liste sagt dem Transkriber, alles, was einem Eintrag bloß *ähnlich klingt*, auf dessen exakte Schreibweise einzuschnappen — aus unverständlichem Audio wird so ein Befehl, den niemand gegeben hat, siehe [5.3](#53-vocabularyjson). Aussprachevarianten gehören in die Varianten- und Triggerlisten, nie ins Vokabular.

Das Startup-Log nennt jede akzeptierte Schreibweise, damit ein Radio, das auf etwas Überraschendes reagiert, auf das zurückgeführt werden kann, worauf es reagieren durfte:

```
251.000 MHz (AM): wake word "Overlord" (also: "over lord", "oberlord"), callsign "Overlord", answers: tactical
```

#### Was das nicht behebt

Wenn in den Transkripten von `--verbose` überhaupt nichts vorkommt, was dem Hotword ähnelt, helfen Varianten nicht — dann kommt das Modell gar nicht dorthin. Das ist ein Problem der Modellgröße (12.1) oder des Audios (12.2). Ein Hotword, das ein gängiges englisches Wort ist, kommt außerdem häufiger unbeschädigt an als ein erfundenes — bei der Wahl für eine nicht englischsprachige Crew ein Argument.

### 12.6 Wenn es weiterhin nicht stimmt

| Symptom | Wo ansetzen |
|---|---|
| Löst auf andere Wörter aus | Größeres Modell. Schlüsselwörter werden schon als ganze Wörter verglichen, ein längeres Wort mit deinem darin kann also nicht auslösen — `--verbose` zeigt, was wirklich gehört wurde. |
| Löst bei einem bestimmten Piloten nie aus | Dessen Pegel, nicht deine Einstellungen: mit einer Aufnahme prüfen, dann 12.3 erwägen. |
| Löst auf die eigenen Antworten aus | Sollte unmöglich sein — das Radio ist während der Aussendung stummgeschaltet. Falls doch, Log und Aufnahme aufbewahren. |
| Verpasst nur bei Nicht-Muttersprachlern | [12.5](#125-akzent-akzeptieren-wie-das-wort-wirklich-ankommt) — die Schreibweisen akzeptieren, die das Modell wirklich produziert, aus den Aufnahmen ermittelt. |
| Zwei Radios reagieren auf das Hotword des anderen | Fast immer ein Verhören des schwachen Modells; `--verbose` bestätigt es. |

---

## 13. Kosten, Grenzen und Datenschutz

- **Was Geld kostet:** im Normalbetrieb nichts. Vosk, SRS und DCS-gRPC sind kostenlos und laufen lokal; Geminis Freikontingent reicht für Tests und kleine Gruppen. Bei starker Nutzung kann das Tageskontingent überschritten werden — dafür gibt es `GeminiFallbackModel`.
- **Was deinen Rechner verlässt:** nur die aufgenommene Frage, nur nach Auslösen des Hotwords und nur, wenn sie nicht schon durch eine feste Phrase oder durch Missionsdaten beantwortet wird. Das dauerhafte Mithören für das Hotword passiert vollständig lokal.
- **Was protokolliert wird:** Der erkannte Text jeder verarbeiteten Anfrage landet in der Logdatei. Falls das in deiner Gruppe relevant ist, sag es den Leuten — oder schalte das Logging über `LoggingEnabled` ab.
- **Grenzen, die man kennen sollte:** Die Antworten nutzen Windows-TTS, die Qualität hängt also von den installierten Stimmen ab. Vosk arbeitet am besten mit englischer Sprache. Die taktischen Antworten sind nur so gut wie die Daten, die DCS bereitstellt.

---

## Weiterführend

- [README.md](../README.md) — Projektüberblick
- [configuration.md](configuration.md) — Konfigurationsreferenz
- [gui.md](gui.md) — der Konfigurationseditor im Detail
- [contributing.md](contributing.md) — Entwicklungshinweise
- [building-the-installer.md](building-the-installer.md) — Installer bauen
- [changelog.md](changelog.md) — Änderungen

Lizenz: GPL-3.0 — siehe [LICENSE](../LICENSE).
