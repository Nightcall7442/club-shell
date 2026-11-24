/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { idleOutcome, idlePromptDue, shouldAdoptSignIn, type IdleSeat } from './idlePolicy.ts';

const seat = (patch: Partial<IdleSeat> = {}): IdleSeat => ({
  isOpen: true,
  isOpenEnded: false,
  isGuest: false,
  isPayAtDesk: false,
  ...patch,
});

describe('idleOutcome', () => {
  it('locks a member’s prepaid time and calls the administrator to a postpaid seat', () => {
    assert.equal(idleOutcome(seat()), 'lock');
    assert.equal(idleOutcome(seat({ isOpenEnded: true, isPayAtDesk: true })), 'callAdmin');
    assert.equal(idleOutcome(seat({ isOpenEnded: true, isPayAtDesk: true, isGuest: true })), 'callAdmin');
  });

  it('never ends a guest’s prepaid time (D-100)', () => {
    assert.equal(idleOutcome(seat({ isGuest: true })), 'keep');
  });

  it('signs the player’s own pay as you go out and does nothing without a session', () => {
    assert.equal(idleOutcome(seat({ isOpenEnded: true })), 'signOut');
    assert.equal(idleOutcome(seat({ isOpen: false })), 'none');
  });
});

describe('idlePromptDue', () => {
  it('asks only when idle, nothing runs and the countdown would do something', () => {
    assert.equal(idlePromptDue(true, seat(), false), true);
    assert.equal(idlePromptDue(false, seat(), false), false, 'any input cancels the countdown');
    assert.equal(idlePromptDue(true, seat(), true), false, 'a game or any program running is activity');
    assert.equal(idlePromptDue(true, seat({ isGuest: true }), false), false, 'a guest’s prepaid time runs as paid');
  });
});

describe('shouldAdoptSignIn', () => {
  it('asks the Agent again for an open session without a player, throttled', () => {
    assert.equal(shouldAdoptSignIn(false, 'active', 10_000, 0, 5_000), true);
    assert.equal(shouldAdoptSignIn(false, 'locked', 10_000, 0, 5_000), true);
    assert.equal(shouldAdoptSignIn(false, 'active', 10_000, 8_000, 5_000), false);
    assert.equal(shouldAdoptSignIn(true, 'active', 10_000, 0, 5_000), false);
    assert.equal(shouldAdoptSignIn(false, 'ended', 10_000, 0, 5_000), false);
    assert.equal(shouldAdoptSignIn(false, 'idle', 10_000, 0, 5_000), false);
  });
});
