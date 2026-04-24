"""
Build-Skript: erzeugt aus docs/handbuch/*.md eine einzige HTML-WebResource.

Output:  solution/src/WebResources/jbe_/handbuch.html
Aufruf:  python solution/scripts/build-handbuch.py

Das Skript:
 1. sammelt alle .md-Dateien unter docs/handbuch/ in lesbarer Reihenfolge
    (README zuerst, dann 01-einführung/*, 02-testfall-schreiben/* usw.)
 2. gibt jedem H1-Heading eine explizite ID fuer interne Navigation
 3. rewritet alle [text](../kap/datei.md)-Links auf #<slug>-Anker
 4. ruft pandoc auf mit inline-CSS fuer saubere Darstellung
 5. schreibt das HTML in solution/src/WebResources/jbe_/handbuch.html

Voraussetzung: pandoc 3.x im PATH.
"""
import os
import re
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
HANDBUCH_DIR = REPO_ROOT / "docs" / "handbuch"
OUTPUT = REPO_ROOT / "solution" / "src" / "WebResources" / "jbe_" / "handbuch.html"

# Inline-CSS fuer die HTML-Ausgabe (minimalistisch, gut lesbar, D365-naeher Look)
CSS = r"""
body {
    font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
    max-width: 1000px;
    margin: 0 auto;
    padding: 2em 2em 6em 2em;
    line-height: 1.55;
    color: #252423;
    background: #ffffff;
}
h1 { border-bottom: 2px solid #0078d4; padding-bottom: .3em; margin-top: 2.5em; color: #0078d4; }
h2 { border-bottom: 1px solid #e1dfdd; padding-bottom: .2em; margin-top: 2em; color: #004578; }
h3 { color: #323130; margin-top: 1.5em; }
h4 { color: #323130; }
a { color: #0078d4; text-decoration: none; }
a:hover { text-decoration: underline; }
code {
    background: #f3f2f1;
    padding: 0.15em 0.4em;
    border-radius: 3px;
    font-family: Consolas, "Courier New", monospace;
    font-size: 0.9em;
}
pre {
    background: #f3f2f1;
    border: 1px solid #e1dfdd;
    border-left: 4px solid #0078d4;
    padding: 1em;
    overflow-x: auto;
    border-radius: 4px;
}
pre code { background: none; padding: 0; font-size: 0.88em; }
table {
    border-collapse: collapse;
    margin: 1em 0;
    width: 100%;
}
th, td {
    border: 1px solid #e1dfdd;
    padding: 0.5em 0.8em;
    text-align: left;
    vertical-align: top;
}
th { background: #faf9f8; font-weight: 600; }
tr:nth-child(even) { background: #faf9f8; }
blockquote {
    border-left: 4px solid #ffb900;
    background: #fff4ce;
    padding: 0.5em 1em;
    margin: 1em 0;
    border-radius: 4px;
}
hr { border: none; border-top: 2px solid #e1dfdd; margin: 3em 0; }
#TOC {
    background: #faf9f8;
    border: 1px solid #e1dfdd;
    padding: 1em 2em 1em 2em;
    margin-bottom: 2em;
    border-radius: 4px;
}
#TOC::before {
    content: "Inhaltsverzeichnis";
    display: block;
    font-weight: 600;
    font-size: 1.15em;
    margin-bottom: 0.5em;
    color: #0078d4;
}
#TOC ul { padding-left: 1.2em; }
#TOC > ul { padding-left: 0; list-style: none; }
#TOC > ul > li { margin-top: 0.3em; }
.title { color: #0078d4; font-size: 2em; margin-bottom: 0.5em; }
"""


def file_slug(path: Path) -> str:
    """Stabiler Slug aus dem Datei-Pfad, nicht aus dem H1."""
    rel = path.relative_to(HANDBUCH_DIR)
    parts = list(rel.parent.parts) + [rel.stem]
    s = "-".join(parts)
    s = s.lower().replace("_", "-").replace(" ", "-")
    # Umlaute für saubere Anker ascii-transliterieren
    s = (s.replace("ä", "ae").replace("ö", "oe").replace("ü", "ue")
           .replace("ß", "ss"))
    s = re.sub(r"[^a-z0-9-]", "-", s)
    s = re.sub(r"-+", "-", s).strip("-")
    return "kap-" + s


