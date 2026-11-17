/**
 * Quick actions at the PC the cashier clicked on the map: the seat's everyday steps — extend, top up, move, the bar, the
 * end of the session; for a free PC, seat a client or a guest — right at the tile instead of across the screen in the
 * seat card. Each one runs the seat card's own step (its sheet, its checks), so nothing differs from the card's buttons.
 * Esc, a click elsewhere, a scroll or another PC closes it.
 */
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import type { Seat } from '@/api';
import { t } from '@/i18n';

/** What the menu asks of the seat card (the move and the bar the map does itself). */
export type QuickAction = 'extend' | 'topup' | 'move' | 'bar' | 'more' | 'end' | 'client' | 'guest';

/** A step the seat card is asked to take; `n` makes the same step asked twice a new request. */
export interface QuickRequest {
  pcId: string;
  action: QuickAction;
  n: number;
}

interface Item {
  action: QuickAction;
  label: string;
  hint?: string;
  tone?: 'primary' | 'danger';
}

/** The menu's items for the seat as it is now. */
export function quickItems(seat: Seat): Item[] {
  const session = seat.session;
  if (session && seat.user) {
    const guest = seat.user.role === 'guest';
    return [
      ...(session.isPrepaid ? [{ action: 'extend' as const, label: t('Продлить'), tone: 'primary' as const }] : []),
      ...(!guest ? [{ action: 'topup' as const, label: t('Пополнить'), hint: 'F2' }] : []),
      { action: 'move', label: t('Пересадить на другой ПК…') },
      { action: 'bar', label: t('Бар') },
      { action: 'more', label: t('Ещё ⋯') },
      { action: 'end', label: t('Завершить сеанс'), tone: 'danger' },
    ];
  }
  if (session) return [{ action: 'more', label: t('Ещё ⋯') }];
  return [
    { action: 'client', label: t('Посадить клиента'), tone: 'primary' },
    { action: 'guest', label: t('Посадить гостя') },
    { action: 'more', label: t('Ещё ⋯') },
  ];
}

const GAP = 8;
const WIDTH = 232;

export function QuickActions({
  seat,
  anchor,
  onAction,
  onClose,
}: {
  seat: Seat;
  /** The tile's box on screen when it was clicked. */
  anchor: DOMRect;
  onAction: (action: QuickAction) => void;
  onClose: () => void;
}): JSX.Element {
  const box = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null);

  // Right of the tile, else left of it; kept on screen.
  useLayoutEffect(() => {
    const h = box.current?.offsetHeight ?? 0;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const left = anchor.right + GAP + WIDTH <= vw - GAP ? anchor.right + GAP : Math.max(GAP, anchor.left - GAP - WIDTH);
    const top = Math.min(Math.max(GAP, anchor.top), Math.max(GAP, vh - h - GAP));
    setPos({ left, top });
  }, [anchor]);

  useEffect(() => {
    const away = (e: PointerEvent): void => {
      const target = e.target as Node | null;
      if (box.current && target && box.current.contains(target)) return;
      // The tile itself: its own click decides (another PC moves the menu, the same one closes it).
      if (target instanceof Element && target.closest(`#seat-${CSS.escape(seat.pc.id)}`)) return;
      onClose();
    };
    const key = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onClose();
      }
    };
    const close = (): void => onClose();
    window.addEventListener('pointerdown', away, true);
    window.addEventListener('keydown', key, true);
    window.addEventListener('resize', close);
    window.addEventListener('scroll', close, true);
    return () => {
      window.removeEventListener('pointerdown', away, true);
      window.removeEventListener('keydown', key, true);
      window.removeEventListener('resize', close);
      window.removeEventListener('scroll', close, true);
    };
  }, [onClose, seat.pc.id]);

  const items = quickItems(seat);
  const heading: ReactNode = (
    <span className="flex min-w-0 items-baseline justify-between gap-2 px-3 pb-1.5 pt-2.5">
      <span className="truncate font-display text-[15px] font-medium text-hi">{seat.pc.name}</span>
      <span className="label-sm truncate">{seat.user ? seat.user.displayName : t('Свободен')}</span>
    </span>
  );
  return (
    <div
      ref={box}
      role="menu"
      aria-label={t('Действия с {pc}', { pc: seat.pc.name })}
      className="panel-solid anim-rise fixed z-50 flex flex-col pb-1.5"
      style={{
        width: WIDTH,
        left: pos?.left ?? anchor.right + GAP,
        top: pos?.top ?? anchor.top,
        visibility: pos ? 'visible' : 'hidden',
      }}
    >
      {heading}
      {items.map((item, i) => (
        <button
          key={item.action}
          type="button"
          role="menuitem"
          autoFocus={i === 0}
          onClick={() => onAction(item.action)}
          className={clsx(
            'focus-ring mx-1.5 flex min-h-10 items-center justify-between gap-2 rounded-md px-2.5 text-left text-[13.5px] font-medium transition-colors',
            item.tone === 'primary'
              ? 'text-accent hover:bg-accent/[0.1]'
              : item.tone === 'danger'
                ? 'mt-1 text-danger hover:bg-danger/[0.1]'
                : 'text-text hover:bg-text/[0.06]',
          )}
        >
          {item.label}
          {item.hint && <span className="font-mono text-[10px] text-muted">{item.hint}</span>}
        </button>
      ))}
    </div>
  );
}
