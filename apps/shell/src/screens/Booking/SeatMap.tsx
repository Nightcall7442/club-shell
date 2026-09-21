import { useMemo } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import type { Booking, PcStatus, Seat } from '@clubshell/contracts';
import { Tooltip } from '@/components/ui/Tooltip';

export interface SeatMapProps {
  seats: Seat[];
  /** Bookings of the displayed day (other users' rows carry the anonymous user id). */
  bookings: Booking[];
  meId: string | null;
  /** Highlights the PC the shell runs on. */
  thisPcId?: string | null;
  selectedPcId: string | null;
  onSelect: (seat: Seat) => void;
  disabled?: boolean;
  className?: string;
}

/** Seats that can never be reserved. */
export function isSeatSelectable(seat: Seat): boolean {
  return seat.status !== 'maintenance' && seat.status !== 'offline';
}

const ACTIVE_BOOKING: ReadonlySet<Booking['status']> = new Set(['reserved', 'confirmed']);

/** `true` when the booking still occupies its slot. */
export function isActiveBooking(b: Booking): boolean {
  return ACTIVE_BOOKING.has(b.status);
}

const STATUS_CLASS: Record<PcStatus, string> = {
  free: 'bg-success/15 text-success border-success/50',
  busy: 'bg-danger/15 text-danger border-danger/50',
  booked: 'bg-accent/15 text-accent border-accent/50',
  locked: 'bg-muted/15 text-muted border-muted/40',
  maintenance: 'bg-muted/10 text-muted/70 border-dashed border-muted/40',
  offline: 'bg-muted/10 text-muted/60 border-muted/30',
};

const STATUS_DOT: Record<PcStatus, string> = {
  free: 'bg-success',
  busy: 'bg-danger',
  booked: 'bg-accent',
  locked: 'bg-muted',
  maintenance: 'bg-muted/60',
  offline: 'bg-muted/40',
};

const ZONE_CLASS = [
  'text-primary border-primary/40',
  'text-accent border-accent/40',
  'text-success border-success/40',
  'text-muted border-muted/40',
] as const;

const LEGEND: readonly PcStatus[] = ['free', 'busy', 'booked', 'locked', 'maintenance', 'offline'];

function MonitorIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-[1.6em] w-[1.6em]"
    >
      <rect x="2" y="4" width="20" height="13" rx="2" />
      <path d="M8 21h8M12 17v4" />
    </svg>
  );
}

