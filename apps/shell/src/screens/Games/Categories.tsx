/**
 * Filter row of the library: category chips, the "installed only" toggle and the sort segmented control.
 * Plain `data-nav` buttons (not `Tabs`): Up/Down must leave the row for spatial navigation to reach the search
 * field and the grid; LB/RB cycling is wired by the screen through {@link cycleCategory}.
 */
import type { GamesSort } from '@clubshell/contracts';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
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
  'focus-ring relative isolate inline-flex shrink-0 select-none items-center whitespace-nowrap rounded-full font-semibold leading-none transition-colors duration-[var(--dur-base)]';
const CHIP_ON = 'text-on-primary';
const CHIP_OFF = 'text-muted hover:text-text';

/** The active pill slides between chips of the same group (`layoutId`), so a filter change reads as one motion. */
function Pill({ id, className }: { id: string; className?: string }): JSX.Element {
  const animations = useThemeStore(selectAnimationsEnabled);
  return (
    <motion.span
      layoutId={id}
      aria-hidden="true"
      className={clsx('absolute inset-0 -z-10 rounded-full', className)}
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

  return (
    <div className={clsx('flex min-w-0 items-center gap-3', className)}>
      <div
        role="group"
        aria-label={t('games.categories')}
        className="glass no-scrollbar flex min-w-0 max-w-full flex-1 items-center gap-1 overflow-x-auto rounded-full p-1"
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
              className={clsx(CHIP, 'h-11 gap-2 px-4 text-base', active ? CHIP_ON : CHIP_OFF)}
            >
              {active && (
                <Pill
                  id="games-category-pill"
                  className="bg-primary shadow-[0_6px_20px_-6px_rgb(var(--c-primary)/0.4)]"
                />
              )}
              {c.label}
            </button>
          );
        })}
      </div>
      <Button
        variant={installedOnly ? 'primary' : 'ghost'}
        size="md"
        aria-pressed={installedOnly}
        icon={installedOnly ? <CheckIcon /> : undefined}
        onClick={() => onInstalledOnlyChange(!installedOnly)}
        className="shrink-0 rounded-full text-sm"
      >
        {t('games.installedOnly')}
      </Button>
      <div role="group" aria-label={t('games.sort')} className="flex shrink-0 items-center gap-1 rounded-full p-1">
        {SORTS.map((s) => {
          const active = s.key === sort;
          return (
            <button
              key={s.key}
              type="button"
              data-nav="true"
              aria-pressed={active}
              onClick={() => onSortChange(s.key)}
              className={clsx(CHIP, 'h-9 px-4 text-sm', active ? 'text-text' : CHIP_OFF)}
            >
              {active && <Pill id="games-sort-pill" className="bg-text/15" />}
              {t(s.labelKey)}
            </button>
          );
        })}
      </div>
    </div>
  );
}

export default Categories;
