/**
 * Top bar of the authenticated shell: club / PC (left), navigation (centre) and, on the right, the player (avatar →
 * profile), the session timer (→ add time), the balance (→ wallet / top up), a link dot only while the link is down,
 * the clock, one sound-and-language menu and lock. Every control is a `data-nav` button.
 */
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router-dom';
import type { Locale, Money } from '@clubshell/contracts';
import { Avatar } from '@/components/ui/Avatar';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { useLocale } from '@/hooks/useLocale';
import { useSession } from '@/hooks/useSession';
import { formatMoney } from '@/lib/format';
import { formatClock, serverNow } from '@/lib/time';
import { NavBar } from '@/screens/Desktop/NavBar';
import { PlusDisc, SessionTimer } from '@/screens/Desktop/SessionTimer';
import { useNotificationsStore } from '@/store/notifications';
import { selectFeatures, useSettingsStore } from '@/store/settings';
import { useWalletStore } from '@/store/wallet';

// ---------------------------------------------------------------------------------------------------------------------
// Icons
// ---------------------------------------------------------------------------------------------------------------------

const svgProps = {
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 2,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
  'aria-hidden': true,
} as const;

const IconVolume = ({ muted }: { muted: boolean }): JSX.Element => (
  <svg {...svgProps}>
    <path d="M4 10v4h3l5 4V6L7 10H4Z" />
    {muted ? <path d="M16 9l5 6M21 9l-5 6" /> : <path d="M15.5 8.5a5 5 0 0 1 0 7M18.5 5.5a9 9 0 0 1 0 13" />}
  </svg>
);
const IconLock = (): JSX.Element => (
  <svg {...svgProps}>
    <rect x="5" y="11" width="14" height="10" rx="2" />
    <path d="M8 11V7a4 4 0 0 1 8 0v4" />
  </svg>
);
const IconGlobe = (): JSX.Element => (
  <svg {...svgProps}>
    <circle cx="12" cy="12" r="9" />
    <path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18" />
  </svg>
);
const IconPc = (): JSX.Element => (
  <svg {...svgProps}>
    <rect x="3" y="4" width="18" height="12" rx="2" />
    <path d="M8 20h8M12 16v4" />
  </svg>
);

// ---------------------------------------------------------------------------------------------------------------------
// Popover (anchored panel; closes on outside pointer-down and Escape)
// ---------------------------------------------------------------------------------------------------------------------

interface PopoverProps {
  open: boolean;
  onClose: () => void;
  label: string;
  trigger: ReactNode;
  children: ReactNode;
  className?: string;
}

