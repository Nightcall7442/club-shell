/**
 * Finding a client at the counter: `GET /admin/clients/lookup` (name or login, any part of the phone, the exact card),
 * debounced, the 8 best matches with the balance and the PC the client is on; an empty field lists the recent clients.
 * Two faces: {@link ClientPicker} — the "Кто" of the seat panel — and {@link GlobalSearch} in the top bar ("/"), whose
 * rows top up the client or show their PC. Arrows move, Enter picks, Esc closes; "+ Новый клиент" under the list opens
 * the registration on the Клиенты page.
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

/** The lookup for `q` while `enabled`, newest answer wins. */
function useLookup(q: string, enabled: boolean): { items: ClientHit[]; loading: boolean; error: string | null } {
  const [items, setItems] = useState<ClientHit[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const seq = useRef(0);
  useEffect(() => {
    if (!enabled) return undefined;
    const mine = ++seq.current;
    setLoading(true);
    const timer = window.setTimeout(() => {
      adminApi
        .lookupClients(q.trim())
        .then((r) => {
          if (seq.current !== mine) return;
          setItems(r.items);
          setError(null);
        })
        .catch((e: unknown) => {
          if (seq.current === mine) setError(describe(e));
        })
        .finally(() => {
          if (seq.current === mine) setLoading(false);
        });
    }, DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [q, enabled]);
  return { items, loading, error };
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
              onMouseEnter={() => onHover(i)}
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
          onMouseEnter={() => onHover(items.length)}
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

/** Keyboard of a combobox over `count` results plus the "new client" row. */
function useActive(count: number): [number, (i: number) => void, (e: KeyboardEvent) => boolean] {
  const [active, setActive] = useState(0);
  useEffect(() => setActive(0), [count]);
  const move = (e: KeyboardEvent): boolean => {
    if (e.key === 'ArrowDown') {
      setActive((a) => Math.min(a + 1, count));
      return true;
    }
    if (e.key === 'ArrowUp') {
      setActive((a) => Math.max(a - 1, 0));
      return true;
    }
    return false;
  };
  return [active, setActive, move];
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
}: {
  value: ClientHit | null;
  onChange: (hit: ClientHit | null) => void;
  label: string;
}): JSX.Element {
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const [q, setQ] = useState('');
  const [open, setOpen] = useState(false);
  const { items, loading, error } = useLookup(q, open);
  const [active, setActive, move] = useActive(items.length);
  // Someone already at a PC cannot be seated on another one.
  const isDisabled = (hit: ClientHit): string | null =>
    hit.playing ? t('Уже играет на {pc}', { pc: pcLabel(hit.playing.pcName) }) : null;

  const pick = (hit: ClientHit): void => {
    onChange(hit);
    setOpen(false);
    setQ('');
  };

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
        aria-activedescendant={open ? `${id}-list-${active}` : undefined}
        aria-autocomplete="list"
        autoComplete="off"
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
            const hit = items[active];
            if (hit && !isDisabled(hit)) pick(hit);
            else if (active === items.length) openNewClient();
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
          onHover={setActive}
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
  const { items, loading, error } = useLookup(q, open);
  const [active, setActive, move] = useActive(items.length);

  const done = (): void => {
    setOpen(false);
    setQ('');
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
  };

  return (
    <div className="relative w-full max-w-[26rem]">
      <input
        ref={ref}
        type="search"
        role="combobox"
        aria-label={t('Поиск клиента')}
        aria-expanded={open}
        aria-controls={`${id}-list`}
        aria-activedescendant={open ? `${id}-list-${active}` : undefined}
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
            const hit = items[active];
            if (hit) {
              if (e.shiftKey && hit.playing) onShowPc(hit);
              else onTopUp(hit);
              done();
            } else if (active === items.length) {
              openNewClient();
              done();
            }
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
          onHover={setActive}
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
