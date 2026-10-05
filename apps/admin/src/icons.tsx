/**
 * The console's line icons (variant F): inline SVG, 24-unit grid, round caps, stroke 1.6 (1.9 on a primary button via
 * `strong`). Always `aria-hidden`: the button or label next to an icon carries the name. `size` is in px (default 20).
 */
import type { ReactNode } from 'react';
import clsx from 'clsx';

export interface IconProps {
  size?: number;
  /** Heavier stroke (1.9) for icons on the accent fill. */
  strong?: boolean;
  strokeWidth?: number;
  className?: string;
}

function icon(paths: ReactNode, displayName: string): (props: IconProps) => JSX.Element {
  const Icon = ({ size = 20, strong, strokeWidth, className }: IconProps): JSX.Element => (
    <svg
      viewBox="0 0 24 24"
      width={size}
      height={size}
      fill="none"
      stroke="currentColor"
      strokeWidth={strokeWidth ?? (strong ? 1.9 : 1.6)}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      className={clsx('shrink-0', className)}
    >
      {paths}
    </svg>
  );
  Icon.displayName = displayName;
  return Icon;
}

/** An icon from one path `d` (the rail's section icons). */
export function pathIcon(d: string, displayName = 'PathIcon'): (props: IconProps) => JSX.Element {
  return icon(<path d={d} />, displayName);
}

// ---- Rail sections ----------------------------------------------------------------------------------------------------

export const MapIcon = icon(
  <>
    <rect x="4" y="4" width="7" height="7" rx="1.5" />
    <rect x="13" y="4" width="7" height="7" rx="1.5" />
    <rect x="4" y="13" width="7" height="7" rx="1.5" />
    <rect x="13" y="13" width="7" height="7" rx="1.5" />
  </>,
  'MapIcon',
);
export const CupIcon = icon(
  <>
    <path d="M5 9h11v4a5 5 0 0 1-5 5h-1a5 5 0 0 1-5-5z" />
    <path d="M16 10h1.5a2.5 2.5 0 0 1 0 5H16" />
    <path d="M8.5 3.5v2.5M12 3.5v2.5" />
  </>,
  'CupIcon',
);
export const ClockIcon = icon(
  <>
    <circle cx="12" cy="12" r="8" />
    <path d="M12 8v4l2.5 2" />
  </>,
  'ClockIcon',
);
export const UsersIcon = icon(
  <>
    <circle cx="9" cy="9" r="3.2" />
    <path d="M3.5 19c.8-3 3-4.5 5.5-4.5s4.7 1.5 5.5 4.5" />
    <path d="M15.5 6.2a3 3 0 0 1 0 5.6M17.5 14.8c1.5.6 2.6 2 3 4.2" />
  </>,
  'UsersIcon',
);
export const BoxIcon = icon(
  <>
    <path d="M4 7.5 12 4l8 3.5v9L12 20l-8-3.5z" />
    <path d="M4 7.5 12 11l8-3.5M12 11v9" />
  </>,
  'BoxIcon',
);
export const PulseIcon = pathIcon('M3 12h4l2.5-6 5 12 2.5-6h4', 'PulseIcon');
export const TagIcon = pathIcon(
  'M20.6 13.4 13.4 20.6a2 2 0 0 1-2.8 0L3 13V3h10l7.6 7.6a2 2 0 0 1 0 2.8zM7.5 7.5h.01',
  'TagIcon',
);
export const MonitorIcon = pathIcon('M3 5h18v11H3zM8 20h8M12 16v4', 'MonitorIcon');
export const GamepadIcon = pathIcon(
  'M6 8h12a4 4 0 0 1 4 4v3a3 3 0 0 1-5.4 1.8L15 15H9l-1.6 1.8A3 3 0 0 1 2 15v-3a4 4 0 0 1 4-4zM7 11v3M5.5 12.5h3',
  'GamepadIcon',
);
export const ScreenIcon = pathIcon('M12 3a9 9 0 1 0 9 9M12 3v9l6-6', 'ScreenIcon');
export const BoltIcon = pathIcon('M13 2 3 14h9l-1 8 10-12h-9l1-8z', 'BoltIcon');
export const NetworkIcon = pathIcon(
  'M12 3v6M5 21v-6h14v6M12 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM5 15v-3h14v3',
  'NetworkIcon',
);
export const ChartIcon = pathIcon('M3 3v18h18M7 15l4-4 3 3 5-6', 'ChartIcon');
export const ShieldIcon = pathIcon('M12 3 4 6v6c0 5 3.4 8.5 8 9 4.6-.5 8-4 8-9V6l-8-3zM9 12l2 2 4-4', 'ShieldIcon');
export const PersonIcon = pathIcon('M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM4 21a8 8 0 0 1 16 0', 'PersonIcon');

// ---- Actions and states -------------------------------------------------------------------------------------------------

