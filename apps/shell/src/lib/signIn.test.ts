/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { AuthLoginRequest } from '@clubshell/contracts';
import { buildLoginRequest, createElsewhereRetry, elsewhereOf } from './signIn.ts';

const elsewhere = {
  code: 'conflict',
  message: 'busy',
  details: { reason: 'activeSessionElsewhere', pcId: 'p7', pcName: 'PC-07' },
};

/** An Agent that refuses while the session is open on PC-07 and ends it there when asked to. */
function agent(): { sent: AuthLoginRequest[]; login: (req: AuthLoginRequest) => Promise<string> } {
  const sent: AuthLoginRequest[] = [];
  return {
    sent,
    login: (req) => {
      sent.push(req);
      return req.endElsewhere ? Promise.resolve('signed in') : Promise.reject(elsewhere);
    },
  };
}

describe('buildLoginRequest', () => {
  it('sends endElsewhere only when the player confirmed it', () => {
    const plain = buildLoginRequest('password', { username: 'demo', password: 'pw' });
    assert.deepEqual(plain, {
      kind: 'password',
      username: 'demo',
      password: 'pw',
      qrToken: null,
      cardId: null,
      token: null,
    });
    assert.equal('endElsewhere' in plain, false);
    assert.equal(buildLoginRequest('card', { cardId: 'C-1' }, true).endElsewhere, true);
  });
});

describe('elsewhereOf', () => {
  it('reads the PC of a session open elsewhere and nothing else', () => {
    assert.deepEqual(elsewhereOf(elsewhere), { pcId: 'p7', pcName: 'PC-07' });
    assert.deepEqual(elsewhereOf({ code: 'conflict', details: { reason: 'activeSessionElsewhere' } }), {
      pcId: null,
      pcName: null,
    });
    assert.equal(elsewhereOf({ code: 'conflict', details: { reason: 'pcNotReady' } }), null);
    assert.equal(elsewhereOf({ code: 'forbidden', details: { reason: 'activeSessionElsewhere' } }), null);
    assert.equal(elsewhereOf(null), null);
  });
});

describe('createElsewhereRetry', () => {
  it('repeats the refused sign-in once, with the same secrets and endElsewhere', async () => {
    const retry = createElsewhereRetry();
    const a = agent();
    await assert.rejects(retry.send(buildLoginRequest('password', { username: 'demo', password: 'pw' }), a.login));
    assert.equal(retry.armed, true);

    const again = retry.take();
    assert.ok(again);
    assert.equal(retry.armed, false, 'the secrets are forgotten as the retry is taken');
    assert.equal(retry.take(), null, 'one retry only');
    assert.equal(await retry.send(again, a.login), 'signed in');
    assert.equal(a.sent.length, 2);
    assert.deepEqual(a.sent[1], { ...a.sent[0], endElsewhere: true });
  });

  it('keeps nothing after a cancel, another error, a guest or a refused retry', async () => {
    const retry = createElsewhereRetry();
    await assert.rejects(retry.send(buildLoginRequest('card', { cardId: 'C-1' }), agent().login));
    retry.clear();
    assert.equal(retry.armed, false);
    assert.equal(retry.take(), null);

    await assert.rejects(
      retry.send(buildLoginRequest('password', { username: 'demo', password: 'x' }), () =>
        Promise.reject({ code: 'unauthorized', details: { reason: 'badCredentials' } }),
      ),
    );
    assert.equal(retry.armed, false);

    await assert.rejects(retry.send(buildLoginRequest('guest', {}), agent().login));
    assert.equal(retry.armed, false, 'a guest has no session elsewhere to end');

    // Still signed in there (a live token): the server refuses the retry the same way; it is not offered again.
    await assert.rejects(retry.send(buildLoginRequest('qr', { qrToken: 't' }, true), () => Promise.reject(elsewhere)));
    assert.equal(retry.armed, false);
  });

  it('forgets a kept sign-in when another one succeeds', async () => {
    const retry = createElsewhereRetry();
    await assert.rejects(retry.send(buildLoginRequest('password', { username: 'a', password: 'b' }), agent().login));
    await retry.send(buildLoginRequest('guest', {}), () => Promise.resolve('guest in'));
    assert.equal(retry.armed, false);
  });
});
