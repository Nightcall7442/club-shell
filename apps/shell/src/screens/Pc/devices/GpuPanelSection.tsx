/**
 * "Видеокарта": one button that opens the graphics-card vendor's own panel (NVIDIA Control Panel, AMD Software, Intel
 * Graphics Command Center) over the shell (`pc_gpu_panel_open`). The kiosk guard steps aside while the panel is open
 * and the shell comes back on top when it closes. Shown only when the club allows it (`features.gpuPanel`: the panel
 * pauses the kiosk guard and keeps its settings) and this PC has the panel for its card.
 */
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { SettingsSection } from '@/components/settings/SettingsSection';
import { Button } from '@/components/ui/Button';
import { api, type GpuPanelInfo } from '@/lib/tauri';
import { useNotificationsStore, useSettingsStore } from '@/store';
import { pickGpuPanel, type GpuPanelChoice } from './gpuVendor';
import { ExternalIcon, GpuIcon } from './icons';

/**
 * The panel to offer (`null`: none, or not known yet — see `ready`). The GPU list is the store's `sys_hardware`;
 * `pc_gpu_panels` is only asked while `enabled` (the club's `features.gpuPanel`).
 */
export function useGpuPanelChoice(enabled: boolean): { choice: GpuPanelChoice | null; ready: boolean } {
  const hardware = useSettingsStore((s) => s.hardware);
  const hardwareFailed = useSettingsStore((s) => s.hardwareStatus === 'error');
  const [panels, setPanels] = useState<GpuPanelInfo[] | null>(null);

  useEffect(() => {
    if (!enabled) {
      return undefined;
    }
    let active = true;
    api.pc.gpuPanels().then(
      (list) => active && setPanels(list),
      () => active && setPanels([]),
    );
    return () => {
      active = false;
    };
  }, [enabled]);

  return useMemo(() => {
    if (!enabled) {
      return { choice: null, ready: true };
    }
    // Wait for the hardware list; when it failed (Agent offline), `pickGpuPanel` offers the first installed panel.
    if (panels === null || (hardware === null && !hardwareFailed)) {
      return { choice: null, ready: false };
    }
    const models = hardware ? hardware.gpu.map((g) => g.model) : null;
    return { choice: pickGpuPanel(models, panels), ready: true };
  }, [enabled, panels, hardware, hardwareFailed]);
}

export interface GpuPanelSectionProps {
  choice: GpuPanelChoice;
  className?: string;
}

export function GpuPanelSection({ choice, className }: GpuPanelSectionProps): JSX.Element {
  const { t } = useTranslation();
  const pushError = useNotificationsStore((s) => s.pushError);
  const [opening, setOpening] = useState(false);

  const open = async (): Promise<void> => {
    setOpening(true);
    try {
      await api.pc.openGpuPanel(choice.vendor);
    } catch (e) {
      pushError(e, t('pcSettings.gpu'));
    } finally {
      setOpening(false);
    }
  };

  return (
    <SettingsSection title={t('pcSettings.gpu')} description={t('pcSettings.gpuHint')} className={className}>
      <div className="flex items-center gap-4">
        <span
          aria-hidden="true"
          className="grid h-14 w-14 shrink-0 place-items-center rounded-lg bg-accent/10 text-accent [&>svg]:h-8 [&>svg]:w-8"
        >
          <GpuIcon />
        </span>
        <div className="min-w-0" translate="no">
          <span className="block truncate text-lg font-semibold">
            {choice.model ?? t(`pcSettings.vendor.${choice.vendor}`)}
          </span>
          <span className="block truncate text-sm text-muted">{choice.panel.name}</span>
        </div>
      </div>
      <Button size="lg" block className="mt-auto" loading={opening} icon={<ExternalIcon />} onClick={() => void open()}>
        {t('pcSettings.gpuOpen', { name: choice.panel.name })}
      </Button>
    </SettingsSection>
  );
}
