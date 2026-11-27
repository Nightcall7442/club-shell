/**
 * The PC's menu on the map (a right click on its tile): every step of the seat — extend, top up, move, the bar, the PC's
 * controls (message, lock, restart, power), the end of the session; for a free PC, seat a client, a guest prepaid or
 * postpaid, switch it on — at the pointer. A left click only picks the PC. A step that needs a form opens in a window
 * over the map (the seat card itself, so nothing differs from the card's buttons), never in the right column.
 * Esc, a click elsewhere, a scroll or another PC closes it.
 */
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import type { Seat } from '@/api';
import { pcLabel } from '@/clientSearch';
import { t } from '@/i18n';

/** What the menu asks of the seat card (the move and the bar the map does itself). */
export type QuickAction =
  | 'extend'
  | 'topup'
  | 'move'
  | 'bar'
  | 'more'
  | 'end'
  | 'client'
  | 'guest'
  | 'guestPost'
  | 'wake'
  | 'open';

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

/** The menu's items for the seat as it is now; `guests`: the desk seats walk-in guests (a server with cash desk 2). */
export function quickItems(seat: Seat, guests = true): Item[] {
  const session = seat.session;
  const controls: Item = { action: 'more', label: t('Управление ПК: сообщение, блокировка, перезагрузка…') };
  if (session && seat.user) {
    const guest = seat.user.role === 'guest';
    return [
      ...(session.isPrepaid ? [{ action: 'extend' as const, label: t('Продлить'), tone: 'primary' as const }] : []),
      ...(!guest ? [{ action: 'topup' as const, label: t('Пополнить'), hint: 'F2' }] : []),
      { action: 'move', label: t('Пересадить на другой ПК…') },
      { action: 'bar', label: t('Бар') },
      controls,
      { action: 'open', label: t('Открыть карточку') },
      { action: 'end', label: t('Завершить сеанс'), tone: 'danger' },
    ];
  }
  if (session) return [controls, { action: 'open', label: t('Открыть карточку') }];
  return [
    ...(seat.pc.status === 'offline'
      ? [{ action: 'wake' as const, label: t('Включить ПК'), tone: 'primary' as const }]
      : []),
    {
      action: 'client',
      label: t('Посадить клиента'),
      tone: seat.pc.status === 'offline' ? undefined : ('primary' as const),
    },
    ...(guests
      ? [
          { action: 'guest' as const, label: t('Посадить гостя · предоплата') },
          { action: 'guestPost' as const, label: t('Посадить гостя · постоплата') },
        ]
      : []),
    controls,
  ];
}

const GAP = 8;
const WIDTH = 268;

export function QuickActions({
  seat,
  anchor,
  guests = true,
  onAction,
  onClose,
}: {
  seat: Seat;
  /** Where the menu opens: the pointer of the right click (a zero-size box), else the tile's box. */
  anchor: DOMRect;
  guests?: boolean;
  onAction: (action: QuickAction) => void;
  onClose: () => void;
}): JSX.Element {
  const box = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null);

  // Right of the anchor, else left of it; kept on screen.
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
      } else if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && box.current) {
        // ↑ ↓ walk the items, as in any menu (Enter runs the focused one); the map behind does not move.
        e.preventDefault();
        e.stopPropagation();
        const items = [...box.current.querySelectorAll<HTMLElement>('[role="menuitem"]')];
        const i = items.indexOf(document.activeElement as HTMLElement);
        const next = items[(i + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length];
        next?.focus();
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

  const items = quickItems(seat, guests);
  const heading: ReactNode = (
    <span className="flex min-w-0 items-baseline justify-between gap-2 px-3 pb-1.5 pt-2.5">
      <span className="truncate font-display text-[15px] font-medium text-hi">{pcLabel(seat.pc.name)}</span>
      <span className="label-sm truncate">{seat.user ? seat.user.displayName : t('Свободен')}</span>
    </span>
  );
  return (
    <div
      ref={box}
      role="menu"
      aria-label={t('Действия с {pc}', { pc: pcLabel(seat.pc.name) })}
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
