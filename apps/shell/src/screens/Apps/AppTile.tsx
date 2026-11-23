/**
 * One launchable application: big icon (resolved through `assetUrl`, initials fallback), title and category.
 * The whole tile is a `data-nav` button; a spinner overlay shows while `apps_launch` is in flight, a "running" badge
 * while the app runs (a tap then brings it to the front instead of opening a second copy).
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import type { App } from '@clubshell/contracts';
import { useResolvedAsset } from '@/components/media/GameArtwork';
import { Badge } from '@/components/ui/Badge';
import { Spinner } from '@/components/ui/Spinner';
import { track } from '@/lib/analytics';
import { initials } from '@/lib/format';
import { notifyProgramsChanged } from '@/hooks/useRunningPrograms';
import { api } from '@/lib/tauri';
import { useNotificationsStore } from '@/store/notifications';

export interface AppTileProps {
  app: App;
  onLaunch: (app: App) => void;
  /** `apps_launch` in flight for this app. */
  launching?: boolean;
  /** Another launch is running (or the app is blocked). */
  disabled?: boolean;
  /** The app runs already: a tap brings it to the front. */
  running?: boolean;
  className?: string;
}

/**
 * `apps_launch` with a per-app launching state and a success / error toast, shared by the Apps screen and Home's
 * favourites; one launch at a time. An app that runs already comes to the front instead (`alreadyRunning`); either way
 * the toast says how to come back to the Shell.
 */
export function useAppLaunch(): { launchingId: string | null; launch: (app: App) => Promise<void> } {
  const { t } = useTranslation();
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [launchingId, setLaunchingId] = useState<string | null>(null);
  const busy = useRef(false);

  const launch = useCallback(
    async (app: App): Promise<void> => {
      if (busy.current) {
        return;
      }
      busy.current = true;
      setLaunchingId(app.id);
      track('app.launch', { appId: app.id });
      try {
        const res = await api.apps.launch(app.id);
        push({
          id: `app-${app.id}`,
          title: t(res.alreadyRunning ? 'apps.alreadyRunning' : 'apps.launched', { title: app.title }),
          body: t('running.backHint'),
          level: 'success',
          ttlSec: 6,
          source: 'local',
        });
        notifyProgramsChanged();
      } catch (e) {
        pushError(e, t('apps.openFailed', { title: app.title }));
      } finally {
        busy.current = false;
        setLaunchingId(null);
      }
    },
    [push, pushError, t],
  );

  return { launchingId, launch };
}

/** `apps.category.<key>` when translated, else the raw category. */
export function useAppCategoryLabel(): (category: string) => string {
  const { t, i18n } = useTranslation();
  return (category) => (i18n.exists(`apps.category.${category}`) ? t(`apps.category.${category}`) : category);
}

export function AppTile({
  app,
  onLaunch,
  launching = false,
  disabled = false,
  running = false,
  className,
}: AppTileProps): JSX.Element {
  const { t } = useTranslation();
  const categoryLabel = useAppCategoryLabel();
  const { url } = useResolvedAsset(app.iconUrl);
  const [failed, setFailed] = useState(false);
  useEffect(() => setFailed(false), [url]);
  const blocked = !app.allowed;

  return (
    <button
      type="button"
      data-nav="true"
      disabled={disabled || blocked || launching}
      aria-busy={launching || undefined}
      aria-label={
        blocked
          ? `${app.title}. ${t('apps.notAllowed')}`
          : `${t(running ? 'running.switchTo' : 'apps.launch')} ${app.title}`
      }
      onClick={() => onLaunch(app)}
      className={clsx(
        'focus-ring hud-focus glass group relative flex w-full flex-col items-center gap-3 rounded-xl p-5 text-center transition-[background-color] duration-[var(--dur-fast)] ease-[var(--ease-out)]',
        'hover:bg-text/[0.04] disabled:cursor-not-allowed',
        blocked && 'opacity-50',
        className,
      )}
    >
      <span className="relative flex h-24 w-24 items-center justify-center overflow-hidden rounded-xl bg-bg/60 transition-transform duration-[var(--dur-base)] group-hover:scale-105 group-focus-visible:scale-105">
        {url && !failed ? (
          <img
            src={url}
            alt={t('apps.iconAlt', { title: app.title })}
            draggable={false}
            loading="lazy"
            decoding="async"
            onError={() => setFailed(true)}
            className="h-full w-full object-cover"
          />
        ) : (
          <span aria-hidden="true" className="text-3xl font-bold text-primary">
            {initials(app.title)}
          </span>
        )}
        {launching && (
          <span className="absolute inset-0 flex items-center justify-center bg-bg/70">
            <Spinner size="md" label={t('apps.launching')} />
          </span>
        )}
      </span>
      <span className="flex min-w-0 w-full flex-col items-center gap-1">
        <span className="w-full truncate text-lg font-bold leading-tight text-text">{app.title}</span>
        <span className="w-full truncate text-sm text-muted">{categoryLabel(app.category)}</span>
      </span>
      {blocked ? (
        <Badge tone="danger" size="sm">
          {t('apps.notAllowed')}
        </Badge>
      ) : running && !launching ? (
        <Badge tone="primary" size="sm" solid live>
          {t('apps.running')}
        </Badge>
      ) : (
        <Badge
          tone="primary"
          size="sm"
          className="opacity-0 transition-opacity duration-[var(--dur-fast)] group-hover:opacity-100 group-focus-visible:opacity-100 group-data-[focused=true]:opacity-100"
        >
          {launching ? t('apps.launching') : t('apps.launch')}
        </Badge>
      )}
    </button>
  );
}
