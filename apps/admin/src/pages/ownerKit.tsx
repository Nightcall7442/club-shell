/**
 * Pieces the owner's setup and business pages share in variant F: the page column (at most 1280 wide), the caption
 * of a group of buttons (chips, a segmented choice) — a group, not a `<label>`, since a label would press its first
 * button when its caption is clicked — and the 7 / 30 / 90-day period chips of the reports, control and network.
 */
import type { ReactNode } from 'react';
import clsx from 'clsx';
import { t } from '@/i18n';
import { Chip } from '@/ui';

/** The column every owner page sits in: 1280 wide at most, its blocks 20 apart. */
export function OwnerPage({ children, className }: { children: ReactNode; className?: string }): JSX.Element {
  return <div className={clsx('flex min-w-0 max-w-[1280px] flex-col gap-5', className)}>{children}</div>;
}

/**
 * A captioned group of buttons in a form: the mono caption of a field (`Field` look), the buttons, a hint below. The
 * caption names the group (`role="group"`).
 */
export function FieldGroup({
  label,
  hint,
  aside,
  children,
  className,
}: {
  label: string;
  hint?: ReactNode;
  /** Right side of the caption row (a tool: «Добавить»). */
  aside?: ReactNode;
  children: ReactNode;
  className?: string;
}): JSX.Element {
  return (
    <div role="group" aria-label={label} className={clsx('flex min-w-0 flex-col gap-2', className)}>
      <span className="flex min-h-3 items-center justify-between gap-3">
        <span className="label-sm">{label}</span>
        {aside}
      </span>
      {children}
      {hint && <span className="text-[11.5px] leading-4 text-muted">{hint}</span>}
    </div>
  );
}

export const PERIODS = [7, 30, 90] as const;

/** «7 дней · 30 дней · 90 дней»: the period of a report, as filter chips (`aria-pressed`). */
export function PeriodChips({ value, onChange }: { value: number; onChange: (days: number) => void }): JSX.Element {
  return (
    <div role="group" aria-label={t('Период')} className="flex items-center gap-1">
      {PERIODS.map((p) => (
        <Chip key={p} pressed={value === p} onClick={() => onChange(p)}>
          {t('{n} дней', { n: p })}
        </Chip>
      ))}
    </div>
  );
}
