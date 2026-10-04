/** Line icons of the PC settings panels (24 × 24, `currentColor`, sized by the parent). */
import type { PcAudioKind } from '@/lib/tauri';

function Svg({ children }: { children: JSX.Element | JSX.Element[] }): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {children}
    </svg>
  );
}

/** Icon of an audio output by kind. */
export function OutputIcon({ kind }: { kind: PcAudioKind }): JSX.Element {
  switch (kind) {
    case 'speakers':
      return (
        <Svg>
          <rect x="6" y="2.5" width="12" height="19" rx="2.5" />
          <circle cx="12" cy="14.5" r="3.2" />
          <circle cx="12" cy="7.2" r="1.1" />
        </Svg>
      );
    case 'headphones':
      return (
        <Svg>
          <path d="M4 15v-3a8 8 0 0 1 16 0v3" />
          <rect x="3" y="14" width="4.5" height="7" rx="1.6" />
          <rect x="16.5" y="14" width="4.5" height="7" rx="1.6" />
        </Svg>
      );
    case 'headset':
      return (
        <Svg>
          <path d="M4 14v-2a8 8 0 0 1 16 0v2" />
          <rect x="3" y="13" width="4.5" height="6.5" rx="1.6" />
          <rect x="16.5" y="13" width="4.5" height="6.5" rx="1.6" />
          <path d="M18.8 19.5c0 1.4-1.6 2.2-4.3 2.2h-1.7" />
        </Svg>
      );
    case 'digital':
      return (
        <Svg>
          <rect x="2.5" y="3.5" width="19" height="13" rx="2" />
          <path d="M8 21h8M12 16.5V21" />
        </Svg>
      );
    default:
      return (
        <Svg>
          <path d="M4 9v6h4l5 4V5L8 9z" />
          <path d="M16.5 9a4 4 0 0 1 0 6M19 6.5a7.5 7.5 0 0 1 0 11" />
        </Svg>
      );
  }
}

export function CheckIcon(): JSX.Element {
  return (
    <Svg>
      <path d="M5 12.5l4.5 4.5L19 7.5" />
    </Svg>
  );
}

export function RefreshIcon(): JSX.Element {
  return (
    <Svg>
      <path d="M20 11a8 8 0 0 0-14.3-4.9L4 8" />
      <path d="M4 3.5V8h4.5" />
      <path d="M4 13a8 8 0 0 0 14.3 4.9L20 16" />
      <path d="M20 20.5V16h-4.5" />
    </Svg>
  );
}

export function GpuIcon(): JSX.Element {
  return (
    <Svg>
      <rect x="2.5" y="6" width="19" height="11" rx="1.8" />
      <circle cx="15" cy="11.5" r="3" />
      <path d="M6 9.5h4M6 13.5h4M5 17v2.5M9 17v2.5M13 17v2.5" />
    </Svg>
  );
}

export function ExternalIcon(): JSX.Element {
  return (
    <Svg>
      <path d="M14 4h6v6" />
      <path d="M20 4l-9 9" />
      <path d="M18 14v4.5a1.5 1.5 0 0 1-1.5 1.5h-11A1.5 1.5 0 0 1 4 18.5v-11A1.5 1.5 0 0 1 5.5 6H10" />
    </Svg>
  );
}

/** Folder of the double-click test: opens on a double click, like the one in Windows' Mouse settings. */
export function FolderIcon({ open }: { open: boolean }): JSX.Element {
  return open ? (
    <Svg>
      <path d="M3 18.5V6a1.5 1.5 0 0 1 1.5-1.5h4.3l2 2.2h7.7A1.5 1.5 0 0 1 20 8.2V10" />
      <path d="M3 18.5l2.6-7.2A1.5 1.5 0 0 1 7 10.3h14l-2.8 7.4a1.5 1.5 0 0 1-1.4 1H3.6" />
    </Svg>
  ) : (
    <Svg>
      <path d="M3 18V6a1.5 1.5 0 0 1 1.5-1.5h4.3l2 2.2h8.7A1.5 1.5 0 0 1 21 8.2V18a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18z" />
    </Svg>
  );
}
