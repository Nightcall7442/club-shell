/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { rearmWarnings, startWarnings, stepMoneyWarnings, stepWarnings, warningMarks } from './lowTime.ts';

/** Counts a prepaid session down from `from` to `to` seconds, one tick a second, and returns what it warned. */
function countDown(marks: number[], sent: number[], from: number, to: number): { sent: number[]; warned: number[] } {
  const warned: number[] = [];
  let state = sent;
  for (let left = from; left >= to; left -= 1) {
    const step = stepWarnings(marks, state, left);
    state = step.sent;
    if (step.warn !== null) warned.push(step.warn);
  }
  return { sent: state, warned };
}

describe('warningMarks (D-109)', () => {
  it('is 10, 7, 5 and the last minute by default', () => {
    assert.deepEqual(warningMarks(), [10, 7, 5, 1]);
    assert.deepEqual(warningMarks(null), [10, 7, 5, 1]);
  });

  it("takes the club's own list, sorted, with the last minute", () => {
    assert.deepEqual(warningMarks([3, 15, 15]), [15, 3, 1]);
    assert.deepEqual(warningMarks([]), [1]);
  });
});

describe('prepaid countdown', () => {
  const marks = warningMarks();

  it('warns once each at 10, 7, 5 and 1 minutes left', () => {
    const { warned, sent } = countDown(marks, startWarnings(marks, 30 * 60), 30 * 60, 1);
    assert.deepEqual(warned, [10, 7, 5, 1]);
    assert.deepEqual(sent, [10, 7, 5, 1]);
    // Ticks past the mark (a late or repeated tick) warn nothing twice.
    assert.equal(stepWarnings(marks, sent, 30).warn, null);
  });

  it('warns exactly when the minute is reached, not a second earlier', () => {
    assert.equal(stepWarnings(marks, [], 601).warn, null);
    assert.equal(stepWarnings(marks, [], 600).warn, 10);
  });

  it('does not open a short session with stale warnings', () => {
    const sent = startWarnings(marks, 6 * 60);
    assert.deepEqual(sent, [10, 7]);
    assert.deepEqual(countDown(marks, sent, 6 * 60, 1).warned, [5, 1]);
  });

  it('re-arms the marks an extension pushed the time back above', () => {
    const first = countDown(marks, startWarnings(marks, 20 * 60), 20 * 60, 290);
    assert.deepEqual(first.warned, [10, 7, 5]);
    // +5 minutes at 4:50 left: 9:50 left, so 7 and 5 warn again, 10 does not.
    const extended = rearmWarnings(first.sent, 590);
    assert.deepEqual(extended, [10]);
    assert.deepEqual(countDown(marks, extended, 590, 1).warned, [7, 5, 1]);
  });

  it('never warns at zero (the end follows)', () => {
    assert.equal(stepWarnings(marks, [], 0).warn, null);
  });
});

describe('postpaid by the money left (balance + bonuses + debt limit)', () => {
  const marks = warningMarks();
  // 20 000 tiyin a started minute, as the server's Standard.
  const costOf = (seconds: number): number => Math.ceil(seconds / 60) * 20_000;

  it('warns at 10, 7, 5 and 1 minutes of money left, once each', () => {
    // 300 000 pays 15 minutes: the cut-off at 900 s.
    const warned: number[] = [];
    let sent: number[] = [];
    for (let used = 0; used < 900; used += 1) {
      const step = stepMoneyWarnings(marks, sent, 300_000, used, costOf);
      sent = step.sent;
      if (step.warn !== null) warned.push(step.warn);
      if (used === 299) assert.deepEqual(sent, [], 'nothing before 10 minutes of money are left');
      if (used === 300) assert.deepEqual(sent, [10]);
    }
    assert.deepEqual(warned, [10, 7, 5, 1]);
  });

  it('counts the bonus account as money for play time (D-106)', () => {
    // 200 000 on the balance + 100 000 bonuses = 15 minutes: at 300 s the 10-minute mark, not at once.
    assert.equal(stepMoneyWarnings(marks, [], 200_000 + 100_000, 299, costOf).warn, null);
    assert.equal(stepMoneyWarnings(marks, [], 200_000 + 100_000, 300, costOf).warn, 10);
  });

  it('warns once, with the fewer minutes, for marks reached together', () => {
    // 140 000 pays 7 minutes from the start: 10 and 7 are both due at once.
    const step = stepMoneyWarnings(marks, [], 140_000, 0, costOf);
    assert.deepEqual(step, { sent: [10, 7], warn: 7 });
  });

  it('takes the marks back after a top-up, so they warn again', () => {
    const low = stepMoneyWarnings(marks, [], 300_000, 300, costOf);
    assert.deepEqual(low.sent, [10]);
    const toppedUp = stepMoneyWarnings(marks, low.sent, 340_000, 301, costOf);
    assert.deepEqual(toppedUp, { sent: [], warn: null });
    assert.equal(stepMoneyWarnings(marks, toppedUp.sent, 340_000, 420, costOf).warn, 10);
  });
});
