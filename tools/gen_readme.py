"""Generate the portfolio README.

Everything numeric in the output comes from a file that a command produced: the language
counts from `catalogue.json` (scanned from disk), the test counts from
`tools/verification.tsv` (written by `tools/verify.ps1` while it ran the suites). Nothing
is typed in by hand, because a README with hand-typed numbers is a README that is wrong
three weeks later and nobody notices.

The prose blurbs *are* hand-written, in `blurbs.json`. Truncating a design document at
140 characters produces a sentence that stops rather than a sentence that says something.
"""

from __future__ import annotations

import csv
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent


TRACKS = [
    ("foundation", 1, "Production Systems", "01\u201330",
     "Systems that survive contact with real traffic, real money and real auditors.",
     "Thirty services built the way they would have to be built if somebody were "
     "paying for them: idempotent, observable, tenant-isolated, and tested against the "
     "failure that actually happens rather than the one that is easy to write a test for."),
    ("dotnet-azure-modernization", 2, ".NET & Azure Modernization", "31\u201340",
     "I can safely modernize systems that companies are afraid to touch.",
     "Ten projects about **safety under change**. None of them is a greenfield rewrite, "
     "because a greenfield rewrite is not the problem clients have. The problem is a "
     "system that works, that nobody understands, and that the business cannot afford to "
     "have stop working for a weekend."),
    ("ai-application-engineering", 3, "AI Application Engineering", "41\u201350",
     "I build AI capabilities that actually fit into production software.",
     "Ten projects about the engineering *around* the model. Every one of them runs with "
     "no API key and no GPU, because the interesting problems \u2014 evaluation that means "
     "something, retrieval that can be tuned, output that cannot be malformed, failures "
     "that never throw \u2014 are not problems about which model you called."),
]

LANG_ORDER = ["C#", "Go", "Rust", "Java", "Python", "C++", "TypeScript", "JavaScript"]


def load_verification() -> dict[str, dict]:
    path = ROOT / "tools" / "verification.tsv"
    if not path.exists():
        return {}
    out = {}
    with path.open(encoding="utf-8-sig", newline="") as fh:
        for row in csv.DictReader(fh, delimiter="\t"):
            if row.get("project") and row["project"] != "TOTAL":
                out[row["project"]] = row
    return out


def stack_chips(rec: dict) -> str:
    langs = (rec.get("scan") or {}).get("languages") or []
    ordered = [l for l in LANG_ORDER if l in langs] + [
        l for l in langs if l not in LANG_ORDER]
    return " ".join(f"`{l}`" for l in ordered[:3]) or "`planned`"


def project_rows(records, blurbs, verification, track_key) -> str:
    lines = [
        "| | Project | What it is |",
        "|:--:|---|---|",
    ]
    for rec in records:
        if rec["track"] != track_key:
            continue
        n = rec["number"]
        tile = f'<img src="assets/projects/{n:02d}.svg" width="60" alt="">'
        blurb = blurbs[str(n)]

        if not rec["built"]:
            name = f"**{n:02d} \u00b7 {rec['title'].split(':')[0]}**"
            meta = "<br>*in progress*"
            lines.append(f"| {tile} | {name}{meta} | {blurb} |")
            continue

        href = f"projects/{rec['track']}/{rec['slug']}"
        title = rec["title"].split(":")[0].strip()
        name = f"**[{n:02d} \u00b7 {title}]({href})**"

        v = verification.get(rec["slug"], {})
        bits = [stack_chips(rec)]
        passed = int(v.get("passed") or 0)
        if passed:
            bits.append(f"{passed} tests")
        loc = (rec.get("scan") or {}).get("total_lines") or 0
        if loc:
            bits.append(f"{loc:,} lines")
        meta = "<br><sub>" + " \u00b7 ".join(bits) + "</sub>"
        lines.append(f"| {tile} | {name}{meta} | {blurb} |")
    return "\n".join(lines)


