/**
 * Labelled range with −/+ buttons for the PC settings: the slider moves freely (mouse, arrows, gamepad left/right by
 * 5 steps) and the buttons give the single-step precision a pad lacks. The value is committed after a short pause so
 * dragging does not flood the native setter.
 */
import { useEffect, useId, useRef, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';

const COMMIT_MS = 150;

export interface StepSliderProps {
  label: string;
  /** Slider position (`min`…`max`, integer steps). */
  value: number;
  min: number;
  max: number;
  /** Shown at the right of the label and read out (`aria-valuetext`). */
  format: (value: number) => string;
  /** Captions under both ends ("Slower" / "Faster"). */
  minCaption: string;
  maxCaption: string;
  onCommit: (value: number) => void;
  disabled?: boolean;
}

function MinusIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.5"
      strokeLinecap="round"
      aria-hidden="true"
    >
      <path d="M6 12h12" />
    </svg>
  );
}

function PlusIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.5"
      strokeLinecap="round"
      aria-hidden="true"
    >
      <path d="M6 12h12M12 6v12" />
    </svg>
  );
}

export function StepSlider({
  label,
  value,
  min,
  max,
  format,
  minCaption,
  maxCaption,
  onCommit,
  disabled = false,
}: StepSliderProps): JSX.Element {
  const { t } = useTranslation();
  const id = useId();
  const [local, setLocal] = useState(value);
  // The −/+ buttons step from the latest position even when presses come faster than renders.
  const latest = useRef(value);
  const timer = useRef<number | null>(null);

  useEffect(() => {
    latest.current = value;
    setLocal(value);
  }, [value]);
  useEffect(
    () => () => {
      if (timer.current !== null) {
        window.clearTimeout(timer.current);
      }
    },
    [],
  );

  const change = (next: number): void => {
    const clamped = Math.min(max, Math.max(min, Math.round(next)));
    latest.current = clamped;
    setLocal(clamped);
    if (timer.current !== null) {
      window.clearTimeout(timer.current);
    }
    timer.current = window.setTimeout(() => {
      timer.current = null;
      if (clamped !== value) {
        onCommit(clamped);
      }
    }, COMMIT_MS);
  };

  return (
    <div className={clsx('flex flex-col gap-2', disabled && 'opacity-50')}>
      <div className="flex items-baseline justify-between gap-4">
        <label htmlFor={id} className="text-base font-medium">
          {label}
        </label>
        <span className="tnum shrink-0 text-base text-muted" aria-hidden="true">
          {format(local)}
        </span>
      </div>
      <div className="flex items-center gap-3">
        <Button
          variant="secondary"
          iconOnly
          aria-label={t('pcSettings.decrease', { label })}
          disabled={disabled || local <= min}
          onClick={() => change(latest.current - 1)}
          icon={<MinusIcon />}
        />
        <input
          id={id}
          type="range"
          min={min}
          max={max}
          step={1}
          value={local}
          disabled={disabled}
          data-nav="true"
          aria-valuetext={format(local)}
          onChange={(e) => change(Number(e.target.value))}
          className="focus-ring h-3 min-w-0 flex-1 cursor-pointer rounded-full disabled:cursor-not-allowed"
        />
        <Button
          variant="secondary"
          iconOnly
          aria-label={t('pcSettings.increase', { label })}
          disabled={disabled || local >= max}
          onClick={() => change(latest.current + 1)}
          icon={<PlusIcon />}
        />
      </div>
      <div className="flex justify-between px-14 text-xs uppercase tracking-wider text-muted" aria-hidden="true">
        <span>{minCaption}</span>
        <span>{maxCaption}</span>
      </div>
    </div>
  );
}