/** Club floor plan on a CSS grid driven by `Seat.x/y`; every seat is a focusable button (spatial navigation works). */
export function SeatMap({
  seats,
  bookings,
  meId,
  thisPcId = null,
  selectedPcId,
  onSelect,
  disabled = false,
  className,
}: SeatMapProps): JSX.Element {
  const { t } = useTranslation();

  const { cols, rows, zones, mineByPc, othersByPc } = useMemo(() => {
    let maxX = 0;
    let maxY = 0;
    const zoneMap = new Map<string, { minY: number; maxY: number; index: number }>();
    for (const s of seats) {
      maxX = Math.max(maxX, s.x);
      maxY = Math.max(maxY, s.y);
      const z = zoneMap.get(s.zone);
      if (z) {
        z.minY = Math.min(z.minY, s.y);
        z.maxY = Math.max(z.maxY, s.y);
      } else {
        zoneMap.set(s.zone, { minY: s.y, maxY: s.y, index: zoneMap.size });
      }
    }
    const mine = new Set<string>();
    const others = new Set<string>();
    for (const b of bookings) {
      if (!isActiveBooking(b)) {
        continue;
      }
      (meId !== null && b.userId === meId ? mine : others).add(b.pcId);
    }
    return { cols: maxX + 1, rows: maxY + 1, zones: Array.from(zoneMap.entries()), mineByPc: mine, othersByPc: others };
  }, [seats, bookings, meId]);

  return (
    <div className={clsx('flex flex-col gap-4', className)}>
      <div
        role="group"
        aria-label={t('booking.map')}
        className="no-scrollbar overflow-x-auto [--seat:clamp(3.75rem,5.2vw,6rem)]"
      >
        <div
          className="grid gap-[clamp(0.4rem,0.6vw,0.75rem)]"
          style={{
            gridTemplateColumns: `auto repeat(${cols}, var(--seat))`,
            gridTemplateRows: `repeat(${rows}, var(--seat))`,
          }}
        >
          {zones.map(([zone, z]) => (
            <div
              key={zone}
              role="presentation"
              style={{ gridColumn: 1, gridRow: `${z.minY + 1} / ${z.maxY + 2}` }}
              className={clsx(
                'flex items-center justify-center rounded-lg border-l-4 px-2 text-sm font-bold uppercase tracking-widest [writing-mode:vertical-rl] [transform:rotate(180deg)]',
                ZONE_CLASS[z.index % ZONE_CLASS.length],
              )}
            >
              {zone}
            </div>
          ))}
          {seats.map((s) => {
            const selectable = isSeatSelectable(s) && !disabled;
            const selected = s.pcId === selectedPcId;
            const mine = mineByPc.has(s.pcId);
            const other = othersByPc.has(s.pcId);
            const status = t(`booking.status.${s.status}`);
            const label = t('booking.seatLabel', { name: s.name, zone: s.zone, status });
            return (
              <div key={s.pcId} style={{ gridColumn: s.x + 2, gridRow: s.y + 1 }} className="flex">
                <Tooltip
                  content={
                    <span className="flex flex-col gap-0.5">
                      <span className="font-semibold">{s.name}</span>
                      <span className="text-muted">
                        {s.zone} · {status}
                      </span>
                      {mine && <span className="text-primary">{t('booking.mine')}</span>}
                      {!mine && other && <span className="text-accent">{t('booking.other')}</span>}
                      {s.pcId === thisPcId && <span className="text-success">{t('booking.thisPc')}</span>}
                    </span>
                  }
                >
                  <button
                    type="button"
                    data-nav={selectable ? 'true' : 'off'}
                    disabled={!selectable}
                    aria-label={label}
                    aria-pressed={selected}
                    onClick={() => onSelect(s)}
                    className={clsx(
                      'focus-ring relative flex h-[var(--seat)] w-[var(--seat)] flex-col items-center justify-center gap-0.5 rounded-xl border transition-[transform,box-shadow,background-color] duration-[var(--dur-fast)] ease-[var(--ease-out)]',
                      STATUS_CLASS[s.status],
                      selectable && 'hover:scale-[1.04] hover:shadow-[var(--shadow-card)] active:scale-[0.98]',
                      !selectable && 'cursor-not-allowed',
                      selected && 'scale-[1.06] shadow-[var(--shadow-glow)] ring-2 ring-primary',
                      mine && !selected && 'ring-2 ring-primary/70',
                    )}
                  >
                    <MonitorIcon />
                    <span className="tnum text-xs font-semibold leading-none">{s.name}</span>
                    {(mine || other) && (
                      <span
                        aria-hidden="true"
                        className={clsx(
                          'absolute right-1.5 top-1.5 h-2.5 w-2.5 rounded-full',
                          mine ? 'bg-primary shadow-[0_0_8px_rgb(var(--c-primary))]' : 'bg-accent',
                        )}
                      />
                    )}
                    {s.pcId === thisPcId && (
                      <span
                        aria-hidden="true"
                        className="absolute left-1.5 top-1.5 h-2.5 w-2.5 rounded-full bg-success shadow-[0_0_8px_rgb(var(--c-success))]"
                      />
                    )}
                  </button>
                </Tooltip>
              </div>
            );
          })}
        </div>
      </div>
      <ul className="flex flex-wrap items-center gap-x-5 gap-y-2 text-sm text-muted" aria-label={t('booking.legend')}>
        {LEGEND.map((s) => (
          <li key={s} className="flex items-center gap-2">
            <span aria-hidden="true" className={clsx('h-3 w-3 rounded-full', STATUS_DOT[s])} />
            {t(`booking.status.${s}`)}
          </li>
        ))}
        <li className="flex items-center gap-2">
          <span aria-hidden="true" className="h-3 w-3 rounded-full ring-2 ring-primary" />
          {t('booking.mine')}
        </li>
        <li className="flex items-center gap-2">
          <span aria-hidden="true" className="h-3 w-3 rounded-full bg-success shadow-[0_0_6px_rgb(var(--c-success))]" />
          {t('booking.thisPc')}
        </li>
      </ul>
    </div>
  );
}
