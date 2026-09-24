import { useEffect, useId, useRef, type KeyboardEvent, type ReactNode } from 'react';
import clsx from 'clsx';
import { events } from '@/lib/tauri';

export interface TabItem<K extends string = string> {
  key: K;
  label: ReactNode;
  icon?: ReactNode;
  /** Counter / status rendered after the label. */
  badge?: ReactNode;
  disabled?: boolean;
}

export type TabsVariant = 'pills' | 'underline';
export type TabsSize = 'md' | 'lg';

export interface TabsProps<K extends string = string> {
  items: TabItem<K>[];
  value: K;
  onChange: (key: K) => void;
  /** `aria-label` of the tab list. */
  label?: string;
  variant?: TabsVariant;
  size?: TabsSize;
  /** React to gamepad LB/RB (default `true`; only the most recently mounted Tabs reacts). */
  gamepad?: boolean;
  /** Prefix used for `id`/`aria-controls`; panels should use `id={`${idPrefix}-panel-${key}`}`. */
  idPrefix?: string;
  className?: string;
}

/** Stack of mounted Tabs; the top one owns LB/RB. */
const mounted: symbol[] = [];

const SIZE: Record<TabsSize, string> = { md: 'h-10 px-4 text-[0.95rem] gap-2', lg: 'h-12 px-5 text-base gap-2.5' };

/** Roving-tabindex tab list: arrows/Home/End move focus and select; gamepad LB/RB cycle. */
export function Tabs<K extends string = string>({
  items,
  value,
  onChange,
  label,
  variant = 'pills',
  size = 'md',
  gamepad = true,
  idPrefix,
  className,
}: TabsProps<K>): JSX.Element {
  const autoId = useId();
  const prefix = idPrefix ?? autoId;
  const list = useRef<HTMLDivElement>(null);
  const token = useRef<symbol>(Symbol('tabs'));
  const latest = useRef({ items, value, onChange });
  latest.current = { items, value, onChange };

  const step = (delta: number, focus: boolean): void => {
    const { items: its, value: v, onChange: change } = latest.current;
    const enabled = its.filter((i) => !i.disabled);
    if (enabled.length === 0) {
      return;
    }
    const idx = enabled.findIndex((i) => i.key === v);
    const next = enabled[(idx + delta + enabled.length) % enabled.length];
    if (!next) {
      return;
    }
    change(next.key);
    if (focus) {
      list.current?.querySelector<HTMLElement>(`[data-tab-key="${next.key}"]`)?.focus();
    }
  };

  useEffect(() => {
    if (!gamepad) {
      return undefined;
    }
    const me = token.current;
    mounted.push(me);
    const off = events.onKiosk('gamepad', (p) => {
      if (p.kind !== 'button' || !p.pressed || mounted[mounted.length - 1] !== me) {
        return;
      }
      if (p.name === 'lb') {
        step(-1, false);
      } else if (p.name === 'rb') {
        step(1, false);
      }
    });
    return () => {
      off();
      const i = mounted.indexOf(me);
      if (i >= 0) {
        mounted.splice(i, 1);
      }
    };
    // step reads latest props through a ref; the subscription itself only depends on `gamepad`.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [gamepad]);

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>): void => {
    switch (e.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        e.preventDefault();
        step(1, true);
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        e.preventDefault();
        step(-1, true);
        break;
      case 'Home': {
        e.preventDefault();
        const first = items.find((i) => !i.disabled);
        if (first) {
          onChange(first.key);
          list.current?.querySelector<HTMLElement>(`[data-tab-key="${first.key}"]`)?.focus();
        }
        break;
      }
      case 'End': {
        e.preventDefault();
        const last = [...items].reverse().find((i) => !i.disabled);
        if (last) {
          onChange(last.key);
          list.current?.querySelector<HTMLElement>(`[data-tab-key="${last.key}"]`)?.focus();
        }
        break;
      }
      default:
        break;
    }
  };

  return (
    <div
      ref={list}
      role="tablist"
      aria-label={label}
      aria-orientation="horizontal"
      onKeyDown={onKeyDown}
      className={clsx(
        'no-scrollbar flex max-w-full items-center overflow-x-auto',
        variant === 'pills' ? 'gap-1' : 'gap-2 border-b border-[color:var(--hairline)]',
        className,
      )}
    >
      {items.map((item) => {
        const active = item.key === value;
        return (
          <button
            key={item.key}
            type="button"
            role="tab"
            id={`${prefix}-tab-${item.key}`}
            aria-selected={active}
            aria-controls={`${prefix}-panel-${item.key}`}
            aria-disabled={item.disabled || undefined}
            disabled={item.disabled}
            tabIndex={active ? 0 : -1}
            data-nav="true"
            data-tab-key={item.key}
            onClick={() => onChange(item.key)}
            className={clsx(
              'focus-ring inline-flex shrink-0 select-none items-center whitespace-nowrap font-medium leading-none transition-colors duration-[var(--dur-fast)]',
              SIZE[size],
              variant === 'pills' && 'rounded-md',
              variant === 'pills' &&
                (active ? 'bg-text/10 text-text' : 'text-muted hover:bg-text/[0.05] hover:text-text'),
              variant === 'underline' && '-mb-px border-b-2 rounded-t-md',
              variant === 'underline' &&
                (active ? 'border-primary text-text' : 'border-transparent text-muted hover:text-text'),
              item.disabled && 'cursor-not-allowed opacity-40',
            )}
          >
            {item.icon && (
              <span
                aria-hidden="true"
                className="inline-flex h-[1.2em] w-[1.2em] items-center justify-center [&>svg]:h-full [&>svg]:w-full"
              >
                {item.icon}
              </span>
            )}
            {item.label}
            {item.badge !== undefined && item.badge !== null && (
              <span className="ml-1 inline-flex items-center">{item.badge}</span>
            )}
          </button>
        );
      })}
    </div>
  );
}
