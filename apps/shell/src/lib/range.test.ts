/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { rangeStepsPerPress } from './range.ts';

describe('rangeStepsPerPress', () => {
  it('moves a short scale one position per press', () => {
    assert.equal(rangeStepsPerPress(1, 20, 1), 1); // mouse speed
    assert.equal(rangeStepsPerPress(0, 14, 1), 1); // double-click speed
    assert.equal(rangeStepsPerPress(0, 100, 5), 1); // the top bar's volume, 5 % a step
    assert.equal(rangeStepsPerPress(0, 30, 1), 1);
  });

  it('moves a long scale five positions per press', () => {
    assert.equal(rangeStepsPerPress(0, 100, 1), 5); // the home volume slider
    assert.equal(rangeStepsPerPress(0, 31, 1), 5);
  });

  it('treats a missing step as 1', () => {
    assert.equal(rangeStepsPerPress(0, 100, 0), 5);
  });
});
