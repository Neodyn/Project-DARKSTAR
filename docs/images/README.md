# Images

Artwork used by the two front pages, kept out of the repository root so that root holds only the
solution, the licence, the two READMEs and the folders.

| File | Used by |
|---|---|
| `darkstar2.jpg` | the banner at the top of `README.md` and `README.de.md` |
| `darkstar-icon-512.png` | the icon as a plain image, for anywhere an `.ico` will not do — a GitHub social preview, a forum post, a Discord server icon |

The application icon itself is **not** here: `src\Darkstar.ico` sits next to the code that embeds
it, because both projects reference it from their `.csproj` and a path into `docs\` would be odd.
It was cut from `darkstar2.jpg` — the aircraft and the radar rings, without the lettering, which
is unreadable below about 64 px. Two crops in one file: tighter for 16–48 px so the rotodome still
reads, wider from 64 px up so the wings and engines are there. Re-cut it with a drawing program if
the artwork changes; nothing regenerates it.

The kneeboard pages are **not** here: they are generated, and they live next to the source they are
generated from, in [`docs/kneeboard/out/`](../kneeboard/out/). Anything in this folder is a file
somebody made by hand and nothing regenerates.
