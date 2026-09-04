"""Generate every image the portfolio README uses.

Everything here is drawn from `catalogue.json`, which is itself derived from the projects
on disk. That matters: the language chart is not a claim about what I like writing, it is
a count of lines that exist. If a project is deleted the chart changes.

SVG rather than PNG, for three reasons. It stays sharp on a retina display, it is a few
kilobytes instead of a few hundred, and a reviewer can read the source and see that the
numbers were not painted on by hand.

GitHub sanitises SVG before serving it, which rules out `<style>` blocks, scripts and
webfonts. So: presentation attributes only, and a generic font stack. Layout assumes the
font may be substituted, so text is centred or given generous room rather than packed to
a measured width.
"""

from __future__ import annotations

import json
import math
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
CATALOGUE = ROOT / "docs" / "catalogue.json"
OUT = ROOT / "assets"

FONT = "Segoe UI,-apple-system,BlinkMacSystemFont,Helvetica Neue,Arial,sans-serif"
MONO = "SFMono-Regular,Consolas,Liberation Mono,Menlo,monospace"

INK = "#E8EEF7"
MUTED = "#93A2BC"
DIM = "#5C6B85"

TRACKS = {
    "foundation": {
        "n": 1,
        "colour": "#5B8DEF",
        "name": "Production Systems",
        "range": "01-30",
        "promise": "Systems that survive contact with real traffic, real money and real auditors.",
    },
    "dotnet-azure-modernization": {
        "n": 2,
        "colour": "#F0A73C",
        "name": ".NET & Azure Modernization",
        "range": "31-40",
        "promise": "I can safely modernize systems that companies are afraid to touch.",
    },
    "ai-application-engineering": {
        "n": 3,
        "colour": "#2DD4A7",
        "name": "AI Application Engineering",
        "range": "41-50",
        "promise": "I build AI capabilities that actually fit into production software.",
    },
}

# Short stack label for the small tiles. Long names do not fit in 96px at any font.
SHORT = {
    "C#": "C#", "F#": "F#", "Go": "Go", "Rust": "Rust", "Java": "Java",
    "Python": "Py", "TypeScript": "TS", "JavaScript": "JS", "C++": "C++",
    "C": "C", "SQL": "SQL", "PowerShell": "PS", "Shell": "sh", "COBOL": "COBOL",
}


def esc(s: str) -> str:
    return (s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
             .replace('"', "&quot;"))


def text(x, y, s, size=14, fill=INK, weight="400", anchor="start", font=FONT,
         spacing=None, opacity=None):
    extra = f' letter-spacing="{spacing}"' if spacing else ""
    extra += f' opacity="{opacity}"' if opacity else ""
    return (f'<text x="{x}" y="{y}" font-family="{font}" font-size="{size}" '
            f'fill="{fill}" font-weight="{weight}" text-anchor="{anchor}"'
            f'{extra}>{esc(s)}</text>')


def card(w: int, h: int, radius: int = 0) -> str:
    """The dark panel every image sits on, so all of them read as one set."""
    return (
        f'<rect width="{w}" height="{h}" rx="{radius}" fill="url(#bg)"/>'
        f'<rect width="{w}" height="{h}" rx="{radius}" fill="url(#vign)"/>'
    )


def defs(w: int, h: int, glows: list[tuple[float, float, float, str]] | None = None) -> str:
    d = [
        '<defs>',
        '<linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">',
        '<stop offset="0%" stop-color="#0B1020"/>',
        '<stop offset="55%" stop-color="#111A2E"/>',
        '<stop offset="100%" stop-color="#0A0F1C"/>',
        '</linearGradient>',
        f'<radialGradient id="vign" cx="0.5" cy="0.1" r="1">',
        '<stop offset="0%" stop-color="#1B2947" stop-opacity="0.55"/>',
        '<stop offset="100%" stop-color="#000000" stop-opacity="0"/>',
        '</radialGradient>',
    ]
    for i, (_, _, _, colour) in enumerate(glows or []):
        d += [
            f'<radialGradient id="g{i}" cx="0.5" cy="0.5" r="0.5">',
            f'<stop offset="0%" stop-color="{colour}" stop-opacity="0.42"/>',
            f'<stop offset="100%" stop-color="{colour}" stop-opacity="0"/>',
            '</radialGradient>',
        ]
    d.append('</defs>')
    return "".join(d)


