# Public resume authoring

The public resume is a checked-in artifact:

- PDF: `public/resume/Paul-Rukwaro-Resume.pdf`
- Source data: `src/data/profile.json`
- Authoring script: `scripts/build-resume.py`

Normal `npm` development and deployment do **not** depend on Python. The resume script is an optional authoring tool only.

PDF authoring requires Python with `reportlab` and `pymupdf`. Install those packages in an isolated Python environment if they are not available. PyMuPDF is required so generation cannot silently skip the PDF content and layout checks.

## Regenerate the PDF

From `portfolio/`:

```bash
python scripts/build-resume.py
```

Or choose a custom output path:

```bash
python scripts/build-resume.py --output public/resume/Paul-Rukwaro-Resume.pdf
```

The script resolves repo-relative paths and creates the output folder if needed.

## What the script is expected to preserve

- maximum of 2 pages
- readable body text; no tiny-font compression
- selectable text
- clickable public links
- professional, conservative styling
- page 1 focused on summary, expertise, and recent experience
- page 2 focused on earlier experience, education, and independent portfolio context

## Privacy guardrails

The resume is public. Keep it intentionally minimal:

- include **LinkedIn** and **GitHub** only
- do **not** add phone numbers or email addresses
- do **not** add raw CV material or application-specific targeting
- do **not** invent endorsements, job availability, team sizes, direct reports, budgets, or other unsupported claims
- if historical credentials are ever included, do not imply they are currently active unless the source data says so
- keep the distinction between professional roles and independent studies explicit

## Source-of-truth rule

`src/data/profile.json` is the single public source for personal/career content. Update that file first, then regenerate the PDF.

If the PDF includes named independent projects, keep them public, repo-verifiable, and tightly scoped. Do not turn the resume into a second, drifting source of truth for project claims.

The script should not hardcode personal absolute paths, private contact details, or external build dependencies beyond its optional Python packages.

## Validation expectations

After regeneration, quickly verify:

1. The PDF opens cleanly and remains within 2 pages.
2. The visible links point only to public LinkedIn/GitHub destinations.
3. Page headers/footers look correct.
4. No line is clipped at page edges.
5. No private or application-specific material appears in extracted text.
