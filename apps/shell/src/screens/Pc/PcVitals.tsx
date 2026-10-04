/**
 * The vitals chip of the top bar, on every screen: CPU and GPU temperature (their load where the PC has no sensor) and
 * the frame rate while a game reports it, red from 85 °C. Read-only and silent (no live region: it changes every few
 * seconds). Nothing until the first sample. A crowded bar — below 2xl, or more than six section tabs — keeps one
 * reading (`leadVital`); the rest stay in the tooltip.
 */
import { useMemo } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { useSettingsStore } from '@/store/settings';
import { leadVital, vitalsOf, type Vital } from './vitals';

export function PcVitals({ compact = false }: { compact?: boolean }): JSX.Element | null {
  const { t } = useTranslation();
  const metrics = useSettingsStore((s) => s.metrics);
  const vitals = useMemo(() => (metrics ? vitalsOf(metrics) : []), [metrics]);
  const lead = leadVital(vitals);

  if (!lead) {
    return null;
  }

  const value = (v: Vital): string =>
    v.unit === 'temp'
      ? t('pcDisplay.vitals.temp', { value: v.value })
      : v.unit === 'load'
        ? t('pcDisplay.vitals.load', { value: v.value })
        : String(v.value);
  const reading = (v: Vital): string =>
    `${t(`pcDisplay.vitals.${v.key}`)} ${value(v)}${v.hot ? ` (${t('pcDisplay.vitals.hot')})` : ''}`;

  return (
    <div
      role="group"
      aria-label={t('pcDisplay.vitals.title')}
      title={vitals.map(reading).join(' · ')}
      className="flex h-9 shrink-0 items-center gap-3 rounded-md px-2.5 shadow-[inset_0_0_0_1px_var(--hairline)]"
    >
      {vitals.map((v) => (
        <span
          key={v.key}
          className={clsx(
            'items-baseline gap-1.5 whitespace-nowrap',
            v === lead ? 'flex' : compact ? 'hidden' : 'hidden 2xl:flex',
          )}
        >
          <span className="hud-label">{t(`pcDisplay.vitals.${v.key}`)}</span>
          <span className={clsx('num-dot text-base leading-none', v.hot ? 'text-danger' : 'text-text')}>
            {value(v)}
          </span>
          {v.hot && <span className="sr-only">{t('pcDisplay.vitals.hot')}</span>}
        </span>
      ))}
    </div>
  );
}
