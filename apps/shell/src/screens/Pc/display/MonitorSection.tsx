/**
 * "Монитор" in the PC block on Home: each display's resolution and refresh rate, the rates it supports as a pick-one
 * row (the highest flagged, and offered outright when the screen runs below it), and Windows' own "keep these
 * settings?" countdown after a switch. The Shell reverts an unconfirmed rate by itself, so the countdown here is only
 * the face. Mounted once in the whole app: a second copy would ask "keep these settings?" twice.
 */
import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { SettingsSection } from '@/components/settings/SettingsSection';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { DotAmount } from '@/components/ui/DotAmount';
import { Modal } from '@/components/ui/Modal';
import { Skeleton } from '@/components/ui/Skeleton';
import { useLocale } from '@/hooks/useLocale';
import type { DisplayInfo } from '@/lib/tauri';
import { betterRate, formatUnit } from './specs';
import { useDisplays } from './useDisplays';

/** Mirrors `CONFIRM_TIMEOUT_SECS` in `src-tauri/src/commands/display.rs`. */
const CONFIRM_SEC = 15;

export function MonitorIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <rect x="2.5" y="3.5" width="19" height="13" rx="1.5" />
      <path d="M8.5 20.5h7M12 16.5v4" />
    </svg>
  );
}

interface RatePickerProps {
  display: DisplayInfo;
  disabled: boolean;
  onPick: (hz: number) => void;
}

/** The display's rates as a radio row; the highest one carries a "max" tab. */
function RatePicker({ display, disabled, onPick }: RatePickerProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const best = Math.max(...display.rates);
  return (
    <div role="radiogroup" aria-label={t('pcDisplay.monitor.rates')} className="flex flex-wrap gap-2 pt-2">
      {display.rates.map((rate) => {
        const active = rate === display.hz;
        const top = rate === best && display.rates.length > 1;
        return (
          <button
            key={rate}
            type="button"
            role="radio"
            aria-checked={active}
            aria-label={
              top
                ? `${formatUnit('hz', rate, locale, t)}, ${t('pcDisplay.monitor.max')}`
                : formatUnit('hz', rate, locale, t)
            }
            data-nav="true"
            disabled={disabled}
            onClick={() => !active && onPick(rate)}
            className={clsx(
              'focus-ring relative inline-flex h-14 min-w-[5.75rem] items-baseline justify-center gap-1 rounded-md px-4 pt-3 transition-colors duration-[var(--dur-fast)] disabled:cursor-not-allowed disabled:opacity-50',
              active ? 'choice choice-on' : 'choice',
              top && !active && 'border-accent/45',
            )}
          >
            <span className="num-dot text-2xl leading-none">{rate}</span>
            <span className="text-xs font-medium text-muted">{t('pcDisplay.units.hzShort')}</span>
            {top && (
              <span className="absolute -top-2 right-2 rounded-sm bg-accent px-1.5 py-0.5 text-[0.6rem] font-bold uppercase leading-none tracking-wider text-on-accent">
                {t('pcDisplay.monitor.max')}
              </span>
            )}
          </button>
        );
      })}
    </div>
  );
}

interface DisplayCardProps {
  display: DisplayInfo;
  /** Several displays: name each one and mark the primary. */
  numbered: boolean;
  /** A switch on this display is under way. */
  switching: boolean;
  /** Picking is off (a switch or an unconfirmed change anywhere). */
  locked: boolean;
  onPick: (hz: number) => void;
}

function DisplayCard({ display, numbered, switching, locked, onPick }: DisplayCardProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const hz = (value: number): string => formatUnit('hz', value, locale, t);
  const better = betterRate(display.hz, display.rates);
  return (
    <div className="flex flex-col gap-4 rounded-lg border border-text/10 bg-text/[0.02] p-4">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div className="flex min-w-0 items-center gap-3">
          <span
            aria-hidden="true"
            className="inline-flex h-12 w-12 shrink-0 items-center justify-center rounded-md bg-accent/10 text-accent [&>svg]:h-7 [&>svg]:w-7"
          >
            <MonitorIcon />
          </span>
          <div className="min-w-0">
            <p className="flex flex-wrap items-center gap-2 text-lg font-semibold">
              {numbered ? t('pcDisplay.monitor.display', { n: display.index + 1 }) : t('pcDisplay.monitor.screen')}
              {numbered && display.primary && (
                <Badge tone="primary" size="sm">
                  {t('pcDisplay.monitor.primary')}
                </Badge>
              )}
            </p>
            <p className="hud-label tnum mt-1">
              {display.width > 0 ? t('pcDisplay.monitor.resolution', { w: display.width, h: display.height }) : '—'}
            </p>
          </div>
        </div>
        <div className="text-right">
          <p className="hud-label">{t('pcDisplay.monitor.current')}</p>
          <p className="mt-1 text-4xl leading-none" aria-live="polite">
            {switching ? (
              <span className="text-base text-muted">{t('pcDisplay.monitor.switching')}</span>
            ) : (
              <DotAmount value={display.hz > 0 ? hz(display.hz) : '—'} />
            )}
          </p>
        </div>
      </div>

      {display.rates.length > 1 ? (
        <RatePicker display={display} disabled={locked} onPick={onPick} />
      ) : (
        <p className="text-sm text-muted">{t('pcDisplay.monitor.onlyRate')}</p>
      )}

      {better !== null && (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-accent/30 bg-accent/[0.06] px-4 py-3">
          <p className="min-w-0 flex-1 text-sm">{t('pcDisplay.monitor.upgradeHint', { hz: hz(better) })}</p>
          <Button size="md" disabled={locked} onClick={() => onPick(better)}>
            {t('pcDisplay.monitor.upgrade', { hz: hz(better) })}
          </Button>
        </div>
      )}
    </div>
  );
}

