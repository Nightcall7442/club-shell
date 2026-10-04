/**
 * "Звук" in the PC block on Home: the system volume (debounced `sys_set_volume`, the level the top bar's sound button
 * shows) with mute, and the interface's own click sounds. Both are this PC's and stay for the next player.
 */
import { useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { SettingsSection, Toggle } from '@/components/settings/SettingsSection';
import { Button } from '@/components/ui/Button';
import { useNotificationsStore, useSettingsStore } from '@/store';

function SpeakerIcon({ muted }: { muted: boolean }): JSX.Element {
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
      <path d="M4 9v6h4l5 4V5L8 9z" />
      {muted ? <path d="M17 9l4 6M21 9l-4 6" /> : <path d="M16 9a4 4 0 0 1 0 6M18.5 6.5a8 8 0 0 1 0 11" />}
    </svg>
  );
}

const VOLUME_COMMIT_MS = 150;

/** Volume slider (debounced `sys_set_volume`) + mute toggle. */
export function VolumeControl(): JSX.Element {
  const { t } = useTranslation();
  const volume = useSettingsStore((s) => s.settings.volume);
  const muted = useSettingsStore((s) => s.settings.muted);
  const setVolume = useSettingsStore((s) => s.setVolume);
  const toggleMute = useSettingsStore((s) => s.toggleMute);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [level, setLevel] = useState(volume);
  const timer = useRef<number | null>(null);

  useEffect(() => setLevel(volume), [volume]);
  useEffect(
    () => () => {
      if (timer.current !== null) {
        window.clearTimeout(timer.current);
      }
    },
    [],
  );

  const onInput = (next: number): void => {
    setLevel(next);
    if (timer.current !== null) {
      window.clearTimeout(timer.current);
    }
    timer.current = window.setTimeout(() => {
      timer.current = null;
      setVolume(next, false).catch((e: unknown) => pushError(e, t('settings.volume')));
    }, VOLUME_COMMIT_MS);
  };

  const onMute = (): void => {
    toggleMute().catch((e: unknown) => pushError(e, t('settings.volume')));
  };

  const label = muted ? t('settings.muted') : t('settings.volumeLevel', { level });

  return (
    <div className="flex items-center gap-4">
      <Button
        variant={muted ? 'danger' : 'secondary'}
        iconOnly
        size="lg"
        aria-label={muted ? t('kiosk.unmuted') : t('kiosk.muted')}
        aria-pressed={muted}
        onClick={onMute}
        icon={<SpeakerIcon muted={muted} />}
      />
      <input
        type="range"
        min={0}
        max={100}
        step={1}
        value={level}
        data-nav="true"
        aria-label={t('settings.volume')}
        aria-valuetext={label}
        onChange={(e) => onInput(Number(e.target.value))}
        className={clsx('focus-ring h-3 min-w-0 flex-1 cursor-pointer rounded-full', muted && 'opacity-50')}
      />
      <span className="tnum w-24 shrink-0 text-right text-base text-muted" aria-live="polite">
        {label}
      </span>
    </div>
  );
}

export function SoundCard(): JSX.Element {
  const { t } = useTranslation();
  const uiSounds = useSettingsStore((s) => s.settings.uiSounds);
  const set = useSettingsStore((s) => s.set);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [busy, setBusy] = useState(false);

  const setUiSounds = async (next: boolean): Promise<void> => {
    setBusy(true);
    try {
      await set({ uiSounds: next });
    } catch (e) {
      pushError(e, t('settings.uiSounds'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <SettingsSection title={t('settings.sound')}>
      <VolumeControl />
      <Toggle label={t('settings.uiSounds')} checked={uiSounds} disabled={busy} onChange={(v) => void setUiSounds(v)} />
    </SettingsSection>
  );
}