def gather_mds():
    files = []
    readme = HANDBUCH_DIR / "README.md"
    if readme.exists():
        files.append(readme)
    for subdir in sorted(HANDBUCH_DIR.iterdir()):
        if subdir.is_dir():
            for md in sorted(subdir.iterdir()):
                if md.suffix == ".md":
                    files.append(md)
    return files


def inject_h1_id(content: str, slug: str) -> str:
    """Dem ersten H1 eine explizite ID verpassen."""
    def repl(m):
        return f"{m.group(0).rstrip()} {{#{slug}}}"
    return re.sub(r"^#\s+.+$", repl, content, count=1, flags=re.MULTILINE)


def rewrite_links(content: str, current_file: Path, slug_map: dict) -> str:
    """[text](path.md) und [text](path.md#anchor) auf interne Anker umschreiben."""
    def repl(m):
        text = m.group(1)
        link = m.group(2).strip()
        if "#" in link and not link.startswith("#"):
            path_part, anchor_part = link.split("#", 1)
        else:
            path_part, anchor_part = link, None
        if not path_part.endswith(".md"):
            return m.group(0)
        try:
            target = (current_file.parent / path_part).resolve()
            rel = target.relative_to(HANDBUCH_DIR.resolve())
        except (ValueError, OSError):
            return m.group(0)
        key = str(rel).replace("\\", "/")
        slug = slug_map.get(key)
        if not slug:
            return m.group(0)
        if anchor_part:
            return f"[{text}](#{anchor_part})"
        return f"[{text}](#{slug})"
    return re.sub(r"\[([^\]]+)\]\(([^)]+)\)", repl, content)


def build():
    if not HANDBUCH_DIR.exists():
        print(f"FEHLER: {HANDBUCH_DIR} existiert nicht.", file=sys.stderr)
        sys.exit(1)

    files = gather_mds()
    print(f"Gefunden: {len(files)} Markdown-Dateien")
    if not files:
        print("FEHLER: keine .md-Dateien gefunden.", file=sys.stderr)
        sys.exit(1)

    # key -> slug
    slug_map = {}
    for f in files:
        key = str(f.relative_to(HANDBUCH_DIR)).replace("\\", "/")
        slug_map[key] = file_slug(f)

    parts = []
    for f in files:
        content = f.read_text(encoding="utf-8")
        key = str(f.relative_to(HANDBUCH_DIR)).replace("\\", "/")
        slug = slug_map[key]
        content = inject_h1_id(content, slug)
        content = rewrite_links(content, f, slug_map)
        parts.append(content)
        parts.append("\n\n---\n\n")

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    tmp_md = OUTPUT.parent / "_combined.md"
    tmp_css = OUTPUT.parent / "_handbuch.css"
    tmp_md.write_text("\n".join(parts), encoding="utf-8")
    tmp_css.write_text(CSS, encoding="utf-8")

    cmd = [
        "pandoc",
        str(tmp_md),
        "-f", "gfm+attributes+implicit_header_references",
        "-t", "html5",
        "--standalone",
        "--embed-resources",
        "--toc",
        "--toc-depth=2",
        "--metadata", "title=D365 Test Center — Entwickler-Handbuch",
        "--metadata", "lang=de",
        f"--css={tmp_css}",
        "-o", str(OUTPUT),
    ]
    print(f"pandoc: {' '.join(cmd[:3])} ...")
    subprocess.run(cmd, check=True)

    tmp_md.unlink()
    tmp_css.unlink()

    size = OUTPUT.stat().st_size
    print(f"-> {OUTPUT}")
    print(f"   {size} bytes ({size // 1024} KB)")


if __name__ == "__main__":
    build()
