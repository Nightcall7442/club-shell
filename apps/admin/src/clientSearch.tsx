/**
 * Finding a client at the counter: `GET /admin/clients/lookup` (name or login, any part of the phone, the exact card),
 * debounced, the 8 best matches with the balance and the PC the client is on; an empty field lists the recent clients.
 * Two faces: {@link ClientPicker} — the "Кто" of the seat panel — and {@link GlobalSearch} in the top bar ("/"), whose
 * rows top up the client or show their PC. Arrows move, Enter picks, Esc closes; "+ Новый клиент" under the list opens
 * the registration on the Клиенты page (Enter reaches it only through the arrows).
 *
 * Enter acts only on the results of what is in the field: a card scanner or a fast cashier types and presses Enter
 * before the debounced lookup answers, so such an Enter runs the lookup at once and picks the best match when it comes.
 */
import { forwardRef, useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import clsx from 'clsx';
import { adminApi, type ClientHit } from '@/api';
import { describe } from '@/errors';
import { money } from '@/format';
import { t } from '@/i18n';
import { Kbd, inputCls } from '@/ui';

const DEBOUNCE_MS = 200;

/** `PC-07` → `ПК 07`; a name without a number stays as it is. */
export function pcLabel(pcName: string): string {
  const n = /(\d+)\s*$/.exec(pcName)?.[1];
  return n ? t('ПК {n}', { n }) : pcName;
}

interface Lookup {
  /** The trimmed field text. */
  term: string;
  items: ClientHit[];
  /** True when `items` answer the current field (not an earlier text still on screen). */
  fresh: boolean;
  loading: boolean;
  error: string | null;
  /** Runs the waiting lookup now instead of after the debounce. */
  flush: () => void;
}

/** The lookup for `q` while `enabled`, newest answer wins. */
function useLookup(q: string, enabled: boolean): Lookup {
  const [result, setResult] = useState<{ items: ClientHit[]; q: string | null }>({ items: [], q: null });
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const seq = useRef(0);
  const run = useRef<(() => void) | null>(null);
  const term = q.trim();
  useEffect(() => {
    if (!enabled) return undefined;
    const mine = ++seq.current;
    setLoading(true);
    setError(null);
    const go = (): void => {
      window.clearTimeout(timer);
      run.current = null;
      adminApi
        .lookupClients(term)
        .then((r) => {
          if (seq.current !== mine) return;
          setResult({ items: r.items, q: term });
          setError(null);
        })
        .catch((e: unknown) => {
          if (seq.current === mine) setError(describe(e));
        })
        .finally(() => {
          if (seq.current === mine) setLoading(false);
        });
    };
    const timer = window.setTimeout(go, DEBOUNCE_MS);
    run.current = go;
    return () => {
      window.clearTimeout(timer);
      run.current = null;
    };
  }, [term, enabled]);
  return {
    term,
    items: result.items,
    fresh: result.q === term && !loading,
    loading,
    error,
    flush: () => run.current?.(),
  };
}

/**
 * An Enter that came before the results of the field: held until they arrive, then `act` gets them. Returns the
 * function the Enter calls with the results it saw and whether they were fresh.
 */
function useHeldEnter(lookup: Lookup, act: (items: ClientHit[]) => void): (enter: () => void) => void {
  // The text the Enter was pressed for: typing on (or a failed lookup) drops it.
  const [held, setHeld] = useState<string | null>(null);
  const latest = useRef(act);
  latest.current = act;
  useEffect(() => {
    if (held === null) return;
    if (held !== lookup.term || lookup.error) setHeld(null);
    else if (lookup.fresh) {
      setHeld(null);
      latest.current(lookup.items);
    }
  }, [held, lookup.term, lookup.error, lookup.fresh, lookup.items]);
  return (enter) => {
    if (lookup.fresh) {
      enter();
      return;
    }
    setHeld(lookup.term);
    lookup.flush();
  };
}

/** Name, login, phone tail, balance and the PC: one line of a result list. */
function HitLine({ hit, extra }: { hit: ClientHit; extra?: ReactNode }): JSX.Element {
  return (
    <span className="flex min-w-0 flex-1 items-center gap-3">
      <span className="flex min-w-0 flex-1 flex-col">
        <span className="truncate text-sm font-medium text-text">{hit.displayName}</span>
        <span className="truncate font-mono text-[0.7rem] text-muted">
          @{hit.username}
          {hit.phoneTail && <span className="tnum"> · ••{hit.phoneTail}</span>}
          {hit.cardId && <span> · {hit.cardId}</span>}
        </span>
      </span>
      {hit.blacklisted && (
        <span className="shrink-0 rounded border border-danger/50 px-1.5 py-0.5 text-[0.65rem] font-semibold text-danger">
          {t('Чёрный список')}
        </span>
      )}
      {hit.playing && (
        <span className="shrink-0 rounded border border-accent/50 px-1.5 py-0.5 font-mono text-[0.65rem] uppercase tracking-[0.08em] text-accent">
          {pcLabel(hit.playing.pcName)}
        </span>
      )}
      <span className="tnum shrink-0 text-sm font-semibold">{money(hit.balance)}</span>
      {extra}
    </span>
  );
}

interface ListProps {
  id: string;
  items: ClientHit[];
  active: number;
  loading: boolean;
  error: string | null;
  query: string;
  onHover: (i: number) => void;
  onPick: (hit: ClientHit) => void;
  onNewClient: () => void;
  isDisabled?: (hit: ClientHit) => string | null;
  rowActions?: (hit: ClientHit, active: boolean) => ReactNode;
  /** Wider than the field (the top bar's search has buttons on every row). */
  wide?: boolean;
}

/** The dropdown: results, then "+ Новый клиент" as the last option (index `items.length`). */
function HitList({
  id,
  items,
  active,
  loading,
  error,
  query,
  onHover,
  onPick,
  onNewClient,
  isDisabled,
  rowActions,
  wide,
}: ListProps): JSX.Element {
  return (
    <div
      className={clsx(
        'panel absolute left-0 top-full z-40 mt-1 overflow-hidden shadow-2xl shadow-black/60',
        wide ? 'w-[min(36rem,calc(100vw-2rem))]' : 'right-0',
      )}
    >
      {query.trim().length < 2 && items.length > 0 && <p className="label px-3 pb-1 pt-2.5">{t('Недавние клиенты')}</p>}
      <ul id={id} role="listbox" aria-label={t('Клиенты')} className="max-h-[22rem] overflow-y-auto py-1">
        {items.map((hit, i) => {
          const why = isDisabled?.(hit) ?? null;
          return (
            <li
              key={hit.id}
              id={`${id}-${i}`}
              role="option"
              aria-selected={i === active}
              aria-disabled={why !== null}
              // move, not enter: a list that opens under a resting cursor picks nothing
              onMouseMove={() => i !== active && onHover(i)}
              // mousedown keeps the focus in the field, so the list does not close before the click lands
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => why === null && onPick(hit)}
              className={clsx(
                'flex cursor-pointer items-center gap-2 px-3 py-2',
                i === active && 'bg-accent/[0.08]',
                why !== null && 'cursor-not-allowed opacity-50',
              )}
            >
              <HitLine hit={hit} extra={rowActions?.(hit, i === active)} />
            </li>
          );
        })}
        {items.length === 0 && (
          <li className="px-3 py-3 text-sm text-muted">
            {error ?? (loading ? t('Ищем…') : query.trim() ? t('Никого не нашли') : t('Клиентов нет'))}
          </li>
        )}
        <li
          id={`${id}-${items.length}`}
          role="option"
          aria-selected={active === items.length}
          onMouseMove={() => active !== items.length && onHover(items.length)}
          onMouseDown={(e) => e.preventDefault()}
          onClick={onNewClient}
          className={clsx(
            'mt-1 flex cursor-pointer items-center gap-2 border-t border-line px-3 py-2.5 text-sm text-accent',
            active === items.length && 'bg-accent/[0.08]',
          )}
        >
          {t('+ Новый клиент')}
        </li>
      </ul>
    </div>
  );
}

