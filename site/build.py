"""Build the dependency-free SkillView website for GitHub Pages."""

from pathlib import Path
from html.parser import HTMLParser
import shutil

ROOT = Path(__file__).resolve().parent.parent
SITE = ROOT / "site"
OUTPUT = ROOT / "dist" / "skillview-site"

PAGES = ("index.html", "docs.html", "styles.css", "app.js", "favicon.svg", "kevin-harder.webp", "robots.txt", "sitemap.xml")
MEDIA = {
    "g1-discover.gif": ROOT / "assets" / "animations" / "g1-discover.gif",
    "g2-installed.gif": ROOT / "assets" / "animations" / "g2-installed.gif",
    "01-discover.png": ROOT / "assets" / "screenshots" / "01-discover.png",
    "02-preview.png": ROOT / "assets" / "screenshots" / "02-preview.png",
    "03-install-dialog.png": ROOT / "assets" / "screenshots" / "03-install-dialog.png",
    "05-installed.png": ROOT / "assets" / "screenshots" / "05-installed.png",
    "08-cleanup.png": ROOT / "assets" / "screenshots" / "08-cleanup.png",
}

if OUTPUT.exists():
    shutil.rmtree(OUTPUT)
OUTPUT.mkdir(parents=True)
(OUTPUT / "media").mkdir()

for name in PAGES:
    shutil.copy2(SITE / name, OUTPUT / name)
for name, source in MEDIA.items():
    shutil.copy2(source, OUTPUT / "media" / name)

(OUTPUT / ".nojekyll").touch()


class LocalReferenceParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.references = set()

    def handle_starttag(self, tag, attrs):
        for name, value in attrs:
            if name in {"src", "href", "data-image"} and value:
                self.references.add(value.split("#", 1)[0])


for page in ("index.html", "docs.html"):
    parser = LocalReferenceParser()
    parser.feed((OUTPUT / page).read_text(encoding="utf-8"))
    for reference in parser.references:
        if not reference or reference == "./" or "://" in reference or reference.startswith("#"):
            continue
        if not (OUTPUT / reference).is_file():
            raise SystemExit(f"Missing local reference in {page}: {reference}")

print(f"Built {OUTPUT} with {len(PAGES)} site files and {len(MEDIA)} media files.")
