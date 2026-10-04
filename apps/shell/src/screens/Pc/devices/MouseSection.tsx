/**
 * "Мышь" in the PC block on Home: pointer speed, "Enhance pointer precision" and double-click speed of this PC
 * (`pc_mouse_*`). Changes apply at once in the player's Windows session and go back to the club's values when the
 * session ends. A test pad judges clicks with the same double-click time Windows now uses.
 */
import { useEffect, useId, useRef, useState, type MouseEvent } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { SettingsSection, Toggle } from '@/components/settings/SettingsSection';
import { Skeleton } from '@/components/ui/Skeleton';
import { api, type PcMouseSettings } from '@/lib/tauri';
import { useNotificationsStore } from '@/store';
import { FolderIcon } from './icons';
import { DOUBLE_CLICK_POSITIONS, doubleClickMsAt, doubleClickPosition, judgeClick } from './pcFormat';
import { StepSlider } from './StepSlider';

const SPEED_MIN = 1;
const SPEED_MAX = 20;
/** How long a test verdict stays before the pad invites a new try. */
const VERDICT_MS = 2500;

type Verdict = 'idle' | 'ok' | 'slow';

/** Double-click test: a folder that opens when two clicks land within `thresholdMs`. */
function DoubleClickPad({ thresholdMs }: { thresholdMs: number }): JSX.Element {
  const { t } = useTranslation();
  const statusId = useId();
  const lastClick = useRef<number | null>(null);
  const timer = useRef<number | null>(null);
  const [verdict, setVerdict] = useState<Verdict>('idle');

  useEffect(
    () => () => {
      if (timer.current !== null) {
        window.clearTimeout(timer.current);
      }
    },
    [],
  );

  const onClick = (e: MouseEvent<HTMLButtonElement>): void => {
    const result = judgeClick(lastClick.current, e.timeStamp, thresholdMs);
    lastClick.current = result === 'ok' ? null : e.timeStamp;
    if (result === 'armed') {
      return;
    }
    setVerdict(result);
    if (timer.current !== null) {
      window.clearTimeout(timer.current);
    }
    timer.current = window.setTimeout(() => {
      timer.current = null;
      setVerdict('idle');
    }, VERDICT_MS);
  };

  const text =
    verdict === 'ok'
      ? t('pcSettings.doubleClickTestOk')
      : verdict === 'slow'
        ? t('pcSettings.doubleClickTestSlow')
        : t('pcSettings.doubleClickTestIdle');

  return (
    <div className="flex items-center gap-4">
      <button
        type="button"
        data-nav="true"
        aria-label={t('pcSettings.doubleClickTest')}
        aria-describedby={statusId}
        onClick={onClick}
        className={clsx(
          'focus-ring grid h-20 w-20 shrink-0 select-none place-items-center rounded-xl border transition-[background-color,border-color,color,transform] duration-[var(--dur-base)] ease-[var(--ease-out)] [&>svg]:h-10 [&>svg]:w-10',
          verdict === 'ok' && 'scale-105 border-success/60 bg-success/10 text-success',
          verdict === 'slow' && 'border-danger/50 bg-danger/10 text-danger',
          verdict === 'idle' && 'border-text/15 bg-text/[0.04] text-muted hover:bg-text/[0.08]',
        )}
      >
        <FolderIcon open={verdict === 'ok'} />
      </button>
      <div className="min-w-0">
        <span className="block text-base font-medium">{t('pcSettings.doubleClickTest')}</span>
        <span id={statusId} aria-live="polite" className="block text-sm text-muted">
          {text}
        </span>
      </div>
    </div>
  );
}

export function MouseSection({ className }: { className?: string }): JSX.Element {
  const { t } = useTranslation();
  const pushError = useNotificationsStore((s) => s.pushError);
  const [mouse, setMouse] = useState<PcMouseSettings | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let active = true;
    api.pc.mouse().then(
      (m) => active && setMouse(m),
      () => active && setFailed(true),
    );
    return () => {
      active = false;
    };
  }, []);

  const apply = async (patch: Partial<PcMouseSettings>): Promise<void> => {
    setMouse((m) => (m ? { ...m, ...patch } : m));
    try {
      setMouse(await api.pc.setMouse(patch));
    } catch (e) {
      pushError(e, t('pcSettings.mouse'));
      api.pc.mouse().then(setMouse, () => undefined);
    }
  };

  return (
    <SettingsSection title={t('pcSettings.mouse')} description={t('pcSettings.restoredHint')} className={className}>
      {mouse ? (
        <div className="flex flex-col gap-6">
          <StepSlider
            label={t('pcSettings.pointerSpeed')}
            value={mouse.speed}
            min={SPEED_MIN}
            max={SPEED_MAX}
            format={(v) => t('pcSettings.speedValue', { value: v, max: SPEED_MAX })}
            minCaption={t('pcSettings.slower')}
            maxCaption={t('pcSettings.faster')}
            onCommit={(speed) => void apply({ speed })}
          />
          <Toggle
            label={t('pcSettings.precision')}
            hint={t('pcSettings.precisionHint')}
            checked={mouse.enhancePrecision}
            onChange={(enhancePrecision) => void apply({ enhancePrecision })}
          />
          <StepSlider
            label={t('pcSettings.doubleClick')}
            value={doubleClickPosition(mouse.doubleClickMs)}
            min={0}
            max={DOUBLE_CLICK_POSITIONS}
            format={(p) => t('pcSettings.doubleClickValue', { ms: doubleClickMsAt(p) })}
            minCaption={t('pcSettings.slower')}
            maxCaption={t('pcSettings.faster')}
            onCommit={(p) => void apply({ doubleClickMs: doubleClickMsAt(p) })}
          />
          <DoubleClickPad thresholdMs={mouse.doubleClickMs} />
        </div>
      ) : failed ? (
        <p className="text-base text-muted">{t('pcSettings.unavailable')}</p>
      ) : (
        <div className="flex flex-col gap-6" aria-busy="true">
          <Skeleton height={88} />
          <Skeleton height={56} />
          <Skeleton height={88} />
          <Skeleton height={80} />
        </div>
      )}
    </SettingsSection>
  );
}
