/**
 * The PC's own label on the player screen, the way the owner set it under «Интерфейс и логотипы» (D-95,
 * `shell.club.pcLabel`): the name and zone (the default), the name alone or the PC number alone; on the lock screen,
 * the idle screen and the top bar, each one switchable; and on the lock and idle screens optionally as a large number
 * readable across the hall. Without the setting the name and zone show everywhere, as before.
 */
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { useSettingsStore } from '@/store/settings';

export type PcLabelPlace = 'lock' | 'idle' | 'topBar';

export interface PcLabelView {
  /** The label text for `place`, or `null` when it is switched off there (or the PC is not known yet). */
  text: string | null;
  /** The PC number, `05`-style, or `null` when unknown. */
  number: string | null;
  /** The large number on the lock and idle screens. */
  large: boolean;
  /** What the label says: `nameZone`, `name` or `number`. */
  format: string;
}

export function usePcLabel(place: PcLabelPlace): PcLabelView {
  const { t } = useTranslation();
  const pc = useSettingsStore((s) => s.pcInfo?.pc ?? null);
  const label = useSettingsStore((s) => s.settings.club?.pcLabel ?? null);
  const shown = label ? label[place] !== false : true;
  const number = pc && pc.number > 0 ? String(pc.number).padStart(2, '0') : null;
  const format = label?.format ?? 'nameZone';
  let text: string | null = null;
  if (pc && shown) {
    if (format === 'number') {
      text = number ? t('pcLabel.number', { number }) : pc.name;
    } else if (format === 'name' || !pc.zone) {
      text = pc.name;
    } else {
      text = t('idle.pcName', { name: pc.name, zone: pc.zone });
    }
  }
  return { text, number, format, large: shown && place !== 'topBar' && label?.large === true && number !== null };
}

/** The label on the lock or idle screen: a small line under the club name, or the large number with the line under it. */
export function PcLabel({ place, className }: { place: 'lock' | 'idle'; className?: string }): JSX.Element | null {
  const { t } = useTranslation();
  const { text, number, large, format } = usePcLabel(place);
  if (!text) return null;
  if (!large) return <p className={clsx('hud-label', className)}>{text}</p>;
  return (
    <div className={clsx('flex items-end gap-3', className)} aria-label={text}>
      <span className="font-display text-7xl font-light leading-none tracking-tight text-text tabular-nums">
        {number}
      </span>
      <span className="hud-label pb-1.5">{format === 'number' ? t('pcLabel.pc') : text}</span>
    </div>
  );
}
