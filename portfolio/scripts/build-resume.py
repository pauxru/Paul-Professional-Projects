from __future__ import annotations

import argparse
import json
import re
from html import escape
from pathlib import Path
import pymupdf

from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, StyleSheet1
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (
    BaseDocTemplate,
    Frame,
    KeepTogether,
    ListFlowable,
    ListItem,
    PageBreak,
    PageTemplate,
    Paragraph,
    Spacer,
    Table,
    TableStyle,
)

PAGE_WIDTH, PAGE_HEIGHT = A4
INK = colors.HexColor("#14231F")
COBALT = colors.HexColor("#2452E8")
MUTED = colors.HexColor("#58645F")
PAPER_RULE = colors.HexColor("#D4DCD4")
LEFT = RIGHT = 16 * mm
TOP = 15 * mm
BOTTOM = 15 * mm


def repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def load_profile(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def register_optional_fonts() -> dict[str, str]:
    candidates = {
        "sans": [
            ("Segoe UI", "segoeui.ttf"),
            ("Arial", "arial.ttf"),
        ],
        "sans_bold": [
            ("Segoe UI Bold", "segoeuib.ttf"),
            ("Arial Bold", "arialbd.ttf"),
        ],
        "serif": [
            ("Georgia", "georgia.ttf"),
            ("Times New Roman", "times.ttf"),
        ],
        "serif_bold": [
            ("Georgia Bold", "georgiab.ttf"),
            ("Times New Roman Bold", "timesbd.ttf"),
        ],
        "mono": [
            ("Consolas", "consola.ttf"),
            ("Courier New", "cour.ttf"),
        ],
    }
    search_roots = [
        Path("C:\\Windows\\Fonts"),
        Path.home() / ".fonts",
        Path("/usr/share/fonts"),
        Path("/Library/Fonts"),
    ]
    fallback = {
        "sans": "Helvetica",
        "sans_bold": "Helvetica-Bold",
        "serif": "Times-Roman",
        "serif_bold": "Times-Bold",
        "mono": "Courier",
    }
    fonts = dict(fallback)
    for key, options in candidates.items():
        for postscript_name, filename in options:
            font_path = next((root / filename for root in search_roots if (root / filename).exists()), None)
            if not font_path:
                continue
            if postscript_name not in pdfmetrics.getRegisteredFontNames():
                pdfmetrics.registerFont(TTFont(postscript_name, str(font_path)))
            fonts[key] = postscript_name
            break
    return fonts


def styles(fonts: dict[str, str]) -> StyleSheet1:
    sheet = StyleSheet1()
    sheet.add(
        ParagraphStyle(
            name="Body",
            fontName=fonts["sans"],
            fontSize=9.8,
            leading=12.6,
            textColor=INK,
            spaceAfter=3,
        )
    )
    sheet.add(
        ParagraphStyle(
            name="BodyMuted",
            parent=sheet["Body"],
            textColor=MUTED,
        )
    )
    sheet.add(
        ParagraphStyle(
            name="Section",
            fontName=fonts["sans_bold"],
            fontSize=10.2,
            leading=12,
            textColor=COBALT,
            spaceBefore=2,
            spaceAfter=5,
        )
    )
    sheet.add(
        ParagraphStyle(
            name="RoleTitle",
            fontName=fonts["sans_bold"],
            fontSize=11.4,
            leading=13.6,
            textColor=INK,
            spaceAfter=1,
        )
    )
    sheet.add(
        ParagraphStyle(
            name="RoleMeta",
            fontName=fonts["sans"],
            fontSize=9.6,
            leading=11.8,
            textColor=MUTED,
            spaceAfter=3,
        )
    )
    sheet.add(
        ParagraphStyle(
            name="Small",
            fontName=fonts["sans"],
            fontSize=8.7,
            leading=10.3,
            textColor=MUTED,
        )
    )
    sheet.add(
        ParagraphStyle(
            name="SmallRight",
            parent=sheet["Small"],
            alignment=TA_LEFT,
        )
    )
    sheet.add(
        ParagraphStyle(
            name="LinkLine",
            fontName=fonts["sans"],
            fontSize=9.4,
            leading=11.4,
            textColor=INK,
            spaceAfter=8,
        )
    )
    sheet.add(
        ParagraphStyle(
            name="Bullet",
            parent=sheet["Body"],
            leftIndent=10,
            firstLineIndent=0,
            bulletIndent=0,
            bulletFontName=fonts["sans_bold"],
            bulletFontSize=9.8,
            spaceAfter=2,
        )
    )
    return sheet


def link(url: str, label: str) -> str:
    return f'<link href="{escape(url, quote=True)}" color="{COBALT}">{escape(label)}</link>'


def role_block(entry: dict, sheet: StyleSheet1, bullets: int) -> KeepTogether:
    items = [
        Paragraph(escape(f"{entry['role']} - {entry['company']}"), sheet["RoleTitle"]),
        Paragraph(escape(f"{entry['context']} | {entry['start']} - {entry['end']}"), sheet["RoleMeta"]),
        Paragraph(escape(entry["summary"]), sheet["Body"]),
    ]
    selected = entry.get("responsibilities", [])[:bullets]
    items.extend(Paragraph(escape(item), sheet["Bullet"], bulletText="•") for item in selected)
    return KeepTogether(items + [Spacer(1, 5)])


def section(title: str, sheet: StyleSheet1) -> Paragraph:
    return Paragraph(title.upper(), sheet["Section"])


def skills_table(profile: dict, sheet: StyleSheet1) -> Table:
    rows = []
    for group in profile.get("skills", []):
        rows.append(
            [
                Paragraph(f"<b>{escape(group['area'])}</b>", sheet["Body"]),
                Paragraph(escape(", ".join(group["items"])), sheet["BodyMuted"]),
            ]
        )
    table = Table(rows, colWidths=[47 * mm, PAGE_WIDTH - LEFT - RIGHT - 47 * mm])
    table.setStyle(
        TableStyle(
            [
                ("VALIGN", (0, 0), (-1, -1), "TOP"),
                ("TEXTCOLOR", (0, 0), (0, -1), COBALT),
                ("LINEBELOW", (0, 0), (-1, -1), 0.4, PAPER_RULE),
                ("TOPPADDING", (0, 0), (-1, -1), 4),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
                ("LEFTPADDING", (0, 0), (-1, -1), 0),
                ("RIGHTPADDING", (0, 0), (-1, -1), 0),
            ]
        )
    )
    return table


def list_block(values: list[str], sheet: StyleSheet1) -> ListFlowable:
    return ListFlowable(
        [ListItem(Paragraph(value, sheet["Body"])) for value in values],
        bulletType="bullet",
        bulletFontName=sheet["Bullet"].fontName,
        bulletFontSize=9.6,
        leftIndent=9,
    )


def portfolio_points(profile: dict) -> list[str]:
    return [
        "Crown Jewels Bridge - controlled Windows C++/.NET interoperability study evaluating boundary contracts, memory ownership and numerical compatibility.",
        "Retrieval Quality Lab - offline, synthetic-corpus evaluation of chunking, lexical/LSA retrieval and ranking, with explicit loss attribution.",
        "Silent-Failure Observability - simulated answer-quality monitoring comparing detection coverage, false positives, delay and modeled cost.",
        f"Repository: {link(profile['repository'], 'github.com/pauxru/Paul-Professional-Projects')}",
    ]


def build_story(profile: dict, sheet: StyleSheet1) -> list:
    experience = profile["experience"]
    story: list = []

    summary = [
        section("Summary", sheet),
        Paragraph(escape(profile["summary"]), sheet["Body"]),
        Paragraph(
            escape(profile["location"]),
            sheet["BodyMuted"],
        ),
        Paragraph(
            f"{link(profile['linkedin'], profile['linkedin'].removeprefix('https://www.'))}  |  {link(profile['github'], profile['github'].removeprefix('https://'))}",
            sheet["LinkLine"],
        ),
        section("Expertise", sheet),
        skills_table(profile, sheet),
        Spacer(1, 8),
        section("Recent experience", sheet),
        role_block(experience[0], sheet, bullets=3),
        role_block(experience[1], sheet, bullets=3),
    ]
    story.extend(summary)
    story.append(PageBreak())

    story.extend(
        [
            section("Earlier experience", sheet),
            role_block(experience[2], sheet, bullets=3),
            role_block(experience[3], sheet, bullets=2),
            section("Technical leadership", sheet),
            list_block([escape(item["description"]) for item in profile["leadership"]], sheet),
            Spacer(1, 7),
            section("Education", sheet),
        ]
    )
    for item in profile["education"]:
        story.append(
            Paragraph(
                f"<b>{escape(item['qualification'])}</b> - {escape(item['institution'])} | {escape(item['dates'])} | {escape(item['status'])}",
                sheet["Body"],
            )
        )
    story.extend(
        [
            Spacer(1, 7),
            section("Selected independent projects", sheet),
            list_block(portfolio_points(profile), sheet),
        ]
    )
    return story


def draw_header_footer(canvas, doc, profile: dict, fonts: dict[str, str]) -> None:
    canvas.saveState()
    canvas.setTitle(f"{profile['name']} Resume")
    canvas.setAuthor(profile["name"])
    canvas.setSubject("Senior Software Engineer resume")
    canvas.setCreator("Paul Rukwaro portfolio")
    canvas.setKeywords("Paul Rukwaro, Senior Software Engineer, Distributed Systems, Databases, Cloud Infrastructure")

    top_y = PAGE_HEIGHT - TOP + 6 * mm
    canvas.setStrokeColor(COBALT)
    canvas.setLineWidth(1)
    canvas.line(LEFT, top_y, PAGE_WIDTH - RIGHT, top_y)

    canvas.setFillColor(INK)
    canvas.setFont(fonts["sans_bold"], 20)
    canvas.drawString(LEFT, PAGE_HEIGHT - TOP + 0.5 * mm, profile["name"])
    canvas.setFont(fonts["sans"], 11)
    canvas.drawString(LEFT, PAGE_HEIGHT - TOP - 5 * mm, profile["title"])
    canvas.setFillColor(MUTED)
    canvas.setFont(fonts["sans"], 9.1)
    canvas.drawString(LEFT, PAGE_HEIGHT - TOP - 10 * mm, profile["specialization"])

    footer_y = BOTTOM - 3 * mm
    canvas.setStrokeColor(PAPER_RULE)
    canvas.setLineWidth(0.6)
    canvas.line(LEFT, footer_y + 4 * mm, PAGE_WIDTH - RIGHT, footer_y + 4 * mm)
    canvas.setFillColor(MUTED)
    canvas.setFont(fonts["sans"], 8.5)
    canvas.drawString(LEFT, footer_y, profile["github"])
    canvas.drawRightString(PAGE_WIDTH - RIGHT, footer_y, f"Page {doc.page}")
    canvas.restoreState()


def build_pdf(profile: dict, output: Path) -> None:
    fonts = register_optional_fonts()
    sheet = styles(fonts)
    output.parent.mkdir(parents=True, exist_ok=True)

    document = BaseDocTemplate(
        str(output),
        pagesize=A4,
        leftMargin=LEFT,
        rightMargin=RIGHT,
        topMargin=TOP + 18 * mm,
        bottomMargin=BOTTOM + 7 * mm,
    )
    frame = Frame(document.leftMargin, document.bottomMargin, document.width, document.height, id="normal")
    document.addPageTemplates(
        [
            PageTemplate(
                id="resume",
                frames=[frame],
                onPage=lambda canvas, doc: draw_header_footer(canvas, doc, profile, fonts),
            )
        ]
    )
    document.build(build_story(profile, sheet))


def validate_pdf(output: Path) -> None:
    forbidden_phrases = [
        "application target",
        "job application",
        "endorsement",
        "job availability",
        "direct report",
        "budget",
        "team size",
    ]
    with pymupdf.open(output) as document:
        if document.page_count > 2:
            raise RuntimeError(f"Resume exceeds the 2-page limit ({document.page_count} pages).")
        metadata_text = " ".join(str(value) for value in document.metadata.values() if value)
        if "\\" in metadata_text:
            raise RuntimeError("PDF metadata appears to contain a local file path.")
        all_text = "\n".join(page.get_text("text") for page in document)
        lowered = all_text.lower()
        for phrase in forbidden_phrases:
            if phrase in lowered:
                raise RuntimeError(f"Forbidden phrase detected in resume text: {phrase}")
        if re.search(r"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", all_text, re.IGNORECASE):
            raise RuntimeError("Public resume must not include an email address.")
        for page in document:
            rect = page.rect
            for x0, y0, x1, y1, *_ in page.get_text("words"):
                if x0 < rect.x0 - 1 or y0 < rect.y0 - 1 or x1 > rect.x1 + 1 or y1 > rect.y1 + 1:
                    raise RuntimeError(f"Text bounds exceed the page frame on page {page.number + 1}.")
            if page.number == 0 and len(page.get_links()) < 2:
                raise RuntimeError("Expected clickable public links were not found in the PDF.")


def parse_args() -> argparse.Namespace:
    root = repo_root()
    parser = argparse.ArgumentParser(description="Build the public portfolio resume PDF.")
    parser.add_argument(
        "--output",
        default=str(root / "public" / "resume" / "Paul-Rukwaro-Resume.pdf"),
        help="Output PDF path. Defaults to public/resume/Paul-Rukwaro-Resume.pdf.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    root = repo_root()
    profile = load_profile(root / "src" / "data" / "profile.json")
    output = Path(args.output).expanduser()
    if not output.is_absolute():
        output = (root / output).resolve()

    build_pdf(profile, output)
    validate_pdf(output)

    print(f"Resume created: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
