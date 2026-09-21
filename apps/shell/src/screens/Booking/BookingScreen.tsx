import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import type { Booking, BookingSeatsResponse, Seat } from '@clubshell/contracts';
import { Badge, type BadgeTone } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { Skeleton } from '@/components/ui/Skeleton';
import { useGamepad } from '@/hooks/useGamepad';
import { useLocale } from '@/hooks/useLocale';
import { formatDate, formatDurationSec, formatTime } from '@/lib/format';
import { api } from '@/lib/tauri';
import { toDateKey } from '@/lib/time';
import { useAuthStore } from '@/store/auth';
import { useNotificationsStore } from '@/store/notifications';
import { useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';
import { isActiveBooking, isSeatSelectable, SeatMap } from './SeatMap';

const DAYS_AHEAD = 7;
const MAX_BOOKING_MINUTES = 8 * 60;
const DEFAULT_SLOT_MINUTES = 30;

const BOOKING_TONE: Record<Booking['status'], BadgeTone> = {
  reserved: 'accent',
  confirmed: 'success',
  cancelled: 'muted',
  expired: 'muted',
};

function parseHm(hm: string | null | undefined): [number, number] | null {
  if (!hm) {
    return null;
  }
  const m = /^(\d{1,2}):(\d{2})$/.exec(hm);
  if (!m) {
    return null;
  }
  return [Number(m[1]), Number(m[2])];
}

/** Slot start times of `dateKey` (local `YYYY-MM-DD`) inside the opening hours (`openTo` ≤ `openFrom` wraps past midnight). */
export function buildSlots(
  dateKey: string,
  slotMinutes: number,
  openFrom?: string | null,
  openTo?: string | null,
): Date[] {
  const [y, mo, d] = dateKey.split('-').map(Number);
  if (y === undefined || mo === undefined || d === undefined || Number.isNaN(y + mo + d)) {
    return [];
  }
  const from = parseHm(openFrom) ?? [0, 0];
  const to = parseHm(openTo) ?? [24, 0];
  const start = new Date(y, mo - 1, d, from[0], from[1]);
  const end = new Date(y, mo - 1, d, to[0], to[1]);
  if (end.getTime() <= start.getTime()) {
    end.setDate(end.getDate() + 1);
  }
  const step = Math.max(5, slotMinutes) * 60_000;
  const out: Date[] = [];
  for (let t = start.getTime(); t + step <= end.getTime(); t += step) {
    out.push(new Date(t));
  }
  return out;
}

function overlaps(fromMs: number, toMs: number, bookings: Booking[]): boolean {
  return bookings.some((b) => Date.parse(b.from) < toMs && Date.parse(b.to) > fromMs);
}

function CalendarIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <rect x="3" y="4" width="18" height="18" rx="2" />
      <path d="M16 2v4M8 2v4M3 10h18" />
    </svg>
  );
}

interface SlotPickerProps {
  label: string;
  options: Date[];
  value: number | null;
  onChange: (ms: number) => void;
  emptyText: string;
  format: (d: Date) => string;
}

