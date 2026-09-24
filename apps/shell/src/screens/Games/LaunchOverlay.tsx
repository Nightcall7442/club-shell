/**
 * The launch sequence, driven by the games store: a full-screen HUD over the game's key art while `launching` is set
 * (brackets closing in on the screen, telemetry rows per step, a tick scale with the percentage), a "ready" beat
 * that stays 1.5 s after the game reports `running`, and a plain error dialog (retry / close) when the launch fails.
 * Also exports {@link launchGame}, the one launch entry point every screen uses (pre-checks + mapped toasts).
 */
import type { Game, LaunchResult } from '@clubshell/contracts';
import { useEffect, useState, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { AnimatePresence, motion } from 'framer-motion';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { GameArtwork } from '@/components/media/GameArtwork';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { useGamepad } from '@/hooks/useGamepad';
import i18n from '@/i18n';
import { chime } from '@/lib/sound';
import { toShellApiError } from '@/lib/tauri';
import { launcherLabelKey } from '@/screens/Games/GameCard';
import { useGamesStore } from '@/store/games';
import { describeError, useNotificationsStore } from '@/store/notifications';
import { useSessionStore } from '@/store/session';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';

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
      <Button variant="secondary" size="lg" autoFocus onClick={() => void cancel()}>
        {t('games.launchCancel')}
      </Button>
    ) : undefined;

  if (phase === 'error' || phase === null) {
    return (
      <Modal
        open={phase === 'error'}
        onClose={close}
        title={title}
        size="md"
        showClose
        closeOnBackdrop={false}
        closeOnEscape
        danger
        footer={footer}
      >
        <div className="flex gap-6">
          <div className="w-32 shrink-0 self-start">
            <GameArtwork src={game?.coverUrl} title={view?.title ?? ''} kind="cover" priority className="rounded-lg" />
          </div>
          <div role="alert" className="flex flex-1 items-start gap-3 rounded-lg bg-danger/10 p-4 text-danger">
            <span className="mt-0.5 shrink-0">
              <AlertIcon />
            </span>
            <p className="text-lg leading-snug text-text">
              {launchError ? describeLaunchError(launchError, game) : t('errors.generic')}
            </p>
          </div>
        </div>
      </Modal>
    );
  }

  return (
    <LaunchSequence
      title={title}
      gameTitle={view?.title ?? ''}
      art={game ? (game.heroUrl ?? game.coverUrl) : null}
      launcher={game ? t(launcherLabelKey(game.launcher)) : ''}
      steps={steps.map((step, i) => ({
        key: step,
        label: t(`games.launchStep.${step}`),
        state: i < stepIndex ? 'done' : i === stepIndex && phase === 'launching' ? 'active' : 'pending',
      }))}
      percent={percent}
      ready={phase === 'running'}
      hint={slow ? t('games.launchTimeout') : t('games.launchHint')}
      slowHint={slow ? t('games.launchSlowHint') : null}
      footer={footer}
    />
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Sequence
// ---------------------------------------------------------------------------------------------------------------------

interface SequenceStep {
  key: string;
  label: string;
  state: 'done' | 'active' | 'pending';
}

interface LaunchSequenceProps {
  title: string;
  gameTitle: string;
  art: string | null;
  launcher: string;
  steps: SequenceStep[];
  percent: number;
  ready: boolean;
  hint: string;
  slowHint: string | null;
  footer: JSX.Element | undefined;
}

/**
 * Full-screen launch HUD: the key art pushing in slowly, four brackets closing in from the middle to the corners of
 * the screen, the title, one telemetry row per step and the progress as a tick scale with a dot-matrix percentage.
 */
function LaunchSequence({
  title,
  gameTitle,
  art,
  launcher,
  steps,
  percent,
  ready,
  hint,
  slowHint,
  footer,
}: LaunchSequenceProps): JSX.Element {
  const { t } = useTranslation();
  const animations = useThemeStore(selectAnimationsEnabled);
  const ease = [0.16, 1, 0.3, 1] as const;
  const d = (s: number): number => (animations ? s : 0);

  return createPortal(
    <motion.div
      role="dialog"
      aria-modal="true"
      aria-label={title}
      className="fixed inset-0 z-[100] overflow-hidden bg-bg"
      initial={{ opacity: 0 }}
      animate={{ opacity: 1 }}
      transition={{ duration: d(0.3) }}
    >
      {/* Key art, pushing in for the whole launch */}
      <div aria-hidden="true" className="absolute inset-0">
        {art && (
          <motion.div
            className="absolute inset-0"
            initial={{ scale: 1.12, opacity: 0 }}
            animate={{ scale: 1.02, opacity: 1 }}
            transition={{ scale: { duration: d(14), ease: 'linear' }, opacity: { duration: d(0.8) } }}
          >
            <GameArtwork
              src={art}
              title={gameTitle}
              kind="hero"
              priority
              className="rounded-none"
              style={{ position: 'absolute', inset: 0, height: '100%', aspectRatio: 'auto' }}
            />
          </motion.div>
        )}
        <div className="absolute inset-0 bg-gradient-to-r from-bg via-bg/75 to-bg/10" />
        <div className="absolute inset-x-0 bottom-0 h-2/3 bg-gradient-to-t from-bg via-bg/70 to-transparent" />
        <div className="hud-grid absolute inset-0 opacity-70" />
      </div>

      {/* Brackets: from the middle of the screen out to its corners */}
      <motion.div
        aria-hidden="true"
        className="hud-brackets pointer-events-none absolute [--brk-size:28px]"
        initial={{ inset: '38% 42%' }}
        animate={{ inset: '20px 20px' }}
        transition={{ duration: d(0.9), ease }}
      />

      <div className="relative flex h-full flex-col justify-end gap-10 px-[calc(var(--gutter)*2)] pb-[calc(var(--gutter)*2)]">
        <motion.div
          className="flex max-w-[60rem] flex-col gap-4"
          initial={{ opacity: 0, y: 24 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: d(0.7), delay: d(0.25), ease }}
        >
          <span className="hud-label text-accent">
            {ready ? t('games.launchReady') : t('games.launching')} · {launcher}
          </span>
          <h1 className="font-display text-[clamp(3rem,6vw,7rem)] font-normal leading-[0.95] tracking-[-0.03em] text-text">
            {gameTitle}
          </h1>
        </motion.div>

        <div className="grid grid-cols-[minmax(0,1fr)_auto] items-end gap-[calc(var(--gutter)*2)]">
          <div className="flex flex-col gap-6">
            {/* Telemetry: one row per step, in launch order */}
            <ol aria-live="polite" className="flex max-w-[40rem] flex-col">
              {steps.map((s, i) => (
                <motion.li
                  key={s.key}
                  aria-current={s.state === 'active' ? 'step' : undefined}
                  initial={{ opacity: 0, x: -12 }}
                  animate={{ opacity: 1, x: 0 }}
                  transition={{ duration: d(0.4), delay: d(0.45 + i * 0.08), ease }}
                  className={clsx(
                    'grid grid-cols-[3rem_1fr_auto] items-center gap-4 border-b border-[color:var(--hairline)] py-2.5 transition-colors duration-[var(--dur-base)]',
                    s.state === 'pending' ? 'text-muted' : 'text-text',
                  )}
                >
                  <span className="font-mono text-xs text-muted">{String(i + 1).padStart(2, '0')}</span>
                  <span className={clsx('text-lg', s.state === 'active' && 'font-semibold')}>{s.label}</span>
                  <span
                    className={clsx(
                      'font-mono text-xs uppercase tracking-[0.14em]',
                      s.state === 'done' ? 'text-success' : s.state === 'active' ? 'text-accent' : 'text-muted/60',
                    )}
                  >
                    {s.state === 'done' ? (
                      <span className="inline-flex items-center gap-1.5">
                        <span className="inline-flex h-4 w-4 [&>svg]:h-full [&>svg]:w-full">
                          <CheckIcon />
                        </span>
                        OK
                      </span>
                    ) : s.state === 'active' ? (
                      <span className="anim-live-dot">···</span>
                    ) : (
                      '—'
                    )}
                  </span>
                </motion.li>
              ))}
            </ol>
            <div className="flex max-w-[40rem] items-end gap-6">
              <span
                role="progressbar"
                aria-label={t('games.launching')}
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={Math.round(percent)}
                className={clsx('tick-scale block h-3 flex-1', ready ? 'text-success' : 'text-accent')}
                style={{ '--value': percent / 100 } as CSSProperties}
              />
              <span className="num-dot w-[5.5ch] text-right text-5xl leading-none text-text">
                {Math.round(percent)}%
              </span>
            </div>
            <div className="flex flex-col gap-1">
              <p className="text-base text-muted">{hint}</p>
              {slowHint && <p className="text-sm text-muted">{slowHint}</p>}
            </div>
          </div>
          <div className="flex items-center gap-3">{footer}</div>
        </div>
      </div>

      <AnimatePresence>
        {ready && (
          <motion.div
            aria-hidden="true"
            className="pointer-events-none absolute inset-0 bg-accent/10"
            initial={{ opacity: 0 }}
            animate={{ opacity: [0, 1, 0] }}
            transition={{ duration: d(0.9), times: [0, 0.2, 1] }}
          />
        )}
      </AnimatePresence>
    </motion.div>,
    document.body,
  );
}

export default LaunchOverlay;