/**
 * Keyboard of a combobox over `items` plus the "new client" row: every new result list starts at its best match.
 * `onNew` — the cashier moved onto "+ Новый клиент" with the arrows (or the mouse), the only way Enter opens it.
 */
function useActive(items: ClientHit[]): {
  active: number;
  onNew: boolean;
  hover: (i: number) => void;
  move: (e: KeyboardEvent) => boolean;
} {
  const count = items.length;
  const [active, setActive] = useState(0);
  const [moved, setMoved] = useState(false);
  useEffect(() => {
    setActive(0);
    setMoved(false);
  }, [items]);
  const move = (e: KeyboardEvent): boolean => {
    if (e.key === 'ArrowDown') {
      setActive((a) => Math.min(a + 1, count));
      setMoved(true);
      return true;
    }
    if (e.key === 'ArrowUp') {
      setActive((a) => Math.max(a - 1, 0));
      setMoved(true);
      return true;
    }
    return false;
  };
  const hover = (i: number): void => {
    setActive(i);
    setMoved(true);
  };
  const onNew = moved && active === count;
  // What Enter acts on, and so what is highlighted: -1 — nothing (no results, and the new-client row not chosen).
  return { active: onNew || active < count ? active : -1, onNew, hover, move };
}

/** Opens the Клиенты page with the registration panel (the existing flow, not a copy of it). */
export function openNewClient(): void {
  window.location.hash = '/clients/new';
}

