/**
 * The player's date of birth on the PC (D-107): asked at registration and once from a signed-in player who has none,
 * before any time is bought. Pure, so the rules are unit-tested; the server checks the same on the club's date.
 */

export const BIRTH_MIN_AGE = 5;
export const BIRTH_MAX_AGE = 100;

/** What is wrong with a date: the i18n key under `birthDate.*`. */
export type BirthDateProblem = 'required' | 'format' | 'future' | 'tooYoung' | 'tooOld';

/** `YYYY-MM-DD` of a local date. */
export function isoDay(d: Date): string {
  const p = (n: number): string => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

/** `YYYY-MM-DD` of the three picks; null until all three are made or when the day does not exist (31 April). */
export function composeBirthDate(year: number | null, month: number | null, day: number | null): string | null {
  if (year === null || month === null || day === null) return null;
  const d = new Date(year, month - 1, day);
  if (d.getFullYear() !== year || d.getMonth() !== month - 1 || d.getDate() !== day) return null;
  return isoDay(d);
}

/** Whole years on `today` (both `YYYY-MM-DD`); null when `birth` is not a date. */
export function ageOn(birth: string, today: string): number | null {
  const b = /^(\d{4})-(\d{2})-(\d{2})$/.exec(birth);
  const t = /^(\d{4})-(\d{2})-(\d{2})$/.exec(today);
  if (!b || !t) return null;
  const [by, bm, bd] = [Number(b[1]), Number(b[2]), Number(b[3])];
  const [ty, tm, td] = [Number(t[1]), Number(t[2]), Number(t[3])];
  const check = new Date(by, bm - 1, bd);
  if (check.getFullYear() !== by || check.getMonth() !== bm - 1 || check.getDate() !== bd) return null;
  return ty - by - (tm < bm || (tm === bm && td < bd) ? 1 : 0);
}

/** The problem with `birth` on `today`, or null — fine. */
export function birthDateProblem(birth: string | null, today: string): BirthDateProblem | null {
  if (!birth) return 'required';
  const age = ageOn(birth, today);
  if (age === null) return 'format';
  if (birth > today) return 'future';
  if (age < BIRTH_MIN_AGE) return 'tooYoung';
  if (age > BIRTH_MAX_AGE) return 'tooOld';
  return null;
}

/** The server's `400 birthDate <reason>` as the form's problem. */
export function birthDateProblemOf(reason: string | null): BirthDateProblem {
  switch (reason) {
    case 'required':
      return 'required';
    case 'future':
      return 'future';
    case 'min':
      return 'tooYoung';
    case 'max':
      return 'tooOld';
    default:
      return 'format';
  }
}

/** Years a player may be born in, newest first: from `BIRTH_MIN_AGE` to `BIRTH_MAX_AGE` years ago. */
export function birthYears(today: Date): number[] {
  const y = today.getFullYear();
  return Array.from({ length: BIRTH_MAX_AGE - BIRTH_MIN_AGE + 1 }, (_, i) => y - BIRTH_MIN_AGE - i);
}

/** A member who still has to give the date (D-107): not a guest, signed in, none on the account. */
export function needsBirthDate(user: { role: string; birthDate?: string | null } | null | undefined): boolean {
  return !!user && user.role !== 'guest' && user.role !== 'admin' && !user.birthDate;
}
