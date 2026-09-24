/**
 * In-game quick panel (`kiosk://overlay { kind: "hud" }`, toggled by the `hud` hotkey while a game owns the screen):
 * time left with the ring, balance, "+30 min", call admin and "back to ClubShell". Lives in the overlay webview,
 * which has no session stores, so it reads through `api` and follows `session.updated` / `wallet.updated`.
 * Esc, a click outside the panel or 15 s without input hide it (the Rust side also has a 60 s backstop).
 */
import type { Balance, Session, User } from '@clubshell/contracts';
import { SESSION_OPEN_ENDED, isSessionOpen } from '@clubshell/contracts';
import { useCallback, useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { DotAmount } from '@/components/ui/DotAmount';
import { useLocale } from '@/hooks/useLocale';
import { CRITICAL_MINUTES, WARNING_MINUTES } from '@/hooks/useSession';
import { formatMoney } from '@/lib/format';
import { log } from '@/lib/logger';
import { api, events } from '@/lib/tauri';
import { secondsSince, secondsUntil } from '@/lib/time';
import { Ring, timerLabel, type RingProps } from '@/screens/Desktop/SessionTimer';

const IDLE_HIDE_MS = 15_000;
const EXTEND_MINUTES = 30;

const svgProps = {
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 2,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
  'aria-hidden': true,
} as const;
const PlusIcon = (): JSX.Element => (
  <svg {...svgProps}>
    <path d="M12 5v14M5 12h14" />
  </svg>
);
const BellIcon = (): JSX.Element => (
  <svg {...svgProps}>
    <path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15L6 16Z" />
    <path d="M10 20a2 2 0 0 0 4 0" />
  </svg>
);
const BackIcon = (): JSX.Element => (
  <svg {...svgProps}>
    <path d="M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1v-9Z" />
  </svg>
);
const CloseIcon = (): JSX.Element => (
  <svg {...svgProps}>
    <path d="M6 6l12 12M18 6L6 18" />
  </svg>
);

function secondsLeftOf(s: Session | null): number {
  if (!s || !isSessionOpen(s.state)) return 0;
  if (s.secondsLeft === SESSION_OPEN_ENDED) return SESSION_OPEN_ENDED;
  return s.endsAt ? Math.max(0, secondsUntil(s.endsAt)) : s.secondsLeft;
}

export function HudScreen({ onClose }: { onClose: () => void }): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const [session, setSession] = useState<Session | null>(null);
  const [balance, setBalance] = useState<Balance['amount'] | null>(null);
  const [tick, setTick] = useState(0);
  const [busy, setBusy] = useState<'extend' | 'admin' | null>(null);
  const [note, setNote] = useState<{ text: string; tone: 'ok' | 'err' } | null>(null);
  const first = useRef<HTMLButtonElement>(null);
  const idle = useRef<number | null>(null);

  // Data: one read each, then follow the agent's pushes.
  useEffect(() => {
    let active = true;
    api.session.get().then(
      (s) => active && setSession(s),
      (e: unknown) => log.warn('hud: session', e),
    );
    api.profile.get().then(
      (u: User) => active && setBalance(u.balance),
      (e: unknown) => log.warn('hud: profile', e),
    );
    const offs = [
      events.on('session.updated', (s) => setSession(s)),
      events.on('wallet.updated', (b) => setBalance(b.amount)),
    ];
    return () => {
      active = false;
      offs.forEach((off) => off());
    };
  }, []);

  useEffect(() => {
    const id = setInterval(() => setTick((n) => n + 1), 1000);
    return () => clearInterval(id);
  }, []);

  // Inactivity → hide; any input inside the window restarts the clock.
  const armIdle = useCallback(() => {
    if (idle.current !== null) window.clearTimeout(idle.current);
    idle.current = window.setTimeout(onClose, IDLE_HIDE_MS);
  }, [onClose]);
  useEffect(() => {
    armIdle();
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') {
        e.preventDefault();
        onClose();
      } else {
        armIdle();
      }
    };
    window.addEventListener('keydown', onKey);
    window.addEventListener('pointermove', armIdle, { passive: true });
    window.addEventListener('pointerdown', armIdle, { passive: true });
    return () => {
      if (idle.current !== null) window.clearTimeout(idle.current);
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('pointermove', armIdle);
      window.removeEventListener('pointerdown', armIdle);
    };
  }, [armIdle, onClose]);

  void tick;
  const open = session !== null && isSessionOpen(session.state);

  // Initial focus once the session is known (the primary button is disabled until then).
  const focused = useRef(false);
  useEffect(() => {
    if (session !== null && !focused.current) {
      focused.current = true;
      first.current?.focus({ preventScroll: true });
    }
  }, [session]);
  const openEnded = open && session.secondsLeft === SESSION_OPEN_ENDED;
  const left = secondsLeftOf(session);
  const used = session ? secondsSince(session.startedAt) : 0;
  const total = used + Math.max(0, left);
  const fraction = openEnded ? 1 : total > 0 ? Math.max(0, left) / total : 0;
  const minutes = left / 60;
  const tone: RingProps['tone'] =
    !open || openEnded
      ? 'muted'
      : minutes <= CRITICAL_MINUTES
        ? 'danger'
        : minutes <= WARNING_MINUTES
          ? 'accent'
          : 'primary';
  const label = timerLabel(open, left, used);

  const extend = async (): Promise<void> => {
    if (!session) return;
    setBusy('extend');
    setNote(null);
    try {
      setSession(await api.session.extend(EXTEND_MINUTES));
      setNote({ text: t('session.extended', { minutes: EXTEND_MINUTES }), tone: 'ok' });
    } catch (e) {
      log.warn('hud: extend', e);
      setNote({ text: t('errors.generic'), tone: 'err' });
    } finally {
      setBusy(null);
      armIdle();
    }
  };

  const callAdmin = async (): Promise<void> => {
    setBusy('admin');
    setNote(null);
    try {
      await api.system.callAdmin('help');
      setNote({ text: t('support.called'), tone: 'ok' });
    } catch (e) {
      log.warn('hud: call admin', e);
      setNote({ text: t('errors.generic'), tone: 'err' });
    } finally {
      setBusy(null);
      armIdle();
    }
  };

  const back = async (): Promise<void> => {
    try {
      await api.kiosk.focus();
    } finally {
      onClose();
    }
  };

  return (
    // The whole window takes input while the HUD is up: a click anywhere outside the panel hands it back to the game.
    <div className="fixed inset-0 flex items-end justify-center pb-[7vh]" onPointerDown={onClose}>
      <section
        role="dialog"
        aria-modal="true"
        aria-label={t('kiosk.hudTitle')}
        onPointerDown={(e) => e.stopPropagation()}
        className="anim-toast-in glass-strong hud-brackets relative flex w-[min(62rem,86vw)] items-center gap-6 rounded-xl py-4 pl-6 pr-4 text-text [--brk-inset:6px] [--brk-size:14px]"
      >
        <div className="flex items-center gap-4">
          <Ring
            progress={fraction}
            size={56}
            stroke={3}
            tone={tone}
            label={t('session.timerLabel')}
            valueText={label}
          />
          <div className="leading-tight">
            <div className="hud-label">
              {!open ? t('session.noSession') : openEnded ? t('session.timeUsed') : t('session.timeLeft')}
            </div>
            <div
              className={clsx(
                'tnum num-dot text-3xl',
                tone === 'danger' ? 'timer-critical' : tone === 'accent' ? 'timer-warning' : 'text-text',
              )}
            >
              {label}
            </div>
          </div>
        </div>

        {balance && (
          <div className="border-l border-[color:var(--hairline)] pl-6 leading-tight">
            <div className="hud-label">{t('desktop.balance')}</div>
            <div className="tnum whitespace-nowrap text-xl">
              <DotAmount value={formatMoney(balance, locale)} />
            </div>
          </div>
        )}

        <div className="ml-auto flex items-center gap-2">
          <Button
            ref={first}
            variant="cta"
            size="lg"
            icon={<PlusIcon />}
            disabled={!open || openEnded}
            loading={busy === 'extend'}
            onClick={() => void extend()}
          >
            {t('kiosk.hudExtend')}
          </Button>
          <Button
            variant="secondary"
            size="lg"
            icon={<BellIcon />}
            loading={busy === 'admin'}
            onClick={() => void callAdmin()}
          >
            {t('desktop.callAdmin')}
          </Button>
          <Button variant="secondary" size="lg" icon={<BackIcon />} onClick={() => void back()}>
            {t('kiosk.returnToShell')}
          </Button>
          <Button
            variant="ghost"
            size="lg"
            iconOnly
            aria-label={t('common.close')}
            icon={<CloseIcon />}
            onClick={onClose}
          />
        </div>

        <span
          aria-hidden="true"
          className="pointer-events-none absolute inset-x-10 top-0 h-px bg-gradient-to-r from-transparent via-accent/60 to-transparent"
        />
        <div className="hud-label pointer-events-none absolute -top-8 left-6 flex items-center gap-3">
          <span>{t('kiosk.hudHint')}</span>
          {note && <span className={note.tone === 'ok' ? 'text-success' : 'text-danger'}>{note.text}</span>}
        </div>
      </section>
    </div>
  );
}

export default HudScreen;
