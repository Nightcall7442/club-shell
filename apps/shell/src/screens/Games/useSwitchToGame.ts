/**
 * «Перейти к игре» on a running game (Home's hero, the game's page): its window to the front, the Shell behind it.
 * A second «Играть» on a running game used to be refused by the Agent (`alreadyRunning`); this is what the player wants
 * instead.
 */
import { useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { focusProgram, toPrograms } from '@/hooks/useRunningPrograms';
import { useGamesStore } from '@/store/games';
import { useNotificationsStore } from '@/store/notifications';

export function useSwitchToGame(): (gameId: string) => Promise<void> {
  const { t } = useTranslation();
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  return useCallback(
    async (gameId: string): Promise<void> => {
      const running = useGamesStore.getState().running.filter((r) => r.gameId === gameId);
      const program = toPrograms(running, [], [])[0];
      if (!program) {
        return;
      }
      try {
        if (!(await focusProgram(program))) {
          push({
            id: `switch-${gameId}`,
            title: t('desktop.dock.switchFailed'),
            body: t(program.starting ? 'running.starting' : 'running.noWindow'),
            level: 'warning',
            source: 'local',
          });
        }
      } catch (e) {
        pushError(e, t('desktop.dock.switchFailed'));
      }
    },
    [push, pushError, t],
  );
}