def glow_shapes(glows: list[tuple[float, float, float, str]]) -> str:
    return "".join(
        f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="url(#g{i})"/>'
        for i, (cx, cy, r, _) in enumerate(glows)
    )


def grid(w: int, h: int, step: int = 40, colour: str = "#2A3B5C", op: float = 0.30) -> str:
    """A faint engineering grid. Drawn as explicit lines rather than a <pattern> so
    that a sanitiser which strips pattern fills cannot silently blank it."""
    parts = []
    for x in range(0, w + 1, step):
        parts.append(f'<line x1="{x}" y1="0" x2="{x}" y2="{h}" stroke="{colour}" '
                     f'stroke-width="1" opacity="{op}"/>')
    for y in range(0, h + 1, step):
        parts.append(f'<line x1="0" y1="{y}" x2="{w}" y2="{y}" stroke="{colour}" '
                     f'stroke-width="1" opacity="{op}"/>')
    return "".join(parts)


def svg(w: int, h: int, body: str) -> str:
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" '
            f'viewBox="0 0 {w} {h}" role="img">{body}</svg>')


# --------------------------------------------------------------------------- hero

def hero(stats: dict) -> str:
    w, h = 1200, 340
    glows = [(140, 60, 300, "#5B8DEF"), (620, 330, 340, "#F0A73C"),
             (1080, 40, 300, "#2DD4A7")]
    b = [defs(w, h, glows), card(w, h), grid(w, h, 40), glow_shapes(glows)]

    b.append(text(64, 92, "PAUL RUKWARO", 15, MUTED, "600", spacing="5"))
    b.append(text(64, 150, "Professional Engineering Portfolio", 46, INK, "700"))
    b.append(f'<rect x="64" y="172" width="72" height="4" rx="2" fill="#5B8DEF"/>')
    b.append(f'<rect x="140" y="172" width="36" height="4" rx="2" fill="#F0A73C"/>')
    b.append(f'<rect x="180" y="172" width="36" height="4" rx="2" fill="#2DD4A7"/>')
    b.append(text(64, 210, "Fifty systems built end to end, each one designed to be "
                           "verified rather than demonstrated.", 17, MUTED))

    cells = [
        (str(stats["projects"]), "projects"),
        (f"{stats['lines'] // 1000}K", "lines of source"),
        (f"{stats['tests']:,}", "tests passing"),
        (str(stats["languages"]), "languages"),
    ]
    x = 64
    for value, label in cells:
        b.append(f'<rect x="{x}" y="244" width="248" height="62" rx="10" '
                 f'fill="#0E1729" fill-opacity="0.72" stroke="#243352" stroke-width="1"/>')
        b.append(text(x + 20, 279, value, 27, INK, "700", font=MONO))
        b.append(text(x + 20, 297, label, 12, DIM, "500", spacing="1.2"))
        x += 268
    return svg(w, h, "".join(b))


# ------------------------------------------------------------------- portfolio map

