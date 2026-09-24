import { useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router-dom';
import { tariffPriceFor, type Locale, type Money, type Session, type Tariff } from '@clubshell/contracts';
import { Background } from '@/components/layout/Background';
import { NotificationCenter } from '@/components/layout/NotificationCenter';
import { Avatar } from '@/components/ui/Avatar';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { Skeleton } from '@/components/ui/Skeleton';
import { Spinner } from '@/components/ui/Spinner';
import { Tabs, type TabItem } from '@/components/ui/Tabs';
import { VirtualKeyboard } from '@/components/ui/VirtualKeyboard';
import { focusElement, useGamepad } from '@/hooks/useGamepad';
import { useIdle } from '@/hooks/useIdle';
import { useLocale } from '@/hooks/useLocale';
import { useSession } from '@/hooks/useSession';
import { trackScreen } from '@/lib/analytics';
import { formatDurationSec, formatMoney } from '@/lib/format';
import { api, toShellApiError } from '@/lib/tauri';
import { formatClock } from '@/lib/time';
import { selectIsGuest, useAuthStore } from '@/store/auth';
import { useNotificationsStore } from '@/store/notifications';
import { useSessionStore } from '@/store/session';
import { useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';
import { useWalletStore } from '@/store/wallet';
import { AdsCarousel } from '@/screens/Idle/AdsCarousel';
import { describeWindow } from '@/screens/Idle/PriceList';
import { GuestLogin } from './GuestLogin';
import { LoginForm, loginErrorMessage } from './LoginForm';
import { QrLogin } from './QrLogin';

// ---------------------------------------------------------------------------------------------------------------------
// Small shared pieces (also used by the idle screen)
// ---------------------------------------------------------------------------------------------------------------------

export interface ClockProps {
  className?: string;
}

/** Live wall clock in `shell.json → ui.clockFormat`. */
export function Clock({ className }: ClockProps): JSX.Element {
  const { t } = useTranslation();
  const fmt = useSettingsStore((s) => s.shellConfig?.ui.clockFormat ?? 'HH:mm');
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(id);
  }, []);
  return (
    <time
      dateTime={now.toISOString()}
      aria-label={t('lock.clock')}
      className={clsx('tnum num-dot leading-none text-text', className)}
    >
      {formatClock(now, fmt)}
    </time>
  );
}

export interface LanguageSwitcherProps {
  className?: string;
}

