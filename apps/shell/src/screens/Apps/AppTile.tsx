/**
 * One launchable application: big icon (resolved through `assetUrl`, initials fallback), title and category.
 * The whole tile is a `data-nav` button; a spinner overlay shows while `apps_launch` is in flight.
 */
import { useEffect, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import type { App } from '@clubshell/contracts';
import { useResolvedAsset } from '@/components/media/GameArtwork';
import { tiltHandlers } from '@/hooks/useTilt';
import { Badge } from '@/components/ui/Badge';
import { Spinner } from '@/components/ui/Spinner';
import { initials } from '@/lib/format';

export interface AppTileProps {
  app: App;
  onLaunch: (app: App) => void;
  /** `apps_launch` in flight for this app. */
  launching?: boolean;
  /** Another launch is running (or the app is blocked). */
  disabled?: boolean;
  className?: string;
}

/** `apps.category.<key>` when translated, else the raw category. */
export function useAppCategoryLabel(): (category: string) => string {
  const { t, i18n } = useTranslation();
  return (category) => (i18n.exists(`apps.category.${category}`) ? t(`apps.category.${category}`) : category);
}

export function AppTile({ app, onLaunch, launching = false, disabled = false, className }: AppTileProps): JSX.Element {
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
      aria-label={blocked ? `${app.title}. ${t('apps.notAllowed')}` : `${t('apps.launch')} ${app.title}`}
      onClick={() => onLaunch(app)}
      {...tiltHandlers(5)}
      className={clsx(
        'focus-ring glass tilt group relative flex flex-col items-center gap-3 overflow-hidden rounded-xl p-5 text-center transition-[background-color] duration-[var(--dur-fast)] ease-[var(--ease-out)]',
        'hover:bg-surface/80 active:[--zoom:0.98] disabled:cursor-not-allowed',
        blocked && 'opacity-50',
        className,
      )}
    >
      <span aria-hidden="true" className="tilt-sheen rounded-xl" />
      <span className="relative flex h-24 w-24 items-center justify-center overflow-hidden rounded-2xl bg-bg/60 shadow-[var(--shadow-card)] transition-transform duration-[var(--dur-base)] group-hover:scale-105 group-focus-visible:scale-105">
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
