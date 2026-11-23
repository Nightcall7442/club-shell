/**
 * «Запущено»: the games and apps the player has running, each with «Перейти» (its window to the front, the Shell
 * behind it) and «Закрыть» (asked twice: a game's progress or a browser's tabs go with it). On Home and the Apps
 * screen with a line on how to come back from a program; in the HUD (`variant="hud"`) compact, and it hides the HUD
 * once a program is in front. Renders nothing while nothing runs.
 */
import { useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { useResolvedAsset } from '@/components/media/GameArtwork';
import { Button } from '@/components/ui/Button';
import { closeProgram, focusProgram, useRunningPrograms, type RunningProgram } from '@/hooks/useRunningPrograms';
import { initials } from '@/lib/format';
import { log } from '@/lib/logger';
import { describeError } from '@/store/notifications';

/** How long «Закрыть» waits for the second press. */
const CONFIRM_MS = 4000;

export interface RunningStripProps {
  /** `page`: Home and Apps, with the "how to come back" line; `hud`: the in-game quick panel. */
  variant?: 'page' | 'hud';
  /** The "how to come back" line next to the title (`page` only; the Apps screen has it in its header). */
  hint?: boolean;
  /** A program was brought to the front (the HUD hides itself). */
  onSwitched?: () => void;
  className?: string;
}

const svg = {
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 2,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
  'aria-hidden': true,
  className: 'h-5 w-5',
} as const;
const SwitchIcon = (): JSX.Element => (
  <svg {...svg}>
    <path d="M14 4h6v6M20 4l-8 8M10 5H6a2 2 0 0 0-2 2v11a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2v-4" />
  </svg>
);
const CloseIcon = (): JSX.Element => (
  <svg {...svg}>
    <path d="M6 6l12 12M18 6L6 18" />
  </svg>
);

function ProgramIcon({ program }: { program: RunningProgram }): JSX.Element {
  const { url } = useResolvedAsset(program.iconUrl);
  const [failed, setFailed] = useState(false);
  useEffect(() => setFailed(false), [url]);
  return (
    <span className="flex h-10 w-10 shrink-0 items-center justify-center overflow-hidden rounded-md bg-bg/60">
      {url && !failed ? (
        <img
          src={url}
          alt=""
          draggable={false}
          onError={() => setFailed(true)}
          className="h-full w-full object-cover"
        />
      ) : (
        <span aria-hidden="true" className="text-sm font-bold text-primary">
          {initials(program.title)}
        </span>
      )}
    </span>
  );
}

function ProgramRow({
  program,
  compact,
  onSwitched,
  onChanged,
}: {
  program: RunningProgram;
  compact: boolean;
  onSwitched?: () => void;
  onChanged: () => void;
}): JSX.Element {
  const { t } = useTranslation();
  const [busy, setBusy] = useState<'switch' | 'close' | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const timer = useRef<number | undefined>(undefined);
  useEffect(() => () => window.clearTimeout(timer.current), []);

  const switchTo = async (): Promise<void> => {
    setBusy('switch');
    setNote(null);
    try {
      if (await focusProgram(program)) {
        onSwitched?.();
      } else {
        setNote(t(program.starting ? 'running.starting' : 'running.noWindow'));
      }
    } catch (e) {
      log.warn(`running: switch to ${program.key}`, e);
      setNote(describeError(e));
    } finally {
      setBusy(null);
    }
  };

  const close = async (): Promise<void> => {
    if (!confirming) {
      setConfirming(true);
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => setConfirming(false), CONFIRM_MS);
      return;
    }
    window.clearTimeout(timer.current);
    setConfirming(false);
    setBusy('close');
    setNote(null);
    try {
      await closeProgram(program);
    } catch (e) {
      log.warn(`running: close ${program.key}`, e);
      setNote(describeError(e));
    } finally {
      setBusy(null);
      onChanged();
    }
  };

  const kind = t(program.kind === 'game' ? 'running.game' : 'running.app');
  return (
    <li
      aria-label={`${program.title} (${kind})`}
      className={clsx('glass flex items-center gap-3 rounded-lg', compact ? 'py-2 pl-2 pr-2' : 'py-2.5 pl-3 pr-2.5')}
    >
      <ProgramIcon program={program} />
      <span className="flex min-w-0 flex-1 flex-col leading-tight">
        <span className="truncate text-base font-semibold text-text">{program.title}</span>
        <span
          className={clsx('truncate text-sm', note ? 'text-accent' : 'text-muted')}
          role={note ? 'status' : undefined}
        >
          {note ?? (program.starting ? t('running.starting') : kind)}
        </span>
      </span>
      <Button
        variant="secondary"
        size="md"
        icon={<SwitchIcon />}
        loading={busy === 'switch'}
        disabled={busy !== null || program.starting}
        onClick={() => void switchTo()}
        aria-label={`${t('running.switchTo')} ${program.title}`}
      >
        {t('running.switchTo')}
      </Button>
      <Button
        variant={confirming ? 'danger' : 'ghost'}
        size="md"
        icon={<CloseIcon />}
        loading={busy === 'close'}
        disabled={busy !== null}
        onClick={() => void close()}
        aria-label={
          confirming ? `${t('running.confirmClose')} ${program.title}` : `${t('running.close')} ${program.title}`
        }
      >
        {confirming ? t('running.confirmClose') : t('running.close')}
      </Button>
    </li>
  );
}

export function RunningStrip({
  variant = 'page',
  hint = true,
  onSwitched,
  className,
}: RunningStripProps): JSX.Element | null {
  const { t } = useTranslation();
  const { programs, refresh } = useRunningPrograms();
  if (programs.length === 0) {
    return null;
  }
  const hud = variant === 'hud';
  return (
    <section aria-label={t('running.title')} className={clsx('flex flex-col gap-2', className)}>
      <div className="flex flex-wrap items-baseline justify-between gap-x-6 gap-y-1">
        <h2 className={hud ? 'hud-label' : 'font-display text-xl font-normal tracking-tight text-text'}>
          {t('running.title')}
        </h2>
        {!hud && hint && <p className="text-sm text-muted">{t('running.backHint')}</p>}
      </div>
      <ul
        className={clsx(
          'grid gap-2',
          hud ? 'grid-cols-[repeat(auto-fill,minmax(22rem,1fr))]' : 'grid-cols-[repeat(auto-fill,minmax(26rem,1fr))]',
        )}
      >
        {programs.map((p) => (
          <ProgramRow key={p.key} program={p} compact={hud} onSwitched={onSwitched} onChanged={refresh} />
        ))}
      </ul>
    </section>
  );
}
