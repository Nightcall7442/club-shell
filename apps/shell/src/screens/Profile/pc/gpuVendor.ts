/**
 * Which graphics-card panel the Settings screen offers: the vendor of the PC's GPU (`sys_hardware`) matched against the
 * panels installed for the kiosk user (`pc_gpu_panels`). Pure, so the e2e package unit-tests it without a browser.
 */
import type { GpuPanelInfo, GpuVendor } from '@/lib/tauri';

/** When a PC has several adapters the first vendor here wins: discrete cards before an Intel iGPU next to them. */
export const GPU_VENDOR_PRIORITY: readonly GpuVendor[] = ['nvidia', 'amd', 'intel'];

const VENDOR_PATTERNS: Record<GpuVendor, RegExp> = {
  nvidia: /\b(nvidia|geforce|quadro|tesla)\b|\b[gr]tx\s?\d/i,
  amd: /\b(amd|radeon|ati)\b/i,
  intel: /\bintel\b|\b(arc|iris)\b|\b(uhd|hd) graphics\b/i,
};

/** Vendor of a GPU model string (`NVIDIA GeForce RTX 4070`); `null` for virtual / unknown adapters. */
export function gpuVendorOf(model: string): GpuVendor | null {
  return GPU_VENDOR_PRIORITY.find((vendor) => VENDOR_PATTERNS[vendor].test(model)) ?? null;
}

/** The panel to offer, with the GPU model it belongs to. */
export interface GpuPanelChoice {
  vendor: GpuVendor;
  panel: GpuPanelInfo;
  /** `null` when the hardware list was unavailable and the panel was picked from what is installed. */
  model: string | null;
}

/**
 * The best vendor that has both a GPU in `models` and an installed panel. Without a hardware list (`models === null`:
 * the Agent is offline) the first installed panel is offered; with one, a panel for a card the PC does not have never is.
 */
export function pickGpuPanel(models: readonly string[] | null, panels: readonly GpuPanelInfo[]): GpuPanelChoice | null {
  for (const vendor of GPU_VENDOR_PRIORITY) {
    const panel = panels.find((p) => p.vendor === vendor);
    if (!panel) {
      continue;
    }
    if (models === null) {
      return { vendor, panel, model: null };
    }
    const model = models.find((m) => gpuVendorOf(m) === vendor);
    if (model !== undefined) {
      return { vendor, panel, model };
    }
  }
  return null;
}
