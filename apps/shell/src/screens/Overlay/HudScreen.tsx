/**
 * In-game quick panel (`kiosk://overlay { kind: "hud" }`, toggled by the `hud` hotkey while a game owns the screen):
 * time left with the ring, balance, "+30 min" (prepaid, non-package time only: the server's price, then a confirm), call
 * admin and "back to ClubShell". Lives in the overlay webview,
 * which has no session stores, so it reads through `api` and follows `session.updated` / `wallet.updated`.
 * Esc, a click outside the panel or 15 s without input hide it (the Rust side also has a 60 s backstop).
 */
import type { Balance, Session, Tariff, User } from '@clubshell/contracts';
import { SESSION_OPEN_ENDED, isSessionOpen } from '@clubshell/contracts';
import { useCallback, useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { DotAmount } from '@/components/ui/DotAmount';
import { formatPrice } from '@/components/ui/PriceNote';
import { useLocale } from '@/hooks/useLocale';
import { useQuote } from '@/hooks/usePrice';
import { CRITICAL_MINUTES, WARNING_MINUTES } from '@/hooks/useSession';
import { formatMoney } from '@/lib/format';
import { log } from '@/lib/logger';
import { listPrice, priceView, type PriceView } from '@/lib/pricing';
import { api, events } from '@/lib/tauri';
import { describeError } from '@/store/notifications';
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
  const [tariffs, setTariffs] = useState<Tariff[] | null>(null);
  const [tick, setTick] = useState(0);
  const [busy, setBusy] = useState<'extend' | 'admin' | null>(null);
  const [confirming, setConfirming] = useState(false);
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
    // The session's tariff: a package is never extended from here, and its list price is the offline fallback.
    api.wallet.tariffs().then(
      (r) => active && setTariffs(r.items),
      (e: unknown) => {
        log.warn('hud: tariffs', e);
        if (active) setTariffs([]);
      },
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
  const openEnded = open && session.secondsLeft === SESSION_OPEN_ENDED;

  // "+30 min" only on prepaid time: pay as you go and a desk's postpaid need no extension, and on a package tariff it
  // would charge a whole package. Priced by the server (`session.quote`) and confirmed before anything is charged.
  const tariff = (session && tariffs?.find((x) => x.id === session.tariffId)) ?? null;
  const quote = useQuote(open && !openEnded && session.isPrepaid ? session.tariffId : null, EXTEND_MINUTES);
  const isPackage = tariff?.isPackage === true || (quote.status === 'ok' && quote.quote.package);
  const canExtend = open && !openEnded && session.isPrepaid && !isPackage;
  const price: PriceView | null = tariff
    ? priceView(quote, listPrice(tariff, EXTEND_MINUTES))
    : quote.status === 'ok'
      ? priceView(quote, quote.quote.total)
      : null;
  const notEnough = price !== null && balance !== null && price.amount.amount > balance.amount;

  // Initial focus once the session and its tariff are known (what the first button is depends on them).
  const focused = useRef(false);
  useEffect(() => {
    if (session !== null && tariffs !== null && !focused.current) {
      focused.current = true;
      first.current?.focus({ preventScroll: true });
    }
  }, [session, tariffs]);
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
    if (!session || !canExtend) return;
    setBusy('extend');
    setNote(null);
    try {
      setSession(await api.session.extend(EXTEND_MINUTES, session.tariffId));
      setNote({ text: t('session.extended', { minutes: EXTEND_MINUTES }), tone: 'ok' });
    } catch (e) {
      log.warn('hud: extend', e);
      setNote({ text: describeError(e), tone: 'err' });
    } finally {
      setBusy(null);
      setConfirming(false);
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
        className="anim-toast-in glass-strong relative flex w-[min(62rem,86vw)] items-center gap-6 rounded-xl py-4 pl-6 pr-4 text-text"
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

        {openEnded && session.cost.amount > 0 && (
          <div className="border-l border-[color:var(--hairline)] pl-6 leading-tight">
            <div className="hud-label">{t('session.due')}</div>
            <div className="tnum whitespace-nowrap text-xl">
              <DotAmount value={formatMoney(session.cost, locale)} />
            </div>
          </div>
        )}

        {balance && (
          <div className="border-l border-[color:var(--hairline)] pl-6 leading-tight">
            <div className="hud-label">{t('desktop.balance')}</div>
            <div className="tnum whitespace-nowrap text-xl">
              <DotAmount value={formatMoney(balance, locale)} />
            </div>
          </div>
        )}

        {confirming && price ? (
          <div role="group" aria-label={t('kiosk.hudExtend')} className="ml-auto flex items-center gap-3">
            <p className="tnum text-lg">
              {t(price.approximate ? 'kiosk.hudExtendConfirmApprox' : 'kiosk.hudExtendConfirm', {
                minutes: EXTEND_MINUTES,
                price: formatMoney(price.amount, locale),
              })}
            </p>
            <Button
              variant="cta"
              size="lg"
              autoFocus
              loading={busy === 'extend'}
              disabled={notEnough}
              onClick={() => void extend()}
            >
              {t('common.confirm')}
            </Button>
            <Button variant="secondary" size="lg" disabled={busy === 'extend'} onClick={() => setConfirming(false)}>
              {t('common.cancel')}
            </Button>
          </div>
        ) : (
          <div className="ml-auto flex items-center gap-2">
            {canExtend && (
              <Button
                ref={first}
                variant="cta"
                size="lg"
                icon={<PlusIcon />}
                disabled={price === null || notEnough}
                title={notEnough ? t('wallet.notEnough') : undefined}
                onClick={() => {
                  setNote(null);
                  setConfirming(true);
                  armIdle();
                }}
              >
                {t('kiosk.hudExtend')}
                {price && (
                  <span className={clsx('tnum ml-2 text-sm font-medium opacity-80', price.loading && 'opacity-50')}>
                    {formatPrice(price, locale)}
                  </span>
                )}
              </Button>
            )}
            <Button
              ref={canExtend ? undefined : first}
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
        )}

        <div className="hud-label pointer-events-none absolute -top-8 left-6 flex items-center gap-3">
          <span>{t('kiosk.hudHint')}</span>
          {note && <span className={note.tone === 'ok' ? 'text-success' : 'text-danger'}>{note.text}</span>}
        </div>
      </section>
    </div>
  );
}

export default HudScreen;