// ---------------------------------------------------------------------------------------------------------------------
// The seat panel's "Кто"
// ---------------------------------------------------------------------------------------------------------------------

export function ClientPicker({
  value,
  onChange,
  label,
  allowPlaying = false,
  autoFocus = false,
}: {
  value: ClientHit | null;
  onChange: (hit: ClientHit | null) => void;
  label: string;
  /** A client playing on a PC may be picked (the bar sells to players; a seat does not seat them twice). */
  allowPlaying?: boolean;
  autoFocus?: boolean;
}): JSX.Element {
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const [q, setQ] = useState('');
  const [open, setOpen] = useState(false);
  const lookup = useLookup(q, open);
  const { items, loading, error } = lookup;
  const { active, onNew, hover, move } = useActive(items);
  // Someone already at a PC cannot be seated on another one; the club refuses a blacklisted client.
  const isDisabled = (hit: ClientHit): string | null =>
    hit.blacklisted
      ? t('Клиент в чёрном списке')
      : hit.playing && !allowPlaying
        ? t('Уже играет на {pc}', { pc: pcLabel(hit.playing.pcName) })
        : null;

  const pick = (hit: ClientHit): void => {
    onChange(hit);
    setOpen(false);
    setQ('');
  };
  // A held Enter picks the best match it was waiting for.
  const enter = useHeldEnter(lookup, (fresh) => {
    const hit = fresh[0];
    if (hit && !isDisabled(hit)) pick(hit);
  });

  if (value) {
    return (
      <div className="flex flex-col gap-1.5">
        <span className="label">{label}</span>
        <div className="flex items-center gap-2 rounded-md border border-accent/40 bg-accent/[0.05] px-3 py-2">
          <HitLine hit={value} />
          <button
            type="button"
            aria-label={t('Сменить клиента')}
            title={t('Сменить клиента')}
            className="focus-ring h-7 w-7 shrink-0 rounded-md text-muted hover:bg-white/[0.06] hover:text-text"
            onClick={() => {
              onChange(null);
              window.setTimeout(() => input.current?.focus(), 0);
            }}
          >
            ×
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="relative flex flex-col gap-1.5">
      <label htmlFor={`${id}-input`} className="label">
        {label}
      </label>
      <input
        id={`${id}-input`}
        ref={input}
        role="combobox"
        aria-expanded={open}
        aria-controls={`${id}-list`}
        aria-activedescendant={open && active >= 0 ? `${id}-list-${active}` : undefined}
        aria-autocomplete="list"
        autoComplete="off"
        autoFocus={autoFocus}
        className={inputCls}
        value={q}
        placeholder={t('Имя, логин, телефон или карта')}
        onFocus={() => setOpen(true)}
        onBlur={() => setOpen(false)}
        onChange={(e) => {
          setQ(e.target.value);
          setOpen(true);
        }}
        onKeyDown={(e) => {
          if (!open && (e.key === 'ArrowDown' || e.key === 'Enter')) {
            setOpen(true);
            e.preventDefault();
            return;
          }
          if (move(e)) {
            e.preventDefault();
          } else if (e.key === 'Enter' && open) {
            e.preventDefault();
            if (e.repeat) return;
            if (onNew) {
              openNewClient();
              return;
            }
            enter(() => {
              const hit = items[active];
              if (hit && !isDisabled(hit)) pick(hit);
            });
          } else if (e.key === 'Escape' && open) {
            e.stopPropagation();
            setOpen(false);
          }
        }}
      />
      {open && (
        <HitList
          id={`${id}-list`}
          items={items}
          active={active}
          loading={loading}
          error={error}
          query={q}
          onHover={hover}
          onPick={pick}
          onNewClient={openNewClient}
          isDisabled={isDisabled}
          rowActions={(hit) => {
            const why = isDisabled(hit);
            return why ? <span className="sr-only">{why}</span> : null;
          }}
        />
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// The top bar's search
// ---------------------------------------------------------------------------------------------------------------------

export const GlobalSearch = forwardRef<
  HTMLInputElement,
  { onTopUp: (hit: ClientHit) => void; onShowPc: (hit: ClientHit) => void }
>(function GlobalSearch({ onTopUp, onShowPc }, ref) {
  const id = useId();
  const [q, setQ] = useState('');
  const [open, setOpen] = useState(false);
  const lookup = useLookup(q, open);
  const { items, loading, error } = lookup;
  const { active, onNew, hover, move } = useActive(items);

  const done = (): void => {
    setOpen(false);
    setQ('');
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
  };
  const act = (hit: ClientHit | undefined, showPc: boolean): void => {
    if (!hit) return;
    if (showPc && hit.playing) onShowPc(hit);
    else onTopUp(hit);
    done();
  };
  const enter = useHeldEnter(lookup, (fresh) => act(fresh[0], false));

  return (
    <div className="relative min-w-[10rem] max-w-[26rem] flex-1">
      <input
        ref={ref}
        type="search"
        role="combobox"
        aria-label={t('Поиск клиента')}
        aria-expanded={open}
        aria-controls={`${id}-list`}
        aria-activedescendant={open && active >= 0 ? `${id}-list-${active}` : undefined}
        aria-autocomplete="list"
        autoComplete="off"
        className={clsx(inputCls, 'h-9 pr-9')}
        value={q}
        placeholder={t('Клиент: имя, телефон, карта')}
        onFocus={() => setOpen(true)}
        onBlur={() => setOpen(false)}
        onChange={(e) => {
          setQ(e.target.value);
          setOpen(true);
        }}
        onKeyDown={(e) => {
          if (move(e)) {
            e.preventDefault();
            setOpen(true);
          } else if (e.key === 'Enter') {
            e.preventDefault();
            if (e.repeat) return;
            if (onNew) {
              openNewClient();
              done();
              return;
            }
            const showPc = e.shiftKey;
            enter(() => act(items[active], showPc));
          } else if (e.key === 'Escape') {
            e.stopPropagation();
            done();
          }
        }}
      />
      <Kbd className="pointer-events-none absolute right-2.5 top-1/2 -translate-y-1/2 text-muted">/</Kbd>
      {open && (
        <HitList
          id={`${id}-list`}
          items={items}
          active={active}
          loading={loading}
          error={error}
          query={q}
          wide
          onHover={hover}
          onPick={(hit) => {
            onTopUp(hit);
            done();
          }}
          onNewClient={() => {
            openNewClient();
            done();
          }}
          rowActions={(hit, isActive) => (
            <span className="flex shrink-0 gap-1">
              <button
                type="button"
                className="focus-ring choice inline-flex h-7 items-center gap-1.5 rounded-md px-2 text-xs font-semibold"
                onClick={(e) => {
                  e.stopPropagation();
                  onTopUp(hit);
                  done();
                }}
              >
                {t('Пополнить')}
                {isActive && <Kbd>Enter</Kbd>}
              </button>
              {hit.playing && (
                <button
                  type="button"
                  className="focus-ring inline-flex h-7 items-center gap-1.5 rounded-md px-2 text-xs font-semibold text-muted hover:bg-white/[0.06] hover:text-text"
                  onClick={(e) => {
                    e.stopPropagation();
                    onShowPc(hit);
                    done();
                  }}
                >
                  {t('Показать ПК')}
                  {isActive && <Kbd>Shift+Enter</Kbd>}
                </button>
              )}
            </span>
          )}
        />
      )}
    </div>
  );
});
