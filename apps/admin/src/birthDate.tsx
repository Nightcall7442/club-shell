/**
 * The client's date of birth at the desk (D-107): the field of the new-client form and of the client card, its age and the
 * minors' «до 18» badge (D-108). The server checks the same rules on the club's date; the browser's date is close enough
 * to say so before the request.
 */
import { t } from '@/i18n';
import { Badge, Field, Input } from '@/ui';

export const BIRTH_MIN_AGE = 5;
export const BIRTH_MAX_AGE = 100;

/** `YYYY-MM-DD` of a local date. */
export function isoDate(d: Date): string {
  const p = (n: number): string => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

/** Whole years on `today` (`YYYY-MM-DD` both); null when `birth` is not a date. */
export function ageOn(birth: string, today: string = isoDate(new Date())): number | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(birth);
  const n = /^(\d{4})-(\d{2})-(\d{2})$/.exec(today);
  if (!m || !n) return null;
  const [by, bm, bd] = [Number(m[1]), Number(m[2]), Number(m[3])];
  const [ty, tm, td] = [Number(n[1]), Number(n[2]), Number(n[3])];
  const check = new Date(by, bm - 1, bd);
  if (check.getFullYear() !== by || check.getMonth() !== bm - 1 || check.getDate() !== bd) return null;
  return ty - by - (tm < bm || (tm === bm && td < bd) ? 1 : 0);
}

/** What is wrong with a typed date of birth, as the server would say it; null — fine. */
export function birthDateProblem(value: string, today: string = isoDate(new Date())): string | null {
  if (!value) return t('Укажите дату рождения клиента');
  const age = ageOn(value, today);
  if (age === null) return t('Дата рождения — в виде ДД.ММ.ГГГГ');
  if (value > today) return t('Дата рождения не может быть в будущем');
  if (age < BIRTH_MIN_AGE) return t('Клиенту должно быть не меньше 5 лет');
  if (age > BIRTH_MAX_AGE) return t('Проверьте год рождения: больше 100 лет');
  return null;
}

/** «до 18»: a minor by the club's age (D-108). */
export function MinorBadge({ minorAge }: { minorAge: number }): JSX.Element {
  return (
    <Badge tone="warn" title={t('Несовершеннолетний: ночью играть нельзя')}>
      <span data-minor-badge>{t('до {n}', { n: minorAge })}</span>
    </Badge>
  );
}

/**
 * The date input with the age beside it and the «до 18» badge for a minor. `missing` — an older account without a date
 * (the card says so in amber until the cashier fills it); `error` — the form's own refusal.
 */
export function BirthDateField({
  value,
  onChange,
  minorAge,
  missing = false,
  error = null,
}: {
  value: string;
  onChange: (v: string) => void;
  minorAge: number;
  missing?: boolean;
  error?: string | null;
}): JSX.Element {
  const today = isoDate(new Date());
  const age = value ? ageOn(value, today) : null;
  const shown = age !== null && age >= 0 && value <= today;
  return (
    <div className="flex flex-col gap-1.5">
      <Field label={t('Дата рождения')}>
        <Input
          type="date"
          data-birth-date
          value={value}
          min="1900-01-01"
          max={today}
          className="tnum"
          aria-invalid={Boolean(error) || missing}
          onChange={(e) => onChange(e.target.value)}
        />
      </Field>
      <div className="flex min-h-5 flex-wrap items-center gap-2 text-[11.5px] leading-4">
        {shown && (
          <span data-age className="tnum text-dim">
            {t('возраст {n}', { n: age })}
          </span>
        )}
        {shown && age < minorAge && <MinorBadge minorAge={minorAge} />}
        {missing && !value && <span className="text-warning">{t('Дата рождения не указана')}</span>}
        {error && <span className="text-danger">{error}</span>}
      </div>
    </div>
  );
}
