/**
 * The PC line under the club name in the top bar, on every screen: name · zone · graphics card · refresh rate of the
 * primary monitor (`PC-12 · Standard · RTX 4070 · 240 Гц`). Pressing it opens the PC block on Home. On a cold PC the
 * card's name arrives a few seconds after sign-in (`sys_hardware` is a WMI scan); the line is shorter until then.
 */
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { useLocale } from '@/hooks/useLocale';
import { selectPrimaryGpu, selectPrimaryMonitor, useSettingsStore } from '@/store/settings';
import { gpuShortName } from './devices/gpuVendor';
import { formatUnit } from './display/specs';

/** Id of the PC block on Home. */
export const THIS_PC_ID = 'this-pc';

/** Router state of `/home` that scrolls to the PC block and focuses its first control. */
export interface HomeFocusState {
  focus?: typeof THIS_PC_ID;
}

export const THIS_PC_STATE: HomeFocusState = { focus: THIS_PC_ID };

export function PcBadge({ className }: { className?: string }): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const pc = useSettingsStore((s) => s.pcInfo?.pc ?? null);
  const gpu = useSettingsStore(selectPrimaryGpu);
  const monitor = useSettingsStore(selectPrimaryMonitor);

  const parts = [
    pc?.name,
    pc?.zone,
    gpu && gpuShortName(gpu.model),
    monitor && monitor.hz > 0 && formatUnit('hz', monitor.hz, locale, t),
  ].filter(Boolean);

  return (
    <button
      type="button"
      data-nav="true"
      title={t('desktop.pcBadge')}
      onClick={() => navigate('/home', { state: THIS_PC_STATE })}
      className={clsx(
        'focus-ring hud-label block max-w-full truncate rounded-sm text-left transition-colors duration-[var(--dur-fast)] hover:text-text',
        className,
      )}
    >
      {parts.length > 0 ? parts.join(' · ') : t('common.loading')}
    </button>
  );
}
