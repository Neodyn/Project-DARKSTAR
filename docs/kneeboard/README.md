# The pilot kneeboard

Three pages telling a player what they can say to the bot, in which words, and what comes back.
Written for the people flying on your server, not for whoever runs it — hand it out, pin it in
Discord, or put it on their kneeboard in the cockpit.

| File | What it is |
|---|---|
| `DARKSTAR-Kneeboard-1.png` … `-3.png` | The kneeboard pages, 1536 × 2048 — the size DCS uses. |
| `DARKSTAR-Kneeboard.pdf` | The same three pages, to print or post. |
| `darkstar-kneeboard.html` | **The source.** Edit this, then re-render. |
| `build-kneeboard.py` | Renders the PNGs and the PDF from the source. |

## Putting it in DCS

Copy the three PNGs into

```
%USERPROFILE%\Saved Games\DCS\Kneeboard\
```

for every aircraft, or into `…\Kneeboard\<Aircraft>\` (e.g. `…\Kneeboard\F-16C_50\`) for one type.
They appear in the kneeboard in file-name order, which is why they are numbered. `DCS.openbeta`
instead of `DCS` if that is your install.

## Before you hand it out

Two things on page 1 are deliberately not filled in, because only you know them:

- **The frequency box is blank.** Write your server's numbers in, or type them into the HTML and
  re-render. A frequency printed here that does not match your SRS is worse than a blank line. The
  values are the ones the bot writes onto the F10 map at mission start.
- **The wake word is "Overlord"** throughout, because that is the default. If you changed
  `VoskKeyword`, search the HTML for `Overlord` and replace it.

Everything else is the shipped default. If you have edited the trigger phrases in `config.json` or
`phrases.json`, the pages are wrong by exactly that much.

## Re-rendering

```
python3 build-kneeboard.py              # finds Chromium or Chrome itself
python3 build-kneeboard.py "C:\Program Files\Google\Chrome\Application\chrome.exe"
```

Needs Chromium or Chrome, and Pillow (`pip install pillow`). It prints how full each page is and
**fails if one of them overflows**, which is the point: a kneeboard page does not scroll, and a
page whose content grew past 1024 px simply loses the bottom of itself with nothing in DCS to say
so. That happened to the first draft of page 1 — two calls were missing and the render looked fine.

The script also measures how much of the window this particular browser gives the page (headless
Chromium keeps some of it) rather than assuming, and checks that the finished image really is
1536 × 2048 with the page reaching the bottom edge.
