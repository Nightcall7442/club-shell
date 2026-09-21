/**
 * Full-screen launch progress dialog driven by the games store: visible while `launching` is set, stays 1.5 s
 * after the game reports `running`, and turns into an error state (retry / close) when the launch fails.
 * Also exports {@link launchGame}, the one launch entry point every screen uses (pre-checks + mapped toasts).
 */
import type { Game, LaunchResult } from '@clubshell/contracts';
import { useEffect, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { GameArtwork } from '@/components/media/GameArtwork';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Spinner } from '@/components/ui/Spinner';
import { useGamepad } from '@/hooks/useGamepad';
import i18n from '@/i18n';
import { chime } from '@/lib/sound';
import { toShellApiError } from '@/lib/tauri';
import { useGamesStore } from '@/store/games';
import { describeError, useNotificationsStore } from '@/store/notifications';
import { useSessionStore } from '@/store/session';

// ---------------------------------------------------------------------------------------------------------------------
// Launch entry point
// ---------------------------------------------------------------------------------------------------------------------

/** Error → user message with launch-specific detail (anti-cheat reason, age rule). */
export function describeLaunchError(e: unknown, game?: Game | null): string {
  const err = toShellApiError(e);
  const details = (typeof err.details === 'object' && err.details !== null ? err.details : {}) as Record<
    string,
    unknown
  >;
  if (err.code === 'antiCheatBlocked' && typeof details['reason'] === 'string') {
    const key = `games.antiCheatReason.${details['reason']}`;
    if (i18n.exists(key)) {
      return `${describeError(err)} — ${i18n.t(key)}`;
    }
  }
  if (err.code === 'policyDenied' && details['rule'] === 'ageRating' && game) {
    return i18n.t('games.ageWarning', { age: game.ageRating });
  }
  return describeError(err);
}

/**
 * Launches a game through the store. Returns the result, or `null` when a pre-check failed or the Agent rejected
 * (a toast with the mapped message is shown in both cases; the overlay shows the error state as well).
 */
export async function launchGame(gameId: string): Promise<LaunchResult | null> {
  const games = useGamesStore.getState();
  const notify = useNotificationsStore.getState();
  const game = games.byId.get(gameId) ?? null;
  if (useSessionStore.getState().state !== 'active') {
    notify.push({
      title: i18n.t('games.sessionRequired'),
      level: 'warning',
      action: { label: i18n.t('session.addTime'), command: '/wallet', args: null },
    });
    return null;
  }
  if (game && !game.installed) {
    notify.push({ title: i18n.t('games.launchFailed'), body: i18n.t('errors.gameNotInstalled'), level: 'error' });
    return null;
  }
  if (games.running.some((r) => r.gameId === gameId)) {
    notify.push({
      title: i18n.t('desktop.gameRunning', { title: game?.title ?? gameId }),
      level: 'info',
      ttlSec: 4,
    });
    return null;
  }
  try {
    return await games.launch(gameId);
  } catch (e) {
    notify.push({
      title: i18n.t('games.launchFailed'),
      body: describeLaunchError(e, game),
      level: 'error',
      source: 'system',
    });
    return null;
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Overlay
// ---------------------------------------------------------------------------------------------------------------------

export type LaunchStep = 'antiCheat' | 'account' | 'launcher' | 'window';
export type LaunchPhase = 'launching' | 'running' | 'error';

interface LaunchView {
  phase: LaunchPhase;
  gameId: string;
  title: string;
  startedAt: number;
}

const STEP_MS = 800;
const RUNNING_LINGER_MS = 1500;
const SLOW_AFTER_MS = 45_000;

/** Ordered steps of a launch; the account step only exists for pooled-account games. */
export function launchSteps(needsAccount: boolean): LaunchStep[] {
  return needsAccount ? ['antiCheat', 'account', 'launcher', 'window'] : ['antiCheat', 'launcher', 'window'];
}

/** Index of the active step: time-based until the Agent returns a pid, then "waiting for the window". */
export function launchStepIndex(steps: LaunchStep[], elapsedMs: number, pidKnown: boolean): number {
  if (pidKnown) {
    return steps.length - 1;
  }
  return Math.min(steps.length - 2, Math.max(0, Math.floor(elapsedMs / STEP_MS)));
}

function CheckIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      className="h-5 w-5"
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

function AlertIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      className="h-6 w-6"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M12 9v4m0 4h.01M10.3 3.9L1.8 18a2 2 0 001.7 3h17a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z" />
    </svg>
  );
}

