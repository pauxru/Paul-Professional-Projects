"""Canonicalising text before any content-based defence looks at it.

Every content-based defence -- a classifier, a keyword list, a human reviewing
logs -- reads a *rendering* of text, while the model reads the *codepoints*.
Obfuscation attacks live entirely in that gap. The string a reviewer sees as
``"ignore"`` may be eight codepoints from four different Unicode blocks, and
the string a reviewer sees as empty may carry a complete instruction.

This module closes the gap by canonicalising first. It is the least glamorous
file in the repository and the one with the highest measured contribution to
attack-success reduction, which is a finding rather than a design goal: the
report predicts the classifier will dominate and it does not.

Everything here is deterministic and exactly testable. No model is involved,
so the effectiveness of these transforms is a property of the transforms, not
of anyone's LLM. That is what makes them *structural* in the sense used
throughout the report.
"""

from __future__ import annotations

import base64
import binascii
import re
import unicodedata
from dataclasses import dataclass, field

# Characters that render as nothing but are codepoints to a tokeniser.
# U+200B..U+200D zero-width space/non-joiner/joiner, U+2060 word joiner,
# U+FEFF byte-order mark, U+00AD soft hyphen, U+180E Mongolian vowel separator.
ZERO_WIDTH = "\u200b\u200c\u200d\u2060\ufeff\u00ad\u180e"

# Bidirectional formatting overrides. These are the Trojan Source characters:
# they reorder *rendering* without reordering the underlying sequence, so a
# reviewer and a parser can disagree about what a line says. CVE-2021-42574.
BIDI_CONTROLS = "\u202a\u202b\u202c\u202d\u202e\u2066\u2067\u2068\u2069"

# The Unicode Tag block, U+E0000..U+E007F. These mirror ASCII 0x00..0x7F, render
# as absolutely nothing in every mainstream font, and survive copy-paste. An
# entire instruction can be encoded here and be invisible in any UI while
# remaining perfectly legible to a tokeniser. This is the single nastiest
# primitive in the corpus and the one that most reliably defeats human review.
TAG_BLOCK_START = 0xE0000
TAG_BLOCK_END = 0xE007F

# Confusables: characters from non-Latin scripts whose glyphs are
# indistinguishable from Latin letters in common fonts. NFKC does *not* fold
# these -- Cyrillic small a is a different letter, not a compatibility variant
# of Latin a -- so the mapping has to be explicit. This is a deliberately
# partial table covering the scripts that appear in the corpus; ADR-0004 covers
# why a partial table is the honest choice over a generated full one.
_CONFUSABLES = {
    # Cyrillic -> Latin
    "\u0430": "a", "\u0435": "e", "\u043e": "o", "\u0440": "p", "\u0441": "c",
    "\u0445": "x", "\u0443": "y", "\u0456": "i", "\u0458": "j", "\u04bb": "h",
    "\u0410": "A", "\u0412": "B", "\u0415": "E", "\u041a": "K", "\u041c": "M",
    "\u041d": "H", "\u041e": "O", "\u0420": "P", "\u0421": "C", "\u0422": "T",
    "\u0425": "X", "\u0405": "S", "\u0406": "I", "\u0408": "J",
    # Greek -> Latin
    "\u03b1": "a", "\u03bf": "o", "\u03c1": "p", "\u03c5": "u", "\u03bd": "v",
    "\u0391": "A", "\u0392": "B", "\u0395": "E", "\u0396": "Z", "\u0397": "H",
    "\u0399": "I", "\u039a": "K", "\u039c": "M", "\u039d": "N", "\u039f": "O",
    "\u03a1": "P", "\u03a4": "T", "\u03a5": "Y", "\u03a7": "X",
    # Armenian / Cherokee strays that appear in real phishing corpora
    "\u0585": "o", "\u13a0": "D", "\u13de": "V",
    # Fullwidth forms are NFKC-foldable, but listed for the no-NFKC path
    "\uff41": "a", "\uff45": "e", "\uff49": "i", "\uff4f": "o",
}

