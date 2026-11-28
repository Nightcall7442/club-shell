/**
 * Which i18n line a failed command reads as: the Agent's `code` plus `details.reason` (`rule` for a club policy) and the
 * form that asked. Pure (no i18n, no stores) so the mapping is unit-tested; `describeError` and `loginErrorMessage`
 * translate the result. Every `forbidden` used to read "banned" and every `conflict` "conflicts with the current
 * state": each reason the Agent and the server send now has its own line.
 */

/** Where the error is shown: a sign-in form, the PIN or the password unlock, or anything else. */
export type ErrorContext = 'login' | 'unlockPin' | 'unlockPassword' | 'action';

/** i18n keys in order of preference (the first that exists wins) and their variables. */
export interface ErrorText {
  keys: string[];
  vars: Record<string, unknown>;
}

/** `details` as a record (anything else reads as empty). */
export function errorDetails(details: unknown): Record<string, unknown> {
  return typeof details === 'object' && details !== null ? (details as Record<string, unknown>) : {};
}

/** `details.reason` when it is a string. */
export function errorReason(details: unknown): string | null {
  const reason = errorDetails(details)['reason'];
  return typeof reason === 'string' ? reason : null;
}

/** `409 activeSessionElsewhere`: the player's session is open on another PC (sign-in). */
export function isActiveSessionElsewhere(code: string, details: unknown): boolean {
  return code === 'conflict' && errorReason(details) === 'activeSessionElsewhere';
}

/** Reasons with a line of their own under `errors.reason.*`, whatever the code. */
const REASON_KEYS: ReadonlySet<string> = new Set([
  'ageRestricted',
  'pcOccupied',
  'staffOnly',
  'staffLock',
  'guestLock',
  'guestDisabled',
  'offlineUnavailable',
  'otherUser',
  // D-107: the date of birth is set; only the desk changes it.
  'birthDateLocked',
]);

/** A sign-in or an unlock form (wrong secrets, lockouts and empty fields read as such). */
const isForm = (context: ErrorContext): boolean => context !== 'action';

