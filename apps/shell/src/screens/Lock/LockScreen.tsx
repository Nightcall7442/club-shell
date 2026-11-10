import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router-dom';
import type { Locale, Money, PcReadiness, Session, Tariff } from '@clubshell/contracts';
import { Background } from '@/components/layout/Background';
import { NotificationCenter } from '@/components/layout/NotificationCenter';
import { Avatar } from '@/components/ui/Avatar';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { formatPrice, PriceNote } from '@/components/ui/PriceNote';
import { Skeleton } from '@/components/ui/Skeleton';
import { Spinner } from '@/components/ui/Spinner';
import { Tabs, type TabItem } from '@/components/ui/Tabs';
import { focusElement, useGamepad } from '@/hooks/useGamepad';
import { useIdle } from '@/hooks/useIdle';
import { useLocale } from '@/hooks/useLocale';
import { usePrice } from '@/hooks/usePrice';
import { useSession } from '@/hooks/useSession';
import { trackScreen } from '@/lib/analytics';
import { pickAutoStartTariff } from '@/lib/autoStart';
import { onCardRead } from '@/lib/cardReader';
import { errorDetails, errorReason } from '@/lib/errorText';
import { formatDurationSec, formatMoney } from '@/lib/format';
import { listPrice } from '@/lib/pricing';
import { fetchQuote } from '@/lib/quotes';
import { api, toShellApiError } from '@/lib/tauri';
import { formatClock, serverNow } from '@/lib/time';
import { selectIsGuest, selectPcReady, useAuthStore } from '@/store/auth';
import { describeError, useNotificationsStore } from '@/store/notifications';
import { useGamesStore } from '@/store/games';
import { useSessionStore } from '@/store/session';
import { useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';
import { useWalletStore } from '@/store/wallet';
import { AdsCarousel } from '@/screens/Idle/AdsCarousel';
import { describeWindow } from '@/screens/Idle/PriceList';
import { tariffAvailableNow } from '@/lib/tariffs';
import { ClubMark } from '@/components/brand/ClubMark';
import { useClub } from '@/hooks/useClub';
import { CardLogin } from './CardLogin';
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

/** PIN first only for a player who has one (`auth.status.pinSet`); otherwise the password. */
const unlockMethodFor = (pinSet: boolean | null): UnlockMethod => (pinSet === true ? 'pin' : 'password');

export function UnlockForm({ onSuccess, className }: UnlockFormProps): JSX.Element {
  const { t } = useTranslation();
  const { user, timeLabel, isOpenEnded, unlock, busy } = useSession();
  const reload = useSessionStore((s) => s.load);
  const pinSet = useAuthStore((s) => s.pinSet);
  const setLockedByStaff = useAuthStore((s) => s.setLockedByStaff);
  const [method, setMethod] = useState<UnlockMethod>(() => unlockMethodFor(pinSet));
  const [secret, setSecret] = useState('');
  const [error, setError] = useState<string | null>(null);
  // `pinSet` arrives with `auth.status` after the lock: follow it until the player picks a tab.
  const picked = useRef(false);
  useEffect(() => {
    if (!picked.current) {
      setMethod(unlockMethodFor(pinSet));
    }
  }, [pinSet]);
  const tabs = useMemo<TabItem<UnlockMethod>[]>(
    () => [
      { key: 'pin', label: t('lock.pin') },
      { key: 'password', label: t('lock.methodPassword') },
    ],
    [t],
  );

  const switchMethod = (m: UnlockMethod): void => {
    picked.current = true;
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
      const reason = errorReason(shellErr.details);
      setSecret('');
      if (shellErr.code === 'forbidden' && reason === 'staffLock') {
        // Locked from the desk: no secret of the player's opens it; the screen says so instead of the form.
        setLockedByStaff(true);
        return;
      }
      if (shellErr.code === 'conflict') {
        // Not locked any more (admin unlocked / session ended): resync and let the screen re-route.
        void reload();
      }
      const message = loginErrorMessage(shellErr, method === 'pin' ? 'unlockPin' : 'unlockPassword');
      const lockedOut = method === 'pin' && errorDetails(shellErr.details)['attemptsLeft'] === 0;
      if (reason === 'noPin' || lockedOut) {
        // No PIN on the account (any more), or PIN unlocks locked out (even the right one is refused): the password.
        picked.current = true;
        setMethod('password');
      }
      setError(message);
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
        <p className="mt-1 text-balance text-base text-muted">{t('lock.lockedHint')}</p>
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
        autoComplete={method === 'pin' ? 'one-time-code' : 'new-password'}
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

/**
 * Locked from the cash desk (`auth.status.lockedByStaff`): only the desk unlocks it (`session.unlock` answers `403
 * staffLock`), so there is no PIN or password form to try — the screen says who locked it and where to go.
 */
export function StaffLockCard({ className }: { className?: string }): JSX.Element {
  const { t } = useTranslation();
  const { user, timeLabel, isOpenEnded } = useSession();
  const name = user?.displayName ?? t('common.unknown');
  return (
    <div role="status" className={clsx('flex flex-col items-center gap-5 py-4 text-center', className)}>
      <Avatar name={name} src={user?.avatarUrl} size="xl" ring />
      <div>
        <h1 className="font-display text-3xl font-light leading-tight tracking-tight text-text">
          {t('lock.staffLockedTitle')}
        </h1>
        <p className="mt-2 text-balance text-base text-muted">{t('lock.staffLockedHint')}</p>
      </div>
      <div className="flex flex-wrap items-center justify-center gap-3">
        <Badge tone="danger" size="lg" dot>
          {t('lock.locked')}
        </Badge>
        <span className="tnum text-lg text-muted">
          {name} · {t('lock.timeLeft')}:{' '}
          <span className="font-bold text-text">{isOpenEnded ? t('session.openEnded') : timeLabel}</span>
        </span>
      </div>
    </div>
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
  hint?: ReactNode;
  disabled?: boolean;
  children?: ReactNode;
  className?: string;
}

function OptionCard({ selected, onSelect, title, hint, disabled, children, className }: OptionCardProps): JSX.Element {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={selected}
      data-nav="true"
      disabled={disabled}
      onClick={onSelect}
      // The shared pick-one look (`.choice`, as for minute presets just below): accent edge and fill when chosen.
      className={clsx(
        'focus-ring choice flex flex-col items-start gap-1 rounded-lg px-4 py-3 text-left disabled:cursor-not-allowed disabled:opacity-50',
        selected && 'choice-on',
        className,
      )}
    >
      <span className="text-lg font-bold leading-tight">{title}</span>
      {hint && <span className="text-sm text-muted">{hint}</span>}
      {children}
    </button>
  );
}

/** A tariff of the picker with its quoted price (an hour, or the package). */
function TariffOption({
  tariff,
  selected,
  available,
  onSelect,
}: {
  tariff: Tariff;
  selected: boolean;
  available: boolean;
  onSelect: () => void;
}): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const price = usePrice(tariff, null);
  const amount = price ? formatPrice(price, locale) : formatMoney(tariff.pricePerHour, locale);
  return (
    <OptionCard
      selected={selected}
      onSelect={onSelect}
      disabled={!available}
      title={tariff.name}
      hint={
        tariff.isPackage
          ? t('wallet.packageMinutes', { minutes: tariff.packageMinutes ?? tariff.minMinutes, price: amount })
          : t('wallet.perHour', { price: amount })
      }
    >
      {price?.discountPct ? <PriceNote view={price} /> : null}
      {tariff.timeWindows.length > 0 && (
        <span className="text-xs text-accent">
          {tariff.timeWindows.map((w) => describeWindow(w, locale, t)).join(' · ')}
        </span>
      )}
    </OptionCard>
  );
}

/**
 * A guest without a session: the time is opened and paid at the desk (the Agent refuses a guest's own pay-as-you-go
 * start, and a guest has no balance for prepaid), so the picker sends them to the administrator instead of offering a
 * start they cannot pay for. The session the desk opens takes them in by itself (`session.updated`).
 */
function GuestPayAtDesk({ open, onLogout }: { open: boolean; onLogout?: () => void }): JSX.Element {
  const { t } = useTranslation();
  const callAdminEnabled = useSettingsStore((s) => s.features.callAdmin);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const lastEnded = useSessionStore((s) => s.lastEnded);
  const [calling, setCalling] = useState(false);

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

  return (
    <Modal
      open={open}
      onClose={() => undefined}
      closeOnBackdrop={false}
      closeOnEscape={false}
      showClose={false}
      size="md"
      title={t('session.guestNoSession.title')}
      description={t('session.guestNoSession.hint')}
      footer={
        <>
          <Button variant="ghost" size="lg" onClick={onLogout}>
            {t('lock.logoutInstead')}
          </Button>
          {callAdminEnabled && (
            <Button variant="cta" size="lg" loading={calling} onClick={() => void callAdmin()}>
              {t('lock.callAdmin')}
            </Button>
          )}
        </>
      }
    >
      {lastEnded && (
        <div role="status" className="rounded-md bg-accent/15 px-4 py-3">
          <p className="font-semibold text-accent">{t('session.endedTitle')}</p>
          <p className="text-sm text-muted">{t(`session.endedReason.${lastEnded.reason}`)}</p>
        </div>
      )}
    </Modal>
  );
}

/**
 * The tariff picker of a member signed in without a session: time is bought in advance from the balance at the
 * server's price (`session.quote`). Pay as you go is not a choice here — it is the cash desk's, and the owner's one
 * auto-start right after a fresh sign-in ({@link pickAutoStartTariff}). A guest gets {@link GuestPayAtDesk}.
 */
export function StartSessionModal({ open, onStarted, onLogout }: StartSessionModalProps): JSX.Element {
  const isGuest = useAuthStore(selectIsGuest);
  return isGuest ? (
    <GuestPayAtDesk open={open} onLogout={onLogout} />
  ) : (
    <MemberTariffPicker open={open} onStarted={onStarted} onLogout={onLogout} />
  );
}

function MemberTariffPicker({ open, onStarted, onLogout }: StartSessionModalProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const tariffs = useWalletStore((s) => s.tariffs);
  const loadTariffs = useWalletStore((s) => s.loadTariffs);
  // The wallet follows `wallet.updated` and the 30 s poll; the user record is the sign-in's.
  const walletBalance = useWalletStore((s) => s.balance?.amount ?? null);
  const userBalance = useAuthStore((s) => s.user?.balance ?? null);
  const balance = walletBalance ?? userBalance;
  const armed = useAuthStore((s) => s.autoStartArmed);
  const disarm = useAuthStore((s) => s.disarmAutoStart);
  const { start, busy, lastEnded } = useSession();
  const reload = useSessionStore((s) => s.load);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const pcZone = useSettingsStore((s) => s.pcInfo?.pc.zone ?? '');
  const [tariffId, setTariffId] = useState<string | null>(null);
  const [minutes, setMinutes] = useState(60);
  const [loadState, setLoadState] = useState<'idle' | 'loading' | 'done'>('idle');

  // Fresh defaults each time the picker opens.
  useEffect(() => {
    if (open) {
      setTariffId(null);
      setMinutes(60);
    }
  }, [open]);

  useEffect(() => {
    if (!open || tariffs.length > 0 || loadState !== 'idle') {
      return;
    }
    setLoadState('loading');
    void loadTariffs().finally(() => setLoadState('done'));
  }, [open, tariffs.length, loadState, loadTariffs]);
  const tariffsReady = tariffs.length > 0 || loadState === 'done';

  // What can be bought right now: this PC's zone and the tariff's hours (club clock, minute resolution).
  const [now, setNow] = useState(() => serverNow());
  useEffect(() => {
    if (!open) {
      return undefined;
    }
    setNow(serverNow());
    const timer = setInterval(() => setNow(serverNow()), 60_000);
    return () => clearInterval(timer);
  }, [open]);
  const availableNow = useCallback((x: Tariff) => tariffAvailableNow(x, pcZone, now), [pcZone, now]);
  const tariff = tariffs.find((x) => x.id === tariffId && availableNow(x)) ?? tariffs.find(availableNow) ?? null;
  const options = useMemo(() => (tariff ? tariffMinuteOptions(tariff) : []), [tariff]);
  const effectiveMinutes = options.includes(minutes) ? minutes : (options.find((m) => m >= 60) ?? options[0] ?? 60);
  const price = usePrice(open ? tariff : null, effectiveMinutes);
  const notEnough = price !== null && balance !== null && price.amount.amount > balance.amount;
  const after: Money | null =
    price && balance ? { amount: balance.amount - price.amount.amount, currency: balance.currency } : null;

  const submit = async (): Promise<void> => {
    if (!tariff) {
      return;
    }
    try {
      const session = await start(tariff.id, true, effectiveMinutes);
      onStarted?.(session);
    } catch (e) {
      const err = toShellApiError(e);
      if (err.code === 'sessionAlreadyActive') {
        void reload();
      }
      pushError(err, t('session.start'));
    }
  };

  // The owner's auto-start: right after a fresh sign-in only, with money on the balance, on the cheapest hourly tariff
  // open now — pay as you go from the balance, which the server stops when the balance runs out. Decided once, when
  // the tariffs are in; the picker shows when it does not apply or the start fails.
  const autoTariff = useMemo(
    () => pickAutoStartTariff({ armed, isGuest: false, balance: balance?.amount ?? null, tariffs, availableNow }),
    [armed, balance, tariffs, availableNow],
  );
  const [autoState, setAutoState] = useState<'idle' | 'starting' | 'failed'>('idle');
  useEffect(() => {
    if (!open) {
      setAutoState('idle');
    }
  }, [open]);
  useEffect(() => {
    if (!open || !armed || autoState !== 'idle' || !tariffsReady) {
      return;
    }
    // One attempt per sign-in, whatever its outcome.
    disarm();
    if (!autoTariff) {
      return;
    }
    setAutoState('starting');
    start(autoTariff.id, false)
      .then((session) => {
        void fetchQuote(autoTariff.id, null)
          .then(
            (q) => q.total,
            () => listPrice(autoTariff, null),
          )
          .then((perHour) =>
            push({
              id: 'auto-start',
              title: t('session.autoStarted.title'),
              body: t('session.autoStarted.body', { tariff: autoTariff.name, price: formatMoney(perHour, locale) }),
              level: 'info',
              ttlSec: 12,
              source: 'local',
            }),
          );
        onStarted?.(session);
      })
      .catch((e: unknown) => {
        const err = toShellApiError(e);
        if (err.code === 'sessionAlreadyActive') {
          void reload();
        }
        pushError(err, t('session.start'));
        setAutoState('failed');
      });
  }, [open, armed, autoState, tariffsReady, autoTariff, disarm, start, onStarted, reload, push, pushError, t, locale]);
  // Hidden while the auto-start decides or runs, so the picker does not flash under a session that is starting.
  const deciding = armed && !tariffsReady && (balance?.amount ?? 0) > 0;
  const modalOpen = open && autoState !== 'starting' && !deciding;

  return (
    <Modal
      open={modalOpen}
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
          <span className="block text-sm">{t('session.prepaidHint')}</span>
        </p>
      )}

      {!tariffsReady ? (
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
            <TariffOption
              key={x.id}
              tariff={x}
              selected={tariff?.id === x.id}
              available={availableNow(x)}
              onSelect={() => setTariffId(x.id)}
            />
          ))}
        </div>
      )}

      {tariff && !tariff.isPackage && (
        <div className="mt-5">
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

      {tariff && price && (
        <dl className="mt-5 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1 rounded-md bg-text/5 px-4 py-3 text-base">
          <dt className="text-muted">{t('wallet.tariff')}</dt>
          <dd className="text-right font-semibold text-text">{tariff.name}</dd>
          <dt className="text-muted">{t('wallet.estimatedCost')}</dt>
          <dd className={clsx('tnum text-right font-bold text-text', price.loading && 'opacity-60')}>
            {formatPrice(price, locale)}
          </dd>
          {(price.base || price.approximate) && (
            <dd className="col-span-2 text-right">
              <PriceNote view={price} />
            </dd>
          )}
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

/**
 * QR first: signing in from the phone needs no keyboard and keeps the password off a shared screen. Card sign-in has
 * no club switch: every club can bind cards at the cash desk. A card tapped on any tab, or on the attract screen,
 * opens the card tab and signs in with it (`onCardRead`).
 */
type LoginTab = 'qr' | 'password' | 'card' | 'guest';

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
const CardIcon = (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <rect x="2" y="5" width="20" height="14" rx="2" />
    <path d="M2 10h20M6 15h4" />
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

/** How long the lock screen shows what the visit that just ended cost. */
const RECEIPT_MS = 15_000;

/** "Сессия завершена · Вы играли 2 ч 05 мин · Списано 25 000 сум" for the player who just left, for a few seconds. */
function LeaveReceiptNote(): JSX.Element | null {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const receipt = useAuthStore((s) => s.receipt);
  const dismiss = useAuthStore((s) => s.dismissReceipt);
  useEffect(() => {
    if (!receipt) {
      return;
    }
    const timer = window.setTimeout(dismiss, Math.max(0, receipt.at + RECEIPT_MS - Date.now()));
    return () => window.clearTimeout(timer);
  }, [receipt, dismiss]);
  if (!receipt || Date.now() - receipt.at > RECEIPT_MS) {
    return null;
  }
  return (
    <div role="status" className="mb-4 rounded-md bg-success/10 px-4 py-3 text-base">
      <p className="font-semibold text-success">{t('session.endedTitle')}</p>
      <p className="tnum mt-1 text-text">
        {t('session.endedPlayed', { duration: formatDurationSec(receipt.secondsUsed, { compact: true }) })}
        {receipt.cost.amount > 0 && <> · {t('session.endedCharged', { amount: formatMoney(receipt.cost, locale) })}</>}
      </p>
    </div>
  );
}

/** Name of a games disk on the preparing screen: the helper's libraries by name, a library root as it is. */
function diskLabel(library: string, t: (key: string) => string): string {
  return library === 'games' ? t('lock.preparing.games') : library === 'solo' ? t('lock.preparing.solo') : library;
}

/**
 * SHELL_CHANGES 86: while the diskless helper is still attaching the games disks no one signs in — a player would land
 * in an empty library. The card shows how far each disk is; with no disk at all after the wait, the PC is "not ready"
 * and the player is sent to the administrator.
 */
function PreparingCard({ readiness }: { readiness: PcReadiness }): JSX.Element {
  const { t } = useTranslation();
  const notReady = readiness.state === 'notReady';
  return (
    <div role="status" aria-live="polite" className="flex flex-col items-center gap-5 py-6 text-center">
      {!notReady && <Spinner size="xl" />}
      <div>
        <h1 className="font-display text-3xl font-light leading-tight tracking-tight text-text">
          {notReady ? t('lock.preparing.notReadyTitle') : t('lock.preparing.title')}
        </h1>
        <p className="mt-1 text-balance text-base text-muted">
          {notReady ? t('lock.preparing.notReadyHint') : t('lock.preparing.hint')}
        </p>
      </div>
      {readiness.disks.length > 0 && (
        <ul className="flex w-full flex-col gap-2" aria-label={t('lock.preparing.disks')}>
          {readiness.disks.map((d) => {
            const state =
              d.state === 'mounted'
                ? 'ready'
                : d.state === 'attaching' || d.state === 'preparing'
                  ? 'attaching'
                  : 'unavailable';
            return (
              <li
                key={`${d.library}-${d.letter ?? ''}`}
                className="flex items-center justify-between gap-3 rounded-md bg-text/[0.04] px-4 py-2.5 text-base"
              >
                <span className="truncate text-text">
                  {diskLabel(d.library, t)}
                  {d.letter && <span className="ml-2 font-mono text-sm text-muted">{d.letter}:</span>}
                </span>
                <Badge tone={state === 'ready' ? 'success' : state === 'attaching' ? 'accent' : 'neutral'} size="sm">
                  {t(`lock.preparing.state.${state}`)}
                </Badge>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

/** Why the player was signed out (`auth.expired`): the desk ended the session, seated someone else, moved it, … */
function ExpiredNote(): JSX.Element | null {
  const { t, i18n } = useTranslation();
  const reason = useAuthStore((s) => s.expiredReason);
  const relogin = useAuthStore((s) => s.relogin);
  if (!reason || relogin) {
    return null;
  }
  const key = `lock.expired.${reason}`;
  return (
    <p role="status" className="mb-4 rounded-md bg-accent/15 px-4 py-3 text-base text-accent">
      {i18n.exists(key) ? t(key) : t('lock.expired.unknown')}
    </p>
  );
}

/**
 * The player whose sign-in ran out mid-session (`auth.expired {tokenExpired}`; the Agent keeps the session locked). A
 * member: their name, why, and the password tab with their username filled in — signing in again (any method) picks
 * the session back up. A guest cannot sign in to it again: the desk unlocks it.
 */
function ReloginNote(): JSX.Element | null {
  const { t } = useTranslation();
  const relogin = useAuthStore((s) => s.relogin);
  const clear = useAuthStore((s) => s.clearRelogin);
  if (!relogin) {
    return null;
  }
  return (
    <div role="status" className="mb-4 flex items-center gap-4 rounded-md bg-accent/15 px-4 py-3 text-left">
      <Avatar name={relogin.displayName} src={relogin.avatarUrl} size="md" />
      <div className="min-w-0 flex-1">
        <p className="font-semibold text-text">
          {relogin.guest ? t('lock.relogin.guestTitle') : t('lock.relogin.title', { name: relogin.displayName })}
        </p>
        <p className="text-sm text-muted">{relogin.guest ? t('lock.relogin.guestHint') : t('lock.relogin.hint')}</p>
        {!relogin.guest && (
          <button
            type="button"
            data-nav="true"
            onClick={clear}
            className="focus-ring mt-1 rounded-sm text-sm font-medium text-accent underline-offset-2 hover:underline"
          >
            {t('lock.relogin.other')}
          </button>
        )}
      </div>
    </div>
  );
}

/**
 * `409 activeSessionElsewhere`: the player's session is open on another PC. «End the session on PC N and sign in
 * here» repeats the same sign-in with `endElsewhere: true` (its secrets were kept in memory for this one retry only);
 * cancel forgets them.
 */
function ElsewhereDialog(): JSX.Element {
  const { t } = useTranslation();
  const elsewhere = useAuthStore((s) => s.elsewhere);
  const confirm = useAuthStore((s) => s.endElsewhereAndLogin);
  const cancel = useAuthStore((s) => s.cancelElsewhere);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Kept while the retry runs: the store clears `elsewhere` as it sends it.
  const [shown, setShown] = useState(elsewhere);
  useEffect(() => {
    if (elsewhere) {
      setShown(elsewhere);
      setError(null);
    }
  }, [elsewhere]);
  const pc = shown?.pcName ?? null;

  const close = (): void => {
    cancel();
    setShown(null);
    setError(null);
  };

  const endThere = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      await confirm();
      setShown(null);
    } catch (e) {
      setError(describeError(e, 'login'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      open={shown !== null}
      onClose={() => !busy && close()}
      title={pc ? t('lock.elsewhere.title', { pc }) : t('lock.elsewhere.titleUnknown')}
      description={t('lock.elsewhere.body')}
      size="md"
      footer={
        <>
          <Button variant="ghost" size="lg" onClick={close} disabled={busy}>
            {t('common.cancel')}
          </Button>
          {error === null && (
            <Button variant="cta" size="lg" loading={busy} onClick={() => void endThere()}>
              {pc ? t('lock.elsewhere.confirm', { pc }) : t('lock.elsewhere.confirmUnknown')}
            </Button>
          )}
        </>
      }
    >
      {error && (
        <p role="alert" className="rounded-md bg-danger/15 px-4 py-3 text-base font-medium text-danger">
          {error}
        </p>
      )}
    </Modal>
  );
}

export default function LockScreen(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const { user, isOpen, isLocked } = useSession();
  const ready = useAuthStore((s) => s.ready);
  const readiness = useAuthStore((s) => s.readiness);
  const pcReady = useAuthStore(selectPcReady);
  const lockedByStaff = useAuthStore((s) => s.lockedByStaff);
  const relogin = useAuthStore((s) => s.relogin);
  const logout = useAuthStore((s) => s.logout);
  const defaultRoute = useSettingsStore((s) => s.shellConfig?.ui.defaultRoute ?? '/home');
  const pcName = useSettingsStore((s) => s.pcInfo?.pc.name ?? null);
  const club = useClub();
  const pcZone = useSettingsStore((s) => s.pcInfo?.pc.zone ?? '');
  const callAdminEnabled = useSettingsStore((s) => s.features.callAdmin);
  // Off until the server can confirm a scanned code (D-18): a QR that can never sign in only costs the player time.
  const qrLogin = useSettingsStore((s) => s.features.qrLogin);
  const animations = useThemeStore((s) => s.theme.animations);
  const playlist = useSettingsStore((s) => s.shellConfig?.ads.playlist);
  // The club's own art, the same the attract screen plays — stills only: a moving picture behind a code the player is
  // trying to scan is a distraction, not a showcase.
  const art = useMemo(() => (playlist ?? []).filter((i) => i.type === 'image'), [playlist]);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [tab, setTab] = useState<LoginTab>(() => (useSettingsStore.getState().features.qrLogin ? 'qr' : 'password'));
  const [calling, setCalling] = useState(false);
  const card = useRef<HTMLDivElement>(null);
  const { idle } = useIdle();
  useGamepad();

  const hasUser = user !== null;
  const showTariffs = ready && pcReady && hasUser && !isOpen;

  useEffect(() => {
    trackScreen(pathname);
  }, [pathname]);

  // Between players, pick up what the owner changed in the admin console (name, wallpaper, banners, rules): the Agent
  // writes the server's club block into shell.json whenever the config version moves.
  useEffect(() => {
    void useSettingsStore.getState().load();
    void useGamesStore.getState().load(true);
  }, []);

  // A session open before the PC is ready (restored after an Agent restart, opened at the desk) waits here too.
  useEffect(() => {
    if (hasUser && isOpen && !isLocked && pcReady) {
      navigate(defaultRoute, { replace: true });
    }
  }, [hasUser, isOpen, isLocked, pcReady, defaultRoute, navigate]);

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

  // The settings arrive after the first render: follow the flag both ways while the player has not picked a tab.
  const picked = useRef(false);
  useEffect(() => {
    if (!picked.current) {
      setTab(qrLogin ? 'qr' : 'password');
    } else if (!qrLogin) {
      setTab((current) => (current === 'qr' ? 'password' : current));
    }
  }, [qrLogin]);

  // Signing the same member in again after their sign-in expired mid-session: the password tab, username filled in.
  useEffect(() => {
    if (relogin && !relogin.guest) {
      picked.current = true;
      setTab('password');
    }
  }, [relogin]);
  const reloginMember = relogin && !relogin.guest ? relogin : null;

  // A reader's card from whatever was showing — another tab, or the attract screen this one replaced — goes to the card
  // tab, which signs in with it; the number is not lost and the player does not tap twice.
  const [tapped, setTapped] = useState<{ cardId: string } | null>(null);
  useEffect(() => {
    if (!ready || !pcReady || isLocked || hasUser) {
      return undefined;
    }
    return onCardRead((cardId) => {
      picked.current = true;
      setTab('card');
      setTapped({ cardId });
    });
  }, [ready, pcReady, isLocked, hasUser]);

  const tabs = useMemo<TabItem<LoginTab>[]>(
    () => [
      ...(qrLogin ? [{ key: 'qr' as const, label: t('lock.methodQr'), icon: QrIcon }] : []),
      { key: 'password', label: t('lock.methodPassword'), icon: KeyIcon },
      { key: 'card', label: t('lock.methodCard'), icon: CardIcon },
      { key: 'guest', label: t('lock.methodGuest'), icon: UserIcon },
    ],
    [t, qrLogin],
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
      {/* The owner's wallpaper from the admin console, when set, wins over the rotating art. */}
      {!club.wallpaperUrl && art.length > 0 ? (
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
            <div className="flex min-w-0 items-center gap-3">
              <ClubMark className="h-7 w-7" />
              <p className="truncate font-display text-2xl font-light tracking-tight text-text">{club.name}</p>
            </div>
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
            // Frosted over the club's art rather than a solid slab cut out of it; the accent line on top marks the
            // one panel to look at.
            className="glass-strong themed-scrollbar relative max-h-full w-[min(92vw,34rem)] overflow-y-auto overflow-x-hidden rounded-xl bg-surface/80 p-[clamp(1.25rem,3vh,2rem)] backdrop-blur-xl before:pointer-events-none before:absolute before:inset-x-8 before:top-0 before:h-px before:bg-[linear-gradient(90deg,transparent,rgb(var(--c-accent)/0.85),transparent)]"
          >
            {!ready ? (
              <div className="flex justify-center py-16">
                <Spinner size="xl" />
              </div>
            ) : !pcReady && readiness ? (
              <PreparingCard readiness={readiness} />
            ) : isLocked ? (
              lockedByStaff ? (
                <StaffLockCard />
              ) : (
                <UnlockForm />
              )
            ) : (
              <>
                {/* Spacing follows the screen height: at 1366×768 the QR card has to fit without a scrollbar. */}
                <div className="mb-[clamp(0.875rem,2.2vh,1.5rem)] text-center">
                  <h1 className="font-display text-3xl font-light leading-tight tracking-tight text-text">
                    {t('lock.title')}
                  </h1>
                  <p className="mt-1 text-balance text-base text-muted">{t('lock.subtitle')}</p>
                </div>
                <ExpiredNote />
                <ReloginNote />
                <LeaveReceiptNote />
                <Tabs
                  items={tabs}
                  value={tab}
                  onChange={(next) => {
                    picked.current = true;
                    // A tapped card is tried once: back on the card tab later, it is not sent again.
                    setTapped(null);
                    setTab(next);
                  }}
                  label={t('lock.chooseMethod')}
                  size="lg"
                  idPrefix="lock"
                  // Tighter sides than a lone tab strip: four methods ("Password", "Mehmon") have to fit the card.
                  className="mb-[clamp(0.875rem,2.2vh,1.5rem)] w-full [&>button]:flex-1 [&>button]:justify-center [&>button]:px-3"
                />
                <div role="tabpanel" id={`lock-panel-${tab}`} aria-labelledby={`lock-tab-${tab}`}>
                  {tab === 'qr' && <QrLogin />}
                  {tab === 'password' && (
                    <LoginForm key={reloginMember?.userId ?? ''} initialUsername={reloginMember?.username} />
                  )}
                  {tab === 'card' && <CardLogin tapped={tapped} />}
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
      <ElsewhereDialog />
      <NotificationCenter />
    </div>
  );
}