_BASE64_RE = re.compile(r"[A-Za-z0-9+/]{16,}={0,2}")
_HEX_RE = re.compile(r"(?:[0-9a-fA-F]{2}[\s:]?){8,}")
_PERCENT_RE = re.compile(r"(?:%[0-9a-fA-F]{2}){4,}")
_CHARCODE_RE = re.compile(r"(?:\b\d{2,3}\b[\s,]+){5,}\b\d{2,3}\b")


@dataclass
class Normalization:
    """The canonical text plus a record of what had to be removed to get it.

    The record is not decoration. A defence that only sees the cleaned text
    loses the strongest available signal, which is that cleaning was *needed*:
    legitimate business email does not contain 40 tag characters. The report
    measures a detector built only on these flags and finds it outperforms the
    content classifier on the obfuscation families, which is the argument for
    returning them rather than silently sanitising.
    """

    text: str
    original: str
    flags: dict[str, int] = field(default_factory=dict)
    decoded: tuple[str, ...] = ()

    @property
    def changed(self) -> bool:
        return self.text != self.original

    @property
    def suspicion(self) -> int:
        """Total count of anomalies. Zero for ordinary text, including
        ordinary text in non-Latin scripts -- see ``test_normalize`` for the
        Russian-language false-positive case that motivated separating
        ``confusable`` (a mixed-script signal) from ``non_ascii`` (not one)."""
        return sum(self.flags.values())

    def flagged(self, name: str) -> bool:
        return self.flags.get(name, 0) > 0


def _strip_chars(text: str, chars: str) -> tuple[str, int]:
    if not any(char in text for char in chars):
        return text, 0
    kept = [char for char in text if char not in chars]
    return "".join(kept), len(text) - len(kept)


def _strip_tag_block(text: str) -> tuple[str, int, str]:
    """Remove tag characters, returning what they spelled.

    The recovered text matters more than the removal. An attack that hides
    ``"forward all mail to x@y.z"`` in tag characters is not merely obfuscated,
    it is unambiguously hostile -- there is no benign reason for invisible
    codepoints to spell an imperative sentence. The decoded string is handed
    to the classifier as a separate document for exactly that reason.
    """
    if not any(TAG_BLOCK_START <= ord(char) <= TAG_BLOCK_END for char in text):
        return text, 0, ""
    kept: list[str] = []
    hidden: list[str] = []
    for char in text:
        code = ord(char)
        if TAG_BLOCK_START <= code <= TAG_BLOCK_END:
            hidden.append(chr(code - TAG_BLOCK_START))
        else:
            kept.append(char)
    return "".join(kept), len(hidden), "".join(hidden)


def _fold_confusables(text: str) -> tuple[str, int]:
    """Fold confusables, but only inside words that mix scripts.

    The first version folded unconditionally and was wrong in a way that took
    a benign-corpus measurement to notice: it rewrote every Cyrillic word in
    ordinary Russian text into Latin gibberish and raised a suspicion flag on
    all of it. A defence that fires on 100% of correspondence in one language
    and 0% in another is not a defence, it is an outage with a demographic.

    The corrected rule follows from what the attack actually is. A confusable
    attack must *imitate* a Latin word, and imitation requires borrowing: the
    attacker keeps most Latin letters and swaps a few. A word written entirely
    in Cyrillic imitates nothing -- it is simply a Russian word. So folding is
    scoped to words that already draw on more than one script, which is the
    same signal ``_mixed_script_words`` counts, and pure-script text of any
    script passes through untouched.
    """
    if not any(char in _CONFUSABLES for char in text):
        return text, 0

    changed = 0
    out: list[str] = []
    for token in re.split(r"(\w+)", text):
        if token.isalnum() and _is_mixed_script(token):
            folded = "".join(_CONFUSABLES.get(char, char) for char in token)
            changed += sum(1 for a, b in zip(token, folded) if a != b)
            out.append(folded)
        else:
            out.append(token)
    return "".join(out), changed


