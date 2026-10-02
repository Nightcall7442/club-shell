/**
 * "Устройство вывода": the PC's active audio outputs (`pc_audio_outputs`) as cards — headphones, speakers, the
 * monitor's HDMI — and one tap makes one the default for games and apps. The player's volume (the "Звук" section) is
 * then re-applied so the new device plays at the level on the slider. Read-only when the PC cannot switch.
 */
import { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/Skeleton';
import { Spinner } from '@/components/ui/Spinner';
import { api, type PcAudioOutput, type PcAudioOutputs } from '@/lib/tauri';
import { useNotificationsStore, useSettingsStore } from '@/store';
import { SettingsSection } from '../Settings';
import { CheckIcon, OutputIcon, RefreshIcon } from './icons';
import { splitDeviceName } from './pcFormat';

interface OutputCardProps {
  device: PcAudioOutput;
  disabled: boolean;
  busy: boolean;
  onSelect: () => void;
}

function OutputCard({ device, disabled, busy, onSelect }: OutputCardProps): JSX.Element {
  const { t } = useTranslation();
  const { title, detail } = splitDeviceName(device.name);
  const active = device.isDefault;
  return (
    <button
      type="button"
      role="radio"
      aria-checked={active}
      aria-label={device.name}
      data-nav="true"
      disabled={disabled}
      title={device.name}
      onClick={onSelect}
      className={clsx(
        'focus-ring flex min-w-0 items-center gap-3 rounded-lg px-3 py-3 text-left disabled:cursor-not-allowed',
        active ? 'choice choice-on' : 'choice',
        disabled && !active && 'opacity-50',
      )}
    >
      <span
        aria-hidden="true"
        className={clsx(
          'grid h-11 w-11 shrink-0 place-items-center rounded-md transition-colors duration-[var(--dur-fast)] [&>svg]:h-6 [&>svg]:w-6',
          active ? 'bg-accent/15 text-accent' : 'bg-text/[0.06] text-muted',
        )}
      >
        <OutputIcon kind={device.kind} />
      </span>
      <span className="min-w-0 flex-1" translate="no">
        <span className="block truncate text-base font-semibold">{title}</span>
        <span className={clsx('block truncate text-sm', active ? 'text-accent' : 'text-muted')}>
          {active ? t('pcSettings.outputDefault') : (detail ?? t(`pcSettings.kind.${device.kind}`))}
        </span>
      </span>
      {busy ? (
        <Spinner size="sm" />
      ) : (
        active && (
          <span aria-hidden="true" className="h-5 w-5 shrink-0 text-accent [&>svg]:h-full [&>svg]:w-full">
            <CheckIcon />
          </span>
        )
      )}
    </button>
  );
}

export function AudioOutputSection(): JSX.Element {
  const { t } = useTranslation();
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [outputs, setOutputs] = useState<PcAudioOutputs | null>(null);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async (): Promise<void> => {
    setLoading(true);
    try {
      setOutputs(await api.pc.audioOutputs());
      setFailed(false);
    } catch {
      setFailed(true);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const select = async (device: PcAudioOutput): Promise<void> => {
    if (busy !== null || device.isDefault || !outputs?.canSwitch) {
      return;
    }
    setBusy(device.id);
    try {
      setOutputs(await api.pc.setAudioOutput(device.id));
      // The new device has its own level: give it the player's.
      const { settings, setVolume } = useSettingsStore.getState();
      await setVolume(settings.volume, settings.muted).catch(() => undefined);
      push({
        title: t('pcSettings.outputChanged', { name: splitDeviceName(device.name).title }),
        level: 'success',
        ttlSec: 3,
      });
    } catch (e) {
      pushError(e, t('pcSettings.output'));
    } finally {
      setBusy(null);
    }
  };

  const devices = outputs?.devices ?? [];
  const canSwitch = outputs?.canSwitch ?? false;

  return (
    <SettingsSection title={t('pcSettings.output')} description={t('pcSettings.outputHint')}>
      {outputs === null && loading && (
        <div className="grid gap-2 sm:grid-cols-2" aria-busy="true">
          <Skeleton height={68} />
          <Skeleton height={68} />
        </div>
      )}
      {failed && <p className="text-sm text-muted">{t('pcSettings.unavailable')}</p>}
      {outputs !== null && devices.length === 0 && <p className="text-sm text-muted">{t('pcSettings.outputNone')}</p>}
      {devices.length > 0 && (
        <div role="radiogroup" aria-label={t('pcSettings.output')} className="grid gap-2 sm:grid-cols-2">
          {devices.map((d) => (
            <OutputCard
              key={d.id}
              device={d}
              busy={busy === d.id}
              disabled={!canSwitch || (busy !== null && busy !== d.id)}
              onSelect={() => void select(d)}
            />
          ))}
        </div>
      )}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="min-w-0 text-sm text-muted">
          {outputs !== null && !canSwitch && devices.length > 0 ? t('pcSettings.outputReadOnly') : null}
        </p>
        <Button
          variant="ghost"
          icon={<RefreshIcon />}
          loading={loading && outputs !== null}
          disabled={busy !== null}
          onClick={() => void load()}
        >
          {t('pcSettings.refresh')}
        </Button>
      </div>
    </SettingsSection>
  );
}
