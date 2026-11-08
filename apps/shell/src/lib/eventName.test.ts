/** `pnpm --filter @clubshell/shell test`: SHELL_CHANGES 88, the names agent events travel under in Tauri. */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { wireEventName } from './wireEventName.ts';

/** Tauri 2's rule for event names: anything else is refused on `emit` and `listen`. */
const tauriAccepts = (name: string): boolean => /^[\p{L}\p{N}\-/:_]+$/u.test(name);

describe('wireEventName', () => {
  it('turns the dots of an IPC name into colons, as the shell Rust side emits it', () => {
    assert.equal(wireEventName('agent://sys.readinessChanged'), 'agent://sys:readinessChanged');
    assert.equal(wireEventName('agent://session.updated'), 'agent://session:updated');
  });

  it('leaves kiosk events alone', () => {
    assert.equal(wireEventName('kiosk://connectivity'), 'kiosk://connectivity');
  });

  it('gives names Tauri accepts', () => {
    for (const name of ['sys.readinessChanged', 'wallet.updated', 'game.stateChanged', 'games.changed']) {
      assert.ok(tauriAccepts(wireEventName(`agent://${name}`)), name);
      assert.ok(!tauriAccepts(`agent://${name}`), name);
    }
  });
});