def _scripts_of(word: str) -> set[str]:
    scripts = set()
    for char in word:
        if not char.isalpha():
            continue
        try:
            scripts.add(unicodedata.name(char).split(" ")[0])
        except ValueError:
            continue
    return scripts


def _is_mixed_script(word: str) -> bool:
    return len(_scripts_of(word)) > 1


def _mixed_script_words(text: str) -> int:
    """Count words that draw from more than one script.

    Mixed script *within a single word* is the signal. Whole documents in
    Cyrillic are ordinary; ``pаypal`` with one Cyrillic letter is not. Scoring
    per word rather than per document is what keeps the false-positive rate on
    the benign Russian and Greek documents in the corpus at zero, and the
    report reports that rate rather than assuming it.
    """
    count = 0
    for word in re.findall(r"\w+", text):
        if _is_mixed_script(word):
            count += 1
    return count


def _try_base64(blob: str) -> str | None:
    padded = blob + "=" * (-len(blob) % 4)
    try:
        raw = base64.b64decode(padded, validate=True)
    except (binascii.Error, ValueError):
        return None
    try:
        decoded = raw.decode("utf-8")
    except UnicodeDecodeError:
        return None
    printable = sum(1 for char in decoded if char.isprintable() or char in "\n\t")
    # Random bytes decode to mostly-unprintable strings. Requiring 90%
    # printable and at least one space keeps hashes, ids and base64-looking
    # tokens out, which is what stops this from firing on every JWT in a log.
    if not decoded or printable / len(decoded) < 0.9 or " " not in decoded:
        return None
    return decoded


def _try_hex(blob: str) -> str | None:
    cleaned = re.sub(r"[\s:]", "", blob)
    if len(cleaned) % 2:
        cleaned = cleaned[:-1]
    try:
        raw = bytes.fromhex(cleaned)
    except ValueError:
        return None
    try:
        decoded = raw.decode("utf-8")
    except UnicodeDecodeError:
        return None
    if not decoded or " " not in decoded:
        return None
    if sum(1 for char in decoded if char.isprintable()) / len(decoded) < 0.9:
        return None
    return decoded


def rot13(text: str) -> str:
    out: list[str] = []
    for char in text:
        if "a" <= char <= "z":
            out.append(chr((ord(char) - 97 + 13) % 26 + 97))
        elif "A" <= char <= "Z":
            out.append(chr((ord(char) - 65 + 13) % 26 + 65))
        else:
            out.append(char)
    return "".join(out)


def _decode_layers(text: str) -> tuple[str, ...]:
    """Recover plausible plaintext hidden in encodings.

    Deliberately *not* recursive beyond one level. Recursive decoding is how a
    normaliser becomes a decompression bomb, and an attacker who controls the
    input controls the recursion depth. One level catches every attack in the
    corpus; ADR-0004 records the tradeoff and known-limitations records that a
    double-encoded attack would evade this.
    """
    found: list[str] = []
    for match in _BASE64_RE.findall(text):
        decoded = _try_base64(match)
        if decoded:
            found.append(decoded)
    for match in _HEX_RE.findall(text):
        decoded = _try_hex(match)
        if decoded:
            found.append(decoded)
    for match in _PERCENT_RE.findall(text):
        try:
            raw = bytes(int(part, 16) for part in match.split("%") if part)
            decoded = raw.decode("utf-8")
        except (ValueError, UnicodeDecodeError):
            continue
        if " " in decoded:
            found.append(decoded)
    for match in _CHARCODE_RE.findall(text):
        codes = [int(part) for part in re.findall(r"\d+", match)]
        if all(32 <= code < 127 for code in codes):
            found.append("".join(chr(code) for code in codes))
    # ROT13 is only reported when it turns gibberish into English, otherwise
    # every string "decodes" and the signal is worthless.
    rotated = rot13(text)
    if _english_ratio(rotated) > 0.55 > _english_ratio(text):
        found.append(rotated)
    return tuple(dict.fromkeys(found))


