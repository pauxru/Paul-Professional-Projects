"""Build `docs/catalogue.json`: one record per project, merging design and reality.

Two sources, deliberately kept separate:

* the design documents in `docs/` say what each project was *meant* to be;
* the filesystem says what each project *is* -- which languages actually carry the
  weight, how much code there is, whether it exists at all.

Where they disagree, the filesystem wins and the disagreement is printed. A portfolio
that advertises a language it does not contain is worse than one that advertises nothing,
and the only reliable way to avoid that is to never let a human type the number.
"""

from __future__ import annotations

import json
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"
PROJECTS = ROOT / "projects"

TRACK_OF = {
    **{n: "foundation" for n in range(1, 31)},
    **{n: "dotnet-azure-modernization" for n in range(31, 41)},
    **{n: "ai-application-engineering" for n in range(41, 51)},
}

# Extension -> display name. Only languages that carry logic.
LANGS = {
    ".cs": "C#", ".fs": "F#", ".go": "Go", ".rs": "Rust", ".java": "Java",
    ".py": "Python", ".ts": "TypeScript", ".tsx": "TypeScript", ".js": "JavaScript",
    ".cpp": "C++", ".cc": "C++", ".hpp": "C++", ".h": "C/C++ header", ".c": "C",
    ".sql": "SQL", ".ps1": "PowerShell", ".sh": "Shell", ".kt": "Kotlin",
    ".rb": "Ruby", ".cbl": "COBOL", ".cob": "COBOL", ".proto": "Protobuf",
}
SKIP = {
    ".git", "bin", "obj", "node_modules", "target", "__pycache__", ".venv", "venv",
    "build", "dist", ".gradle", ".idea", ".vs", "packages", ".pytest_cache",
    ".mypy_cache", "TestResults", ".next", "coverage", "artifacts", "wwwroot",
}


# ------------------------------------------------------------------ design documents

def parse_foundation(text: str) -> dict[int, dict]:
    """'### NN - Title' followed by a '- **Key:** value' block."""
    out: dict[int, dict] = {}
    chunks = re.split(r"^### (\d{2}) [-\u2014]+ (.+)$", text, flags=re.MULTILINE)
    for i in range(1, len(chunks) - 2, 3):
        num, title, body = int(chunks[i]), chunks[i + 1].strip(), chunks[i + 2]
        fields = dict(
            re.findall(r"^- \*\*([A-Za-z ]+):\*\*\s*(.+)$", body, flags=re.MULTILINE))
        out[num] = {
            "number": num, "title": title,
            "problem": fields.get("Problem", ""),
            "tech": fields.get("Tech", ""),
            "skills": fields.get("Skills", ""),
        }
    return out


def parse_principal(text: str) -> dict[int, dict]:
    """'### NN - Title' followed by bolded prose paragraphs."""
    out: dict[int, dict] = {}
    chunks = re.split(r"^### (\d{2}) [-\u2014]+ (.+)$", text, flags=re.MULTILINE)
    for i in range(1, len(chunks) - 2, 3):
        num, title, body = int(chunks[i]), chunks[i + 1].strip(), chunks[i + 2]

        def para(label: str) -> str:
            m = re.search(r"\*\*" + label + r"\.?\*\*\s*(.*?)(?=\n\*\*|\n---|\Z)",
                          body, flags=re.DOTALL)
            return re.sub(r"\s+", " ", m.group(1)).strip() if m else ""

        out[num] = {
            "number": num, "title": title,
            "story": para("Story"),
            "what": para("What it does"),
            "signal": para("Principal signal"),
        }
    return out


# ------------------------------------------------------------------- the filesystem

def scan(project: pathlib.Path) -> dict:
    counts: dict[str, int] = {}
    lines: dict[str, int] = {}
    n_files = 0
    for p in project.rglob("*"):
        if not p.is_file() or any(part in SKIP for part in p.relative_to(project).parts):
            continue
        n_files += 1
        lang = LANGS.get(p.suffix.lower())
        if lang is None:
            continue
        counts[lang] = counts.get(lang, 0) + 1
        try:
            lines[lang] = lines.get(lang, 0) + sum(
                1 for _ in p.open("r", encoding="utf-8", errors="ignore"))
        except OSError:
            pass

    # The "C/C++ header" bucket exists only so a .h file in a C++ project is not
    # reported as C. Fold it into whichever of the two the project actually is.
    if "C/C++ header" in lines:
        target = "C++" if "C++" in lines else "C"
        lines[target] = lines.get(target, 0) + lines.pop("C/C++ header")
        counts[target] = counts.get(target, 0) + counts.pop("C/C++ header")

    # Rank by lines, not by file count: one 2,000-line engine matters more than nine
    # 20-line shell scripts, and file count says the opposite.
    ranked = sorted(lines.items(), key=lambda kv: -kv[1])
    total = sum(lines.values()) or 1
    return {
        "files": n_files,
        "total_lines": total,
        "lines_by_language": dict(ranked),
        "languages": [name for name, n in ranked if n / total >= 0.06][:4],
        "has_readme": (project / "README.md").exists(),
        "has_test_script": (project / "test.ps1").exists(),
        "adr_count": sum(
            len(list((project / "docs" / d).glob("*.md")))
            for d in ("adr", "adrs", "decisions")
            if (project / "docs" / d).is_dir()),
        "doc_count": len([p for p in project.rglob("*.md")
                          if not any(x in SKIP for x in p.relative_to(project).parts)]),
    }


def main() -> int:
    designs: dict[int, dict] = {}
    for name, parser in (("PORTFOLIO_INDEX.md", parse_foundation),
                         ("PROJECT_DESIGNS_31-50.md", parse_principal)):
        path = DOCS / name
        if not path.exists():
            print(f"! missing design document {path}")
            continue
        designs.update(parser(path.read_text(encoding="utf-8")))

    on_disk: dict[int, pathlib.Path] = {}
    if PROJECTS.is_dir():
        for track_dir in PROJECTS.iterdir():
            for d in track_dir.iterdir() if track_dir.is_dir() else []:
                m = re.match(r"^(\d{2})-", d.name)
                if d.is_dir() and m:
                    on_disk[int(m.group(1))] = d

    records, problems = [], []
    for n in range(1, 51):
        rec = dict(designs.get(n) or {"number": n, "title": f"Project {n:02d}"})
        rec["track"] = TRACK_OF[n]
        d = on_disk.get(n)
        rec["built"] = d is not None
        rec["slug"] = d.name if d else None
        rec["scan"] = scan(d) if d else None
        if n not in designs:
            problems.append(f"{n:02d}: no design document entry")
        if d is None:
            problems.append(f"{n:02d}: not built")
        records.append(rec)

    out = DOCS / "catalogue.json"
    out.write_text(json.dumps(records, indent=2), encoding="utf-8")

    built = [r for r in records if r["scan"]]
    print(f"wrote {out.relative_to(ROOT)}: {len(records)} projects, {len(built)} built")
    print(f"  {sum(r['scan']['total_lines'] for r in built):,} lines, "
          f"{sum(r['scan']['files'] for r in built):,} files")
    for p in problems:
        print("  !", p)
    return 0


if __name__ == "__main__":
    sys.exit(main())