interface ConfirmRateModalProps {
  /** The display whose change waits for confirmation; `null` closes the dialog. */
  display: DisplayInfo | null;
  busy: boolean;
  onKeep: () => void;
  onRevert: () => void;
}

/** "Keep these settings?" with the time left before the previous rate comes back; reverting is the default focus. */
function ConfirmRateModal({ display, busy, onKeep, onRevert }: ConfirmRateModalProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const revertRef = useRef<HTMLButtonElement>(null);
  const pending = display?.pending ?? null;
  const revertAt = pending?.revertAt ?? null;
  const [now, setNow] = useState(() => Date.now());
  const fired = useRef<string | null>(null);

  useEffect(() => {
    if (revertAt === null) {
      return undefined;
    }
    setNow(Date.now());
    const id = window.setInterval(() => setNow(Date.now()), 200);
    return () => window.clearInterval(id);
  }, [revertAt]);

  const deadline = revertAt === null ? 0 : Date.parse(revertAt);
  const leftMs = Number.isNaN(deadline) ? 0 : Math.max(0, deadline - now);
  const seconds = Math.ceil(leftMs / 1000);

  useEffect(() => {
    // The Shell reverts at the same moment; asking as well refreshes the list and tells the player. A confirmation
    // still in flight settles it instead.
    if (revertAt !== null && leftMs === 0 && !busy && fired.current !== revertAt) {
      fired.current = revertAt;
      onRevert();
    }
  }, [revertAt, leftMs, busy, onRevert]);

  const hz = (value: number): string => formatUnit('hz', value, locale, t);
  const previous = pending ? hz(pending.previousHz) : '';

  return (
    <Modal
      open={pending !== null}
      onClose={() => !busy && onRevert()}
      title={t('pcDisplay.monitor.confirmTitle')}
      description={display ? t('pcDisplay.monitor.confirmBody', { hz: hz(display.hz) }) : undefined}
      size="sm"
      closeOnBackdrop={false}
      showClose={false}
      initialFocusRef={revertRef}
      footer={
        <>
          <Button ref={revertRef} variant="ghost" size="lg" disabled={busy} onClick={onRevert}>
            {t('pcDisplay.monitor.revert', { hz: previous })}
          </Button>
          <Button size="lg" loading={busy} onClick={onKeep}>
            {t('pcDisplay.monitor.keep')}
          </Button>
        </>
      }
    >
      <div className="flex items-center gap-5">
        <span className="w-16 shrink-0 text-center text-5xl leading-none text-accent">
          <DotAmount value={String(seconds)} />
        </span>
        <div className="flex min-w-0 flex-1 flex-col gap-2">
          <p className="text-sm text-muted">{t('pcDisplay.monitor.revertIn', { hz: previous, seconds })}</p>
          <span
            aria-hidden="true"
            className="tick-scale block text-accent"
            style={{ '--value': Math.min(1, leftMs / (CONFIRM_SEC * 1000)) } as CSSProperties}
          />
        </div>
      </div>
    </Modal>
  );
}

export function MonitorSection(): JSX.Element {
  const { t } = useTranslation();
  const { displays, failed, busy, reload, setRate, confirm, revert } = useDisplays();
  const pending = displays?.find((d) => d.pending !== null) ?? null;
  const pendingDevice = pending?.device ?? null;
  const onRevert = useCallback(() => {
    if (pendingDevice !== null) {
      void revert(pendingDevice);
    }
  }, [pendingDevice, revert]);

  let body: JSX.Element;
  if (displays === null && !failed) {
    body = <Skeleton variant="rect" height="10rem" />;
  } else if (displays === null || displays.length === 0) {
    body = (
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-muted">{t('pcDisplay.monitor.unavailable')}</p>
        <Button variant="secondary" size="md" onClick={reload}>
          {t('common.retry')}
        </Button>
      </div>
    );
  } else {
    body = (
      // One column even for two displays: the card shares the home block's row with two others.
      <div className="grid gap-3">
        {displays.map((d) => (
          <DisplayCard
            key={d.device}
            display={d}
            numbered={displays.length > 1}
            switching={busy === d.device && pending === null}
            locked={busy !== null || pending !== null}
            onPick={(hz) => void setRate(d.device, hz)}
          />
        ))}
      </div>
    );
  }

  return (
    <SettingsSection title={t('pcDisplay.monitor.title')} description={t('pcDisplay.monitor.description')}>
      {body}
      <ConfirmRateModal
        display={pending}
        busy={pendingDevice !== null && busy === pendingDevice}
        onKeep={() => pendingDevice !== null && void confirm(pendingDevice)}
        onRevert={onRevert}
      />
    </SettingsSection>
  );
}

export default MonitorSection;
