import clsx from 'clsx';

/** Runs of digits and the characters that sit between them in formatted numbers, times and money. */
const NUMERIC_RUN = /([\d\s  :.,+\-−%]*\d[\d\s  :.,%]*)/;

export interface DotAmountProps {
  /** Already formatted value ("45 000 сум", "UZS 45,000", "01:26:58"). */
  value: string;
  className?: string;
}

/**
 * A formatted number in the dot-matrix face, with any words around it (the currency, "ч", "мин") set small in the
 * UI face: Doto has no Cyrillic, and a unit as loud as the number competes with it.
 */
export function DotAmount({ value, className }: DotAmountProps): JSX.Element {
  const parts = value.split(NUMERIC_RUN).filter((p) => p.length > 0);
  return (
    <span className={clsx('inline-flex items-baseline whitespace-nowrap', className)}>
      {parts.map((part, i) =>
        NUMERIC_RUN.test(part) ? (
          <span key={i} className="num-dot">
            {/* Doto's space is a full dot cell; a narrow gap keeps "45 000" reading as one number. */}
            {part
              .trim()
              .split(/[\s\u00a0\u202f]+/)
              .map((group, j) => (
                <span key={j} className={j > 0 ? 'ml-[0.22em]' : undefined}>
                  {group}
                </span>
              ))}
          </span>
        ) : (
          <span key={i} className="mx-[0.3em] font-sans text-[0.5em] font-medium text-muted">
            {part.trim()}
          </span>
        ),
      )}
    </span>
  );
}
