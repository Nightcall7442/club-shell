/**
 * The date of birth as three pickers — day, month, year (D-107): no typing of a date format, and the browser's own
 * calendar is not kiosk-friendly. Years run from 5 to 100 years back, newest first; a day that does not exist in the
 * month (31 April) makes no date.
 */
import { useMemo } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { useLocale } from '@/hooks/useLocale';
import { birthYears, composeBirthDate } from '@/lib/birthDate';

export interface BirthDateParts {
  day: number | null;
  month: number | null;
  year: number | null;
}

export const EMPTY_BIRTH_DATE: BirthDateParts = { day: null, month: null, year: null };

/** `YYYY-MM-DD` of the picks, or null. */
export const birthDateOf = (p: BirthDateParts): string | null => composeBirthDate(p.year, p.month, p.day);

export interface BirthDatePickerProps {
  value: BirthDateParts;
  onChange: (next: BirthDateParts) => void;
  error?: string | null;
  disabled?: boolean;
  className?: string;
}

const SELECT =
  'field focus-ring h-14 w-full rounded-md px-3 text-lg text-text disabled:opacity-50 [&>option]:bg-surface [&>option]:text-text';

export function BirthDatePicker({ value, onChange, error, disabled, className }: BirthDatePickerProps): JSX.Element {
  const { t } = useTranslation();
  const { tag } = useLocale();
  const months = useMemo(() => {
    const f = new Intl.DateTimeFormat(tag, { month: 'long' });
    return Array.from({ length: 12 }, (_, i) => f.format(new Date(2000, i, 1)));
  }, [tag]);
  const years = useMemo(() => birthYears(new Date()), []);
  const num = (v: string): number | null => (v === '' ? null : Number(v));
  return (
    <div role="group" aria-label={t('birthDate.label')} className={clsx('flex flex-col gap-1.5', className)}>
      <span className="text-sm font-medium text-muted">{t('birthDate.label')}</span>
      <div className="grid grid-cols-[5.5rem_1fr_7rem] gap-3">
        <select
          name="birthDay"
          aria-label={t('birthDate.day')}
          data-nav="true"
          className={SELECT}
          disabled={disabled}
          value={value.day ?? ''}
          aria-invalid={Boolean(error)}
          onChange={(e) => onChange({ ...value, day: num(e.target.value) })}
        >
          <option value="">{t('birthDate.day')}</option>
          {Array.from({ length: 31 }, (_, i) => i + 1).map((d) => (
            <option key={d} value={d}>
              {d}
            </option>
          ))}
        </select>
        <select
          name="birthMonth"
          aria-label={t('birthDate.month')}
          data-nav="true"
          className={SELECT}
          disabled={disabled}
          value={value.month ?? ''}
          aria-invalid={Boolean(error)}
          onChange={(e) => onChange({ ...value, month: num(e.target.value) })}
        >
          <option value="">{t('birthDate.month')}</option>
          {months.map((m, i) => (
            <option key={m} value={i + 1}>
              {m}
            </option>
          ))}
        </select>
        <select
          name="birthYear"
          aria-label={t('birthDate.year')}
          data-nav="true"
          className={SELECT}
          disabled={disabled}
          value={value.year ?? ''}
          aria-invalid={Boolean(error)}
          onChange={(e) => onChange({ ...value, year: num(e.target.value) })}
        >
          <option value="">{t('birthDate.year')}</option>
          {years.map((y) => (
            <option key={y} value={y}>
              {y}
            </option>
          ))}
        </select>
      </div>
      {error && (
        <p role="alert" className="text-sm font-medium text-danger">
          {error}
        </p>
      )}
    </div>
  );
}