function SlotPicker({ label, options, value, onChange, emptyText, format }: SlotPickerProps): JSX.Element {
  return (
    <div className="flex flex-col gap-2">
      <span className="text-sm font-medium text-muted">{label}</span>
      {options.length === 0 ? (
        <p className="text-sm text-muted">{emptyText}</p>
      ) : (
        <div
          role="group"
          aria-label={label}
          className="no-scrollbar flex max-h-[9.5rem] flex-wrap gap-2 overflow-y-auto"
        >
          {options.map((d) => {
            const ms = d.getTime();
            const active = ms === value;
            return (
              <button
                key={ms}
                type="button"
                data-nav="true"
                aria-pressed={active}
                onClick={() => onChange(ms)}
                className={clsx(
                  'focus-ring tnum h-11 min-w-[4.5rem] rounded-lg px-3 text-base font-semibold transition-colors duration-[var(--dur-fast)]',
                  active
                    ? 'bg-primary text-on-primary shadow-[var(--shadow-glow)]'
                    : 'glass text-text hover:bg-surface/80',
                )}
              >
                {format(d)}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}

/** Seat booking: pick a day, a seat on the club map and a slot-aligned time range; manage own bookings. */
export default function BookingScreen(): JSX.Element {
  const { t } = useTranslation();
  const { locale, tag } = useLocale();
  const navigate = useNavigate();
  const animations = useThemeStore((s) => s.theme.animations);
  const meId = useAuthStore((s) => s.user?.id ?? null);
  const thisPcId = useSettingsStore((s) => s.pcInfo?.pc.id ?? null);
  const defaultRoute = useSettingsStore((s) => s.shellConfig?.ui.defaultRoute ?? '/home');
  const pushError = useNotificationsStore((s) => s.pushError);
  const push = useNotificationsStore((s) => s.push);

  const days = useMemo(() => {
    const today = new Date();
    return Array.from({ length: DAYS_AHEAD }, (_, i) => {
      const d = new Date(today.getFullYear(), today.getMonth(), today.getDate() + i);
      return { key: toDateKey(d), date: d };
    });
  }, []);

  const [dateKey, setDateKey] = useState(days[0]?.key ?? toDateKey(new Date()));
  const [data, setData] = useState<BookingSeatsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  const [selected, setSelected] = useState<Seat | null>(null);
  const [fromMs, setFromMs] = useState<number | null>(null);
  const [toMs, setToMs] = useState<number | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [reserving, setReserving] = useState(false);
  const [cancellingId, setCancellingId] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const todayChipRef = useRef<HTMLButtonElement | null>(null);

  const reload = useCallback(
    async (key: string): Promise<void> => {
      setLoading(true);
      setFailed(false);
      try {
        const res = await api.booking.seats(key);
        setData(res);
      } catch (e) {
        setFailed(true);
        pushError(e, t('booking.title'));
      } finally {
        setLoading(false);
      }
    },
    [pushError, t],
  );

  useEffect(() => {
    void reload(dateKey);
  }, [dateKey, reload]);

  useEffect(() => {
    todayChipRef.current?.focus();
  }, []);

  // Past slots drop out as the clock advances.
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 60_000);
    return () => window.clearInterval(id);
  }, []);

  const slotMinutes = data?.slotMinutes ?? DEFAULT_SLOT_MINUTES;
  const slotMs = slotMinutes * 60_000;
  const bookings = data?.bookings ?? [];

  const seatBookings = useMemo(
    () => (selected ? bookings.filter((b) => b.pcId === selected.pcId && isActiveBooking(b)) : []),
    [bookings, selected],
  );

  const fromOptions = useMemo(() => {
    if (!selected || !data) {
      return [];
    }
    return buildSlots(data.date, slotMinutes, data.openFrom, data.openTo).filter((d) => {
      const ms = d.getTime();
      return ms > now && !overlaps(ms, ms + slotMs, seatBookings);
    });
  }, [selected, data, slotMinutes, now, slotMs, seatBookings]);

  const closingMs = useMemo(() => {
    if (!data) {
      return Number.POSITIVE_INFINITY;
    }
    const slots = buildSlots(data.date, slotMinutes, data.openFrom, data.openTo);
    const last = slots[slots.length - 1];
    return last ? last.getTime() + slotMs : Number.POSITIVE_INFINITY;
  }, [data, slotMinutes, slotMs]);

  const toOptions = useMemo(() => {
    if (fromMs === null) {
      return [];
    }
    const out: Date[] = [];
    const maxEnd = Math.min(fromMs + MAX_BOOKING_MINUTES * 60_000, closingMs);
    for (let end = fromMs + slotMs; end <= maxEnd; end += slotMs) {
      if (overlaps(fromMs, end, seatBookings)) {
        break;
      }
      out.push(new Date(end));
    }
    return out;
  }, [fromMs, slotMs, closingMs, seatBookings]);

  const selectSeat = (seat: Seat): void => {
    setSelected((prev) => (prev?.pcId === seat.pcId ? null : seat));
    setFromMs(null);
    setToMs(null);
  };

  const pickFrom = (ms: number): void => {
    setFromMs(ms);
    setToMs(null);
  };

  const changeDate = (key: string): void => {
    setDateKey(key);
    setSelected(null);
    setFromMs(null);
    setToMs(null);
  };

  const myBookings = useMemo(
    () =>
      bookings
        .filter((b) => meId !== null && b.userId === meId)
        .sort((a, b) => Date.parse(a.from) - Date.parse(b.from)),
    [bookings, meId],
  );

  const seatName = (pcId: string): string => data?.seats.find((s) => s.pcId === pcId)?.name ?? pcId;
  const fmtTime = (d: Date): string => formatTime(d.toISOString(), locale);
  const canReserve = Boolean(selected && fromMs !== null && toMs !== null && !reserving && meId);

  const reserve = async (): Promise<void> => {
    if (!selected || fromMs === null || toMs === null) {
      return;
    }
    setReserving(true);
    try {
      await api.booking.reserve(selected.pcId, new Date(fromMs).toISOString(), new Date(toMs).toISOString());
      push({
        title: t('booking.reserved'),
        body: t('booking.confirmText', {
          seat: selected.name,
          date: formatDate(new Date(fromMs).toISOString(), locale),
          from: fmtTime(new Date(fromMs)),
          to: fmtTime(new Date(toMs)),
        }),
        level: 'success',
      });
      setConfirmOpen(false);
      setSelected(null);
      setFromMs(null);
      setToMs(null);
      await reload(dateKey);
    } catch (e) {
      pushError(e, t('booking.reserve'));
      setConfirmOpen(false);
    } finally {
      setReserving(false);
    }
  };

  const cancel = async (b: Booking): Promise<void> => {
    setCancellingId(b.id);
    try {
      await api.booking.cancel(b.id);
      push({
        title: t('booking.cancelled'),
        body: `${seatName(b.pcId)} · ${t('booking.timeRange', { from: formatTime(b.from, locale), to: formatTime(b.to, locale) })}`,
        level: 'info',
      });
      await reload(dateKey);
    } catch (e) {
      pushError(e, t('booking.cancel'));
    } finally {
      setCancellingId(null);
    }
  };

  useGamepad({
    onBack: () => (confirmOpen ? setConfirmOpen(false) : navigate(defaultRoute)),
    onTab: (dir) => {
      const i = days.findIndex((d) => d.key === dateKey);
      const next = days[dir === 'next' ? i + 1 : i - 1];
      if (next) {
        changeDate(next.key);
      }
    },
  });

  const freeCount = data ? data.seats.filter((s) => s.status === 'free').length : 0;
  const weekday = new Intl.DateTimeFormat(tag, { weekday: 'short' });
  const dayMonth = new Intl.DateTimeFormat(tag, { day: 'numeric', month: 'short' });

  return (
    <motion.div
      initial={{ opacity: 0, y: 12 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: animations ? 0.2 : 0, ease: 'easeOut' }}
      className="grid h-full min-h-0 grid-cols-[1fr_clamp(20rem,26vw,28rem)] gap-[var(--gap)]"
    >
      <section className="flex min-h-0 flex-col gap-4">
        <header className="flex flex-wrap items-end justify-between gap-3">
          <div>
            <h1 className="text-3xl font-bold text-text">{t('booking.title')}</h1>
            <p className="text-muted">{t('booking.subtitle')}</p>
          </div>
          {data && (
            <div className="flex items-center gap-3 text-sm text-muted">
              {data.openFrom && data.openTo && (
                <span>{t('booking.openHours', { from: data.openFrom, to: data.openTo })}</span>
              )}
              <span>·</span>
              <span>{t('booking.slotMinutes', { minutes: slotMinutes })}</span>
              <span>·</span>
              <span className="text-success">{t('booking.freeSeats', { count: freeCount })}</span>
            </div>
          )}
        </header>

        <div role="group" aria-label={t('booking.chooseDate')} className="no-scrollbar flex gap-2 overflow-x-auto pb-1">
          {days.map((d, i) => {
            const active = d.key === dateKey;
            const label = i === 0 ? t('common.today') : i === 1 ? t('common.tomorrow') : weekday.format(d.date);
            return (
              <button
                key={d.key}
                ref={i === 0 ? todayChipRef : undefined}
                type="button"
                data-nav="true"
                aria-pressed={active}
                onClick={() => changeDate(d.key)}
                className={clsx(
                  'focus-ring flex h-16 min-w-[6.5rem] shrink-0 flex-col items-center justify-center rounded-xl px-4 transition-colors duration-[var(--dur-fast)]',
                  active
                    ? 'bg-primary text-on-primary shadow-[var(--shadow-glow)]'
                    : 'glass text-text hover:bg-surface/80',
                )}
              >
                <span className="text-xs font-medium uppercase tracking-wide opacity-80">{label}</span>
                <span className="tnum text-lg font-bold leading-tight">{dayMonth.format(d.date)}</span>
              </button>
            );
          })}
        </div>

        <div className="glass no-scrollbar min-h-0 flex-1 overflow-y-auto rounded-2xl p-5">
          {loading && !data ? (
            <div className="grid grid-cols-6 gap-3" aria-hidden="true">
              {Array.from({ length: 24 }, (_, i) => (
                <Skeleton key={i} variant="rect" className="aspect-square rounded-xl" />
              ))}
            </div>
          ) : failed && !data ? (
            <div className="flex h-full flex-col items-center justify-center gap-4 text-center">
              <p className="text-lg text-muted">{t('errors.generic')}</p>
              <Button variant="secondary" onClick={() => void reload(dateKey)}>
                {t('common.retry')}
              </Button>
            </div>
          ) : data ? (
            <SeatMap
              seats={data.seats}
              bookings={data.bookings}
              meId={meId}
              thisPcId={thisPcId}
              selectedPcId={selected?.pcId ?? null}
              onSelect={selectSeat}
              disabled={loading}
            />
          ) : null}
        </div>
      </section>

      <aside className="flex min-h-0 flex-col gap-4">
        <div className="glass flex flex-col gap-4 rounded-2xl p-5">
          <h2 className="flex items-center gap-2 text-xl font-semibold text-text">
            <span className="inline-flex h-6 w-6 text-primary [&>svg]:h-full [&>svg]:w-full" aria-hidden="true">
              <CalendarIcon />
            </span>
            {selected ? t('booking.selectedSeat', { name: selected.name }) : t('booking.selectSeat')}
          </h2>
          {!selected ? (
            <p className="text-muted">{t('booking.selectSeatHint')}</p>
          ) : !isSeatSelectable(selected) ? (
            <p className="text-danger">{t('booking.unavailable')}</p>
          ) : (
            <>
              <p className="text-sm text-muted">
                {t('booking.zone')}: <span className="text-text">{selected.zone}</span> ·{' '}
                {t(`booking.status.${selected.status}`)}
              </p>
              <SlotPicker
                label={t('booking.from')}
                options={fromOptions}
                value={fromMs}
                onChange={pickFrom}
                emptyText={t('booking.noSlots')}
                format={fmtTime}
              />
              {fromMs !== null && (
                <SlotPicker
                  label={t('booking.to')}
                  options={toOptions}
                  value={toMs}
                  onChange={setToMs}
                  emptyText={t('booking.noSlots')}
                  format={fmtTime}
                />
              )}
              {fromMs !== null && toMs !== null && (
                <p className="tnum text-sm text-muted">
                  {t('booking.duration')}:{' '}
                  <span className="text-text">{formatDurationSec((toMs - fromMs) / 1000)}</span>
                </p>
              )}
              <Button size="lg" block disabled={!canReserve} onClick={() => setConfirmOpen(true)}>
                {t('booking.reserve')}
              </Button>
            </>
          )}
        </div>

        <div className="glass flex min-h-0 flex-1 flex-col rounded-2xl p-5">
          <h2 className="mb-3 text-xl font-semibold text-text">{t('booking.myBookings')}</h2>
          {myBookings.length === 0 ? (
            <p className="text-muted">{t('booking.noBookings')}</p>
          ) : (
            <ul role="list" className="no-scrollbar flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto">
              {myBookings.map((b) => {
                const active = isActiveBooking(b) && Date.parse(b.to) > now;
                return (
                  <li key={b.id} className="flex items-center gap-3 rounded-xl bg-surface/50 px-4 py-3">
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2">
                        <span className="font-semibold text-text">{seatName(b.pcId)}</span>
                        <Badge tone={BOOKING_TONE[b.status]} size="sm">
                          {t(`booking.bookingStatus.${b.status}`)}
                        </Badge>
                      </div>
                      <div className="tnum text-sm text-muted">
                        {formatDate(b.from, locale)} ·{' '}
                        {t('booking.timeRange', { from: formatTime(b.from, locale), to: formatTime(b.to, locale) })}
                      </div>
                    </div>
                    {active && (
                      <Button
                        variant="ghost"
                        size="md"
                        loading={cancellingId === b.id}
                        onClick={() => void cancel(b)}
                        className="text-danger"
                      >
                        {t('common.cancel')}
                      </Button>
                    )}
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      </aside>

      <Modal
        open={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        title={t('booking.confirmTitle')}
        size="sm"
        footer={
          <div className="flex justify-end gap-3">
            <Button variant="secondary" onClick={() => setConfirmOpen(false)} disabled={reserving}>
              {t('common.cancel')}
            </Button>
            <Button onClick={() => void reserve()} loading={reserving}>
              {t('booking.reserve')}
            </Button>
          </div>
        }
      >
        {selected && fromMs !== null && toMs !== null && (
          <p className="text-lg text-text">
            {t('booking.confirmText', {
              seat: selected.name,
              date: formatDate(new Date(fromMs).toISOString(), locale),
              from: fmtTime(new Date(fromMs)),
              to: fmtTime(new Date(toMs)),
            })}
          </p>
        )}
      </Modal>
    </motion.div>
  );
}
