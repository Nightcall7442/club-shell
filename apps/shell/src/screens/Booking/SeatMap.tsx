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

/** Free seats are the bright ones; everything unavailable recedes, so the eye finds a place without a colour key. */
const STATUS_CLASS: Record<PcStatus, string> = {
  free: 'bg-text/[0.07] text-text border-text/25 hover:bg-text/[0.12]',
  busy: 'bg-transparent text-muted/70 border-text/[0.07]',
  booked: 'bg-accent/10 text-accent border-accent/35',
  locked: 'bg-transparent text-muted/70 border-text/[0.07]',
  maintenance: 'bg-transparent text-muted/50 border-dashed border-text/15',
  offline: 'bg-transparent text-muted/40 border-text/[0.05]',
};

const STATUS_DOT: Record<PcStatus, string> = {
  free: 'bg-text',
  busy: 'bg-text/25',
  booked: 'bg-accent',
  locked: 'bg-text/25',
  maintenance: 'bg-text/15',
  offline: 'bg-text/10',
};

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
    const zoneMap = new Map<string, { minY: number; maxY: number }>();
    for (const s of seats) {
      maxX = Math.max(maxX, s.x);
      maxY = Math.max(maxY, s.y);
      const z = zoneMap.get(s.zone);
      if (z) {
        z.minY = Math.min(z.minY, s.y);
        z.maxY = Math.max(z.maxY, s.y);
      } else {
        zoneMap.set(s.zone, { minY: s.y, maxY: s.y });
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
        {/* w-max: an `auto` track in a stretched grid swallows all spare width, which parked the zone labels half a
            screen away from their seats. Sized to content and centred instead; the auto margins collapse and the
            wrapper scrolls when the hall is wider than the panel. */}
        <div
          className="mx-auto grid w-max gap-[clamp(0.4rem,0.6vw,0.75rem)]"
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
              className="flex items-center justify-center border-l border-text/15 px-2 text-sm font-medium text-muted [writing-mode:vertical-rl] [transform:rotate(180deg)]"
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
                      'focus-ring relative flex h-[var(--seat)] w-[var(--seat)] flex-col items-center justify-center gap-0.5 rounded-lg border transition-[box-shadow,background-color] duration-[var(--dur-fast)] ease-[var(--ease-out)]',
                      STATUS_CLASS[s.status],
                      !selectable && 'cursor-not-allowed',
                      selected && 'ring-2 ring-primary',
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
                          mine ? 'bg-primary' : 'bg-accent',
                        )}
                      />
                    )}
                    {s.pcId === thisPcId && (
                      <span
                        aria-hidden="true"
                        className="absolute left-1.5 top-1.5 h-2.5 w-2.5 rounded-full bg-success"
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
          <span aria-hidden="true" className="h-3 w-3 rounded-full bg-success" />
          {t('booking.thisPc')}
        </li>
      </ul>
    </div>
  );
}
