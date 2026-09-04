/**
 * Written before any experiment was run, and not edited afterwards.
 *
 * The scoreboard in the report scores these mechanically against the measured
 * numbers. Several are wrong. That is the reason they are checked in: a design
 * document that agrees with its own results was written after them.
 */
export const PREDICTIONS: readonly string[] = [
  /*  1 */ 'With positional idempotency keys and a deduplicating gateway, every crash point recovers and the refund is issued exactly once.',
  /*  2 */ 'The unknown window will be a substantial share of crash points -- more than one in ten -- because effects are the slow part of the workflow.',
  /*  3 */ 'An idempotency key makes duplicate refunds impossible.',
  /*  4 */ 'Generating a fresh idempotency key on retry causes a duplicate refund.',
  /*  5 */ 'The escalate policy is strictly safer at no cost: it still completes every run automatically.',
  /*  6 */ 'Every corpus pattern I labelled detectable is caught by an in-process replay.',
  /*  7 */ 'No pattern is invisible to an in-process replay but visible after the clock has moved; replay either diverges or it does not.',
  /*  8 */ 'Every pattern in the corpus is a real hazard, since each one contains genuine nondeterminism.',
  /*  9 */ 'A cost budget behaves the same whether a run is fresh or resumed.',
  /* 10 */ 'Resuming re-executes some step bodies -- the last few before the crash, at least.',
  /* 11 */ 'A stack-unwinding approval gate survives repeated restarts without re-executing work or growing the journal.',
  /* 12 */ 'The deterministic control workflow replays cleanly and is never flagged.',
  /* 13 */ 'The crash-free control run moves money exactly once.',
];