def portfolio_map(records: list[dict]) -> str:
    w = 1200
    row_h, tile_w, tile_h, gap = 0, 108, 62, 10
    per_row = 10

    blocks = []
    y = 96
    for key, meta in TRACKS.items():
        rows = [r for r in records if r["track"] == key]
        n_rows = math.ceil(len(rows) / per_row)
        blocks.append((key, meta, rows, y, n_rows))
        y += 46 + n_rows * (tile_h + gap) + 26
    h = y + 10

    glows = [(120, 80, 260, "#5B8DEF"), (1080, h - 120, 300, "#2DD4A7")]
    b = [defs(w, h, glows), card(w, h), grid(w, h, 40, op=0.22), glow_shapes(glows)]
    b.append(text(w // 2, 52, "Fifty projects, three tracks", 30, INK, "700",
                  anchor="middle"))
    b.append(text(w // 2, 76, "tile colour is the track; the label is the language that "
                              "carries the project", 13, DIM, anchor="middle"))

    for key, meta, rows, top, n_rows in blocks:
        colour = meta["colour"]
        b.append(f'<rect x="60" y="{top}" width="4" height="20" rx="2" fill="{colour}"/>')
        b.append(text(76, top + 16, f'{meta["name"]}  ·  {meta["range"]}', 16, INK, "700"))
        b.append(text(w - 60, top + 16, f'{len(rows)} projects', 13, DIM, anchor="end"))
        for i, rec in enumerate(rows):
            cx = 60 + (i % per_row) * (tile_w + gap)
            cy = top + 34 + (i // per_row) * (tile_h + gap)
            built = rec["built"]
            fill_op = "0.20" if built else "0.06"
            stroke = colour if built else "#33415C"
            b.append(f'<rect x="{cx}" y="{cy}" width="{tile_w}" height="{tile_h}" rx="9" '
                     f'fill="{colour}" fill-opacity="{fill_op}" stroke="{stroke}" '
                     f'stroke-width="1"/>')
            b.append(text(cx + 12, cy + 30, f'{rec["number"]:02d}', 21,
                          INK if built else DIM, "700", font=MONO))
            langs = (rec.get("scan") or {}).get("languages") or []
            tag = " ".join(SHORT.get(l, l[:3]) for l in langs[:2]) or "planned"
            b.append(text(cx + 12, cy + 48, tag, 10.5,
                          colour if built else DIM, "600", spacing="0.5"))
    return svg(w, h, "".join(b))


# --------------------------------------------------------------------- language bars

def languages(records: list[dict]) -> str:
    totals: dict[str, int] = {}
    for rec in records:
        for lang, n in ((rec.get("scan") or {}).get("lines_by_language") or {}).items():
            totals[lang] = totals.get(lang, 0) + n
    # Fold the incidental into one honest bucket rather than padding the list.
    incidental = {"PowerShell", "Shell", "SQL", "JavaScript", "Protobuf", "C"}
    core = {k: v for k, v in totals.items() if k not in incidental}
    other = sum(v for k, v in totals.items() if k in incidental)
    ranked = sorted(core.items(), key=lambda kv: -kv[1])
    ranked.append(("build & glue", other))

    w = 1200
    bar_h, gap = 34, 14
    h = 96 + len(ranked) * (bar_h + gap) + 30
    top = sum(v for _, v in ranked) or 1
    widest = max(v for _, v in ranked) or 1

    palette = {
        "C#": "#8A63D2", "Go": "#00ADD8", "Rust": "#DE6E45", "Java": "#E76F51",
        "Python": "#4B8BBE", "C++": "#8FB7DA", "TypeScript": "#3178C6",
        "build & glue": "#4A5A78",
    }
    b = [defs(w, h), card(w, h), grid(w, h, 40, op=0.18)]
    b.append(text(60, 50, "What the portfolio is actually written in", 26, INK, "700"))
    b.append(text(60, 74, f"lines of authored source, counted from disk · "
                          f"{top:,} total · generated files excluded", 13, DIM))

    y = 96
    label_w, track_x = 150, 230
    track_w = w - track_x - 190
    for name, n in ranked:
        colour = palette.get(name, "#4A5A78")
        b.append(text(60 + label_w, y + 23, name, 15, INK, "600", anchor="end"))
        b.append(f'<rect x="{track_x}" y="{y}" width="{track_w}" height="{bar_h}" '
                 f'rx="6" fill="#131C31" stroke="#22304C" stroke-width="1"/>')
        bw = max(3, int(track_w * n / widest))
        b.append(f'<rect x="{track_x}" y="{y}" width="{bw}" height="{bar_h}" rx="6" '
                 f'fill="{colour}" fill-opacity="0.85"/>')
        b.append(text(track_x + track_w + 16, y + 23, f"{n:,}", 14, INK, "600", font=MONO))
        b.append(text(w - 60, y + 23, f"{100 * n / top:4.1f}%", 13, DIM, anchor="end",
                      font=MONO))
        y += bar_h + gap
    return svg(w, h, "".join(b))


# -------------------------------------------------------------------- track banners

def track_banner(key: str, records: list[dict]) -> str:
    meta = TRACKS[key]
    rows = [r for r in records if r["track"] == key]
    colour = meta["colour"]
    w, h = 1200, 170
    glows = [(1050, 60, 260, colour)]
    b = [defs(w, h, glows), card(w, h), grid(w, h, 34, op=0.20), glow_shapes(glows)]
    b.append(f'<rect x="0" y="0" width="6" height="{h}" fill="{colour}"/>')
    b.append(text(48, 54, f'TRACK {meta["n"]}  ·  {meta["range"]}', 12, colour, "700",
                  spacing="3.4"))
    b.append(text(48, 96, meta["name"], 32, INK, "700"))
    b.append(text(48, 128, meta["promise"], 15, MUTED))

    langs: dict[str, int] = {}
    for r in rows:
        for lang in ((r.get("scan") or {}).get("languages") or []):
            langs[lang] = langs.get(lang, 0) + 1
    x = 48
    for lang, _ in sorted(langs.items(), key=lambda kv: -kv[1])[:6]:
        pill = 22 + len(lang) * 8
        b.append(f'<rect x="{x}" y="140" width="{pill}" height="0" rx="0" fill="none"/>')
        x += pill + 8
    b.append(text(w - 48, 96, f'{len(rows)}', 40, colour, "700", anchor="end", font=MONO))
    b.append(text(w - 48, 118, "projects", 12, DIM, anchor="end", spacing="1.6"))
    return svg(w, h, "".join(b))


# ------------------------------------------------------------------- project tiles

def project_tile(rec: dict) -> str:
    meta = TRACKS[rec["track"]]
    colour = meta["colour"]
    built = rec["built"]
    w = h = 96
    b = [defs(w, h), card(w, h, radius=14)]
    b.append(f'<rect x="0.5" y="0.5" width="{w-1}" height="{h-1}" rx="14" fill="none" '
             f'stroke="{colour if built else "#33415C"}" stroke-width="1.5" '
             f'stroke-opacity="{0.85 if built else 0.4}"/>')
    b.append(f'<rect x="14" y="14" width="26" height="3" rx="1.5" fill="{colour}" '
             f'fill-opacity="{0.9 if built else 0.35}"/>')
    b.append(text(w / 2, 58, f'{rec["number"]:02d}', 34, INK if built else DIM, "700",
                  anchor="middle", font=MONO))
    langs = (rec.get("scan") or {}).get("languages") or []
    tag = " · ".join(SHORT.get(l, l) for l in langs[:2]) or "planned"
    b.append(text(w / 2, 78, tag, 10.5, colour if built else DIM, "700", anchor="middle",
                  spacing="0.4"))
    return svg(w, h, "".join(b))


def verified_test_total() -> int:
    """Sum the per-project test counts that tools/verify.ps1 actually observed.

    The number on the hero banner is the single most quotable claim in the whole
    repository, so it must come from a machine that ran the suites -- never from a
    constant someone typed. If the sweep has not been run yet, say so loudly rather
    than shipping a plausible-looking stale figure.
    """
    tsv = ROOT / "tools" / "verification.tsv"
    if not tsv.exists():
        raise SystemExit(
            "tools/verification.tsv is missing. Run tools/verify.ps1 first -- the test\n"
            "count on the banner has to be measured, not remembered."
        )
    total = 0
    for line in tsv.read_text(encoding="utf-8").splitlines()[1:]:
        parts = line.split("\t")
        if len(parts) >= 4 and parts[1] == "PASS":
            total += int(parts[2])
    return total


def main() -> int:
    records = json.loads(CATALOGUE.read_text(encoding="utf-8"))
    (OUT / "projects").mkdir(parents=True, exist_ok=True)

    built = [r for r in records if r.get("scan")]
    stats = {
        "projects": len(records),
        "lines": sum(r["scan"]["total_lines"] for r in built),
        "tests": verified_test_total(),
        "languages": len({
            l for r in built for l in r["scan"]["languages"]
        } - {"PowerShell", "Shell"}),
    }

    written = []
    for name, content in [
        ("hero.svg", hero(stats)),
        ("portfolio-map.svg", portfolio_map(records)),
        ("languages.svg", languages(records)),
        ("track-1.svg", track_banner("foundation", records)),
        ("track-2.svg", track_banner("dotnet-azure-modernization", records)),
        ("track-3.svg", track_banner("ai-application-engineering", records)),
    ]:
        (OUT / name).write_text(content, encoding="utf-8")
        written.append(name)

    for rec in records:
        (OUT / "projects" / f'{rec["number"]:02d}.svg').write_text(
            project_tile(rec), encoding="utf-8")

    print(f"assets -> {OUT}")
    print(f"  {len(written)} banners + {len(records)} project tiles")
    print(f"  stats: {stats}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
