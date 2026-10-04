/**
 * "Компьютер и настройки" on Home, right under the game hero: this PC and everything a player may set on it, in three
 * columns — the PC and its mouse; sound, the output device and the graphics-card panel; the monitor, the idle lock
 * and the theme. Language stays in the top bar's sound-and-language menu.
 *
 * Each card here is mounted exactly once in the whole app. The monitor, mouse and output cards hold live state of the
 * PC (a refresh rate waiting for "keep these settings?", the pointer speed being dragged): a second copy anywhere
 * would ask twice or show stale values. No tab list in here, so LB / RB keep switching the sections.
 */
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { selectFeature, useSettingsStore } from '@/store/settings';
import { AudioOutputSection } from './devices/AudioOutputSection';
import { GpuPanelSection, useGpuPanelChoice } from './devices/GpuPanelSection';
import { MouseSection } from './devices/MouseSection';
import { MonitorSection } from './display/MonitorSection';
import { THIS_PC_ID } from './PcBadge';
import { PcSummaryCard } from './PcSummaryCard';
import { IdleLockCard, ThemeCard } from './ShellPrefs';
import { SoundCard } from './SoundCard';

export function ThisPcSection(): JSX.Element {
  const { t } = useTranslation();
  const headingId = useId();
  const gpuPanel = useSettingsStore(selectFeature('gpuPanel'));
  const gpu = useGpuPanelChoice(gpuPanel);

  return (
    <section
      id={THIS_PC_ID}
      aria-labelledby={headingId}
      // The top bar floats over the screen: a scroll to the block stops below it.
      className="flex min-w-0 scroll-mt-[calc(var(--topbar-h)+var(--gap))] flex-col"
    >
      <div className="mb-3 flex h-11 items-center">
        <h2 id={headingId} className="font-display text-base font-normal tracking-tight text-text">
          {t('desktop.pcAndSettings')}
        </h2>
      </div>
      <div className="grid items-start gap-[var(--gap)] md:grid-cols-2 xl:grid-cols-3">
        <div className="flex min-w-0 flex-col gap-[var(--gap)]">
          <PcSummaryCard />
          <MouseSection />
        </div>
        <div className="flex min-w-0 flex-col gap-[var(--gap)]">
          <SoundCard />
          <AudioOutputSection />
          {gpu.choice && <GpuPanelSection choice={gpu.choice} />}
        </div>
        <div className="flex min-w-0 flex-col gap-[var(--gap)]">
          <MonitorSection />
          <IdleLockCard />
          <ThemeCard />
        </div>
      </div>
    </section>
  );
}