function Popover({ open, onClose, label, trigger, children, className }: PopoverProps): JSX.Element {
  const root = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) {
      return undefined;
    }
    const onPointer = (e: PointerEvent): void => {
      if (root.current && e.target instanceof Node && !root.current.contains(e.target)) {
        onClose();
      }
    };
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onClose();
      }
    };
    document.addEventListener('pointerdown', onPointer, true);
    window.addEventListener('keydown', onKey, true);
    const frame = requestAnimationFrame(() =>
      root.current?.querySelector<HTMLElement>('[data-nav]:not([data-popover-trigger])')?.focus(),
    );
    return () => {
      document.removeEventListener('pointerdown', onPointer, true);
      window.removeEventListener('keydown', onKey, true);
      cancelAnimationFrame(frame);
    };
  }, [open, onClose]);

  return (
    <div ref={root} className="relative">
      {trigger}
      {open && (
        <div
          role="dialog"
          aria-label={label}
          className={clsx(
            // Near-opaque: at glass-strong's 82% the page's own text read through the menu's lines.
            'glass-strong anim-pop absolute right-0 top-[calc(100%+0.5rem)] z-50 rounded-lg bg-surface/95 p-3',
            className,
          )}
        >
          {children}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Clock
// ---------------------------------------------------------------------------------------------------------------------

export function Clock({ format }: { format: string }): JSX.Element {
  const { t } = useTranslation();
  const [now, setNow] = useState(() => serverNow());
  useEffect(() => {
    const id = setInterval(() => setNow(serverNow()), 1000);
    return () => clearInterval(id);
  }, []);
  return (
    <time dateTime={now.toISOString()} aria-label={t('desktop.clock')} className="tnum text-xl font-semibold text-text">
      {formatClock(now, format)}
    </time>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Sound and language (one menu: both are set once per visit, neither earns a permanent icon in the bar)
// ---------------------------------------------------------------------------------------------------------------------

const VOLUME_COMMIT_MS = 150;

export function SystemMenu(): JSX.Element {
  const { t } = useTranslation();
  const volume = useSettingsStore((s) => s.settings.volume);
  const muted = useSettingsStore((s) => s.settings.muted);
  const setVolume = useSettingsStore((s) => s.setVolume);
  const toggleMute = useSettingsStore((s) => s.toggleMute);
  const { locale, setLocale, locales, names } = useLocale();
  const pushError = useNotificationsStore((s) => s.pushError);
  const [open, setOpen] = useState(false);
  const [local, setLocal] = useState(volume);
  const timer = useRef<number | null>(null);

  useEffect(() => setLocal(volume), [volume]);
  useEffect(
    () => () => {
      if (timer.current !== null) {
        window.clearTimeout(timer.current);
      }
    },
    [],
  );

  const commit = (level: number): void => {
    setLocal(level);
    if (timer.current !== null) {
      window.clearTimeout(timer.current);
    }
    timer.current = window.setTimeout(() => {
      timer.current = null;
      setVolume(level, false).catch((e: unknown) => pushError(e, t('desktop.volume')));
    }, VOLUME_COMMIT_MS);
  };

  const close = useCallback(() => setOpen(false), []);

  const choose = (next: Locale): void => {
    setOpen(false);
    if (next !== locale) {
      setLocale(next).catch((e: unknown) => pushError(e, t('desktop.language')));
    }
  };

  return (
    <Popover
      open={open}
      onClose={close}
      label={t('desktop.soundAndLanguage')}
      className="w-72"
      trigger={
        <Button
          variant="ghost"
          iconOnly
          data-popover-trigger="true"
          aria-label={t('desktop.soundAndLanguage')}
          title={t('desktop.soundAndLanguage')}
          aria-haspopup="dialog"
          aria-expanded={open}
          icon={<IconVolume muted={muted || volume === 0} />}
          onClick={() => setOpen((v) => !v)}
        />
      }
    >
      <div className="flex flex-col gap-3">
        <div className="flex items-center justify-between text-sm text-muted">
          <span>{t('desktop.volume')}</span>
          <span className="tnum">{local}%</span>
        </div>
        <input
          type="range"
          min={0}
          max={100}
          step={5}
          value={local}
          data-nav="true"
          aria-label={t('desktop.volume')}
          onChange={(e) => commit(Number(e.currentTarget.value))}
          className="focus-ring h-2 w-full cursor-pointer accent-primary"
        />
        <Button
          variant="secondary"
          size="md"
          block
          icon={<IconVolume muted={!muted} />}
          onClick={() => toggleMute().catch((e: unknown) => pushError(e, t('desktop.volume')))}
        >
          {muted ? t('desktop.unmute') : t('desktop.mute')}
        </Button>
      </div>
      <div className="my-3 h-px bg-text/10" aria-hidden="true" />
      <p className="mb-2 flex items-center gap-2 text-sm text-muted">
        <span className="inline-flex h-4 w-4" aria-hidden="true">
          <IconGlobe />
        </span>
        {t('desktop.language')}
      </p>
      <ul role="listbox" aria-label={t('desktop.language')} className="flex flex-col gap-1">
        {locales.map((l) => (
          <li key={l} role="none">
            <button
              type="button"
              role="option"
              aria-selected={l === locale}
              data-nav="true"
              onClick={() => choose(l)}
              className={clsx(
                'focus-ring flex w-full items-center justify-between rounded-md px-3 py-2 text-left text-base transition-colors duration-[var(--dur-fast)]',
                l === locale ? 'bg-primary/15 text-primary' : 'text-text hover:bg-text/10',
              )}
            >
              <span>{names[l]}</span>
              <span className="text-xs uppercase text-muted">{l}</span>
            </button>
          </li>
        ))}
      </ul>
    </Popover>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Connectivity
// ---------------------------------------------------------------------------------------------------------------------

/** A dot that only appears when something is wrong: a green "all fine" light is one more thing to look past. */
export function ConnectivityIndicator(): JSX.Element | null {
  const { t } = useTranslation();
  const agentConnected = useNotificationsStore((s) => s.agentConnected);
  const server = useNotificationsStore((s) => s.serverConnectivity);
  if (agentConnected && server === 'online') {
    return null;
  }
  const label = agentConnected ? t('desktop.serverOffline') : t('kiosk.agentDisconnected');
  return (
    <span
      role="status"
      title={label}
      aria-label={`${t('desktop.connection')}: ${label}`}
      className="inline-flex h-10 w-6 shrink-0 items-center justify-center"
    >
      <span
        aria-hidden="true"
        className={clsx(
          'h-2 w-2 rounded-full shadow-[0_0_8px_currentColor]',
          agentConnected ? 'bg-accent' : 'anim-live-dot bg-danger',
        )}
      />
    </span>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Balance
// ---------------------------------------------------------------------------------------------------------------------

/**
 * Balance as its own button to the wallet, with a "+" when top-ups are enabled. It replaces both the balance line
 * under the user's name and the top-up card Home used to carry, so the money action is one click away on every
 * screen instead of only on the first one.
 */
function BalanceButton({ amount, topUp }: { amount: Money; topUp: boolean }): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const money = formatMoney(amount, locale);
  return (
    <button
      type="button"
      data-nav="true"
      aria-label={
        topUp ? `${t('desktop.balance')}: ${money}. ${t('desktop.topUp')}` : `${t('desktop.balance')}: ${money}`
      }
      title={topUp ? t('desktop.topUp') : undefined}
      onClick={() => navigate('/wallet')}
      className={clsx(
        'focus-ring glass group flex h-12 shrink-0 items-center gap-2.5 rounded-full pl-4 transition-colors duration-[var(--dur-fast)] hover:bg-surface/80',
        topUp ? 'pr-1.5' : 'pr-4',
      )}
    >
      <span className="tnum text-lg font-semibold text-text">{money}</span>
      {topUp && <PlusDisc />}
    </button>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Top bar
// ---------------------------------------------------------------------------------------------------------------------

export function TopBar(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  // The avatar is the only way to the profile now, so it carries the "you are here" state the nav item used to.
  const onProfile = useLocation().pathname.startsWith('/profile');
  const pc = useSettingsStore((s) => s.pcInfo?.pc ?? null);
  const showClock = useSettingsStore((s) => s.shellConfig?.ui.showClock ?? true);
  const clockFormat = useSettingsStore((s) => s.shellConfig?.ui.clockFormat ?? 'HH:mm');
  const features = useSettingsStore(selectFeatures);
  const pushError = useNotificationsStore((s) => s.pushError);
  const { user, isGuest, isActive, lock, busy } = useSession();
  // The wallet store follows `wallet.updated`; the user record only refreshes on login.
  const walletBalance = useWalletStore((w) => w.balance?.amount ?? null);
  const balance = walletBalance ?? user?.balance ?? null;
  const [locking, setLocking] = useState(false);

  const onLock = async (): Promise<void> => {
    setLocking(true);
    try {
      await lock('user');
      navigate('/lock');
    } catch (e) {
      pushError(e, t('session.lockTitle'));
    } finally {
      setLocking(false);
    }
  };

  const roleBadge = isGuest ? (
    <Badge tone="muted" size="sm">
      {t('desktop.guestBadge')}
    </Badge>
  ) : user?.role === 'vip' ? (
    <Badge tone="accent" size="sm" solid>
      {t('desktop.vipBadge')}
    </Badge>
  ) : null;

  return (
    <div className="flex h-full w-full items-center gap-[var(--gap)] bg-bg/60 px-[var(--gutter)] backdrop-blur-2xl">
      {/* Left: club + PC identity */}
      <div className="flex min-w-0 shrink-0 items-center gap-3">
        <span
          aria-hidden="true"
          className="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-text/10 text-text [&>svg]:h-5 [&>svg]:w-5"
        >
          <IconPc />
        </span>
        <div className="min-w-0 leading-tight">
          <div className="truncate text-[0.7rem] font-bold uppercase tracking-[0.18em] text-text/80">
            {t('idle.clubName')}
          </div>
          <div className="truncate text-xs text-muted">{pc ? pc.name : t('common.loading')}</div>
        </div>
      </div>

      {/* Centre: navigation */}
      <NavBar className="flex min-w-0 flex-1 justify-center" />

      {/* Right: who, how long, how much — then clock, sound & language, lock. The link dot only shows when broken. */}
      <div className="flex shrink-0 items-center justify-end gap-2">
        {user && (
          <button
            type="button"
            data-nav="true"
            aria-label={`${t('desktop.userMenu')}: ${user.displayName}`}
            aria-current={onProfile ? 'page' : undefined}
            disabled={!features.profile}
            onClick={() => navigate('/profile')}
            className={clsx(
              'focus-ring flex h-11 items-center gap-2.5 rounded-full pl-1 pr-3 transition-colors duration-[var(--dur-fast)] hover:bg-text/10 disabled:cursor-default disabled:hover:bg-transparent',
              onProfile && 'bg-text/10',
            )}
          >
            <Avatar name={user.displayName} src={user.avatarUrl ?? null} size="sm" ring={user.role === 'vip'} />
            <span className="hidden min-w-0 items-center gap-1.5 2xl:flex">
              <span className="max-w-[10rem] truncate text-sm font-semibold text-text">{user.displayName}</span>
              {roleBadge}
            </span>
          </button>
        )}
        <SessionTimer compact />
        {balance && <BalanceButton amount={balance} topUp={features.topup} />}
        <ConnectivityIndicator />
        {showClock && <Clock format={clockFormat} />}
        <SystemMenu />
        {isActive && (
          <Button
            variant="ghost"
            iconOnly
            aria-label={t('desktop.lock')}
            title={t('desktop.lock')}
            loading={locking || busy}
            icon={<IconLock />}
            onClick={() => void onLock()}
          />
        )}
      </div>
    </div>
  );
}
