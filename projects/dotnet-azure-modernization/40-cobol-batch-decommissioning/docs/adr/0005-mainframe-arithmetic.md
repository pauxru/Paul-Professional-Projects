# 0005 — Model mainframe arithmetic, including its bugs

**Status:** accepted

## Context

`BILLRUN` computes:

```cobol
COMPUTE WS-GROSS   = CM-BALANCE + WS-SERVICE-CHARGE
COMPUTE WS-VAT     = WS-GROSS * WS-VAT-RATE
COMPUTE CM-BALANCE = WS-GROSS + WS-VAT - CM-CREDIT
```

Three things about that are easy to get wrong, and none of them are the formula.

**Storing truncates.** Each `COMPUTE` stores into a `PIC S9(7)V99` field. Without
`ROUNDED`, COBOL truncates toward zero. `102.50 × 0.175 = 17.9375` is stored as
`17.93`, not `17.94`.

**Intermediates are stored.** Folding the three statements into one expression
evaluated at full precision and rounded once gives a different answer. The
truncation happens three times, not once.

**There is no `ON SIZE ERROR`.** A result too large for `S9(7)V99` has its
high-order digits silently dropped. 10,766,540.84 becomes 766,540.84.

## Decision

Implement the reference semantics exactly — `ROUND_DOWN` at each intermediate
store, plus high-order truncation modulo 10⁷ on the final store — and implement
three wrong versions beside it, so the difference can be measured rather than
argued about.

| mode | what produces it | differs on |
|---|---|---|
| `ROUND_HALF_UP` | `Decimal.quantize` with the obvious argument | 52.0% |
| `ROUND_FLOOR` | `int()`, `//`, `math.floor` on a scaled integer | 32.1% |
| `float` | using `float` for money | 50.4% |

## The result that justified building all three

`ROUND_HALF_UP` is what everyone would call correct, and it is wrong on half of
all records. It is caught immediately.

`ROUND_FLOOR` is identical to the mainframe on **every positive amount** and
wrong on **every negative one**, because floor and truncate-toward-zero only
diverge below zero. Against a 3,347-record test extract containing no refunds and
no credit balances it differs on **zero records**. It ships.

The implementations are ordered by how often they fail and inversely ordered by
how likely they are to survive testing. That inversion is the finding, and it
generalises well beyond this project: *the defects that reach production are the
ones correlated with a data characteristic your sample lacks.*

## On reproducing the size error

Reproducing a bug on purpose needs justification. The argument is that the
mainframe's behaviour is the current behaviour, 722 of 5,000 records hit it, and
downstream systems have been receiving those wrapped balances for thirty years.
Something reconciles against them. A reimplementation that raises an exception
instead is not more correct — it is differently wrong, at 03:00, in a batch
window.

So `bill_detail` returns `Billed(value, size_error)`: it reproduces the
mainframe's answer *and* flags it, and `RunStats.size_errors` counts them. The
migration then gets to make a decision with a number attached, before cutover
rather than after.

## Consequences

`VAT_RATE`, `SERVICE_CHARGE` and `BALANCE_LIMIT` are constants in
`pipeline.py`. In a real engagement they come from the COBOL source and every one
of them needs a citation back to a line number, because a wrong constant here
produces a plausible wrong answer rather than an error.

The four modes are not a strategy pattern anyone will extend. They exist to run
an experiment, and they are worth keeping afterwards as executable documentation
of why the reference implementation looks the way it does.
