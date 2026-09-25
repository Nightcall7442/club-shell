/**
 * "My game settings" tab: the games whose binds, sensitivity and graphics follow the player from PC to PC
 * (`profile.gameSettings`), with when they were last saved and a reset per game. Saving and restoring happen on their
 * own around every launch; this is where the player sees it and can start a game from scratch.
 */
import { useCallback, useEffect, useState } from 'react';
import type { PlayerSettingsItem } from '@clubshell/contracts';
import { useTranslation } from 'react-i18next';
import { GameArtwork } from '@/components/media/GameArtwork';
import { Button } from '@/components/ui/Button';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { useLocale } from '@/hooks/useLocale';
import { formatBytes, formatRelativeDay } from '@/lib/format';
import { api } from '@/lib/tauri';
import { useGamesStore } from '@/store/games';
import { useNotificationsStore } from '@/store/notifications';

function SyncIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M20 11a8 8 0 0 0-14.3-4.9L4 8M4 4v4h4M4 13a8 8 0 0 0 14.3 4.9L20 16M20 20v-4h-4" />
    </svg>
  );
}

export default function GameSettings(): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const gamesById = useGamesStore((s) => s.byId);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [items, setItems] = useState<PlayerSettingsItem[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    api.profile
      .gameSettings()
      .then((list) => alive && setItems(list))
      .catch((e: unknown) => {
        if (alive) setItems([]);
        pushError(e, t('profile.gameSettings.title'));
      });
    return () => {
      alive = false;
    };
  }, [pushError, t]);

  const reset = useCallback(
    async (gameId: string) => {
      setBusy(gameId);
      try {
        setItems(await api.profile.resetGameSettings(gameId));
      } catch (e) {
        pushError(e, t('profile.gameSettings.reset'));
      } finally {
        setBusy(null);
      }
    },
    [pushError, t],
  );

  return (
    <section aria-label={t('profile.gameSettings.title')} className="flex flex-col gap-[var(--gap)]">
      <p className="max-w-[46rem] text-base text-muted">{t('profile.gameSettings.hint')}</p>

      {items === null && (
        <div className="flex flex-col gap-3">
          {Array.from({ length: 3 }, (_, i) => (
            <Skeleton key={i} variant="rect" height="4.5rem" />
          ))}
        </div>
      )}

      {items !== null && items.length === 0 && (
        <EmptyState
          icon={<SyncIcon />}
          title={t('profile.gameSettings.emptyTitle')}
          hint={t('profile.gameSettings.emptyHint')}
        />
      )}

      {items !== null && items.length > 0 && (
        <ul className="glass flex flex-col divide-y divide-[color:var(--hairline)] rounded-xl">
          {items.map((item) => {
            const game = gamesById.get(item.gameId);
            return (
              <li key={item.gameId} className="flex items-center gap-4 px-5 py-3">
                <span className="w-10 shrink-0">
                  <GameArtwork src={game?.coverUrl} title={item.title} kind="cover" className="rounded-md" />
                </span>
                <div className="min-w-0 flex-1">
                  <p className="truncate text-lg font-medium text-text">{item.title}</p>
                  <p className="text-sm text-muted">
                    {t('profile.gameSettings.saved', {
                      when: formatRelativeDay(item.updatedAt, locale),
                      size: formatBytes(item.sizeBytes, locale),
                    })}
                  </p>
                </div>
                <Button
                  variant="ghost"
                  size="md"
                  disabled={busy === item.gameId}
                  onClick={() => void reset(item.gameId)}
                  aria-label={t('profile.gameSettings.resetFor', { title: item.title })}
                >
                  {t('profile.gameSettings.reset')}
                </Button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
