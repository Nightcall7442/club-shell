/**
 * Top bar of the authenticated shell: PC / zone (left), session countdown (centre) and, on the right, balance,
 * user, connectivity, clock, volume popover, language switch and lock. Every control is a `data-nav` button.
 */
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import type { Locale } from '@clubshell/contracts';
import { Avatar } from '@/components/ui/Avatar';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { useLocale } from '@/hooks/useLocale';
import { useSession } from '@/hooks/useSession';
import { formatMoney } from '@/lib/format';
import { formatClock, serverNow } from '@/lib/time';
import { NavBar } from '@/screens/Desktop/NavBar';
import { SessionTimer } from '@/screens/Desktop/SessionTimer';
import { useNotificationsStore } from '@/store/notifications';
import { selectFeatures, useSettingsStore } from '@/store/settings';

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
            'glass-strong anim-pop absolute right-0 top-[calc(100%+0.5rem)] z-50 rounded-lg p-3',
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
// Volume
// ---------------------------------------------------------------------------------------------------------------------

const VOLUME_COMMIT_MS = 150;

export function VolumeControl(): JSX.Element {
  const { t } = useTranslation();
  const volume = useSettingsStore((s) => s.settings.volume);
  const muted = useSettingsStore((s) => s.settings.muted);
  const setVolume = useSettingsStore((s) => s.setVolume);
  const toggleMute = useSettingsStore((s) => s.toggleMute);
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

  return (
    <Popover
      open={open}
      onClose={close}
      label={t('desktop.volume')}
      className="w-64"
      trigger={
        <Button
          variant="ghost"
          iconOnly
          data-popover-trigger="true"
          aria-label={t('desktop.volume')}
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
          <span className="tnum">{t('settings.volumeLevel', { level: local })}</span>
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
    </Popover>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Language
// ---------------------------------------------------------------------------------------------------------------------

export function LocaleSwitch(): JSX.Element {
  const { t } = useTranslation();
  const { locale, setLocale, locales, names } = useLocale();
  const pushError = useNotificationsStore((s) => s.pushError);
  const [open, setOpen] = useState(false);
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
      label={t('desktop.language')}
      className="w-48"
      trigger={
        <Button
          variant="ghost"
          data-popover-trigger="true"
          aria-label={`${t('desktop.language')}: ${names[locale]}`}
          aria-haspopup="listbox"
          aria-expanded={open}
          iconOnly
          icon={<IconGlobe />}
          onClick={() => setOpen((v) => !v)}
        />
      }
    >
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

export function ConnectivityIndicator(): JSX.Element {
  const { t } = useTranslation();
  const agentConnected = useNotificationsStore((s) => s.agentConnected);
  const server = useNotificationsStore((s) => s.serverConnectivity);
  const tone = !agentConnected ? 'danger' : server === 'online' ? 'success' : 'accent';
  const label = !agentConnected
    ? t('kiosk.agentDisconnected')
    : server === 'online'
      ? t('desktop.serverOnline')
      : t('desktop.serverOffline');
  const DOT = { danger: 'bg-danger', success: 'bg-success', accent: 'bg-accent' } as const;
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
          DOT[tone],
          !agentConnected && 'anim-live-dot',
        )}
      />
    </span>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Top bar
// ---------------------------------------------------------------------------------------------------------------------

export function TopBar(): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const pc = useSettingsStore((s) => s.pcInfo?.pc ?? null);
  const showClock = useSettingsStore((s) => s.shellConfig?.ui.showClock ?? true);
  const clockFormat = useSettingsStore((s) => s.shellConfig?.ui.clockFormat ?? 'HH:mm');
  const features = useSettingsStore(selectFeatures);
  const pushError = useNotificationsStore((s) => s.pushError);
  const { user, isGuest, isActive, lock, busy } = useSession();
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

      {/* Right: balance, user + countdown, status, clock, controls */}
      <div className="flex shrink-0 items-center justify-end gap-1.5">
        {user && (
          <button
            type="button"
            data-nav="true"
            aria-label={`${t('desktop.userMenu')}: ${user.displayName}. ${t('desktop.balance')}: ${formatMoney(user.balance, locale)}`}
            disabled={!features.profile}
            onClick={() => navigate('/profile')}
            className="focus-ring flex h-11 items-center gap-2.5 rounded-full pl-1 pr-3 transition-colors duration-[var(--dur-fast)] hover:bg-text/10 disabled:cursor-default disabled:hover:bg-transparent"
          >
            <Avatar name={user.displayName} src={user.avatarUrl ?? null} size="sm" ring={user.role === 'vip'} />
            <span className="hidden min-w-0 flex-col items-start leading-tight 2xl:flex">
              <span className="flex items-center gap-1.5">
                <span className="max-w-[10rem] truncate text-sm font-semibold text-text">{user.displayName}</span>
                {roleBadge}
              </span>
              <span className="tnum text-xs text-muted">{formatMoney(user.balance, locale)}</span>
            </span>
          </button>
        )}
        <SessionTimer compact />
        <ConnectivityIndicator />
        {showClock && <Clock format={clockFormat} />}
        <VolumeControl />
        <LocaleSwitch />
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
