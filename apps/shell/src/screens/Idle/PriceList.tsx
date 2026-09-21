import { useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import {
  tariffWindowContains,
  WEEKDAYS,
  weekdayFromJsDay,
  type Locale,
  type Tariff,
  type TariffTimeWindow,
} from '@clubshell/contracts';
import { Badge } from '@/components/ui/Badge';
import { Skeleton } from '@/components/ui/Skeleton';
import { LOCALE_TAGS } from '@/i18n';
import { useLocale } from '@/hooks/useLocale';
import { formatMoney } from '@/lib/format';
import { formatClock, serverNow } from '@/lib/time';
import { useWalletStore } from '@/store/wallet';

export interface PriceListProps {
  /** Tariffs to show; defaults to the wallet store (loaded on mount when empty). */
  tariffs?: Tariff[];
  /** Smaller type and tighter spacing (side panel). */
  compact?: boolean;
  className?: string;
}

/** 2024-01-01 is a Monday; used to render localized weekday names. */
const MONDAY_MS = Date.UTC(2024, 0, 1, 12);

/** Localized short weekday names, Monday first. */
export function weekdayNames(locale: Locale): string[] {
  const fmt = new Intl.DateTimeFormat(LOCALE_TAGS[locale], { weekday: 'short', timeZone: 'UTC' });
  return WEEKDAYS.map((_, i) => fmt.format(new Date(MONDAY_MS + i * 86_400_000)));
}

/** `Mon–Fri`, `Sat, Sun` or `Mon–Sun` for the days of a window. */
export function describeDays(days: readonly string[], locale: Locale): string {
  const names = weekdayNames(locale);
  const idx = WEEKDAYS.map((d, i) => (days.includes(d) ? i : -1)).filter((i) => i >= 0);
  if (idx.length === 0) {
    return '';
  }
  const consecutive = idx.every((v, i) => i === 0 || v === (idx[i - 1] ?? -2) + 1);
  const first = names[idx[0] ?? 0] ?? '';
  const last = names[idx[idx.length - 1] ?? 0] ?? '';
  if (consecutive && idx.length > 2) {
    return `${first}–${last}`;
  }
  return idx.map((i) => names[i] ?? '').join(', ');
}

/** `Valid Mon–Sun 22:00–06:00`. */
export function describeWindow(w: TariffTimeWindow, locale: Locale, t: TFunction): string {
  return t('wallet.timeWindow', { days: describeDays(w.days, locale), from: w.from, to: w.to });
}

/** `true` when the tariff has no windows or one of them contains the current club time. */
export function tariffAvailableNow(tariff: Tariff, now: Date): boolean {
  if (tariff.timeWindows.length === 0) {
    return true;
  }
  const day = weekdayFromJsDay(now.getDay());
  const hhmm = formatClock(now, 'HH:mm');
  return tariff.timeWindows.some((w) => w.days.length > 0 && tariffWindowContains(w, day, hhmm));
}

export interface ZoneGroup {
  /** Zones the group applies to, joined with " · "; null = every zone. */
  zone: string | null;
  tariffs: Tariff[];
}

/** Groups tariffs by their zone set (each tariff appears once); zone-less tariffs come first under `null`. */
export function groupByZone(tariffs: readonly Tariff[]): ZoneGroup[] {
  const map = new Map<string | null, Tariff[]>();
  for (const tariff of tariffs) {
    const key = tariff.zones.length > 0 ? tariff.zones.join(' · ') : null;
    const list = map.get(key) ?? [];
    list.push(tariff);
    map.set(key, list);
  }
  return Array.from(map.entries())
    .map(([zone, list]) => ({ zone, tariffs: list }))
    .sort((a, b) => (a.zone === null ? -1 : b.zone === null ? 1 : a.zone.localeCompare(b.zone)));
}

/** Big-type price board of the club's tariffs, grouped by zone, with validity windows and an "available now" mark. */
export function PriceList({ tariffs, compact = false, className }: PriceListProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const stored = useWalletStore((s) => s.tariffs);
  const loadTariffs = useWalletStore((s) => s.loadTariffs);
  const [loading, setLoading] = useState(false);
  const [now, setNow] = useState(() => serverNow());

  const list = tariffs ?? stored;

  useEffect(() => {
    if (tariffs !== undefined || stored.length > 0) {
      return;
    }
    let active = true;
    setLoading(true);
    void loadTariffs().finally(() => {
      if (active) {
        setLoading(false);
      }
    });
    return () => {
      active = false;
    };
  }, [tariffs, stored.length, loadTariffs]);

  // Minute resolution is enough for window boundaries.
  useEffect(() => {
    const id = setInterval(() => setNow(serverNow()), 60_000);
    return () => clearInterval(id);
  }, []);

  const groups = useMemo(() => groupByZone(list), [list]);

  return (
    <section
      aria-label={t('idle.priceList')}
      className={clsx('glass flex flex-col rounded-2xl', compact ? 'gap-3 p-5' : 'gap-5 p-7', className)}
    >
      <h2 className={clsx('font-black uppercase tracking-[0.15em] text-text', compact ? 'text-lg' : 'text-2xl')}>
        {t('idle.tariffsTitle')}
      </h2>

      {loading && list.length === 0 ? (
        <div className="flex flex-col gap-3">
          {[0, 1, 2].map((i) => (
            <Skeleton key={i} height={compact ? '3.5rem' : '4.5rem'} className="rounded-lg" />
          ))}
        </div>
      ) : list.length === 0 ? (
        <p className="text-base text-muted">{t('idle.noTariffs')}</p>
      ) : (
        groups.map((group) => (
          <div key={group.zone ?? '*'} className="flex flex-col gap-2">
            <h3 className={clsx('font-semibold uppercase tracking-wide text-muted', compact ? 'text-xs' : 'text-sm')}>
              {group.zone ?? t('idle.allZones')}
            </h3>
            <ul role="list" className="flex flex-col gap-2">
              {group.tariffs.map((tariff) => {
                const available = tariffAvailableNow(tariff, now);
                const price =
                  tariff.isPackage && tariff.packagePrice
                    ? t('idle.packageFor', {
                        minutes: tariff.packageMinutes ?? tariff.minMinutes,
                        price: formatMoney(tariff.packagePrice, locale),
                      })
                    : t('idle.perHour', { price: formatMoney(tariff.pricePerHour, locale) });
                return (
                  <li
                    key={tariff.id}
                    className={clsx(
                      'flex items-center justify-between gap-4 rounded-lg px-4',
                      compact ? 'py-2' : 'py-3',
                      available ? 'bg-text/5' : 'bg-text/[0.03] opacity-60',
                    )}
                  >
                    <div className="min-w-0">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className={clsx('font-bold text-text', compact ? 'text-lg' : 'text-xl')}>
                          {tariff.name}
                        </span>
                        {available && tariff.timeWindows.length > 0 && (
                          <Badge tone="success" size="sm" dot>
                            {t('idle.availableNow')}
                          </Badge>
                        )}
                      </div>
                      {tariff.timeWindows.length > 0 && (
                        <p className={clsx('text-muted', compact ? 'text-xs' : 'text-sm')}>
                          {tariff.timeWindows.map((w) => describeWindow(w, locale, t)).join(' · ')}
                        </p>
                      )}
                    </div>
                    <span
                      className={clsx(
                        'tnum shrink-0 whitespace-nowrap font-black text-primary',
                        compact ? 'text-xl' : 'text-2xl',
                      )}
                    >
                      {price}
                    </span>
                  </li>
                );
              })}
            </ul>
          </div>
        ))
      )}
    </section>
  );
}

export default PriceList;
