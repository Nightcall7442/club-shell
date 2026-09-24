/**
 * The two HUD bars that frame every authenticated screen, like a game's pause menu.
 *
 * - Top: club mark and PC, the section tabs (`LB` · tabs · `RB`), a link dot only while the link is down, the clock,
 *   one sound-and-language menu and lock.
 * - Bottom (status line): the player (→ profile), time left (→ add time), balance (→ wallet / top up) and the
 *   controller prompts for what the buttons do here.
 *
 * Every control is a `data-nav` button.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router-dom';
import type { Locale, Money } from '@clubshell/contracts';
import { Avatar } from '@/components/ui/Avatar';
import { Button } from '@/components/ui/Button';
import { DotAmount } from '@/components/ui/DotAmount';
import { Popover } from '@/components/ui/Popover';
import { useLocale } from '@/hooks/useLocale';
import { useSession } from '@/hooks/useSession';
import { formatMoney } from '@/lib/format';
import { formatClock, serverNow } from '@/lib/time';
import { NavBar } from '@/screens/Desktop/NavBar';
import { PlusButton, SessionTimer } from '@/screens/Desktop/SessionTimer';
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
    <time
      dateTime={now.toISOString()}
      aria-label={t('desktop.clock')}
      className="num-dot text-2xl leading-none text-text"
    >
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
          className="h-9 w-9 text-muted hover:text-text"
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
      className="inline-flex h-5 w-5 shrink-0 items-center justify-center"
    >
      <span
        aria-hidden="true"
        className={clsx('h-2 w-2 rounded-full', agentConnected ? 'bg-accent' : 'anim-live-dot bg-danger')}
      />
    </span>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Balance
// ---------------------------------------------------------------------------------------------------------------------

/** Balance in the status line: opens the wallet, where top-ups live; the "+" says so when top-ups are enabled. */
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
      className="focus-ring group flex h-12 items-center gap-3 rounded-md px-3 text-left transition-colors duration-[var(--dur-fast)] hover:bg-text/[0.04]"
    >
      <span className="hud-label">{t('desktop.balance')}</span>
      <DotAmount value={money} className="text-[1.45rem] leading-none text-text" />
      {topUp && <PlusButton />}
    </button>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Controller prompts
// ---------------------------------------------------------------------------------------------------------------------

/** A face-button glyph (Ⓐ-style disc) with what it does here. */
function Prompt({ glyph, label, round = true }: { glyph: string; label: string; round?: boolean }): JSX.Element {
  return (
    <span className="flex items-center gap-2 whitespace-nowrap">
      <span
        aria-hidden="true"
        className={clsx(
          'inline-flex h-6 min-w-6 items-center justify-center border border-text/25 px-1 font-mono text-[0.62rem] font-medium text-text',
          round ? 'rounded-full' : 'rounded-md',
        )}
      >
        {glyph}
      </span>
      <span className="hud-label text-text/70">{label}</span>
    </span>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Top bar
// ---------------------------------------------------------------------------------------------------------------------

export function TopBar(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const pc = useSettingsStore((s) => s.pcInfo?.pc ?? null);
  const showClock = useSettingsStore((s) => s.shellConfig?.ui.showClock ?? true);
  const clockFormat = useSettingsStore((s) => s.shellConfig?.ui.clockFormat ?? 'HH:mm');
  const pushError = useNotificationsStore((s) => s.pushError);
  const { isActive, lock, busy } = useSession();
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

  return (
    <div className="grid h-full w-full grid-cols-[1fr_auto_1fr] items-center gap-[var(--gap)] bg-gradient-to-b from-bg via-bg/80 to-transparent px-[var(--gutter)]">
      {/* Club mark + PC */}
      <div className="flex min-w-0 items-center gap-3">
        <span aria-hidden="true" className="h-6 w-6 shrink-0 rotate-45 border border-accent/70" />
        <div className="min-w-0 leading-tight">
          <div className="truncate font-display text-sm font-normal tracking-tight text-text">{t('idle.clubName')}</div>
          <div className="hud-label mt-0.5 truncate">{pc ? `${pc.name} · ${pc.zone}` : t('common.loading')}</div>
        </div>
      </div>

      <NavBar className="justify-center" />

      <div className="flex items-center justify-end gap-2">
        <ConnectivityIndicator />
        {showClock && <Clock format={clockFormat} />}
        <span aria-hidden="true" className="mx-2 h-6 w-px bg-text/15" />
        <SystemMenu />
        {isActive && (
          <Button
            variant="ghost"
            iconOnly
            aria-label={t('desktop.lock')}
            title={t('desktop.lock')}
            className="h-10 w-10 text-muted hover:text-text"
            loading={locking || busy}
            icon={<IconLock />}
            onClick={() => void onLock()}
          />
        )}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Status line
// ---------------------------------------------------------------------------------------------------------------------

export function StatusBar(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  // The avatar is the only way to the profile, so it carries the "you are here" state.
  const onProfile = useLocation().pathname.startsWith('/profile');
  const features = useSettingsStore(selectFeatures);
  const { user, isGuest } = useSession();
  // The wallet store follows `wallet.updated`; the user record only refreshes on login.
  const walletBalance = useWalletStore((w) => w.balance?.amount ?? null);
  const balance = walletBalance ?? user?.balance ?? null;
  const role = isGuest ? t('desktop.guestBadge') : user?.role === 'vip' ? t('desktop.vipBadge') : null;

  return (
    <div className="flex h-full w-full items-center gap-2 border-t border-[color:var(--hairline)] bg-bg/90 px-[var(--gutter)]">
      {user && (
        <button
          type="button"
          data-nav="true"
          aria-label={`${t('desktop.userMenu')}: ${user.displayName}`}
          aria-current={onProfile ? 'page' : undefined}
          disabled={!features.profile}
          onClick={() => navigate('/profile')}
          className={clsx(
            'focus-ring flex h-12 min-w-0 items-center gap-2.5 rounded-md pl-1.5 pr-3 text-left transition-colors duration-[var(--dur-fast)] hover:bg-text/[0.04] disabled:cursor-default disabled:hover:bg-transparent',
            onProfile && 'bg-text/[0.08]',
          )}
        >
          <Avatar name={user.displayName} src={user.avatarUrl ?? null} size="sm" />
          <span className="min-w-0 leading-tight">
            <span className="block max-w-[12rem] truncate text-sm font-medium text-text">{user.displayName}</span>
            {role && <span className="hud-label block truncate">{role}</span>}
          </span>
        </button>
      )}
      <span aria-hidden="true" className="mx-1 h-6 w-px bg-text/15" />
      <SessionTimer compact />
      {balance && <BalanceButton amount={balance} topUp={features.topup} />}

      <div aria-label={t('desktop.prompts.title')} className="ml-auto flex items-center gap-6">
        <Prompt glyph="A" label={t('desktop.prompts.select')} />
        <Prompt glyph="B" label={t('desktop.prompts.back')} />
        <Prompt glyph="LB RB" label={t('desktop.prompts.sections')} round={false} />
      </div>
    </div>
  );
}
