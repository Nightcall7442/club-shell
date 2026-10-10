/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { BURST_GAP_MS, createBurstDetector, type ReaderKeyEvent } from './cardReader.ts';

/** Feeds `text` then Enter, `gap` ms apart from `start`; returns what the Enter gave. */
function type(
  detect: (e: ReaderKeyEvent) => string | null,
  text: string,
  gap: number,
  start = 1_000,
  extra: Partial<ReaderKeyEvent> = {},
): string | null {
  let at = start;
  for (const key of text) {
    assert.equal(detect({ key, code: '', timeStamp: at, ...extra }), null);
    at += gap;
  }
  return detect({ key: 'Enter', code: 'Enter', timeStamp: at });
}

describe('createBurstDetector', () => {
  it('takes a reader burst ended by Enter', () => {
    assert.equal(type(createBurstDetector(), '0012345678', 4), '0012345678');
    assert.equal(type(createBurstDetector(), 'CARD-0001', 8), 'CARD-0001');
  });

  it('leaves a person typing alone', () => {
    assert.equal(type(createBurstDetector(), '0012345678', 120), null);
    assert.equal(type(createBurstDetector(), '0012345678', BURST_GAP_MS + 1), null);
  });

  it('needs at least six characters', () => {
    assert.equal(type(createBurstDetector(), '12345', 4), null);
    assert.equal(type(createBurstDetector(), '123456', 4), '123456');
  });

  it('starts over after a pause, so only the burst before Enter counts', () => {
    const detect = createBurstDetector();
    assert.equal(detect({ key: 'x', code: 'KeyX', timeStamp: 0 }), null);
    assert.equal(type(detect, '0012345678', 4, 500), '0012345678');
  });

  it('starts over on a key that is no card character, a chord or a held key', () => {
    const detect = createBurstDetector();
    for (const key of '001234') {
      detect({ key, code: '', timeStamp: 10 });
    }
    detect({ key: 'Tab', code: 'Tab', timeStamp: 12 });
    assert.equal(detect({ key: 'Enter', code: 'Enter', timeStamp: 14 }), null);
    assert.equal(type(createBurstDetector(), 'wwwwwwww', 4, 1_000, { repeat: true }), null);
    assert.equal(type(createBurstDetector(), 'aaaaaaaa', 4, 1_000, { ctrlKey: true }), null);
  });

  it('passes over Shift, with which readers type capitals', () => {
    const detect = createBurstDetector();
    let at = 0;
    for (const key of 'CARD-0001') {
      if (/[A-Z]/.test(key)) {
        detect({ key: 'Shift', code: 'ShiftLeft', timeStamp: (at += 2) });
      }
      detect({ key, code: '', timeStamp: (at += 2) });
    }
    assert.equal(detect({ key: 'Enter', code: 'Enter', timeStamp: (at += 2) }), 'CARD-0001');
  });

  it('reads letters on the Russian layout as the Latin keys', () => {
    const detect = createBurstDetector();
    let at = 0;
    for (const [key, code] of [
      ['0', 'Digit0'],
      ['Ф', 'KeyA'],
      ['1', 'Digit1'],
      ['И', 'KeyB'],
      ['2', 'Digit2'],
      ['С', 'KeyC'],
    ] as const) {
      detect({ key, code, timeStamp: (at += 3) });
    }
    assert.equal(detect({ key: 'Enter', code: 'Enter', timeStamp: (at += 3) }), '0A1B2C');
  });

  it('refuses a burst longer than any card', () => {
    assert.equal(type(createBurstDetector(), '1'.repeat(65), 1), null);
    assert.equal(type(createBurstDetector(), '1'.repeat(64), 1), '1'.repeat(64));
  });
});
