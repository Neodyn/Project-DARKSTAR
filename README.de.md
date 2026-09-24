# D.A.R.K.S.T.A.R.

**D**igital **A**ssistant for **R**adio **K**eyword-activated **S**peech **T**ranscription **A**nd **R**esponse

*🇬🇧 [This page in English](README.md) · 📖 [Vollständiges Handbuch](docs/handbuch-de.md)*

Ein sprachgesteuerter Funk-Assistent für [DCS World](https://www.digitalcombatsimulator.com/), der sich als External-AWACS-Client mit einem [DCS-SimpleRadio-Standalone](https://github.com/ciribob/DCS-SimpleRadio-Standalone)-Server (SRS) verbindet.

Ein Pilot drückt auf einer überwachten Frequenz die Sendetaste, sagt das Hotword und stellt eine Frage — der Bot transkribiert sie, ermittelt eine Antwort und funkt sie mit synthetischer Stimme zurück.

> *„Overlord, bogey dope."*
> *„Enfield 1-1, this is Overlord… Bogey, bearing zero, niner, zero, thirty five miles, twenty two thousand, hot, group of two, type MiG-29."*

---

## Was er kann

- **Hotword-Erkennung offline** — [Vosk](https://alphacephei.com/vosk/) transkribiert lokal und achtet auf dein Schlüsselwort. Kein Account, keine Kosten pro Anfrage, und für diesen Schritt verlässt kein Audio den Rechner.
- **Antworten aus der laufenden Mission** — „bogey dope", „picture" und „threat check" werden über [DCS-gRPC](https://github.com/DCS-gRPC/rust-server) aus echten DCS-Daten beantwortet: BRAA mit Aspect vom eigenen Flugzeug aus oder im Bullseye-Format.
- **Threat Circle** — ein Pilot schaltet eine mitfliegende Überwachung um sein Flugzeug scharf („threat circle forty miles") und wird gewarnt, sobald ein Feind eindringt.
- **Feste Phrasen und freie Antworten** — bekannte Frage-/Antwortpaare werden direkt bedient; alles andere kann an Google Gemini gehen oder abgelehnt werden, ganz wie du willst.
- **Mehrere Radios gleichzeitig** — jede Frequenz mit eigenem Hotword, Rufzeichen und Gesprächsverlauf, etwa „Overlord" für AWACS und „Texaco" für den Tanker.
- **Koalitionsbewusst** — kann die Gegenseite vollständig ignorieren, so wie es ein echter Funkkreis täte.
- **Konfigurationseditor** — eine Desktop-GUI für jede Einstellung, mit DCS-gRPC-Verbindungstest, nur lesendem Missionsdaten-Explorer und Dienstverwaltung per Knopfdruck.
- **Installer per Einzelbefehl** — erzeugt eine `Setup.exe`, die auf einem nackten Windows alle Laufzeitabhängigkeiten mitinstalliert.

## Schnellstart

```powershell
# 1. Entwicklungsrechner einrichten (SDK, Workloads, Runtimes, Sprachmodell)
.\setup-dev-environment.ps1 -ProjectRoot "C:\pfad\zum\repo" -VoskModelSize Standard

# 2. Erster Start: legt config.json neben der EXE an und beendet sich
dotnet run --project Darkstar.csproj

# 3. SRS-Server, Radios, VoskModelPath und GeminiApiKey eintragen - von Hand oder in der GUI - dann erneut starten
```

Du willst es stattdessen auf einem anderen Rechner installieren? Dann den Installer bauen:

```powershell
.\build-installer.ps1                      # mit mitgeliefertem Sprachmodell
.\build-installer.ps1 -Slim                # schlanker Installer, Modell kommt separat
```

Alles im Detail — Voraussetzungen, jede Einstellung, Bedienung am Funk, DCS-gRPC-Einrichtung, Fehlersuche — steht im **[vollständigen Handbuch](docs/handbuch-de.md)**.

## Dokumentation

| Dokument | Inhalt |
|---|---|
| **[Handbuch](docs/handbuch-de.md)** | Die vollständige Anleitung: Voraussetzungen, Installation, Konfiguration, Bedienung am Funk, DCS-gRPC, Windows-Dienst, Fehlersuche. |
| [Konfigurationsreferenz](docs/configuration.md) | Jedes Feld von `config.json`, `phrases.json` und `vocabulary.json` (englisch). |
| [Konfigurationseditor (GUI)](docs/gui.md) | Alle neun Kanäle und die Besonderheiten des Blazor-Hybrid-Builds (englisch). |
| [Installer bauen](docs/building-the-installer.md) | Wie `build-installer.ps1` und das Inno-Setup-Skript zusammenspielen (englisch). |
| [Mitarbeiten](docs/contributing.md) | Grundregeln und Projektaufbau für die Arbeit am Code (englisch). |
| [Änderungen](docs/changelog.md) | Was sich geändert hat (englisch). |
| 🇬🇧 [Manual (English)](docs/manual-en.md) | The same complete documentation in English. |

## Voraussetzungen

Windows, das [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), ein laufender SRS-Server samt `DCS-SR-ExternalAudio.exe`, ein [Vosk-Modell](https://alphacephei.com/vosk/models) und ein [Gemini-API-Key](https://aistudio.google.com/apikey) (die kostenlose Stufe reicht). Optional: ein [DCS-gRPC](https://github.com/DCS-gRPC/rust-server)-Server für die taktischen Antworten. Das [Handbuch](docs/handbuch-de.md#2-was-du-brauchst) erklärt jeden Punkt einzeln.

## Projektaufbau

```
Darkstar.csproj, *.cs        Der Bot: SRS-Verbindung, Audio, Hotword, Antworten
Darkstar.Core/               Gemeinsame Bibliothek: Konfiguration, Phrasen, Logging, DCS-gRPC, Dienstverwaltung
Darkstar.Gui/                Konfigurationseditor (Blazor Hybrid über WPF)
installer/                   Inno-Setup-Skript
docs/                        Die gesamte Dokumentation
build-installer.ps1          Baut die auslieferbare Setup.exe
setup-dev-environment.ps1    Richtet einen Entwicklungsrechner ein
```

`Darkstar.csproj` liegt bewusst direkt im Wurzelverzeichnis — seine Projektverweise zeigen relativ zu sich selbst auf `Darkstar.Core\`. In einem Unterordner lässt sich die Solution nicht mehr laden.

## Lizenz

GPL-3.0 — siehe [LICENSE](LICENSE).
