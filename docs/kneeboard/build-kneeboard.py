#!/usr/bin/env python3
"""
Renders darkstar-kneeboard.html into the PNG files DCS uses as kneeboard pages, and refuses to
produce one that is missing part of its content.

    python3 build-kneeboard.py [path-to-chromium-or-chrome]

WHY A SCRIPT AND NOT "EXPORT FROM THE BROWSER": a kneeboard page is a fixed 1536x2048 image. A
page whose content grew past that does not scroll in the cockpit and does not warn anybody - it is
simply cut off, and the two calls at the bottom are gone. That happened on the first draft. So the
page is rendered twice: once at its real size for the file, and once with the height released, to
measure how tall the content actually wanted to be. Anything over the limit fails the build with
the number of pixels it overflowed by.

Output next to this file:
    DARKSTAR-Kneeboard-1.png  2.png  3.png    -> Saved Games\\DCS\\Kneeboard\\
    DARKSTAR-Kneeboard.pdf                    -> the same three pages to print as a handout

Needs: Chromium or Chrome, and Pillow (pip install pillow).
"""

import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "darkstar-kneeboard.html")

PAGES = (1, 2, 3)
CSS_WIDTH, CSS_HEIGHT = 768, 1024      # the page as it is designed
SCALE = 2                              # 1536x2048 - what DCS expects of a kneeboard page
MEASURE_HEIGHT = 2600                  # tall enough that nothing is clipped while measuring


def find_browser(argv):
    if len(argv) > 1:
        return argv[1]
    for candidate in ("/opt/pw-browsers/chromium", "chromium", "chromium-browser",
                      "google-chrome", "chrome"):
        found = shutil.which(candidate) or (candidate if os.path.exists(candidate) else None)
        if found:
            return found
    sys.exit("No Chromium/Chrome found. Pass the path as the first argument.")


def viewport_offset(browser, scratch):
    """
    How much of the window this browser keeps for itself, in CSS pixels.

    Headless Chromium does not give the page the whole window: this build hands it 87 px less,
    so a screenshot taken at exactly 768x1024 ends with a band of background where the bottom of
    the page should be - which is how the first rendered kneeboard lost its footer. The number
    differs between versions, so it is measured here rather than written down: render a box of a
    known height and see where it actually ends.
    """
    probe_html = os.path.join(scratch, "probe.html")
    probe_png = os.path.join(scratch, "probe.png")
    with open(probe_html, "w", encoding="utf-8") as handle:
        handle.write('<html><body style="margin:0;background:#000">'
                     f'<div style="width:{CSS_WIDTH}px;height:{CSS_HEIGHT}px;background:#fff">'
                     '</div></body></html>')

    subprocess.run(
        [browser, "--headless", "--no-sandbox", "--disable-gpu", "--hide-scrollbars",
         "--no-first-run", "--disable-background-networking", "--disable-default-apps",
         f"--force-device-scale-factor={SCALE}",
         f"--window-size={CSS_WIDTH},{CSS_HEIGHT}",
         f"--screenshot={probe_png}", f"file://{probe_html}"],
        check=True, capture_output=True)

    visible = content_height(probe_png)
    return max(0, round(CSS_HEIGHT - visible))


def shoot(browser, fragment, out_path, width, height):
    subprocess.run(
        [browser, "--headless", "--no-sandbox", "--disable-gpu", "--hide-scrollbars",
         # Nothing here needs the network, and a browser that reaches for it on a build machine
         # only produces noise in the log and seconds on the clock.
         "--no-first-run", "--disable-background-networking", "--disable-default-apps",
         f"--force-device-scale-factor={SCALE}",
         f"--window-size={width},{height}",
         f"--screenshot={out_path}",
         f"file://{SOURCE}{fragment}"],
        check=True, capture_output=True)


