/**
 * The PC part of Profile → Settings, mounted by `Settings.tsx` under the language / sound row: the mouse on the left,
 * the audio output device and the graphics-card panel (when this PC has one) stacked on the right, so both columns end
 * level. Everything here is the player's for the session: the Shell puts the club's values back when it ends.
 */
import { AudioOutputSection } from './AudioOutputSection';
import { GpuPanelSection, useGpuPanelChoice } from './GpuPanelSection';
import { MouseSection } from './MouseSection';

export function PcInputSettings(): JSX.Element {
  const gpu = useGpuPanelChoice();
  return (
    <div className="grid gap-[var(--gap)] xl:grid-cols-2">
      <MouseSection />
      <div className="flex flex-col gap-[var(--gap)]">
        <AudioOutputSection />
        {gpu.choice && <GpuPanelSection choice={gpu.choice} className="flex-1" />}
      </div>
    </div>
  );
}