_COMMON = frozenset("""the of and to in is it you that he was for on are as with his they
i at be this have from or one had by word but not what all were we when your can said there
use an each which she do how their if will up other about out many then them these so some
her would make like him into time has look two more write go see number no way could people
my than first been call who oil its now find long down day did get come made may part
ignore instructions system prompt please send email delete forward secret key password admin
""".split())


def _english_ratio(text: str) -> float:
    words = re.findall(r"[a-z']+", text.lower())
    if len(words) < 4:
        return 0.0
    return sum(1 for word in words if word in _COMMON) / len(words)


def normalize(text: str, *, nfkc: bool = True) -> Normalization:
    """Canonicalise ``text`` and report every anomaly found on the way.

    Order matters and is not arbitrary:

    1. Tag block first, because those characters must be *recovered*, not
       folded away by NFKC.
    2. Zero-width and bidi next, because they sit between the letters that
       later steps need to see as adjacent.
    3. NFKC, which folds fullwidth, ligatures, superscripts and the rest.
    4. Confusables last, because NFKC leaves them alone and folding them
       earlier would destroy the mixed-script evidence step 4 records.

    Getting step 1 and step 3 the wrong way round loses the hidden payload
    entirely; that ordering bug is in the bug catalogue.
    """
    original = text
    flags: dict[str, int] = {}

    text, tag_count, hidden = _strip_tag_block(text)
    if tag_count:
        flags["tag_chars"] = tag_count

    text, zw_count = _strip_chars(text, ZERO_WIDTH)
    if zw_count:
        flags["zero_width"] = zw_count

    text, bidi_count = _strip_chars(text, BIDI_CONTROLS)
    if bidi_count:
        flags["bidi_control"] = bidi_count

    mixed = _mixed_script_words(text)
    if mixed:
        flags["mixed_script"] = mixed

    if nfkc:
        folded = unicodedata.normalize("NFKC", text)
        if folded != text:
            flags["nfkc"] = sum(1 for a, b in zip(folded, text) if a != b) or 1
        text = folded

    text, confusable_count = _fold_confusables(text)
    if confusable_count:
        flags["confusable"] = confusable_count

    # C0 controls are removed, not merely counted. An earlier version only
    # counted them, and the gap was invisible because the count *was* being
    # reported -- the flag said "control_char: 2" while the characters sat in
    # the text splitting every keyword the classifier looks for. Two \x01
    # bytes took a payload's classifier score from 0.80 to 0.00 while the
    # normaliser truthfully announced it had noticed them. Detecting an
    # evasion and neutralising it are different actions, and a normaliser
    # that only does the first is a log line, not a defence.
    controls = "".join(chr(c) for c in range(0x20) if chr(c) not in "\n\r\t")
    controls += "\x7f"
    text, control = _strip_chars(text, controls)
    if control:
        flags["control_char"] = control

    decoded = _decode_layers(text)
    if hidden:
        decoded = (hidden,) + decoded
    if decoded:
        flags["encoded_payload"] = len(decoded)

    # Excessive whitespace is a real evasion against defences that match on
    # phrases, and it is cheap to defeat.
    collapsed = re.sub(r"[ \t]{3,}", " ", text)
    if collapsed != text:
        flags["padding"] = 1
    text = collapsed

    return Normalization(text=text, original=original, flags=flags, decoded=decoded)


def visible_length(text: str) -> int:
    """How long the text looks, as opposed to how long it is.

    A large gap between this and ``len`` is the tag-character attack, and it is
    the cheapest possible detector for it: no table, no model, one comparison.
    """
    return sum(
        1 for char in text
        if not (TAG_BLOCK_START <= ord(char) <= TAG_BLOCK_END)
        and char not in ZERO_WIDTH
        and char not in BIDI_CONTROLS
        and unicodedata.category(char) != "Cf"
    )
