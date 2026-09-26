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
3. Wähle aus, was installiert werden soll:

   | Komponente | Was es ist |
   |---|---|
   | Bot service | Der Bot selbst. Wird immer installiert. |
   | Config GUI | Der grafische Konfigurationseditor. Empfohlen. |
   | Vosk model | Das Offline-Sprachmodell. Nur im vollen Installer, nicht in der `-slim`-Variante. |

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
| `DebugLogging` | `false` | Ausführliche Konsolenausgabe (UDP-Pakete, rohes JSON, Pegelwerte). Die Log*datei* enthält ohnehin immer alle Details. |

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
| `Radios` | `[]` | Die gleichzeitig überwachten Radios. Je Eintrag: `FrequencyHz`, `Modulation` (`"AM"`/`"FM"`), optional `Keyword`, optional `Callsign`. |
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
| `PlayerNameCallsignSeparator` | `"\|"` | Schneidet das Rufzeichen aus dem SRS-Namen: `Enfield 1-1 \| neodym` → der Bot sagt „Enfield 1-1“. Fehlt das Trennzeichen, wird der ganze Name verwendet. |

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
| `HotwordAudioFilter` | `"LowPass"` | Audio-Aufbereitung vor Vosk. `"Average"` ist der alte Weg, nur zum Vergleich — siehe [Kapitel 12](#12-hotword-genauigkeit). |
| `HotwordAutoGain` | `false` | Leise Piloten für die Erkennung verstärken. Standardmäßig aus; kann Fehlauslösungen verursachen. |
| `SaveRecordings` | `false` | Jede Aussendung nach `recordings\` speichern, um die Genauigkeit zu messen. |
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
| `VoiceName` | `""` | Windows-TTS-Stimme, z. B. `"Microsoft David Desktop"`. Leer = Standardstimme. `DCS-SR-ExternalAudio.exe --help` listet die Optionen. |
| `ExternalAudioExtraArgs` | `""` | Zusätzliche Argumente für jede Aussendung, für Optionen, die der Bot nicht selbst setzt. Ob deine SRS-Version einen Parameter fürs Sprechtempo hat und wie er heißt, hängt von der Version ab — in `--help` nachsehen und hier eintragen (z. B. `--speed=-1`). |

#### Zwischenansage bei langsamer Antwort

| Feld | Vorgabe | Beschreibung |
|---|---|---|
| `AckEnabled` | `false` | Hauptschalter für die Zwischenansage. |
| `AckAfterSeconds` | `4.0` | Wie lange der Bot schweigen darf, gezählt **ab dem Hotword**. Ist die echte Antwort früher fertig, wird nichts gesendet. |
| `AckMessage` | `"{pilot}, this is {callsign}, message received, standby."` | `{pilot}` = Rufzeichen des Piloten, `{callsign}` = Rufzeichen dieses Radios. Ist der Pilot unbekannt, entfällt `"{pilot}, "` automatisch. |

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
| `DcsIntelNoContactsReply` | `"Picture clean."` | Wenn nichts auf die Filter passt. |
| `DcsIntelUnavailableReply` | `"Negative, no tactical data available at this time."` | Wenn die Missionsdaten gar nicht lesbar sind. |

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
| `DiscordWebhookUrl` | `""` | Webhook für Start/Stopp und SRS-Verbindungsverlust. In Discord unter *Kanaleinstellungen → Integrationen → Webhooks* anlegen. |

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

### 5.3 `vocabulary.json`

Eine schlichte Liste von Begriffen, die Gemini als Hinweis mitbekommt. Sie ändert nicht, worüber der Bot sprechen kann, sondern verbessert die Erkennung von Wörtern, die kein gewöhnliches Englisch sind:

```json
["Viggen", "Overlord", "Bullseye", "Texaco", "Enfield"]
```

---

## 6. Der Konfigurationseditor (GUI)

`Darkstar.ConfigEditor.exe` bearbeitet alle drei Dateien grafisch — und benutzt dafür exakt denselben Code wie der Bot, sodass nichts auseinanderlaufen kann. Den Konfigurationsordner findet er automatisch, indem er seinen eigenen Ordner, die darüberliegenden Ordner und deren `bin\`-Unterordner durchsucht.

Zum Ansehen muss der Bot nicht gestoppt werden; geänderte Einstellungen greifen aber erst nach einem Neustart des Bots.

| Kanal | Inhalt |
|---|---|
| **CH1 Connection** | Konfigurationsordner, SRS-Host/-Port, Clientname, EAM-Passwort, der Pfad zu `DCS-SR-ExternalAudio.exe` samt Knopf **Detect SRS installation**, Koalition, Koalitionsbeschränkung. |
| **CH2 Radios** | Die Radioliste: Frequenz, Modulation, Hotword und Rufzeichen je Radio, hinzufügen/entfernen. |
| **CH3 Speech** | Gemini-Key/-Modell/-Wiederholungen, TTS-Stimme, Pre-Roll, Vosk-Modellordner, globales Hotword, Stille-Frames, die Zwischenansage und die Werte des Platzhalter-Detektors. |
| **CH4 Phrases** | Die Trigger-/Antworttabelle plus `RestrictToKnownPhrases` und die Fallback-Antwort. |
| **CH5 Vocabulary** | Die Hinweiswortliste als Chips. |
| **CH6 Discord** | Hauptschalter und Webhook-URL. |
| **CH7 Logging** | Log-Schalter, ermittelter Log-Ordner und eine Live-Ansicht der neuesten Logzeilen. |
| **CH8 DCS-gRPC** | Verbindungstest, Einstellungen der taktischen Antworten mit Testknopf, Missionsdaten-Explorer. |
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

Peilungen werden ziffernweise gesprochen („zero niner zero“), weil die TTS `090` sonst als „ninety“ liest. Mit `DcsIntelSlowSpeech` (standardmäßig an) steht zwischen den Ziffern ein Komma und die übrigen Zahlen werden als Wörter ausgeschrieben — das hält die Stimme davon ab, sie herunterzurattern. Flugzeug- und Helikoptertypen werden angesagt, wenn `DcsIntelSayContactType` an ist. Der Aspect folgt der üblichen Brevity: **hot** (fliegt auf dich zu), **flanking**, **beaming**, **cold** (fliegt weg).

Für BRAA vom eigenen Flugzeug aus muss der Bot *dein* Flugzeug finden: Er gleicht deinen SRS-Namen mit den DCS-Spielernamen ab. Klappt das nicht — etwa weil dein SRS-Name ganz anders lautet als dein DCS-Name — wechselt er automatisch aufs Bullseye-Format, statt die Anfrage abzulehnen.

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


### Mehrere Radios

Jedes Radio antwortet nur auf sein eigenes Hotword und mit seinem eigenen Rufzeichen. Eine Tankerfrequenz kann als „Texaco“ laufen, während die AWACS-Frequenz gleichzeitig als „Overlord“ arbeitet — jede mit eigenem Gesprächsverlauf.

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

1. GUI → **CH8 DCS-gRPC** → **Enable DCS-gRPC** einschalten, Adresse und Port prüfen.
2. **Test connection.** Ein erfolgreicher Test belegt zugleich, dass tatsächlich eine Mission läuft, denn er fragt die Missionszeit ab.

### 8.3 Taktische Antworten aktivieren

Ebenfalls auf CH8, unter **Tactical replies**:

- **Answer tactical requests from mission data** einschalten.
- **Contact source** und **AWACS unit name:** zusammen bestimmen sie, woher die Kontakte kommen dürfen — siehe unten.
- Reichweite, Gruppenzahl und Triggerphrasen nach Geschmack anpassen.
- **„Try it without flying“** zeigt den exakten Satz, den der Bot sagen würde, samt Datenquelle — gesendet wird dabei nichts.

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

### 8.4 Der Missionsdaten-Explorer

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
- **Ein Dienst hat kein Konsolenfenster.** Was er tut, steht in den Logdateien unter `logs\`.

Alternativ kann der Installer den Dienst während der Installation registrieren, oder du machst es von Hand mit `sc.exe`.

---

## 10. Dateien, Logs und Backups

Alles liegt neben der EXE des Bots:

| Pfad | Inhalt |
|---|---|
| `config.json` | Sämtliche Einstellungen. |
| `phrases.json` | Feste Frage-/Antwortpaare. |
| `vocabulary.json` | Erkennungshinweise. |
| `logs\` | Logdateien mit Zeitstempel, immer mit allen Details. |
| `Backup\` | Automatische Sicherungskopien vor jeder automatischen Änderung. |
| `grpc-dumps\` | Gespeicherte JSON-Ergebnisse aus dem Missionsdaten-Explorer. |

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

---

## 11. Fehlersuche

| Symptom | Wahrscheinliche Ursache und Abhilfe |
|---|---|
| **Bot beendet sich gleich nach dem Start, im Log steht „the wake word model could not be loaded"** | Der Ordner in `VoskModelPath` fehlt oder enthält kein Vosk-Modell. Das Log nennt den Ordner und was zu tun ist; ein Modellordner enthält `am\`, `conf\`, `graph\` und `ivector\`. Nach einer `-slim`-Installation muss das Modell separat geladen werden. |
| **Bot erscheint nicht in der SRS-Clientliste** | Falscher `SrsHost`/`SrsPort`, Server läuft nicht, oder Firewall. Verbindungszeile im Log prüfen. |
| **Bot reagiert auf gar nichts** | `VoskModelPath` leer oder falsch (das Log sagt es beim Start), falsche Frequenz/Modulation, oder das Hotword wird nicht erkannt — `[STT]`-Zeilen ansehen und [Kapitel 12](#12-hotword-genauigkeit). |
| **Hotword wird nur manchmal erkannt** | Modell zu schwach. Auf `Standard` wechseln (`-VoskModelSize Standard`), vorher den alten Modellordner löschen. Das ist mit Abstand die häufigste Ursache. Systematisch nachmessen: [Kapitel 12](#12-hotword-genauigkeit). |
| **Ein Radio reagiert auf das falsche Hotword** | Fast immer eine Fehltranskription des schwachen Modells — `[STT]` prüfen. Schlüsselwörter werden als ganze Wörter verglichen, ein längeres Wort löst also nicht aus. |
| **Keine Antwort, im Log ein Gemini-Fehler** | Key fehlt/ungültig oder Kontingent erschöpft. `GeminiFallbackModel` setzen oder auf die Zurücksetzung warten. |
| **Antwort wird erzeugt, aber nie gehört** | `ExternalAudioExePath` falsch oder TTS-Stimme nicht installiert. Auf CH1 **Detect SRS installation** drücken, dann eine Aussendung manuell testen (siehe [Kapitel 4](#4-erster-start)). Das Log nennt den gefundenen Pfad, wenn der eingetragene nicht existiert. |
| **Bot antwortet auf seine eigenen Antworten** | Sollte unmöglich sein — das Radio ist währenddessen selbst stummgeschaltet. Falls doch, bitte mit Log melden. |
| **Zahlen werden heruntergerattert / sind schwer verständlich** | `DcsIntelSlowSpeech` einschalten (GUI: CH8 → *Slow, clearly spoken numbers*). Ist die Stimme insgesamt zu schnell, eine andere `VoiceName` probieren oder — falls deine SRS-Version das kann — einen Tempo-Parameter über `ExternalAudioExtraArgs` setzen. |
| **Antwort kommt sehr spät** | 2–5 s sind normal. Zwischenansage aktivieren, damit Piloten wissen, dass sie gehört wurden. |
| **Threat-Circle-Warnungen kommen nie an** | Im Log nach `[ThreatCircle]`-Zeilen sehen, die jeden Durchlauf protokollieren. Meist lässt sich das Flugzeug des Piloten nicht seinem SRS-Namen zuordnen (der Kreis braucht es als Mittelpunkt), oder der Kreis ist bereits abgelaufen. |
| **Taktische Anfragen liefern „no tactical data“** | DCS-gRPC läuft nicht, keine Mission geladen, falsche Adresse, oder `DcsGrpcEnabled` aus. Auf CH8 testen. |
| **Taktische Antworten nutzen immer Bullseye statt BRAA** | Dein SRS-Name ließ sich keinem DCS-Spielernamen zuordnen. Namen angleichen oder die Trennzeichen-Konvention nutzen (`CALLSIGN 1-1 \| handle`). |
| **„Picture clean“, obwohl Feinde in der Luft sind** | Die konfigurierte AWACS-Einheit ist spielergeflogen oder existiert nicht, dazu eine zu enge Reichweitengrenze. Zum Test `DcsIntelAwacsUnitName` leeren oder `DcsIntelMaxRangeNm` erhöhen. |
| **Dienst startet nicht** | Meist ein Pfadproblem: Auf CH9 die registrierte EXE prüfen und ob die `config.json` in *diesem* Ordner liegt. |
| **Dienst lässt sich nicht entfernen** | Etwas hält noch ein Handle darauf (services.msc, Task-Manager). Beides schließen und erneut versuchen; ein Neustart räumt es immer auf. |
| **GUI meldet „config.json exists but has invalid JSON“** | Absicht: Der Editor weigert sich, eine kaputte Datei mit Standardwerten zu überschreiben. Syntax korrigieren (das Log nennt die Stelle) und neu laden. |
| **Aussendung an einen entfernten SRS-Server kommt nicht an** | `DCS-SR-ExternalAudio.exe` wird derzeit ohne Serverparameter aufgerufen und sendet damit an `127.0.0.1`. Bei entferntem SRS-Server bleiben die Antworten lokal. Den richtigen Parameternamen in `--help` nachsehen und einbauen lassen. |

Kommst du nicht weiter: Die Logdatei samt den `[STT]`-Zeilen rund um den Fehler erklärt das meiste — und genau das gehört in einen Fehlerbericht (mit geschwärztem `GeminiApiKey`, `DiscordWebhookUrl` und `DcsGrpcApiKey`).

---

## 12. Hotword-Genauigkeit

Ein Hotword, das nicht anspringt — oder anspringt, obwohl niemand es gesagt hat — ist die häufigste Beschwerde über so einen Aufbau. Drei Dinge entscheiden darüber, in dieser Reihenfolge.

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

Nützliche Optionen: `--verbose` zeigt, was Vosk tatsächlich transkribiert hat — meist der Moment, in dem klar wird, was schiefging; `--keyword` und `--model` probieren ein anderes Wort oder Modell, ohne `config.json` anzufassen; `--filter` läuft nur einen der beiden Wege. `Darkstar.exe --test-hotword` ohne Pfad nimmt den Ordner `recordings`.

Eigene Fälle ergänzt man durch Umbenennen: eine Datei mit `_silence_` im Namen soll *nicht* auslösen. So lässt sich ein Satz Aufnahmen pflegen, der den Bot niemals wecken darf — Motorengeräusch, Funkverkehr anderer Piloten, die eigene Stimme des Bots.

> Aufnahmen kosten etwa 100 KB pro Sekunde Sprache, und nichts löscht sie wieder. Die Einstellung nach der Messung wieder ausschalten.

### 12.5 Wenn es weiterhin nicht stimmt

| Symptom | Wo ansetzen |
|---|---|
| Löst auf andere Wörter aus | Größeres Modell. Schlüsselwörter werden schon als ganze Wörter verglichen, ein längeres Wort mit deinem darin kann also nicht auslösen — `--verbose` zeigt, was wirklich gehört wurde. |
| Löst bei einem bestimmten Piloten nie aus | Dessen Pegel, nicht deine Einstellungen: mit einer Aufnahme prüfen, dann 12.3 erwägen. |
| Löst auf die eigenen Antworten aus | Sollte unmöglich sein — das Radio ist während der Aussendung stummgeschaltet. Falls doch, Log und Aufnahme aufbewahren. |
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