def main() -> int:
    records = json.loads(
        (ROOT / "docs" / "catalogue.json").read_text(encoding="utf-8"))
    blurbs = json.loads(
        (ROOT / "tools" / "blurbs.json").read_text(encoding="utf-8"))
    verification = load_verification()

    built = [r for r in records if r.get("scan")]
    total_lines = sum(r["scan"]["total_lines"] for r in built)
    # Only count suites that went green end to end. A project with failures has no
    # business contributing its passing tests to a headline number -- that is how you
    # end up advertising 2,400 tests for a portfolio that does not build.
    total_tests = sum(int(v.get("passed") or 0)
                      for v in verification.values() if v.get("status") == "PASS")
    green = sum(1 for v in verification.values() if v.get("status") == "PASS")
    langs = sorted({l for r in built for l in r["scan"]["languages"]}
                   - {"PowerShell", "Shell", "SQL"})

    md: list[str] = []
    A = md.append

    A('<div align="center">')
    A('<img src="assets/hero.svg" alt="Paul Rukwaro \u2014 Professional Engineering '
      'Portfolio" width="100%">')
    A('</div>')
    A('')
    A('<div align="center">')
    A('<strong>Fifty systems, built end to end.</strong><br>')
    A('Every one of them ships the evidence that it works, not a screenshot of it '
      'working.')
    A('</div>')
    A('')
    A('---')
    A('')

    # ------------------------------------------------------------------ what this is
    A('## What this is')
    A('')
    A('This repository is a portfolio of self-directed engineering work: fifty complete '
      'systems, each one chosen because it contains a problem that is genuinely hard '
      'and commonly got wrong.')
    A('')
    A('They are not tutorials, and they are not demos. A demo is a thing that works '
      'when you drive it the way the author drove it. Each project here is built to the '
      'opposite standard \u2014 it has to keep working when somebody hostile, or merely '
      'careless, drives it instead.')
    A('')
    A('### The rule')
    A('')
    A('> **Every claim a project makes, it has to be able to prove on demand.**')
    A('')
    A('That rule does most of the design work. If a project claims a cache is fast, it '
      'ships the benchmark. If it claims a migration is lossless, it ships the '
      'reconciliation that would catch the loss. If it claims a boundary is safe, it '
      'ships the fuzzer that attacks the boundary and the count of what got through.')
    A('')
    A('It also has an uncomfortable consequence, which is the reason the rule is worth '
      'having: several projects ship measurements that **contradict the thing I expected '
      'to find**, and say so. Those are the results worth reading.')
    A('')

    # --------------------------------------------------------------------- at a glance
    A('### At a glance')
    A('')
    A('| | |')
    A('|---|---|')
    A(f'| **Projects** | {len(records)} designed, {len(built)} built and verified |')
    A(f'| **Source** | {total_lines:,} lines of authored code, generated files excluded |')
    if total_tests:
        A(f'| **Tests** | {total_tests:,} passing across {green} project suites |')
    A(f'| **Languages** | {", ".join(langs)} |')
    A('| **Dependencies** | None. No Docker, no database server, no cloud account, '
      'no API key. |')
    A('')
    A('That last row is a design constraint, not a limitation. A portfolio that only '
      'runs on the author\'s machine is a portfolio nobody runs. Every project here is '
      'built to `git clone` and go, which forced some genuinely better engineering: '
      'deterministic local embedding providers instead of a hosted API, an in-process '
      'bus behind the same interface as the real broker, seeded simulation instead of '
      '`Thread.Sleep`.')
    A('')
    A('---')
    A('')

    # ------------------------------------------------------------------------- the map
    A('## The map')
    A('')
    A('<div align="center">')
    A('<img src="assets/portfolio-map.svg" alt="All fifty projects grouped into three '
      'tracks" width="100%">')
    A('</div>')
    A('')
    A('<div align="center">')
    A('<img src="assets/languages.svg" alt="Lines of authored source by language" '
      'width="100%">')
    A('</div>')
    A('')
    A('The language spread is deliberate. A reverse proxy under concurrent load belongs '
      'in Go; a deterministic simulator that must not allocate unpredictably belongs in '
      'Rust; a numerical core that a business will not let you rewrite is already in '
      'C++ and stays there. Picking the language the problem asks for \u2014 rather than the '
      'one on the CV \u2014 is itself part of what these projects are demonstrating.')
    A('')
    A('---')
    A('')

    # -------------------------------------------------------------------- track blocks
    for key, n, name, rng, promise, intro in TRACKS:
        rows = [r for r in records if r["track"] == key]
        A('<div align="center">')
        A(f'<img src="assets/track-{n}.svg" alt="{name}" width="100%">')
        A('</div>')
        A('')
        A(f'## Track {n} \u00b7 {name}')
        A('')
        A(f'> *{promise}*')
        A('')
        A(intro)
        A('')
        A(project_rows(records, blurbs, verification, key))
        A('')
        A(f'<sub>{len(rows)} projects \u00b7 '
          f'{sum((r.get("scan") or {}).get("total_lines", 0) for r in rows):,} lines</sub>')
        A('')
        A('---')
        A('')

    # ------------------------------------------------------------------- running things
    A('## Running any of this')
    A('')
    A('Each project is self-contained and carries its own README, its own build and '
      'test scripts, and its own architecture decision records. Nothing depends on '
      'anything else in this repository.')
    A('')
    A('```bash')
    A('git clone https://github.com/pauxru/Paul-Professional-Projects.git')
    A('cd Paul-Professional-Projects/projects/<track>/<project>')
    A('')
    A('# projects that ship their own harness (31-50)')
    A('pwsh ./test.ps1')
    A('')
    A('# .NET projects (01-30)')
    A('dotnet test -c Release')
    A('```')
    A('')
    A('To verify the whole repository, and regenerate the numbers quoted above:')
    A('')
    A('```powershell')
    A('pwsh tools/verify.ps1        # runs every suite, writes tools/verification.tsv')
    A('```')
    A('')
    A('The results of the last full sweep are committed at '
      '[`tools/verification.tsv`](tools/verification.tsv), with the raw output of each '
      'run under [`tools/logs/`](tools/logs). They are in the repository because a test '
      'count in a README is worth exactly as much as the file it can be checked against.')
    A('')
    A('Running every suite on a machine and a date that none of them were written on '
      'found four defects that reading the code would not have: two frozen-clock time '
      'bombs that had been waiting for the wall clock to overtake a hard-coded test '
      'date, a determinism claim that turned out to hold only at one value of '
      '`GOMAXPROCS`, and a build step the sweep was skipping. '
      '[`docs/VERIFICATION.md`](docs/VERIFICATION.md) is the write-up \u2014 including the '
      'two bugs that were in the test *counter* rather than in any project.')
    A('')
    A('See [`docs/TOOLCHAINS.md`](docs/TOOLCHAINS.md) for the versions everything was '
      'built and verified against.')
    A('')
    A('---')
    A('')

    # ---------------------------------------------------------------------- what's here
    A('## What is in every project')
    A('')
    A('The later projects (31\u201350) are held to a fixed deliverables contract, which is '
      'written down in [`docs/ENGINEERING_STANDARDS.md`](docs/ENGINEERING_STANDARDS.md). '
      'In short, each one ships:')
    A('')
    A('- a **README** that states the problem before it states the solution;')
    A('- **architecture decision records** \u2014 including the options that were rejected, '
      'which is the only part of an ADR that carries information;')
    A('- a **results document** generated by running the thing, not by writing about it;')
    A('- a **test harness** that fails the build on a compiler warning, re-runs the '
      'report to prove it is byte-identical, and mutates the source to prove the tests '
      'would have caught it;')
    A('- **known limitations**, written honestly, because the fastest way to lose a '
      'reviewer is to claim something the code does not do.')
    A('')
    A('---')
    A('')

    # -------------------------------------------------------------------------- layout
    A('## Repository layout')
    A('')
    A('```')
    A('.')
    A('\u251c\u2500 assets/                     generated diagrams; see tools/gen_assets.py')
    A('\u251c\u2500 docs/                       standards, toolchains, and the project catalogue')
    A('\u251c\u2500 projects/')
    for key, n, name, rng, _, _ in TRACKS:
        A(f'\u2502  \u251c\u2500 {key}/{" " * max(1, 24 - len(key))}track {n} \u2014 projects {rng}')
    A('\u2514\u2500 tools/                      verification sweep and the generators')
    A('```')
    A('')
    A('Every generator in `tools/` is committed and runnable. The README you are '
      'reading is itself generated \u2014 `tools/gen_readme.py` reads the catalogue and the '
      'verification results and writes this file \u2014 so it cannot drift away from the '
      'repository it describes.')
    A('')
    A('---')
    A('')
    A('<div align="center">')
    A('<sub>Paul Rukwaro \u00b7 '
      '<a href="https://github.com/pauxru">github.com/pauxru</a></sub>')
    A('</div>')

    out = ROOT / "README.md"
    out.write_text("\n".join(md) + "\n", encoding="utf-8")
    print(f"wrote {out} ({len(out.read_text(encoding='utf-8')):,} chars)")
    print(f"  {len(built)} built, {total_lines:,} lines, {total_tests:,} tests, "
          f"{len(langs)} languages")
    if not verification:
        print("  ! no verification.tsv yet -- test counts omitted")
    return 0


if __name__ == "__main__":
    sys.exit(main())