export function errorText(code: string, details: unknown, context: ErrorContext = 'action'): ErrorText {
  const d = errorDetails(details);
  const reason = errorReason(details);
  const vars: Record<string, unknown> = {};
  const one = (...keys: string[]): ErrorText => ({ keys, vars });

  switch (code) {
    case 'unauthorized': {
      if (reason === 'noPin') {
        return one('lock.noPin');
      }
      if (reason === 'userToken') {
        // The player's token was refused (expired, revoked): signing in again is the way back.
        return one('errors.reason.userToken', 'errors.unauthorized');
      }
      const wrongSecret =
        reason === 'badSecret' || reason === 'badCredentials' || reason === 'wrongPin' || reason === 'wrongPassword';
      const left = d['attemptsLeft'];
      if (left === 0) {
        // Locked out of PIN unlocks (the server refuses even the right PIN then): the password still works.
        return context === 'unlockPin' ? one('lock.pinLockedOut', 'lock.tooManyAttempts') : one('lock.tooManyAttempts');
      }
      if (typeof left === 'number' && left > 0) {
        vars['attempts'] = left;
      }
      const counted = typeof left === 'number' && left > 0;
      if (context === 'unlockPin' && (wrongSecret || reason === null)) {
        return counted ? one('lock.wrongPinLeft', 'lock.wrongPin') : one('lock.wrongPin');
      }
      if (context === 'unlockPassword' && (wrongSecret || reason === null)) {
        return counted ? one('lock.wrongPasswordLeft', 'lock.wrongPassword') : one('lock.wrongPassword');
      }
      if (reason === 'wrongPin') {
        return one('lock.wrongPin');
      }
      if (reason === 'wrongPassword') {
        return one('lock.wrongPassword');
      }
      if (context === 'login') {
        return counted ? one('lock.invalidCredentialsLeft', 'lock.invalidCredentials') : one('lock.invalidCredentials');
      }
      return one('errors.unauthorized');
    }
    case 'forbidden':
      if (reason === 'banned' || reason === 'blacklisted') {
        return one('lock.banned');
      }
      // D-108: a minor in the forbidden hours, with the club's hours when the server names them.
      if (reason === 'ageRestricted' && typeof d['from'] === 'string' && typeof d['to'] === 'string') {
        vars['from'] = d['from'];
        vars['to'] = d['to'];
        return one('errors.reason.ageRestrictedHours', 'errors.reason.ageRestricted');
      }
      if (reason !== null && REASON_KEYS.has(reason)) {
        return one(`errors.reason.${reason}`, 'errors.forbidden');
      }
      return context === 'login' ? one('lock.forbidden', 'errors.forbidden') : one('errors.forbidden');
    case 'conflict':
      if (reason === 'activeSessionElsewhere') {
        const pc = d['pcName'];
        if (typeof pc === 'string' && pc.trim().length > 0) {
          vars['pc'] = pc;
          return one('errors.reason.activeSessionElsewhere', 'errors.conflict');
        }
        return one('errors.reason.activeSessionElsewhereUnknown', 'errors.conflict');
      }
      if (reason === 'pcNotReady') {
        return one('errors.pcNotReady');
      }
      if (reason !== null && REASON_KEYS.has(reason)) {
        return one(`errors.reason.${reason}`, 'errors.conflict');
      }
      return one('errors.conflict');
    case 'policyDenied': {
      // Which club rule said no (`rule` from the server's policy engine, `reason` from the Agent's own checks).
      const rule = typeof d['rule'] === 'string' ? d['rule'] : reason;
      // D-108: «Несовершеннолетним нельзя играть с 22:00 до 06:00».
      if (rule === 'minorCurfew' && typeof d['from'] === 'string' && typeof d['to'] === 'string') {
        vars['from'] = d['from'];
        vars['to'] = d['to'];
        return one('errors.policy.minorCurfewHours', 'errors.policy.minorCurfew');
      }
      return rule && !rule.includes('.')
        ? one(`errors.policy.${rule}`, 'errors.policyDenied')
        : one('errors.policyDenied');
    }
    case 'notFound':
      return reason === 'noPin' ? one('lock.noPin') : one('errors.notFound');
    case 'rateLimited':
      if (isForm(context)) {
        return one('lock.tooManyAttempts');
      }
      vars['seconds'] = typeof d['retryAfterSec'] === 'number' ? d['retryAfterSec'] : 30;
      return one('errors.rateLimited');
    case 'validation': {
      const field = d['field'];
      // D-108: a minor's purchase that would still run when the forbidden hours start.
      if (field === 'minutes' && reason === 'curfew' && typeof d['maxMinutes'] === 'number') {
        vars['minutes'] = d['maxMinutes'];
        vars['from'] = typeof d['from'] === 'string' ? d['from'] : '22:00';
        vars['to'] = typeof d['to'] === 'string' ? d['to'] : '06:00';
        if (d['noBirthDate'] === true) return one('errors.policy.birthDateRequired');
        return d['maxMinutes'] > 0
          ? one('errors.policy.minorCurfewUntil', 'errors.policy.minorCurfew')
          : one('errors.policy.minorCurfewHours', 'errors.policy.minorCurfew');
      }
      if (isForm(context)) {
        if (field === 'username') return one('lock.usernameRequired');
        if (field === 'password') return one('lock.passwordRequired');
        if (field === 'pin') return one('lock.pinRequired');
        if (field === 'cardId') return one('lock.cardRequired');
      }
      return one('errors.validation');
    }
    case 'gameLaunchFailed':
      // SHELL_CHANGES 96: the game's exe asks for administrator rights and the club has not marked it to start without
      // them — the player can do nothing about it, the club's administrator can.
      return d['stage'] === 'elevation'
        ? one('errors.gameNeedsAdmin', 'errors.gameLaunchFailed')
        : one('errors.gameLaunchFailed');
    case 'insufficientFunds':
      // Money is formatted by the caller (it needs the locale).
      vars['required'] = d['required'];
      vars['available'] = d['available'];
      return one('errors.insufficientFunds');
    default:
      return one(`errors.${code}`);
  }
}