export function LaunchOverlay(): JSX.Element {
  const { t } = useTranslation();
  const launching = useGamesStore((s) => s.launching);
  const launchError = useGamesStore((s) => s.launchError);
  const running = useGamesStore((s) => s.running);
  const byId = useGamesStore((s) => s.byId);
  const kill = useGamesStore((s) => s.kill);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [view, setView] = useState<LaunchView | null>(null);
  const [now, setNow] = useState(() => Date.now());

  // Store → view-phase transitions. `launch()` clears `launchError`, so a non-null error after `launching`
  // dropped to null always belongs to this launch.
  useEffect(() => {
    if (launching) {
      setView((v) =>
        v && v.phase === 'launching' && v.gameId === launching.gameId
          ? v
          : { phase: 'launching', gameId: launching.gameId, title: launching.title, startedAt: launching.startedAt },
      );
      return;
    }
    setView((v) => {
      if (!v || v.phase !== 'launching') {
        return v;
      }
      if (launchError) {
        return { ...v, phase: 'error' };
      }
      const r = running.find((x) => x.gameId === v.gameId);
      return r && r.state === 'running' ? { ...v, phase: 'running' } : null;
    });
  }, [launching, launchError, running]);

  const phase = view?.phase ?? null;

  useEffect(() => {
    if (phase !== 'launching') {
      return undefined;
    }
    setNow(Date.now());
    const id = setInterval(() => setNow(Date.now()), 250);
    return () => clearInterval(id);
  }, [phase]);

  useEffect(() => {
    if (phase !== 'running') {
      return undefined;
    }
    chime();
    const id = setTimeout(() => setView(null), RUNNING_LINGER_MS);
    return () => clearTimeout(id);
  }, [phase]);

  const close = (): void => setView(null);

  const cancel = async (): Promise<void> => {
    if (!view) {
      return;
    }
    try {
      await kill(view.gameId);
    } catch (e) {
      pushError(e, t('games.killTitle'));
    }
    setView(null);
  };

  const retry = (): void => {
    if (view) {
      void launchGame(view.gameId);
    }
  };

  useGamepad({
    enabled: view !== null,
    onBack: () => {
      if (phase === 'error') {
        close();
      }
    },
  });

  const game = view ? (byId.get(view.gameId) ?? null) : null;
  const steps = launchSteps(game?.requiresAccount ?? true);
  const elapsed = view ? Math.max(0, now - view.startedAt) : 0;
  const stepIndex = phase === 'running' ? steps.length : launchStepIndex(steps, elapsed, launching?.pid != null);
  const percent = phase === 'running' ? 100 : ((stepIndex + 0.5) / steps.length) * 100;
  const slow = phase === 'launching' && elapsed > SLOW_AFTER_MS;

  const title =
    phase === 'error'
      ? t('games.launchFailed')
      : phase === 'running'
        ? t('games.launchReady')
        : t('games.launchTitle', { title: view?.title ?? '' });

  const footer =
    phase === 'error' ? (
      <>
        <Button variant="secondary" size="lg" onClick={close}>
          {t('common.close')}
        </Button>
        <Button variant="primary" size="lg" onClick={retry}>
          {t('common.retry')}
        </Button>
      </>
    ) : phase === 'launching' ? (
      <Button variant="danger" size="lg" onClick={() => void cancel()}>
        {t('games.launchCancel')}
      </Button>
    ) : undefined;

  return (
    <Modal
      open={view !== null}
      onClose={close}
      title={title}
      size="md"
      showClose={phase === 'error'}
      closeOnBackdrop={false}
      closeOnEscape={phase === 'error'}
      danger={phase === 'error'}
      footer={footer}
    >
      <div className="flex gap-6">
        <div className="w-32 shrink-0 self-start">
          <GameArtwork src={game?.coverUrl} title={view?.title ?? ''} kind="cover" priority className="rounded-lg" />
        </div>
        <div className="flex min-w-0 flex-1 flex-col gap-4">
          {phase === 'error' ? (
            <div role="alert" className="flex items-start gap-3 rounded-lg bg-danger/10 p-4 text-danger">
              <span className="mt-0.5 shrink-0">
                <AlertIcon />
              </span>
              <p className="text-lg leading-snug text-text">
                {launchError ? describeLaunchError(launchError, game) : t('errors.generic')}
              </p>
            </div>
          ) : (
            <>
              <ol aria-live="polite" className="flex flex-col gap-2">
                {steps.map((step, i) => {
                  const done = i < stepIndex;
                  const active = i === stepIndex && phase === 'launching';
                  return (
                    <li
                      key={step}
                      aria-current={active ? 'step' : undefined}
                      className={clsx(
                        'flex items-center gap-3 text-lg transition-colors duration-[var(--dur-base)]',
                        done ? 'text-success' : active ? 'text-text' : 'text-muted',
                      )}
                    >
                      <span className="inline-flex h-7 w-7 shrink-0 items-center justify-center">
                        {done ? (
                          <CheckIcon />
                        ) : active ? (
                          <Spinner size="sm" inherit />
                        ) : (
                          <span aria-hidden="true" className="h-2 w-2 rounded-full bg-current opacity-50" />
                        )}
                      </span>
                      <span className={clsx(active && 'font-semibold')}>{t(`games.launchStep.${step}`)}</span>
                    </li>
                  );
                })}
              </ol>
              <ProgressBar
                value={percent}
                tone={phase === 'running' ? 'success' : 'primary'}
                size="md"
                label={t('games.launching')}
              />
              <p className="text-base text-muted">{slow ? t('games.launchTimeout') : t('games.launchHint')}</p>
              {slow && <p className="text-sm text-muted">{t('games.launchSlowHint')}</p>}
            </>
          )}
        </div>
      </div>
    </Modal>
  );
}

export default LaunchOverlay;
