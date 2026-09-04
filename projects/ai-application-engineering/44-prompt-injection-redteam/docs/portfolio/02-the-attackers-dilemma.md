# The attacker's dilemma, and the mistake I made measuring it

Obfuscation is the technique everyone reaches for. Base64 the payload,
interleave zero-width characters, hide it in Unicode tag-block codepoints that
render as nothing at all. The lexical detector sees noise and passes it
through.

The obvious conclusion is that obfuscation is strictly good for the attacker.
It is not, and the reason is worth stating carefully because it is the only
structural asymmetry in the attacker's favour that runs *backwards*.

## The dilemma

**An instruction the model cannot read is an instruction the model cannot
follow.**

Encoding a payload does two things at once. It lowers the probability that a
lexical defence recognises it, and it lowers the probability that the model
comprehends it. These move together. A heavier encoding buys more evasion and
costs more comprehension, and the attacker has to pick a point on that curve
without knowing where the defender is standing.

Section 5 measures both ends against an undefended target — where the encoding
buys the attacker nothing at all, because there is nothing to evade:

| encoding class | success, no defence |
|---|---|
| heavy (base64, charcode, rot13) | 31.2% |
| legibility-preserving (zero-width, confusable, bidi) | 58.3% |

That is the cost, isolated. Now the benefit — lexical detection of the same
attacks:

| | detection |
|---|---|
| classifier alone | 3.6% |
| classifier after normalisation | 78.6% |

So the heavy encodings do evade a naive lexical layer almost completely. They
also lose two thirds of their effectiveness against a model that has to read
them. And a normaliser costing microseconds recovers most of the detection.

The attacker's best play is the middle: zero-width splitting and confusable
substitution, which preserve legibility while defeating exact string matching.
That is where the corpus's most effective obfuscations sit, and it is where a
defender's effort belongs.

## The mistake

The first version of section 5 produced a clean result in the predicted
direction, and it was manufactured.

`SimulatedAgent` computed the comprehension penalty for a payload like this:

```python
p *= 1.0 if not normalize(attack.payload).decoded else 0.4
```

`normalize` is the **defender's** function. So the model's ability to read a
payload was defined as the defender's ability to decode it.

Follow that through. If the normaliser recognised base64, the payload counted
as "decodable" and the model was penalised. If the normaliser did *not*
recognise an encoding — rot13, which ADR-0004 says is out of scope — the
payload counted as plain text and the model read it perfectly.

**Every encoding that beat the defence was scored as free for the attacker,
for no reason other than that it beat the defence.** The attacker was rewarded
precisely where the defender was weakest, and the section's thesis was being
produced by the coupling rather than tested through it.

It was invisible in every ordinary way. The code was short and read correctly:
"if the payload needed decoding, the model finds it harder". Every test
passed. The result matched the prediction, which is the strongest anaesthetic
there is.

What exposed it was going back to ask why rot13 — an encoding a large model
handles trivially — was the *least* effective attack in the corpus. There was
no story for that in terms of models. There was an immediate story in terms of
the normaliser's coverage, and that was the wrong kind of story.

## The fix, and why it is structural rather than a patch

Legibility is now declared by the corpus. `ENCODING_LEGIBILITY` maps each
obfuscation to a number, `Attack.legibility` carries it, and the target
multiplies by it. The value for rot13 is low because rot13 is hard for a small
model to read — a claim about models, arguable on its own terms, and
*independent* of whether anything in `normalize.py` handles it.

The stronger part of the fix is the constraint that came with it. `target.py`
no longer imports `normalize`, and a test asserts it never will:

```python
tree = ast.parse(inspect.getsource(target_module))
imported = {n.module for n in ast.walk(tree) if isinstance(n, ast.ImportFrom)}
assert not any("normalize" in (m or "") for m in imported)
```

Parsing the AST rather than grepping the source, because the module now
contains a long comment explaining this bug, and a substring check would trip
on the explanation.

That test encodes the real rule, which is bigger than one function: **the
system under test must not be able to see the instrument.** A simulator that
consults defence code cannot measure that defence. The same failure appears
elsewhere in this project — bug 10, where the simulator matched tool names
against the *ciphertext* and so silently emitted no tool call for obfuscated
attacks — and the fix there is the same shape: the attacker's intent is
recorded by the attacker, not recovered by the defender's decoder.

## The general form

Any harness where the thing being measured shares a module with the thing
doing the measuring is at risk of this. It is easy to write, it produces
plausible numbers, and it tends to produce numbers in the direction you
expected, which is why it survives review.

The tell is a result that is *too clean* — an effect with no exceptions, a
mechanism with no residual, a curve with no noise. In this project that tell
appeared five separate times, and each time the explanation was that two
things which should have been independent were reading from each other.

Be suspicious of good results. Especially the ones that agree with you.
