import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
  CrashError,
  CrashingJournal,
  InMemoryJournal,
  type JournalEvent,
} from '../src/journal.ts';

const ev = (seq: number, type: JournalEvent['type'] = 'run.started'): JournalEvent =>
  ({ seq, type, at: 0 }) as JournalEvent;

describe('journal', () => {
  it('starts empty', () => {
    assert.equal(new InMemoryJournal().length, 0);
  });

  it('appends in order', () => {
    const j = new InMemoryJournal();
    j.append(ev(0));
    j.append(ev(1, 'run.completed'));
    assert.equal(j.length, 2);
    assert.equal(j.events()[0]!.seq, 0);
    assert.equal(j.events()[1]!.seq, 1);
  });

  it('rejects a sequence gap', () => {
    const j = new InMemoryJournal();
    j.append(ev(0));
    assert.throws(() => j.append(ev(2)), /sequence gap/i);
  });

  it('rejects a repeated sequence number', () => {
    const j = new InMemoryJournal();
    j.append(ev(0));
    assert.throws(() => j.append(ev(0)), /sequence gap/i);
  });

  it('rejects a sequence that moves backwards', () => {
    const j = new InMemoryJournal();
    j.append(ev(0));
    j.append(ev(1));
    assert.throws(() => j.append(ev(1)), /sequence gap/i);
  });

  it('the gap check is the only defence against two writers, and it fires', () => {
    const j = new InMemoryJournal();
    j.append(ev(0));
    const second = ev(1);
    j.append(second);
    assert.throws(() => j.append(ev(1)), /expected 2, got 1/);
  });

  it('round-trips through serialise and parse', () => {
    const j = new InMemoryJournal();
    j.append(ev(0));
    j.append(ev(1, 'run.completed'));
    assert.deepEqual(InMemoryJournal.parse(j.serialise()).events(), j.events());
  });

  it('serialises one event per line', () => {
    const j = new InMemoryJournal();
    j.append(ev(0));
    j.append(ev(1));
    assert.equal(j.serialise().split('\n').length, 2);
  });

  it('serialising an empty journal produces an empty string', () => {
    assert.equal(new InMemoryJournal().serialise(), '');
  });

  it('parse of an empty string yields an empty journal', () => {
    assert.equal(InMemoryJournal.parse('').length, 0);
  });

  it('parse discards a torn final line', () => {
    const j = new InMemoryJournal();
    j.append(ev(0));
    j.append(ev(1));
    const torn = j.serialise().slice(0, -8);
    assert.equal(InMemoryJournal.parse(torn).length, 1, 'the half-written record must be dropped');
  });

  it('a torn tail leaves the surviving prefix appendable', () => {
    const j = new InMemoryJournal();
    for (let i = 0; i < 5; i++) j.append(ev(i));
    const back = InMemoryJournal.parse(j.serialise().slice(0, -5));
    const n = back.length;
    back.append(ev(n));
    assert.equal(back.length, n + 1);
  });

  it('parse tolerates a trailing newline without inventing an event', () => {
    const j = new InMemoryJournal();
    j.append(ev(0));
    assert.equal(InMemoryJournal.parse(j.serialise() + '\n').length, 1);
  });

  it('parse stops at the first unreadable line rather than skipping it', () => {
    const good = JSON.stringify(ev(0));
    const text = `${good}\n{"seq":1,"type":"run.\n${JSON.stringify(ev(2))}`;
    assert.equal(
      InMemoryJournal.parse(text).length,
      1,
      'resuming past a hole would replay a history that never happened',
    );
  });
});

describe('crashing journal', () => {
  it('passes writes through before the crash point', () => {
    const j = new CrashingJournal(new InMemoryJournal(), 3);
    j.append(ev(0));
    j.append(ev(1));
    assert.equal(j.length, 2);
  });

  it('throws CrashError at the nominated write', () => {
    const j = new CrashingJournal(new InMemoryJournal(), 1);
    j.append(ev(0));
    assert.throws(() => j.append(ev(1)), CrashError);
  });

  it('reports the sequence it crashed at', () => {
    const j = new CrashingJournal(new InMemoryJournal(), 2);
    j.append(ev(0));
    j.append(ev(1));
    try {
      j.append(ev(2));
      assert.fail('expected a crash');
    } catch (e) {
      assert.equal((e as CrashError).atSequence, 2);
    }
  });

  it('does not persist the write that crashed', () => {
    const inner = new InMemoryJournal();
    const j = new CrashingJournal(inner, 1);
    j.append(ev(0));
    assert.throws(() => j.append(ev(1)));
    assert.equal(inner.length, 1);
  });

  it('crashing at write 0 leaves nothing behind', () => {
    const inner = new InMemoryJournal();
    assert.throws(() => new CrashingJournal(inner, 0).append(ev(0)));
    assert.equal(inner.length, 0);
  });

  it('a crash point beyond the run never fires', () => {
    const j = new CrashingJournal(new InMemoryJournal(), 1000);
    for (let i = 0; i < 10; i++) j.append(ev(i));
    assert.equal(j.length, 10);
  });

  it('the surviving journal is exactly the prefix, and is replayable', () => {
    const inner = new InMemoryJournal();
    const j = new CrashingJournal(inner, 4);
    for (let i = 0; i < 4; i++) j.append(ev(i));
    assert.throws(() => j.append(ev(4)));
    assert.deepEqual(
      InMemoryJournal.parse(inner.serialise()).events().map((e) => e.seq),
      [0, 1, 2, 3],
    );
  });
});
