# Das Kneeboard für Piloten

[English](README.md) · **Deutsch**

Drei Seiten, die einem Spieler sagen, was er dem Bot sagen kann, in welchem Wortlaut und was
zurückkommt. Geschrieben für die Leute, die auf deinem Server fliegen, nicht für den, der ihn
betreibt — zum Austeilen, in Discord anpinnen oder im Cockpit aufs Kneeboard legen.

| Datei | Was es ist |
|---|---|
| `DARKSTAR-Kneeboard-DE-1.png` … `-3.png` | Die Kneeboard-Seiten, 1536 × 2048 — die Größe, die DCS verwendet. |
| `DARKSTAR-Kneeboard-DE.pdf` | Dieselben drei Seiten zum Drucken oder Posten. |
| `DARKSTAR-Kneeboard-EN-*` | Der englische Satz. Gleiche Seiten, gleiche Calls. |
| `darkstar-kneeboard-de.html` / `-en.html` | **Die Quellen.** Hier ändern, dann neu rendern. |
| `build-kneeboard.py` | Rendert beide Sprachen. |

**Die Calls sind in beiden Fassungen Englisch**, denn darauf hört der Bot und so antwortet er.
Übersetzt ist nur, was drumherum erklärt — wer die deutschen Seiten liest, sagt trotzdem
*„Overlord, bogey dope."*

## In DCS einbauen

Die drei PNGs **einer** Sprache kopieren nach

```
%USERPROFILE%\Saved Games\DCS\Kneeboard\
```

für alle Flugzeuge, oder nach `…\Kneeboard\<Flugzeug>\` (z. B. `…\Kneeboard\F-16C_50\`) für
einen Typ. Sie erscheinen im Kneeboard in Dateinamen-Reihenfolge, daher die Nummerierung.
`DCS.openbeta` statt `DCS`, falls das deine Installation ist. Beide Sprachen gleichzeitig geht
auch — dann sind es eben sechs Seiten.

## Vor dem Austeilen

Zwei Dinge auf Seite 1 sind absichtlich nicht ausgefüllt, weil nur du sie kennst:

- **Das Frequenz-Kästchen ist leer.** Trag die Zahlen deines Servers ein oder schreib sie in die
  HTML und render neu. Eine gedruckte Frequenz, die nicht zu deinem SRS passt, ist schlechter als
  eine leere Zeile. Die Werte stehen auf den F10-Markern, die der Bot beim Missionsstart schreibt.
- **Das Hotword heißt überall „Overlord"**, weil das der Standard ist. Wenn du `VoskKeyword`
  geändert hast: in der HTML nach `Overlord` suchen und ersetzen.

Alles andere ist der Auslieferungszustand. Wenn du die Auslösephrasen in `config.json` oder
`phrases.json` geändert hast, stimmen die Seiten genau um diesen Unterschied nicht mehr.

## Neu rendern

```
python3 build-kneeboard.py              # findet Chromium oder Chrome selbst
python3 build-kneeboard.py "C:\Program Files\Google\Chrome\Application\chrome.exe"
```

Braucht Chromium oder Chrome und Pillow (`pip install pillow`). Es werden immer beide Sprachen
gerendert, die können also nicht auseinanderlaufen.

Das Skript gibt aus, wie voll jede Seite ist, und **bricht ab, wenn eine überläuft**. Genau darum
geht es: Eine Kneeboard-Seite scrollt nicht — was über 1024 px hinausgeht, fehlt einfach, ohne dass
DCS etwas dazu sagt. Dem ersten Entwurf von Seite 1 ist genau das passiert: zwei Calls fehlten, und
das Rendering sah tadellos aus.

Außerdem misst das Skript, wie viel der Fensterhöhe dieser Browser der Seite tatsächlich gibt
(Headless-Chromium behält einen Teil für sich), statt eine Zahl anzunehmen, und prüft, dass das
fertige Bild wirklich 1536 × 2048 ist und die Seite bis zur Unterkante reicht.

## Weiterführend

[Vollständiges Handbuch](../manual-de.md) ·
[alle Standard-Calls](../manual-de.md#alle-standard-calls-auf-einen-blick) ·
[Konfigurationsfelder](../configuration.md) *(englisch)*
