/**
 * "Видеокарта": one button that opens the graphics-card vendor's own panel (NVIDIA Control Panel, AMD Software, Intel
 * Graphics Command Center) over the shell (`pc_gpu_panel_open`). The kiosk guard steps aside while the panel is open
 * and the shell comes back on top when it closes. Shown only when this PC has the panel for its card.
 */
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { api } from '@/lib/tauri';
import { useNotificationsStore } from '@/store';
import { SettingsSection } from '../Settings';
import { pickGpuPanel, type GpuPanelChoice } from './gpuVendor';
import { ExternalIcon, GpuIcon } from './icons';

/** The panel to offer (`null`: none, or not known yet — see `ready`). */
export function useGpuPanelChoice(): { choice: GpuPanelChoice | null; ready: boolean } {
  const [state, setState] = useState<{ choice: GpuPanelChoice | null; ready: boolean }>({
    choice: null,
    ready: false,
  });
  useEffect(() => {
    let active = true;
    void Promise.all([
      api.pc.gpuPanels().catch(() => []),
      api.system
        .hardware()
        .then((h) => h.gpu.map((g) => g.model))
        .catch(() => null),
    ]).then(([panels, models]) => {
      if (active) {
        setState({ choice: pickGpuPanel(models, panels), ready: true });
      }
    });
    return () => {
      active = false;
    };
  }, []);
  return state;
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
