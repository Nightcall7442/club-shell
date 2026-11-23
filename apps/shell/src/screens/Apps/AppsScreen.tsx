/**
 * Apps (`/apps`): allowed applications grouped by category → `apps_launch` with a per-tile launching state and
 * a success / error toast. Loads `apps_list` on mount; skeletons while loading, retry on failure, empty state. A member
 * stars a tile into the favourites on Home (D-86). The «Запущено» strip on top lists what runs (switch to / close); a
 * running app's tile says so, and tapping it brings the app to the front instead of opening a second copy.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import type { App } from '@clubshell/contracts';
import { Button } from '@/components/ui/Button';
import { FavoriteButton } from '@/components/ui/FavoriteButton';
import { Skeleton } from '@/components/ui/Skeleton';
import { collectNavigables, focusElement } from '@/hooks/useGamepad';
import { useRunningPrograms } from '@/hooks/useRunningPrograms';
import { api, type ShellError } from '@/lib/tauri';
import { AppTile, useAppCategoryLabel, useAppLaunch } from '@/screens/Apps/AppTile';
import { RunningStrip } from '@/screens/Desktop/RunningStrip';
import { useNotificationsStore } from '@/store/notifications';
import { asShellError } from '@/store/settings';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';

/** Display order of categories; unknown ones follow alphabetically. */
const CATEGORY_ORDER: readonly string[] = ['browser', 'voice', 'media', 'tool', 'other'];

/** Groups allowed apps by category in display order. */
export function groupApps(apps: App[]): { category: string; apps: App[] }[] {
  const groups = new Map<string, App[]>();
  for (const app of apps) {
    if (!app.allowed) {
      continue;
    }
    const list = groups.get(app.category) ?? [];
    list.push(app);
    groups.set(app.category, list);
  }
  const rank = (c: string): number => {
    const i = CATEGORY_ORDER.indexOf(c);
    return i < 0 ? CATEGORY_ORDER.length : i;
  };
  return Array.from(groups, ([category, list]) => ({
    category,
    apps: [...list].sort((a, b) => a.title.localeCompare(b.title)),
  })).sort((a, b) => rank(a.category) - rank(b.category) || a.category.localeCompare(b.category));
}

export default function AppsScreen(): JSX.Element {
  const { t } = useTranslation();
  const categoryLabel = useAppCategoryLabel();
  const animations = useThemeStore(selectAnimationsEnabled);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [apps, setApps] = useState<App[] | null>(null);
  const [error, setError] = useState<ShellError | null>(null);
  const { launchingId, launch } = useAppLaunch();
  const { programs } = useRunningPrograms();
  const runningIds = useMemo(() => new Set(programs.filter((p) => p.kind === 'app').map((p) => p.id)), [programs]);
  const root = useRef<HTMLDivElement>(null);

  const load = useCallback(async () => {
    setError(null);
    setApps(null);
    try {
      setApps(await api.apps.list());
    } catch (e) {
      setError(asShellError(e));
      pushError(e, t('apps.title'));
    }
  }, [pushError, t]);

  useEffect(() => {
    void load();
  }, [load]);

  const groups = useMemo(() => groupApps(apps ?? []), [apps]);

  // Initial focus on the first tile once the list is in.
  useEffect(() => {
    if (!apps) {
      return undefined;
    }
    const frame = requestAnimationFrame(() => {
      const active = document.activeElement;
      if (root.current && (active === null || active === document.body || active.id === 'main')) {
        const first = collectNavigables(root.current)[0];
        if (first) {
          focusElement(first);
        }
      }
    });
    return () => cancelAnimationFrame(frame);
  }, [apps]);

  return (
    <motion.div
      ref={root}
      className="flex w-full flex-col gap-[calc(var(--gap)*1.5)]"
      initial={animations ? { opacity: 0, y: 12 } : false}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: animations ? 0.25 : 0, ease: 'easeOut' }}
    >
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="font-display text-[length:var(--fs-2xl)] font-light leading-[1.2] tracking-tight text-text">
            {t('apps.title')}
          </h1>
          <p className="mt-1 text-base text-muted">{t('running.backHint')}</p>
        </div>
        {error && (
          <Button variant="secondary" onClick={() => void load()}>
            {t('common.retry')}
          </Button>
        )}
      </header>

      <RunningStrip hint={false} />

      {apps === null && !error && (
        <div
          className="grid grid-cols-[repeat(auto-fill,minmax(clamp(160px,11vw,220px),1fr))] gap-[var(--gap)]"
          aria-busy="true"
          aria-label={t('common.loading')}
        >
          {Array.from({ length: 6 }, (_, i) => (
            <Skeleton key={i} variant="rect" height="14rem" />
          ))}
        </div>
      )}

      {error && apps === null && (
        <div className="glass flex flex-col items-start gap-3 rounded-xl p-6">
          <p className="text-lg font-semibold text-text">{t('common.error')}</p>
          <p className="text-base text-muted">{t('common.tryAgain')}</p>
          <Button onClick={() => void load()}>{t('common.retry')}</Button>
        </div>
      )}

      {apps !== null && groups.length === 0 && (
        <div className="glass flex flex-col items-start gap-2 rounded-xl p-6">
          <p className="text-lg font-semibold text-text">{t('apps.empty')}</p>
          <p className="text-base text-muted">{t('apps.notAllowed')}</p>
        </div>
      )}

      {groups.map((group) => (
        <section key={group.category} aria-label={categoryLabel(group.category)} className="flex flex-col gap-3">
          <h2 className="font-display text-xl font-normal text-text tracking-tight">{categoryLabel(group.category)}</h2>
          <div
            role="list"
            className="grid grid-cols-[repeat(auto-fill,minmax(clamp(160px,11vw,220px),1fr))] gap-[var(--gap)]"
          >
            {group.apps.map((app) => (
              <div key={app.id} role="listitem" className="relative">
                <AppTile
                  app={app}
                  onLaunch={(a) => void launch(a)}
                  launching={launchingId === app.id}
                  disabled={launchingId !== null && launchingId !== app.id}
                  running={runningIds.has(app.id)}
                />
                {/* Next to the tile, not in it (no button in a button); an app has no page of its own, so this star
                    is the one keyboard and gamepad reach too. */}
                <FavoriteButton kind="app" id={app.id} title={app.title} className="absolute right-3 top-3" />
              </div>
            ))}
          </div>
        </section>
      ))}
    </motion.div>
  );
}
