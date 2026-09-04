"""List every top-level module this project imports, by parsing rather than grepping.

`test.ps1` asserts that the Python half depends on numpy and nothing else. That assertion
used to be made with a regex over the raw source, which is the obvious way to do it and is
wrong: a docstring line that happens to begin "from the healthy baseline ..." is
indistinguishable, to a regex, from an import of a module named `the`. It failed exactly
that way, on this project's own prose.

Parsing removes the class of error rather than the instance. `ast.parse` sees imports and
does not see strings, comments, or code inside `if TYPE_CHECKING`, and it reports the
module of a relative import as None rather than as the package it resolves to.
"""

from __future__ import annotations

import ast
import sys
from pathlib import Path


def top_level_imports(source: str) -> set[str]:
    """Return the top-level module names imported by a Python source string."""
    modules: set[str] = set()
    for node in ast.walk(ast.parse(source)):
        if isinstance(node, ast.Import):
            for alias in node.names:
                modules.add(alias.name.split(".")[0])
        elif isinstance(node, ast.ImportFrom):
            # A relative import (`from .corpus import ...`) has level > 0 and is internal
            # by construction, so it is not a dependency on anything.
            if node.level == 0 and node.module:
                modules.add(node.module.split(".")[0])
    return modules


def scan(root: Path) -> dict[str, set[str]]:
    """Map every .py file under `root` to the top-level modules it imports."""
    found: dict[str, set[str]] = {}
    for path in sorted(root.rglob("*.py")):
        if "__pycache__" in path.parts:
            continue
        found[str(path.relative_to(root)).replace("\\", "/")] = top_level_imports(
            path.read_text(encoding="utf-8")
        )
    return found


def main() -> int:
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent
    for file, modules in scan(root).items():
        for module in sorted(modules):
            print(f"{module}\t{file}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