def content_height(image_path):
    """
    How tall the rendered page really is, in CSS pixels.

    The page is white on a grey body, so the last row that still contains a white pixel is the
    bottom of the page box. Measuring the drawing rather than asking the browser keeps this to one
    tool: no debugging protocol, no JSON, nothing to get out of sync with the renderer that
    produces the actual file.
    """
    from PIL import Image

    with Image.open(image_path) as image:
        grey = image.convert("L")
        width, height = grey.size
        pixels = grey.load()

        for y in range(height - 1, -1, -1):
            # The page is white (255); the body behind it is mid grey. Sampling across the row is
            # enough and is far quicker than reading every pixel of a 1536x2600 image.
            for x in range(0, width, 16):
                if pixels[x, y] > 240:
                    return (y + 1) / SCALE

    return 0


def crop_to_page(image_path):
    """
    Trims the render to exactly one kneeboard page and checks that it really is one: the last row
    has to be part of the page, not the background behind it. A page that came out short would
    otherwise ship with a grey stripe across the bottom of the cockpit.
    """
    from PIL import Image

    want = (CSS_WIDTH * SCALE, CSS_HEIGHT * SCALE)
    with Image.open(image_path) as image:
        if image.size != want:
            image = image.crop((0, 0, want[0], want[1]))
        page = image.convert("RGB")
        page.load()

    bottom_row = [page.getpixel((x, want[1] - 1)) for x in range(0, want[0], 64)]
    if not any(sum(pixel) > 720 for pixel in bottom_row):
        raise SystemExit(f"{os.path.basename(image_path)}: the page does not reach the bottom of "
                         f"the image - the renderer gave it less height than asked for.")

    page.save(image_path)


def main():
    browser = find_browser(sys.argv)
    if not os.path.exists(SOURCE):
        sys.exit(f"Source page not found: {SOURCE}")

    print(f"Rendering {os.path.basename(SOURCE)} with {browser}")
    overflowing = []
    produced = []

    with tempfile.TemporaryDirectory() as scratch:
        offset = viewport_offset(browser, scratch)
        print(f"  viewport is {offset} px shorter than the window on this browser"
              if offset else "  viewport matches the window")

        for page in PAGES:
            # 1. Measure: the page may grow, so an overflow shows up as extra height.
            probe = os.path.join(scratch, f"measure-{page}.png")
            shoot(browser, f"#p{page}-measure", probe, CSS_WIDTH, MEASURE_HEIGHT)
            tall = content_height(probe)

            # 2. The real file, at the size DCS wants: rendered in a window tall enough that the
            #    page gets its full height, then cropped to exactly 1536x2048.
            out = os.path.join(HERE, f"DARKSTAR-Kneeboard-{page}.png")
            shoot(browser, f"#p{page}", out, CSS_WIDTH, CSS_HEIGHT + offset)
            crop_to_page(out)
            produced.append(out)

            over = tall - CSS_HEIGHT
            status = "ok" if over <= 0.5 else f"OVERFLOWS by {over:.0f} px"
            print(f"  page {page}: content {tall:.0f} of {CSS_HEIGHT} px  [{status}]")
            if over > 0.5:
                overflowing.append((page, over))

    if overflowing:
        print()
        for page, over in overflowing:
            print(f"ERROR: page {page} is {over:.0f} CSS px too tall - that much is cut off the "
                  f"bottom of the kneeboard and nothing in DCS will say so.")
        print("Shorten the page or move the surplus onto the next one, then run this again.")
        return 1

    # The handout: the same three pages, in one printable file.
    pdf = os.path.join(HERE, "DARKSTAR-Kneeboard.pdf")
    try:
        from PIL import Image
        Image.init()   # registers the JPEG writer the PDF encoder reaches for; some builds don't
        sheets = [Image.open(p).convert("RGB") for p in produced]
        sheets[0].save(pdf, save_all=True, append_images=sheets[1:], resolution=150.0)
        print(f"  handout:  {os.path.basename(pdf)}")
    except Exception as error:                      # a missing PDF is not worth failing the build
        print(f"  handout:  skipped ({error})")

    print("\nDone. Copy the PNGs into  Saved Games\\DCS\\Kneeboard\\  (all aircraft) or")
    print("Saved Games\\DCS\\Kneeboard\\<Aircraft>\\  for one type only.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
