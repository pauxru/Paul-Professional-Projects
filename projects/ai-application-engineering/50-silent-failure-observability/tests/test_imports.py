"""Tests for the import scanner that guards this project's dependency claim.

The scanner exists because the previous regex version reported that this project imports a
module named `the`, having found the line "from the healthy baseline in either direction"
inside a docstring. The first test below is that exact input.
"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from tools.imports import scan, top_level_imports  # noqa: E402


def test_prose_beginning_with_from_is_not_an_import() -> None:
    """The regression that motivated parsing: a docstring line starting with 'from'."""
    source = '''
"""A statistic is judged by how far it sits
from the healthy baseline in either direction, which is all the reference window
tells us."""
'''
    assert top_level_imports(source) == set()


def test_prose_beginning_with_import_is_not_an_import() -> None:
    source = '"""It matters, and this is the\\nimport ant part, only in aggregate."""'
    assert top_level_imports(source) == set()


def test_a_commented_out_import_is_not_an_import() -> None:
    assert top_level_imports("# import scipy\nimport numpy\n") == {"numpy"}


def test_an_import_inside_a_string_literal_is_not_an_import() -> None:
    assert top_level_imports('CODE = "import scipy"\n') == set()


def test_plain_import() -> None:
    assert top_level_imports("import numpy") == {"numpy"}


def test_from_import() -> None:
    assert top_level_imports("from numpy import ndarray") == {"numpy"}


def test_dotted_imports_report_the_top_level_package() -> None:
    assert top_level_imports("import numpy.linalg") == {"numpy"}
    assert top_level_imports("from numpy.linalg import norm") == {"numpy"}


def test_aliased_import_reports_the_real_module_not_the_alias() -> None:
    """`import numpy as np` is a dependency on numpy, not on np."""
    assert top_level_imports("import numpy as np") == {"numpy"}


def test_multiple_names_on_one_import_line() -> None:
    assert top_level_imports("import json, math") == {"json", "math"}


def test_relative_imports_are_not_dependencies() -> None:
    """`from . import corpus` resolves inside the package and depends on nothing."""
    assert top_level_imports("from . import corpus") == set()
    assert top_level_imports("from .corpus import Turn") == set()
    assert top_level_imports("from ..src.corpus import Turn") == set()


def test_a_nested_import_is_still_found() -> None:
    """An import inside a function body is a dependency exactly like a top-level one."""
    source = "def f():\n    import subprocess\n    return subprocess\n"
    assert top_level_imports(source) == {"subprocess"}


def test_a_conditional_import_is_still_found() -> None:
    source = "if TYPE_CHECKING:\n    import numpy\n"
    assert top_level_imports(source) == {"numpy"}


def test_syntactically_invalid_source_raises_rather_than_reporting_nothing() -> None:
    """A parse failure must not be silently read as 'this file imports nothing'."""
    with pytest.raises(SyntaxError):
        top_level_imports("def f(:\n")


def test_scanning_this_project_finds_numpy_and_no_other_third_party_module() -> None:
    root = Path(__file__).resolve().parent.parent
    found: set[str] = set()
    for modules in scan(root).values():
        found |= modules
    third_party = found - {
        "__future__", "abc", "argparse", "ast", "collections", "dataclasses",
        "functools", "hashlib", "inspect", "itertools", "json", "math", "os",
        "pathlib", "random", "re", "shutil", "subprocess", "sys", "tempfile",
        "time", "typing", "src", "tests", "tools",
    }
    assert third_party == {"numpy", "pytest"}


def test_scanning_skips_bytecode_caches() -> None:
    root = Path(__file__).resolve().parent.parent
    assert not any("__pycache__" in path for path in scan(root))


def test_scan_reports_paths_with_forward_slashes_on_every_platform() -> None:
    """Paths appear in a thrown error message; they should read the same everywhere."""
    root = Path(__file__).resolve().parent.parent
    assert all("\\" not in path for path in scan(root))
    assert "src/detectors.py" in scan(root)