export const SearchIcon = icon(
  <>
    <circle cx="11" cy="11" r="6.5" />
    <path d="m16 16 4 4" />
  </>,
  'SearchIcon',
);
export const BellIcon = icon(
  <>
    <path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15z" />
    <path d="M10 20.5a2 2 0 0 0 4 0" />
  </>,
  'BellIcon',
);
export const ChevronRightIcon = pathIcon('m9 6 6 6-6 6', 'ChevronRightIcon');
export const ChevronDownIcon = pathIcon('m6 9 6 6 6-6', 'ChevronDownIcon');
export const TimerIcon = icon(
  <>
    <circle cx="12" cy="13" r="7" />
    <path d="M12 10v3.2l2.2 1.4M9.5 3h5" />
  </>,
  'TimerIcon',
);
export const PlusMinusIcon = pathIcon('M12 4v8M8 8h8M8 18h8', 'PlusMinusIcon');
export const SwapIcon = pathIcon('M4 8h13l-3-3M20 16H7l3 3', 'SwapIcon');
export const StopSquareIcon = icon(<rect x="6" y="6" width="12" height="12" rx="2" />, 'StopSquareIcon');
export const LockIcon = icon(
  <>
    <rect x="5" y="11" width="14" height="9" rx="2" />
    <path d="M8 11V8a4 4 0 0 1 8 0v3" />
  </>,
  'LockIcon',
);
export const PowerIcon = pathIcon('M12 3v8M6.3 6.3a8 8 0 1 0 11.4 0', 'PowerIcon');
export const CalendarIcon = icon(
  <>
    <rect x="4" y="5" width="16" height="15" rx="2" />
    <path d="M4 10h16M8 3v4M16 3v4" />
  </>,
  'CalendarIcon',
);
export const WrenchIcon = pathIcon(
  'M14.7 6.3a4 4 0 0 0-5.4 5.4L3.5 17.5a1.8 1.8 0 0 0 2.5 2.5l5.8-5.8a4 4 0 0 0 5.4-5.4l-2.6 2.6-2.3-.6-.6-2.3z',
  'WrenchIcon',
);
export const SignInIcon = pathIcon('M10 4h8a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-8M4 12h10M11 8l4 4-4 4', 'SignInIcon');
export const AlertTriangleIcon = icon(
  <>
    <path d="M12 4 21 19.5H3z" />
    <path d="M12 10v4.5M12 17.2v.3" />
  </>,
  'AlertTriangleIcon',
);
export const InfinityIcon = pathIcon(
  'M7 9c-3 0-4 2-4 3s1 3 4 3c3.5 0 6.5-6 10-6 3 0 4 2 4 3s-1 3-4 3c-3.5 0-6.5-6-10-6z',
  'InfinityIcon',
);
export const CheckIcon = pathIcon('m5 12.5 4.5 4.5L19 7.5', 'CheckIcon');
export const CheckSquareIcon = icon(
  <>
    <rect x="4.5" y="4.5" width="15" height="15" rx="3" />
    <path d="m8.5 12 2.5 2.5 4.5-5" />
  </>,
  'CheckSquareIcon',
);
export const CloseIcon = pathIcon('M6 6l12 12M18 6 6 18', 'CloseIcon');
export const LogoutIcon = pathIcon('M14 4h4a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-4M10 8l-4 4 4 4M6 12h9', 'LogoutIcon');
export const DownloadIcon = pathIcon('M12 4v11m0 0-4-4m4 4 4-4M5 19h14', 'DownloadIcon');
export const ArrowRightIcon = pathIcon('M5 12h14M13 6l6 6-6 6', 'ArrowRightIcon');
export const CashIcon = icon(
  <>
    <rect x="3" y="6" width="18" height="12" rx="2" />
    <circle cx="12" cy="12" r="2.5" />
    <path d="M6.5 9.5v.01M17.5 14.5v.01" />
  </>,
  'CashIcon',
);
export const PrintIcon = icon(
  <>
    <path d="M7 9V4h10v5" />
    <rect x="4" y="9" width="16" height="8" rx="2" />
    <path d="M7 14h10v6H7z" />
  </>,
  'PrintIcon',
);
export const SpeakerOffIcon = pathIcon('M11 5 6 9H2v6h4l5 4V5zM23 9l-6 6M17 9l6 6', 'SpeakerOffIcon');
export const MoreIcon = icon(
  <>
    <path d="M6 12h.01M12 12h.01M18 12h.01" strokeWidth={2.4} />
  </>,
  'MoreIcon',
);

/** The play dot of a live readout («В игре»): a filled accent dot, not a stroke icon. */
export function PlayDot({ className }: { className?: string }): JSX.Element {
  return (
    <span
      aria-hidden="true"
      className={clsx(
        'inline-block h-1.5 w-1.5 shrink-0 rounded-full bg-accent shadow-[0_0_8px_rgb(var(--c-accent)/0.9)]',
        className,
      )}
    />
  );
}

/** The ClubShell mark: an outlined diamond around an accent one. */
export function LogoMark({ size = 30, className }: { size?: number; className?: string }): JSX.Element {
  return (
    <svg width={size} height={size} viewBox="0 0 30 30" aria-hidden="true" focusable="false" className={className}>
      <path d="M15 2 28 15 15 28 2 15z" strokeWidth="1.2" style={{ fill: 'none', stroke: 'rgb(var(--c-text))' }} />
      <path
        d="M15 9.5 20.5 15 15 20.5 9.5 15z"
        strokeWidth="1"
        style={{ fill: 'rgb(var(--c-accent) / 0.16)', stroke: 'rgb(var(--c-accent))' }}
      />
    </svg>
  );
}