/** EN / RU / UZ pills; persists through the settings store. */
export function LanguageSwitcher({ className }: LanguageSwitcherProps): JSX.Element {
  const { t } = useTranslation();
  const { locale, setLocale, locales, names } = useLocale();
  const pushError = useNotificationsStore((s) => s.pushError);
  const [busy, setBusy] = useState<Locale | null>(null);

  const change = async (next: Locale): Promise<void> => {
    if (next === locale || busy) {
      return;
    }
    setBusy(next);
    try {
      await setLocale(next);
    } catch (e) {
      pushError(e, t('lock.language'));
    } finally {
      setBusy(null);
    }
  };

  return (
    <div
      role="group"
      aria-label={t('lock.language')}
      className={clsx('glass flex items-center gap-1 rounded-lg p-1', className)}
    >
      {locales.map((l) => (
        <button
          key={l}
          type="button"
          data-nav="true"
          aria-pressed={l === locale}
          aria-label={names[l]}
          title={names[l]}
          disabled={busy !== null}
          onClick={() => void change(l)}
          className={clsx(
            'focus-ring h-9 min-w-[3.25rem] rounded-md px-3 font-mono text-xs font-medium uppercase tracking-[0.12em] transition-colors duration-[var(--dur-fast)]',
            l === locale ? 'bg-accent/15 text-accent' : 'text-muted hover:bg-text/[0.06] hover:text-text',
          )}
        >
          {l}
        </button>
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Locked session → unlock with PIN / password
// ---------------------------------------------------------------------------------------------------------------------

export interface UnlockFormProps {
  onSuccess?: (session: Session) => void;
  className?: string;
}

type UnlockMethod = 'pin' | 'password';

export function UnlockForm({ onSuccess, className }: UnlockFormProps): JSX.Element {
  const { t } = useTranslation();
  const { user, timeLabel, isOpenEnded, unlock, busy } = useSession();
  const reload = useSessionStore((s) => s.load);
  const [method, setMethod] = useState<UnlockMethod>('pin');
  const [secret, setSecret] = useState('');
  const [error, setError] = useState<string | null>(null);
  const tabs = useMemo<TabItem<UnlockMethod>[]>(
    () => [
      { key: 'pin', label: t('lock.pin') },
      { key: 'password', label: t('lock.methodPassword') },
    ],
    [t],
  );

  const switchMethod = (m: UnlockMethod): void => {
    setMethod(m);
    setSecret('');
    setError(null);
  };

  const submit = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    const v = secret.trim();
    if (method === 'pin') {
      if (v.length === 0) {
        setError(t('lock.pinRequired'));
        return;
      }
      if (!/^\d{4,6}$/.test(v)) {
        setError(t('lock.pinInvalid'));
        return;
      }
    } else if (v.length === 0) {
      setError(t('lock.passwordRequired'));
      return;
    }
    setError(null);
    try {
      const session = await unlock(method === 'pin' ? { pin: v } : { password: v });
      setSecret('');
      onSuccess?.(session);
    } catch (err) {
      const shellErr = toShellApiError(err);
      if (shellErr.code === 'conflict') {
        // Not locked any more (admin unlocked / session ended): resync and let the screen re-route.
        void reload();
      }
      setError(loginErrorMessage(shellErr, t));
      setSecret('');
    }
  };

  const name = user?.displayName ?? t('common.unknown');

  return (
    <form
      onSubmit={(e) => void submit(e)}
      noValidate
      className={clsx('flex flex-col items-center gap-5 text-center', className)}
    >
      <Avatar name={name} src={user?.avatarUrl} size="xl" ring />
      <div>
        <h1 className="font-display text-3xl font-light leading-tight tracking-tight text-text">{name}</h1>
        <p className="mt-1 text-base text-muted">{t('lock.lockedHint')}</p>
      </div>
      <div className="flex flex-wrap items-center justify-center gap-3">
        <Badge tone="accent" size="lg" dot>
          {t('lock.locked')}
        </Badge>
        <span className="tnum text-lg text-muted">
          {t('lock.timeLeft')}:{' '}
          <span className="font-bold text-text">{isOpenEnded ? t('session.openEnded') : timeLabel}</span>
        </span>
      </div>
      <Tabs items={tabs} value={method} onChange={switchMethod} label={t('lock.chooseMethod')} idPrefix="unlock" />
      <Input
        key={method}
        name={method}
        size="lg"
        type="password"
        inputMode={method === 'pin' ? 'numeric' : undefined}
        autoComplete={method === 'pin' ? 'one-time-code' : 'current-password'}
        maxLength={method === 'pin' ? 6 : undefined}
        autoFocus
        label={method === 'pin' ? t('lock.pin') : t('lock.password')}
        placeholder={method === 'pin' ? t('lock.pinPlaceholder') : t('lock.passwordPlaceholder')}
        value={secret}
        onChange={(e) => setSecret(e.target.value)}
        error={error}
        disabled={busy}
        wrapperClassName="text-left"
      />
      <Button type="submit" size="xl" block loading={busy}>
        {busy ? t('lock.unlocking') : method === 'pin' ? t('lock.unlockWithPin') : t('lock.unlockWithPassword')}
      </Button>
    </form>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Logged in without a session → choose a tariff
// ---------------------------------------------------------------------------------------------------------------------

export interface StartSessionModalProps {
  open: boolean;
  onStarted?: (session: Session) => void;
  onLogout?: () => void;
}

const MINUTE_OPTIONS = [30, 60, 90, 120, 180, 240, 300, 360];

/** Purchasable minute presets of a tariff (a package has exactly one). */
export function tariffMinuteOptions(tariff: Tariff): number[] {
  if (tariff.isPackage) {
    return [tariff.packageMinutes ?? tariff.minMinutes];
  }
  const max = tariff.maxMinutes ?? Number.POSITIVE_INFINITY;
  const options = MINUTE_OPTIONS.filter((m) => m >= tariff.minMinutes && m <= max);
  return options.length > 0 ? options : [tariff.minMinutes];
}

interface OptionCardProps {
  selected: boolean;
  onSelect: () => void;
  title: string;
  hint?: string;
  children?: ReactNode;
  className?: string;
}

function OptionCard({ selected, onSelect, title, hint, children, className }: OptionCardProps): JSX.Element {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={selected}
      data-nav="true"
      onClick={onSelect}
      className={clsx(
        'focus-ring flex flex-col items-start gap-1 rounded-lg border px-4 py-3 text-left transition-colors duration-[var(--dur-fast)]',
        selected
          ? 'border-primary bg-primary/15 text-text shadow-[var(--shadow-glow)]'
          : 'border-text/10 bg-text/5 text-text hover:bg-text/10',
        className,
      )}
    >
      <span className="text-lg font-bold leading-tight">{title}</span>
      {hint && <span className="text-sm text-muted">{hint}</span>}
      {children}
    </button>
  );
}

export function StartSessionModal({ open, onStarted, onLogout }: StartSessionModalProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const tariffs = useWalletStore((s) => s.tariffs);
  const loadTariffs = useWalletStore((s) => s.loadTariffs);
  const balance = useAuthStore((s) => s.user?.balance ?? null);
  const isGuest = useAuthStore(selectIsGuest);
  const { start, busy, lastEnded } = useSession();
  const reload = useSessionStore((s) => s.load);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [tariffId, setTariffId] = useState<string | null>(null);
  const [prepaid, setPrepaid] = useState(true);
  const [minutes, setMinutes] = useState(60);
  const [loading, setLoading] = useState(false);

  // Fresh defaults each time the picker opens; guests (no balance) start postpaid.
  useEffect(() => {
    if (!open) {
      return;
    }
    setTariffId(null);
    setMinutes(60);
    setPrepaid(!isGuest && (balance?.amount ?? 0) > 0);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  useEffect(() => {
    if (!open || tariffs.length > 0) {
      return;
    }
    setLoading(true);
    void loadTariffs().finally(() => setLoading(false));
  }, [open, tariffs.length, loadTariffs]);

  const tariff = tariffs.find((x) => x.id === tariffId) ?? tariffs[0] ?? null;
  const options = useMemo(() => (tariff ? tariffMinuteOptions(tariff) : []), [tariff]);
  const effectivePrepaid = tariff?.isPackage === true || prepaid;
  const effectiveMinutes = options.includes(minutes) ? minutes : (options.find((m) => m >= 60) ?? options[0] ?? 60);
  const price: Money | null = tariff && effectivePrepaid ? tariffPriceFor(tariff, effectiveMinutes) : null;
  const notEnough = price !== null && balance !== null && price.amount > balance.amount;
  const after: Money | null =
    price && balance ? { amount: balance.amount - price.amount, currency: balance.currency } : null;

  const submit = async (): Promise<void> => {
    if (!tariff) {
      return;
    }
    try {
      const session = await start(tariff.id, effectivePrepaid, effectivePrepaid ? effectiveMinutes : undefined);
      onStarted?.(session);
    } catch (e) {
      const err = toShellApiError(e);
      if (err.code === 'sessionAlreadyActive') {
        void reload();
      }
      pushError(err, t('session.start'));
    }
  };

  return (
    <Modal
      open={open}
      onClose={() => undefined}
      closeOnBackdrop={false}
      closeOnEscape={false}
      showClose={false}
      size="lg"
      title={t('wallet.chooseTariff')}
      description={t('session.noSessionHint')}
      footer={
        <>
          <Button variant="ghost" size="lg" disabled={busy} onClick={onLogout}>
            {t('lock.logoutInstead')}
          </Button>
          <Button variant="cta" size="lg" loading={busy} disabled={!tariff || notEnough} onClick={() => void submit()}>
            {busy ? t('session.starting') : t('lock.startPlaying')}
          </Button>
        </>
      }
    >
      {lastEnded && (
        <div role="status" className="mb-4 rounded-md bg-accent/15 px-4 py-3">
          <p className="font-semibold text-accent">{t('session.endedTitle')}</p>
          <p className="text-sm text-muted">{t(`session.endedReason.${lastEnded.reason}`)}</p>
        </div>
      )}
      {balance && (
        <p className="mb-4 text-base text-muted">
          {t('wallet.balance')}: <span className="tnum font-bold text-text">{formatMoney(balance, locale)}</span>
        </p>
      )}

      {loading ? (
        <div className="grid grid-cols-[repeat(auto-fit,minmax(13rem,1fr))] gap-3">
          {[0, 1, 2].map((i) => (
            <Skeleton key={i} height="7.5rem" className="rounded-lg" />
          ))}
        </div>
      ) : tariffs.length === 0 ? (
        <p className="py-6 text-center text-base text-muted">{t('idle.noTariffs')}</p>
      ) : (
        <div
          role="radiogroup"
          aria-label={t('wallet.tariffs')}
          className="grid grid-cols-[repeat(auto-fit,minmax(13rem,1fr))] gap-3"
        >
          {tariffs.map((x) => (
            <OptionCard
              key={x.id}
              selected={tariff?.id === x.id}
              onSelect={() => setTariffId(x.id)}
              title={x.name}
              hint={
                x.isPackage && x.packagePrice
                  ? t('wallet.packageMinutes', {
                      minutes: x.packageMinutes ?? x.minMinutes,
                      price: formatMoney(x.packagePrice, locale),
                    })
                  : t('wallet.perHour', { price: formatMoney(x.pricePerHour, locale) })
              }
            >
              {x.timeWindows.length > 0 && (
                <span className="text-xs text-accent">
                  {x.timeWindows.map((w) => describeWindow(w, locale, t)).join(' · ')}
                </span>
              )}
            </OptionCard>
          ))}
        </div>
      )}

      {tariff && !tariff.isPackage && (
        <div className="mt-5 flex flex-col gap-4">
          <div role="radiogroup" aria-label={t('wallet.chooseMinutes')} className="grid grid-cols-2 gap-3">
            <OptionCard
              selected={prepaid}
              onSelect={() => setPrepaid(true)}
              title={t('wallet.prepaid')}
              hint={t('wallet.prepaidHint')}
            />
            <OptionCard
              selected={!prepaid}
              onSelect={() => setPrepaid(false)}
              title={t('wallet.postpaid')}
              hint={t('wallet.postpaidHint')}
            />
          </div>
          {prepaid && (
            <div>
              <p className="mb-2 text-sm text-muted">{t('wallet.chooseMinutes')}</p>
              <div role="radiogroup" aria-label={t('session.minutes')} className="flex flex-wrap gap-2">
                {options.map((m) => (
                  <button
                    key={m}
                    type="button"
                    role="radio"
                    aria-checked={m === effectiveMinutes}
                    data-nav="true"
                    onClick={() => setMinutes(m)}
                    className={clsx(
                      'focus-ring tnum h-11 rounded-md px-4 text-base font-semibold',
                      m === effectiveMinutes ? 'choice choice-on' : 'choice',
                    )}
                  >
                    {formatDurationSec(m * 60)}
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>
      )}

      {tariff && (
        <dl className="mt-5 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1 rounded-md bg-text/5 px-4 py-3 text-base">
          <dt className="text-muted">{t('wallet.tariff')}</dt>
          <dd className="text-right font-semibold text-text">{tariff.name}</dd>
          <dt className="text-muted">{t('wallet.estimatedCost')}</dt>
          <dd className="tnum text-right font-bold text-text">
            {price
              ? formatMoney(price, locale)
              : t('wallet.perHour', { price: formatMoney(tariff.pricePerHour, locale) })}
          </dd>
          {after && (
            <>
              <dt className="text-muted">{t('wallet.balanceAfter')}</dt>
              <dd className={clsx('tnum text-right font-semibold', notEnough ? 'text-danger' : 'text-success')}>
                {formatMoney(after, locale)}
              </dd>
            </>
          )}
        </dl>
      )}
      {notEnough && (
        <p role="alert" className="mt-3 text-base font-medium text-danger">
          {t('wallet.notEnough')}
        </p>
      )}
    </Modal>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Screen
// ---------------------------------------------------------------------------------------------------------------------

/** QR first: signing in from the phone needs no keyboard and keeps the password off a shared screen. */
type LoginTab = 'qr' | 'password' | 'guest';

const KeyIcon = (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <circle cx="8" cy="15" r="4" />
    <path d="M10.85 12.15L19 4M18 5l2 2M15 8l2 2" />
  </svg>
);
const QrIcon = (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <rect x="3" y="3" width="7" height="7" rx="1" />
    <rect x="14" y="3" width="7" height="7" rx="1" />
    <rect x="3" y="14" width="7" height="7" rx="1" />
    <path d="M14 14h3v3h-3zM20 14v.01M20 20h-6M20 17v3" />
  </svg>
);
const UserIcon = (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <circle cx="12" cy="8" r="4" />
    <path d="M4 21c0-4 3.6-7 8-7s8 3 8 7" />
  </svg>
);
const BellIcon = (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M6 16V11a6 6 0 0 1 12 0v5l2 2H4l2-2zM10 21h4" />
  </svg>
);

/**
 * Sign-in / unlock screen (route `/lock`, outside the AppShell). Re-routes to the default screen as soon as an open,
 * unlocked session exists; shows the tariff picker when the user is logged in without a session; drops to `/idle`
 * after the idle timeout while nobody is signed in.
 */
/**
 * The only line about this PC's connection on the sign-in screen, and only while it is down — a player about to sign
 * in needs to know why it might not work; when all is well there is nothing to say.
 */
function LinkWarning(): JSX.Element | null {
  const { t } = useTranslation();
  const agentConnected = useNotificationsStore((s) => s.agentConnected);
  const serverOnline = useNotificationsStore((s) => s.serverConnectivity === 'online');
  if (agentConnected && serverOnline) {
    return null;
  }
  return (
    <p role="status" className="glass flex items-center gap-3 rounded-full px-5 py-2.5 text-base text-text">
      <span
        aria-hidden="true"
        className={clsx('h-2.5 w-2.5 shrink-0 rounded-full', agentConnected ? 'bg-accent' : 'anim-live-dot bg-danger')}
      />
      {agentConnected ? t('lock.serverOffline') : t('lock.agentOffline')}
    </p>
  );
}

export default function LockScreen(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const { user, isOpen, isLocked } = useSession();
  const ready = useAuthStore((s) => s.ready);
  const expiredReason = useAuthStore((s) => s.expiredReason);
  const logout = useAuthStore((s) => s.logout);
  const defaultRoute = useSettingsStore((s) => s.shellConfig?.ui.defaultRoute ?? '/home');
  const pcName = useSettingsStore((s) => s.pcInfo?.pc.name ?? null);
  const pcZone = useSettingsStore((s) => s.pcInfo?.pc.zone ?? '');
  const callAdminEnabled = useSettingsStore((s) => s.features.callAdmin);
  const animations = useThemeStore((s) => s.theme.animations);
  const playlist = useSettingsStore((s) => s.shellConfig?.ads.playlist);
  // The club's own art, the same the attract screen plays — stills only: a moving picture behind a code the player is
  // trying to scan is a distraction, not a showcase.
  const art = useMemo(() => (playlist ?? []).filter((i) => i.type === 'image'), [playlist]);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [tab, setTab] = useState<LoginTab>('qr');
  const [calling, setCalling] = useState(false);
  const card = useRef<HTMLDivElement>(null);
  const { idle } = useIdle();
  useGamepad();

  const hasUser = user !== null;
  const showTariffs = ready && hasUser && !isOpen;

  useEffect(() => {
    trackScreen(pathname);
  }, [pathname]);

  useEffect(() => {
    if (hasUser && isOpen && !isLocked) {
      navigate(defaultRoute, { replace: true });
    }
  }, [hasUser, isOpen, isLocked, defaultRoute, navigate]);

  useEffect(() => {
    if (idle && !hasUser && !isLocked) {
      navigate('/idle', { replace: true });
    }
  }, [idle, hasUser, isLocked, navigate]);

  // Initial focus: the active method tab (inputs focus themselves in locked mode).
  useEffect(() => {
    if (!ready || isLocked || hasUser) {
      return;
    }
    const el = card.current?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]');
    if (el) {
      focusElement(el);
    }
  }, [ready, isLocked, hasUser]);

  const tabs = useMemo<TabItem<LoginTab>[]>(
    () => [
      { key: 'qr', label: t('lock.methodQr'), icon: QrIcon },
      { key: 'password', label: t('lock.methodPassword'), icon: KeyIcon },
      { key: 'guest', label: t('lock.methodGuest'), icon: UserIcon },
    ],
    [t],
  );

  const callAdmin = async (): Promise<void> => {
    setCalling(true);
    try {
      await api.system.callAdmin('help');
      push({
        id: 'call-admin',
        title: t('notifications.callAdminSent'),
        body: t('admin.adminOnWay'),
        level: 'success',
        source: 'system',
      });
    } catch (e) {
      pushError(e, t('admin.callAdminTitle'));
    } finally {
      setCalling(false);
    }
  };

  const duration = animations ? 0.3 : 0;

  return (
    <div className="relative h-full w-full overflow-hidden">
      {art.length > 0 ? (
        <div aria-hidden="true" className="pointer-events-none absolute inset-0 z-0">
          <AdsCarousel items={art} showCounter={false} />
          <div className="absolute inset-0 bg-[linear-gradient(180deg,rgb(var(--c-bg)/0.8)_0%,rgb(var(--c-bg)/0.35)_28%,rgb(var(--c-bg)/0.4)_62%,rgb(var(--c-bg)/0.92)_100%)]" />
        </div>
      ) : (
        <Background dim={0.6} />
      )}
      <div className="relative z-10 flex h-full w-full flex-col gap-[var(--gap)] px-[var(--gutter)] py-[var(--gap)]">
        <header className="flex items-start justify-between gap-[var(--gap)]">
          <div className="min-w-0">
            <p className="truncate font-display text-2xl font-light tracking-tight text-text">{t('idle.clubName')}</p>
            {pcName && <p className="hud-label mt-2">{t('idle.pcName', { name: pcName, zone: pcZone })}</p>}
          </div>
          <div className="flex items-center gap-[var(--gap)]">
            <LanguageSwitcher />
            <Clock className="text-4xl" />
          </div>
        </header>

        <main className="flex min-h-0 flex-1 items-center justify-center pb-[var(--vk-h,0px)]">
          <motion.div
            ref={card}
            initial={{ opacity: 0, y: 24 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration, ease: 'easeOut' }}
            className="glass-strong themed-scrollbar max-h-full w-[min(92vw,34rem)] overflow-y-auto overflow-x-hidden rounded-xl p-8"
          >
            {!ready ? (
              <div className="flex justify-center py-16">
                <Spinner size="xl" />
              </div>
            ) : isLocked ? (
              <UnlockForm />
            ) : (
              <>
                <div className="mb-6 text-center">
                  <h1 className="font-display text-3xl font-light leading-tight tracking-tight text-text">
                    {t('lock.title')}
                  </h1>
                  <p className="mt-1 text-base text-muted">{t('lock.subtitle')}</p>
                </div>
                {expiredReason && (
                  <p role="status" className="mb-4 rounded-md bg-accent/15 px-4 py-3 text-base text-accent">
                    {t('lock.sessionExpired')}
                  </p>
                )}
                <Tabs
                  items={tabs}
                  value={tab}
                  onChange={setTab}
                  label={t('lock.chooseMethod')}
                  size="lg"
                  idPrefix="lock"
                  className="mb-6 w-full [&>button]:flex-1 [&>button]:justify-center"
                />
                <div role="tabpanel" id={`lock-panel-${tab}`} aria-labelledby={`lock-tab-${tab}`}>
                  {tab === 'qr' && <QrLogin />}
                  {tab === 'password' && <LoginForm />}
                  {tab === 'guest' && <GuestLogin />}
                </div>
              </>
            )}
          </motion.div>
        </main>

        <footer className="flex items-end justify-between gap-[var(--gap)]">
          {/* Versions and CPU/GPU/RAM used to sit here for every passer-by; staff find them on Support. */}
          <div className="min-w-0">
            <LinkWarning />
          </div>
          {callAdminEnabled && (
            <Button variant="ghost" size="lg" icon={BellIcon} loading={calling} onClick={() => void callAdmin()}>
              {t('lock.callAdmin')}
            </Button>
          )}
        </footer>
      </div>

      <StartSessionModal open={showTariffs} onLogout={() => void logout('user')} />
      <NotificationCenter />
      <VirtualKeyboard />
    </div>
  );
}
