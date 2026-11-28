/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
  ageOn,
  birthDateProblem,
  birthDateProblemOf,
  birthYears,
  composeBirthDate,
  needsBirthDate,
} from './birthDate.ts';

describe('the date of birth (D-107)', () => {
  it('composes the three picks into a real date only', () => {
    assert.equal(composeBirthDate(2008, 2, 29), '2008-02-29');
    assert.equal(composeBirthDate(2009, 2, 29), null);
    assert.equal(composeBirthDate(2009, 4, 31), null);
    assert.equal(composeBirthDate(null, 4, 1), null);
  });

  it('counts whole years on the date', () => {
    assert.equal(ageOn('2008-11-29', '2026-11-28'), 17);
    assert.equal(ageOn('2008-11-29', '2026-11-29'), 18);
    assert.equal(ageOn('2008-02-30', '2026-11-29'), null);
  });

  it('refuses what the server refuses', () => {
    const today = '2026-11-28';
    assert.equal(birthDateProblem(null, today), 'required');
    assert.equal(birthDateProblem('2027-01-01', today), 'future');
    assert.equal(birthDateProblem('2023-01-01', today), 'tooYoung');
    assert.equal(birthDateProblem('1920-01-01', today), 'tooOld');
    assert.equal(birthDateProblem('2010-05-05', today), null);
    assert.equal(birthDateProblemOf('min'), 'tooYoung');
    assert.equal(birthDateProblemOf('weird'), 'format');
  });

  it('offers the years of 5 to 100 years ago and asks members only', () => {
    const years = birthYears(new Date(2026, 10, 28));
    assert.deepEqual([years[0], years.at(-1), years.length], [2021, 1926, 96]);
    assert.equal(needsBirthDate({ role: 'member', birthDate: null }), true);
    assert.equal(needsBirthDate({ role: 'member', birthDate: '2001-01-01' }), false);
    assert.equal(needsBirthDate({ role: 'guest' }), false);
    assert.equal(needsBirthDate(null), false);
  });
});
