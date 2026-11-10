/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { errorText, isActiveSessionElsewhere, type ErrorContext } from './errorText.ts';

const first = (code: string, details?: unknown, context?: ErrorContext): string =>
  errorText(code, details, context).keys[0] ?? '';

type Bundle = Record<string, unknown>;
const bundles: Record<string, Bundle> = Object.fromEntries(
  ['en', 'ru', 'uz'].map((l) => [
    l,
    JSON.parse(readFileSync(new URL(`../i18n/${l}.json`, import.meta.url), 'utf8')) as Bundle,
  ]),
);
const has = (bundle: Bundle, key: string): boolean =>
  typeof key
    .split('.')
    .reduce<unknown>((n, p) => (typeof n === 'object' && n !== null ? (n as Bundle)[p] : undefined), bundle) ===
  'string';

describe('errorText', () => {
  it('reads each forbidden reason on its own instead of "banned"', () => {
    assert.equal(first('forbidden', { reason: 'banned' }, 'login'), 'lock.banned');
    assert.equal(first('forbidden', { reason: 'blacklisted' }, 'login'), 'lock.banned');
    assert.equal(first('forbidden', { reason: 'ageRestricted' }, 'login'), 'errors.reason.ageRestricted');
    assert.equal(first('forbidden', { reason: 'pcOccupied' }, 'login'), 'errors.reason.pcOccupied');
    assert.equal(first('forbidden', { reason: 'staffOnly' }), 'errors.reason.staffOnly');
    assert.equal(first('forbidden', { reason: 'staffLock' }, 'unlockPin'), 'errors.reason.staffLock');
    assert.equal(first('forbidden', { reason: 'guestLock' }), 'errors.reason.guestLock');
    // An unknown reason is not "banned" either.
    assert.equal(first('forbidden', { reason: 'somethingNew' }, 'login'), 'lock.forbidden');
    assert.equal(first('forbidden', undefined), 'errors.forbidden');
  });

  it('names the PC of a session open elsewhere', () => {
    const r = errorText('conflict', { reason: 'activeSessionElsewhere', pcId: 'x', pcName: 'PC-07' }, 'login');
    assert.deepEqual(r.keys[0], 'errors.reason.activeSessionElsewhere');
    assert.equal(r.vars['pc'], 'PC-07');
    assert.equal(
      first('conflict', { reason: 'activeSessionElsewhere', pcName: null }),
      'errors.reason.activeSessionElsewhereUnknown',
    );
    assert.ok(isActiveSessionElsewhere('conflict', { reason: 'activeSessionElsewhere' }));
    assert.ok(!isActiveSessionElsewhere('forbidden', { reason: 'activeSessionElsewhere' }));
    assert.ok(!isActiveSessionElsewhere('conflict', { reason: 'pcNotReady' }));
  });

  it('reads the other conflicts and policies', () => {
    assert.equal(first('conflict', { reason: 'pcNotReady' }), 'errors.pcNotReady');
    assert.equal(first('conflict', { reason: 'offlineUnavailable' }), 'errors.reason.offlineUnavailable');
    assert.equal(first('conflict', { reason: 'whatever' }), 'errors.conflict');
    assert.equal(first('policyDenied', { reason: 'postpaidNotAllowed' }), 'errors.policy.postpaidNotAllowed');
    assert.equal(first('policyDenied', { rule: 'tariffTime' }), 'errors.policy.tariffTime');
    assert.equal(first('policyDenied', { rule: 'kiosk.blockTaskManager' }), 'errors.policyDenied');
    assert.equal(first('insufficientFunds', {}), 'errors.insufficientFunds');
  });

  it('reads wrong secrets by the form that asked', () => {
    assert.equal(first('unauthorized', { reason: 'badSecret' }, 'unlockPin'), 'lock.wrongPin');
    assert.equal(first('unauthorized', { reason: 'badSecret' }, 'unlockPassword'), 'lock.wrongPassword');
    assert.equal(first('unauthorized', { reason: 'noPin' }, 'unlockPin'), 'lock.noPin');
    assert.equal(first('notFound', { reason: 'noPin' }, 'unlockPin'), 'lock.noPin');
    assert.equal(first('unauthorized', { reason: 'badCredentials' }, 'login'), 'lock.invalidCredentials');
    const left = errorText('unauthorized', { reason: 'badCredentials', attemptsLeft: 2 }, 'login');
    assert.equal(left.keys[0], 'lock.invalidCredentialsLeft');
    assert.equal(left.vars['attempts'], 2);
    assert.equal(first('unauthorized', { reason: 'badCredentials', attemptsLeft: 0 }, 'login'), 'lock.tooManyAttempts');
    const pinLeft = errorText('unauthorized', { reason: 'badSecret', attemptsLeft: 3 }, 'unlockPin');
    assert.equal(pinLeft.keys[0], 'lock.wrongPinLeft');
    assert.equal(pinLeft.vars['attempts'], 3);
    // Locked out (the right PIN is refused too): the password, or the administrator.
    assert.equal(
      first('unauthorized', { reason: 'badCredentials', attemptsLeft: 0 }, 'unlockPin'),
      'lock.pinLockedOut',
    );
    assert.equal(
      first('unauthorized', { reason: 'badCredentials', attemptsLeft: 0 }, 'unlockPassword'),
      'lock.tooManyAttempts',
    );
    assert.equal(first('unauthorized', { reason: 'userToken' }), 'errors.reason.userToken');
    assert.equal(first('forbidden', { reason: 'otherUser' }), 'errors.reason.otherUser');
    assert.equal(first('rateLimited', {}, 'login'), 'lock.tooManyAttempts');
    assert.equal(first('rateLimited', { retryAfterSec: 9 }), 'errors.rateLimited');
    assert.equal(first('validation', { field: 'cardId' }, 'login'), 'lock.cardRequired');
  });

  it('has every line it can return in all three languages', () => {
    const cases: [string, unknown, ErrorContext][] = [
      ['forbidden', { reason: 'banned' }, 'login'],
      ['forbidden', { reason: 'ageRestricted' }, 'login'],
      ['forbidden', { reason: 'pcOccupied' }, 'login'],
      ['forbidden', { reason: 'staffOnly' }, 'action'],
      ['forbidden', { reason: 'staffLock' }, 'action'],
      ['forbidden', { reason: 'guestLock' }, 'action'],
      ['forbidden', { reason: 'guestDisabled' }, 'login'],
      ['forbidden', { reason: 'x' }, 'login'],
      ['conflict', { reason: 'activeSessionElsewhere', pcName: 'PC-1' }, 'login'],
      ['conflict', { reason: 'activeSessionElsewhere' }, 'login'],
      ['conflict', { reason: 'offlineUnavailable' }, 'action'],
      ['conflict', { reason: 'pcNotReady' }, 'login'],
      ['policyDenied', { reason: 'postpaidNotAllowed' }, 'action'],
      ['unauthorized', { reason: 'noPin' }, 'unlockPin'],
      ['unauthorized', { reason: 'badSecret', attemptsLeft: 2 }, 'unlockPin'],
      ['unauthorized', { reason: 'badSecret', attemptsLeft: 2 }, 'unlockPassword'],
      ['unauthorized', { reason: 'userToken' }, 'action'],
      ['unauthorized', { reason: 'badCredentials', attemptsLeft: 0 }, 'unlockPin'],
      ['forbidden', { reason: 'otherUser' }, 'action'],
      ['unauthorized', { reason: 'badCredentials', attemptsLeft: 3 }, 'login'],
    ];
    for (const [code, details, context] of cases) {
      const key = first(code, details, context);
      for (const [locale, bundle] of Object.entries(bundles)) {
        assert.ok(has(bundle, key), `${locale}: missing ${key}`);
      }
    }
  });
});
