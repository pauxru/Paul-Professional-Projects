"""Copybook parsing and record layout.

A copybook is a data declaration, and the layout it implies is entirely
positional — there are no field delimiters in the file, no lengths, no types.
Every byte's meaning comes from this text. That is why a decommissioning project
that loses the copybook is unrecoverable, and why the copybook is the artefact
worth being pedantic about.

Supported: level numbers and nesting, ``PIC``, ``USAGE``, ``OCCURS n TIMES``,
``OCCURS n TO m TIMES DEPENDING ON f``, ``REDEFINES``, ``FILLER``.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Dict, Iterator, List, Optional

from .picture import Category, Picture, Usage, parse_picture


class CopybookError(ValueError):
    pass


@dataclass
class Field:
    level: int
    name: str
    picture: Optional[Picture] = None
    occurs_min: int = 1
    occurs_max: int = 1
    depending_on: Optional[str] = None
    redefines: Optional[str] = None
    children: List["Field"] = field(default_factory=list)
    offset: int = 0
    """Byte offset from the start of the record, at minimum ODO length."""

    @property
    def is_group(self) -> bool:
        return self.picture is None

    @property
    def is_variable(self) -> bool:
        return self.depending_on is not None

    @property
    def occurs(self) -> bool:
        return self.occurs_max > 1 or self.depending_on is not None

    def one_length(self) -> int:
        """Storage for a single occurrence, at the minimum ODO count."""
        if self.picture is not None:
            return self.picture.storage_bytes()
        return sum(c.total_length() for c in self.children if c.redefines is None)

    def total_length(self) -> int:
        return self.one_length() * self.occurs_min

    def max_length(self) -> int:
        one = (
            self.picture.storage_bytes()
            if self.picture is not None
            else sum(c.max_length() for c in self.children if c.redefines is None)
        )
        return one * self.occurs_max

    def walk(self) -> Iterator["Field"]:
        yield self
        for c in self.children:
            yield from c.walk()

    def find(self, name: str) -> Optional["Field"]:
        target = _norm(name)
        for f in self.walk():
            if _norm(f.name) == target:
                return f
        return None


def _norm(name: str) -> str:
    return name.upper().replace("_", "-")


_LEVEL = re.compile(r"^\s*(\d{2})\s+([A-Za-z0-9_\-]+)\s*(.*)$")
_PIC = re.compile(r"\bPIC(?:TURE)?\s+(?:IS\s+)?([^\s.]+)", re.IGNORECASE)
_USAGE = re.compile(
    r"\b(?:USAGE\s+(?:IS\s+)?)?(COMP-3|COMPUTATIONAL-3|PACKED-DECIMAL|"
    r"COMP-4|COMPUTATIONAL-4|COMP|COMPUTATIONAL|BINARY|DISPLAY)\b",
    re.IGNORECASE,
)
_OCCURS = re.compile(
    r"\bOCCURS\s+(\d+)(?:\s+TO\s+(\d+))?\s*(?:TIMES)?"
    r"(?:\s+DEPENDING\s+ON\s+([A-Za-z0-9_\-]+))?",
    re.IGNORECASE,
)
_REDEFINES = re.compile(r"\bREDEFINES\s+([A-Za-z0-9_\-]+)", re.IGNORECASE)


def _statements(text: str) -> Iterator[str]:
    """Yields one logical declaration per element.

    Copybooks are free-form within columns 8-72 and a declaration may span
    lines; the terminator is a period. Columns 1-6 are sequence numbers and
    column 7 is the indicator area, where ``*`` means comment. Real copybooks
    genuinely still carry sequence numbers, and a parser that ignores column 7
    will treat commented-out fields as live ones — which silently shifts every
    offset after them.
    """
    buf: List[str] = []
    for raw in text.splitlines():
        line = raw.rstrip("\n")
        if len(line) >= 7 and line[:6].strip().isdigit() and line[6:7] in "*/":
            continue
        if line.lstrip().startswith("*"):
            continue
        if len(line) > 6 and line[:6].strip().isdigit():
            line = line[6:]
            if line[:1] in ("*", "/"):
                continue
            line = line[1:] if line[:1] == " " else line
        if len(line) > 72:
            line = line[:72]
        buf.append(line)
        if "." in line:
            joined = " ".join(buf)
            for part in joined.split("."):
                if part.strip():
                    yield part.strip()
            buf = []
    if buf and " ".join(buf).strip():
        yield " ".join(buf).strip()


def parse_copybook(text: str) -> Field:
    root: Optional[Field] = None
    stack: List[Field] = []

    for stmt in _statements(text):
        m = _LEVEL.match(stmt)
        if not m:
            raise CopybookError(f"cannot parse declaration: {stmt!r}")
        level = int(m.group(1))
        name = m.group(2)
        rest = m.group(3)

        if level == 88:
            # Condition names declare no storage.
            continue

        usage = Usage.DISPLAY
        um = _USAGE.search(rest)
        if um:
            usage = Usage.parse(um.group(1))

        pic = None
        pm = _PIC.search(rest)
        if pm:
            pic = parse_picture(pm.group(1), usage)
        elif um and level != 1:
            # USAGE on a group applies to its children; not supported, and
            # silently ignoring it would produce wrong offsets.
            pass

        occurs_min = occurs_max = 1
        depending_on = None
        om = _OCCURS.search(rest)
        if om:
            occurs_min = int(om.group(1))
            occurs_max = int(om.group(2)) if om.group(2) else occurs_min
            depending_on = om.group(3)
            if depending_on and om.group(2) is None:
                raise CopybookError(
                    f"{name}: OCCURS DEPENDING ON requires a TO range, got {stmt!r}"
                )
            if occurs_max < occurs_min:
                raise CopybookError(f"{name}: OCCURS {occurs_min} TO {occurs_max}")

        redefines = None
        rm = _REDEFINES.search(rest)
        if rm:
            redefines = rm.group(1)

        node = Field(
            level=level,
            name=name,
            picture=pic,
            occurs_min=occurs_min,
            occurs_max=occurs_max,
            depending_on=depending_on,
            redefines=redefines,
        )

        if root is None:
            if pic is not None and level != 1:
                raise CopybookError("the first declaration must be a level-01 group")
            root = node
            stack = [node]
            continue

        while stack and stack[-1].level >= level:
            stack.pop()
        if not stack:
            raise CopybookError(
                f"{name} at level {level:02d} has no parent; "
                "a copybook must describe exactly one record"
            )
        stack[-1].children.append(node)
        stack.append(node)

    if root is None:
        raise CopybookError("copybook is empty")
    _assign_offsets(root, 0)
    _validate(root)
    return root


def _assign_offsets(f: Field, base: int) -> int:
    f.offset = base
    if f.picture is not None:
        return base + f.total_length()
    cursor = base
    for child in f.children:
        if child.redefines is not None:
            # A REDEFINES entry starts where the field it redefines starts.
            target = None
            for sib in f.children:
                if _norm(sib.name) == _norm(child.redefines):
                    target = sib
                    break
            if target is None:
                raise CopybookError(
                    f"{child.name} REDEFINES {child.redefines}, which is not a sibling"
                )
            _assign_offsets(child, target.offset)
            continue
        cursor = _assign_offsets(child, cursor)
    return base + f.total_length()


def _validate(root: Field) -> None:
    names: Dict[str, int] = {}
    for f in root.walk():
        if _norm(f.name) == "FILLER":
            continue
        names[_norm(f.name)] = names.get(_norm(f.name), 0) + 1
    dupes = sorted(n for n, c in names.items() if c > 1)
    if dupes:
        raise CopybookError(
            "duplicate field names make positional access ambiguous: " + ", ".join(dupes)
        )
    for f in root.walk():
        if f.depending_on:
            ctrl = root.find(f.depending_on)
            if ctrl is None:
                raise CopybookError(
                    f"{f.name} DEPENDING ON {f.depending_on}, which is not in this record"
                )
            if ctrl.picture is None or ctrl.picture.category is not Category.NUMERIC:
                raise CopybookError(
                    f"{f.name} DEPENDING ON {f.depending_on}, which is not numeric"
                )
            if ctrl.offset >= f.offset:
                # The count has to be readable before the array it sizes.
                raise CopybookError(
                    f"{f.name} DEPENDING ON {f.depending_on}, which appears after it "
                    "in the record; the length would be unknowable when it is needed"
                )
        if f.redefines and f.picture is None and not f.children:
            raise CopybookError(f"{f.name} REDEFINES but declares no storage")


def record_length(root: Field, odo_counts: Optional[Dict[str, int]] = None) -> int:
    """Length of one record.

    ``odo_counts`` maps a DEPENDING ON control field name to its value. Without
    it, variable arrays are counted at their minimum.
    """
    counts = {_norm(k): v for k, v in (odo_counts or {}).items()}

    def size(f: Field) -> int:
        if f.picture is not None:
            one = f.picture.storage_bytes()
        else:
            # A REDEFINES child overlays a sibling, so it adds nothing to the
            # group's length. Adding it is the classic way to get every offset
            # after a redefined field wrong.
            one = sum(size(c) for c in f.children if c.redefines is None)
        if f.depending_on:
            n = counts.get(_norm(f.depending_on))
            if n is None:
                n = f.occurs_min
            if not f.occurs_min <= n <= f.occurs_max:
                raise CopybookError(
                    f"{f.name} OCCURS {f.occurs_min} TO {f.occurs_max} but "
                    f"{f.depending_on} = {n}"
                )
            return one * n
        return one * f.occurs_min

    return size(root)


def describe(root: Field) -> str:
    """A flat layout listing — the artefact a migration team actually argues over."""
    lines = [f"{'off':>5} {'len':>4} {'lvl':>3}  {'name':<28} picture / notes"]
    lines.append("-" * 78)

    def emit(f: Field, indent: int) -> None:
        note = ""
        if f.picture is not None:
            note = f"{f.picture} {f.picture.usage.value}"
            if f.picture.scale:
                note += f"  (implied {f.picture.integer_digits}.{f.picture.scale})"
        if f.redefines:
            note += f"  REDEFINES {f.redefines}"
        if f.depending_on:
            note += f"  OCCURS {f.occurs_min}-{f.occurs_max} DEPENDING ON {f.depending_on}"
        elif f.occurs_max > 1:
            note += f"  OCCURS {f.occurs_max}"
        lines.append(
            f"{f.offset:>5} {f.total_length():>4} {f.level:>3}  "
            f"{'  ' * indent + f.name:<28} {note}"
        )
        for c in f.children:
            emit(c, indent + 1)

    emit(root, 0)
    return "\n".join(lines)
