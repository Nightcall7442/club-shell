/**
 * Filter row of the library: category chips, the "installed only" toggle and the sort segmented control.
 * Plain `data-nav` buttons (not `Tabs`): Up/Down must leave the row for spatial navigation to reach the search
 * field and the grid; LB/RB cycling is wired by the screen through {@link cycleCategory}.
 */
import type { GamesSort } from '@clubshell/contracts';
import { useCallback, useRef, useState } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Popover } from '@/components/ui/Popover';
import { useOverflowFade } from '@/hooks/useOverflowFade';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';

export interface CategoriesProps {
  categories: string[];
  /** Selected category; `null` = all. */
  value: string | null;
  onChange: (category: string | null) => void;
  installedOnly: boolean;
  onInstalledOnlyChange: (installedOnly: boolean) => void;
  sort: GamesSort;
  onSortChange: (sort: GamesSort) => void;
  className?: string;
}

/** Localized category name (`games.cat.<id>`), falling back to the raw id from the catalogue. */
export function categoryLabel(t: TFunction, category: string): string {
  return t(`games.cat.${category}`, { defaultValue: category });
}

/** Next/previous category in the chip order (`null` = "all" comes first); wraps around. */
export function cycleCategory(categories: string[], value: string | null, dir: 'prev' | 'next'): string | null {
  const order: (string | null)[] = [null, ...categories];
  const idx = Math.max(0, order.indexOf(value));
  const next = order[(idx + (dir === 'next' ? 1 : -1) + order.length) % order.length];
  return next ?? null;
}

const SORTS: readonly { key: GamesSort; labelKey: string }[] = [
  { key: 'popularity', labelKey: 'games.sortPopularity' },
  { key: 'title', labelKey: 'games.sortTitle' },
  { key: 'lastPlayed', labelKey: 'games.sortLastPlayed' },
];

const CHIP =
  'focus-ring relative isolate inline-flex shrink-0 select-none items-center whitespace-nowrap rounded-md font-medium leading-none transition-colors duration-[var(--dur-base)]';
const CHIP_ON = 'text-text';
const CHIP_OFF = 'text-muted hover:text-text';

/** The active pill slides between chips of the same group (`layoutId`), so a filter change reads as one motion. */
function Pill({ id, className }: { id: string; className?: string }): JSX.Element {
  const animations = useThemeStore(selectAnimationsEnabled);
  return (
    <motion.span
      layoutId={id}
      aria-hidden="true"
      className={clsx('absolute inset-0 -z-10 rounded-md', className)}
      transition={animations ? { type: 'spring', stiffness: 520, damping: 42, mass: 0.8 } : { duration: 0 }}
    />
  );
}

function CheckIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M5 12l5 5L19 7" />
    </svg>
  );
}

export function Categories({
  categories,
  value,
  onChange,
  installedOnly,
  onInstalledOnlyChange,
  sort,
  onSortChange,
  className,
}: CategoriesProps): JSX.Element {
  const { t } = useTranslation();
  const chips: { key: string; value: string | null; label: string }[] = [
    { key: '__all', value: null, label: t('games.allCategories') },
    ...categories.map((c) => ({ key: c, value: c, label: categoryLabel(t, c) })),
  ];
  const row = useRef<HTMLDivElement>(null);
  useOverflowFade(row, chips.length);

  return (
    <div className={clsx('flex min-w-0 items-center gap-3', className)}>
      <div
        ref={row}
        role="group"
        aria-label={t('games.categories')}
        className="fade-x no-scrollbar flex min-w-0 max-w-full flex-1 items-center gap-1 overflow-x-auto"
      >
        {chips.map((c) => {
          const active = c.value === value;
          return (
            <button
              key={c.key}
              type="button"
              data-nav="true"
              aria-pressed={active}
              onClick={() => onChange(c.value)}
              className={clsx(CHIP, 'h-10 gap-2 px-4 text-[0.95rem]', active ? CHIP_ON : CHIP_OFF)}
            >
              {active && <Pill id="games-category-pill" className="bg-text/10" />}
              {c.label}
            </button>
          );
        })}
      </div>
      {/* A filter, so it reads as one: a box that gets ticked, not a chip beside the sort options. */}
      <Button
        variant={installedOnly ? 'primary' : 'ghost'}
        size="md"
        aria-pressed={installedOnly}
        icon={installedOnly ? <CheckIcon /> : <BoxIcon />}
        onClick={() => onInstalledOnlyChange(!installedOnly)}
        className="shrink-0 rounded-md text-sm"
      >
        {t('games.installedOnly')}
      </Button>
      <SortMenu value={sort} onChange={onSortChange} />
    </div>
  );
}

function BoxIcon(): JSX.Element {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
      <rect x="4" y="4" width="16" height="16" rx="4" />
    </svg>
  );
}

function SortIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M4 7h16M7 12h10M10 17h4" />
    </svg>
  );
}

/**
 * Sort as one button naming the current order, with the three orders in a list under it. As a segmented row it took
 * a third of the toolbar and looked like more category chips; this leaves that width to the categories.
 */
function SortMenu({ value, onChange }: { value: GamesSort; onChange: (sort: GamesSort) => void }): JSX.Element {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const close = useCallback(() => setOpen(false), []);
  const current = SORTS.find((s) => s.key === value) ?? SORTS[0];
  const currentLabel = current ? t(current.labelKey) : '';

  return (
    <Popover
      open={open}
      onClose={close}
      label={t('games.sort')}
      className="w-56"
      trigger={
        <Button
          variant="ghost"
          size="md"
          data-popover-trigger="true"
          aria-haspopup="listbox"
          aria-expanded={open}
          aria-label={`${t('games.sort')}: ${currentLabel}`}
          icon={<SortIcon />}
          onClick={() => setOpen((v) => !v)}
          className="shrink-0 rounded-md text-sm"
        >
          {currentLabel}
        </Button>
      }
    >
      <ul role="listbox" aria-label={t('games.sort')} className="flex flex-col gap-1">
        {SORTS.map((s) => {
          const active = s.key === value;
          return (
            <li key={s.key} role="none">
              <button
                type="button"
                role="option"
                aria-selected={active}
                data-nav="true"
                onClick={() => {
                  setOpen(false);
                  onChange(s.key);
                }}
                className={clsx(
                  'focus-ring flex w-full items-center justify-between gap-3 rounded-md px-3 py-2 text-left text-base transition-colors duration-[var(--dur-fast)]',
                  active ? 'bg-primary/15 text-primary' : 'text-text hover:bg-text/10',
                )}
              >
                {t(s.labelKey)}
                {active && (
                  <span className="inline-flex h-4 w-4 shrink-0">
                    <CheckIcon />
                  </span>
                )}
              </button>
            </li>
          );
        })}
      </ul>
    </Popover>
  );
}

export default Categories;
