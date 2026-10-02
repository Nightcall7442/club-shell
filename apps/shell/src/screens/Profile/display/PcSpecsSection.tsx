/**
 * Settings → "Мой компьютер": a read-only spec sheet of this PC from `sys_hardware` (CPU, GPUs with VRAM, RAM, disks,
 * OS, network, peripherals) plus its name, zone and seat from `sys_pc_info`. Monitors come from the live kiosk state,
 * so a refresh rate changed a minute ago shows here too.
 */
import { useCallback, useEffect, useState, type ReactNode } from 'react';
import type { DiskInfo, HardwareInfo, MonitorInfo, PcInfo } from '@clubshell/contracts';
import { useTranslation } from 'react-i18next';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { DotAmount } from '@/components/ui/DotAmount';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Skeleton } from '@/components/ui/Skeleton';
import { useLocale } from '@/hooks/useLocale';
import { api } from '@/lib/tauri';
import { useSettingsStore } from '@/store';
import { SettingsSection } from '../Settings';
import { MonitorIcon } from './MonitorSection';
import { cpuGhz, cpuName, formatGib, formatMib, formatUnit } from './specs';

const ICON_PROPS = {
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 2,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
  'aria-hidden': true,
} as const;

function CpuIcon(): JSX.Element {
  return (
    <svg {...ICON_PROPS}>
      <rect x="6" y="6" width="12" height="12" rx="1.5" />
      <rect x="9.5" y="9.5" width="5" height="5" />
      <path d="M9 2.5V6M15 2.5V6M9 18v3.5M15 18v3.5M2.5 9H6M2.5 15H6M18 9h3.5M18 15h3.5" />
    </svg>
  );
}

function GpuIcon(): JSX.Element {
  return (
    <svg {...ICON_PROPS}>
      <rect x="2.5" y="6" width="19" height="11" rx="1.5" />
      <circle cx="9" cy="11.5" r="3" />
      <path d="M15 9.5h3.5M15 13.5h3.5M5 17v2.5h6V17" />
    </svg>
  );
}

function RamIcon(): JSX.Element {
  return (
    <svg {...ICON_PROPS}>
      <path d="M3 7.5h18v8H3z" />
      <path d="M7 10.5v2M11 10.5v2M15 10.5v2M5 15.5v3M9 15.5v3M13 15.5v3M17 15.5v3" />
    </svg>
  );
}

function DiskIcon(): JSX.Element {
  return (
    <svg {...ICON_PROPS}>
      <rect x="3" y="4" width="18" height="16" rx="2" />
      <path d="M3 14.5h18" />
      <path d="M16.5 17.25h.01M7 17.25h5" />
    </svg>
  );
}

function OsIcon(): JSX.Element {
  return (
    <svg {...ICON_PROPS}>
      <path d="M3.5 5.5l7-1v7h-7zM13 4.2l7.5-1.2v8.5H13zM3.5 13h7v7l-7-1zM13 13h7.5v8.5L13 20.3z" />
    </svg>
  );
}

function NetworkIcon(): JSX.Element {
  return (
    <svg {...ICON_PROPS}>
      <rect x="9" y="2.5" width="6" height="5" rx="1" />
      <rect x="2.5" y="16.5" width="6" height="5" rx="1" />
      <rect x="15.5" y="16.5" width="6" height="5" rx="1" />
      <path d="M12 7.5v4.5M5.5 16.5V12h13v4.5" />
    </svg>
  );
}

function GamepadIcon(): JSX.Element {
  return (
    <svg {...ICON_PROPS}>
      <path d="M7 7.5h10a4.5 4.5 0 0 1 4.4 5.5l-1 4.2a2.4 2.4 0 0 1-4.1 1.1L14 16h-4l-2.3 2.3a2.4 2.4 0 0 1-4.1-1.1l-1-4.2A4.5 4.5 0 0 1 7 7.5z" />
      <path d="M7.5 10.5v3M6 12h3M15.5 11h.01M17.5 13h.01" />
    </svg>
  );
}

interface SpecTileProps {
  icon: ReactNode;
  label: string;
  children: ReactNode;
  className?: string;
}

/** One component: icon, mono caps label, then whatever lines it needs. */
function SpecTile({ icon, label, children, className }: SpecTileProps): JSX.Element {
  return (
    <li className={className}>
      <div className="flex h-full gap-4 rounded-lg border border-text/10 bg-text/[0.02] p-4">
        <span
          aria-hidden="true"
          className="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-md bg-accent/10 text-accent [&>svg]:h-6 [&>svg]:w-6"
        >
          {icon}
        </span>
        <div className="flex min-w-0 flex-1 flex-col gap-1">
          <p className="hud-label">{label}</p>
          {children}
        </div>
      </div>
    </li>
  );
}

function Main({ children }: { children: ReactNode }): JSX.Element {
  const title = typeof children === 'string' ? children : undefined;
  return (
    <p className="truncate text-lg font-semibold leading-snug" title={title}>
      {children}
    </p>
  );
}

/** Secondary facts joined with a middle dot; nothing at all when none is known. */
function Detail({ parts }: { parts: readonly (string | null | undefined | false)[] }): JSX.Element | null {
  const text = parts.filter(Boolean).join(' · ');
  return text ? <p className="text-sm text-muted">{text}</p> : null;
}

function DiskRow({ disk }: { disk: DiskInfo }): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const used = Math.max(0, disk.totalGb - disk.freeGb);
  const full = disk.totalGb > 0 && disk.freeGb / disk.totalGb < 0.1;
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex items-baseline justify-between gap-3">
        <p className="min-w-0 truncate">
          <span className="font-semibold">{disk.mount}</span>{' '}
          <span className="text-sm text-muted">{t(`pcDisplay.specs.diskType.${disk.type}`)}</span>
        </p>
        <p className="tnum shrink-0 text-sm text-muted">
          {t('pcDisplay.specs.diskFree', {
            free: formatGib(disk.freeGb, locale, t),
            total: formatGib(disk.totalGb, locale, t),
          })}
        </p>
      </div>
      <ProgressBar
        value={used}
        max={disk.totalGb}
        size="sm"
        tone={full ? 'danger' : 'primary'}
        label={t('pcDisplay.specs.diskUsed', { mount: disk.mount })}
      />
    </div>
  );
}

function SpecsSkeleton(): JSX.Element {
  return (
    <ul className="grid gap-3 md:grid-cols-2 2xl:grid-cols-3" aria-hidden="true">
      {Array.from({ length: 6 }, (_, i) => (
        <li key={i}>
          <Skeleton variant="rect" height="6.5rem" />
        </li>
      ))}
    </ul>
  );
}

interface SpecsGridProps {
  hardware: HardwareInfo;
  monitors: readonly MonitorInfo[];
}

function SpecsGrid({ hardware: hw, monitors }: SpecsGridProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const ghz = cpuGhz(hw.cpu.model);
  const disks = hw.disks.filter((d) => d.totalGb > 0);
  return (
    <ul className="grid gap-3 md:grid-cols-2 2xl:grid-cols-3">
      <SpecTile icon={<CpuIcon />} label={t('pcDisplay.specs.cpu')}>
        <Main>{cpuName(hw.cpu.model) || '—'}</Main>
        <Detail
          parts={[
            hw.cpu.cores > 0 && t('pcDisplay.specs.cores', { count: hw.cpu.cores }),
            hw.cpu.threads > 0 && t('pcDisplay.specs.threads', { count: hw.cpu.threads }),
            ghz !== null && formatUnit('ghz', ghz, locale, t),
          ]}
        />
      </SpecTile>

      {hw.gpu.map((gpu, i) => (
        <SpecTile key={`${gpu.model}-${i}`} icon={<GpuIcon />} label={t('pcDisplay.specs.gpu')}>
          <Main>{gpu.model || '—'}</Main>
          <Detail
            parts={[
              gpu.vramMb > 0 && t('pcDisplay.specs.vram', { value: formatMib(gpu.vramMb, locale, t) }),
              gpu.driver && t('pcDisplay.specs.driver', { version: gpu.driver }),
            ]}
          />
        </SpecTile>
      ))}

      <SpecTile icon={<RamIcon />} label={t('pcDisplay.specs.ram')}>
        <p className="mt-1 text-3xl leading-none">
          <DotAmount value={hw.ramMb > 0 ? formatMib(hw.ramMb, locale, t) : '—'} />
        </p>
      </SpecTile>

      <SpecTile icon={<MonitorIcon />} label={t('pcDisplay.specs.monitors')}>
        {monitors.length === 0 && <Main>—</Main>}
        {monitors.map((m) => (
          <p key={m.index} className="tnum text-lg font-semibold leading-snug">
            {t('pcDisplay.monitor.resolution', { w: m.width, h: m.height })}
            {m.hz > 0 && <span className="text-accent"> · {formatUnit('hz', m.hz, locale, t)}</span>}
            {monitors.length > 1 && m.primary && (
              <span className="ml-2 text-sm font-normal text-muted">{t('pcDisplay.monitor.primary')}</span>
            )}
          </p>
        ))}
      </SpecTile>

      {disks.length > 0 && (
        <SpecTile icon={<DiskIcon />} label={t('pcDisplay.specs.disks')} className="md:col-span-2 2xl:col-span-1">
          <div className="mt-1 flex flex-col gap-3">
            {disks.map((d) => (
              <DiskRow key={d.mount} disk={d} />
            ))}
          </div>
        </SpecTile>
      )}

      <SpecTile icon={<OsIcon />} label={t('pcDisplay.specs.os')}>
        <Main>{hw.os.version || '—'}</Main>
        <Detail parts={[hw.os.build && t('pcDisplay.specs.build', { build: hw.os.build })]} />
      </SpecTile>

      <SpecTile icon={<NetworkIcon />} label={t('pcDisplay.specs.network')}>
        <Main>{hw.network.adapter || '—'}</Main>
        <Detail parts={[hw.network.ip && t('pcDisplay.specs.ip', { ip: hw.network.ip })]} />
      </SpecTile>

      {hw.peripherals.length > 0 && (
        <SpecTile
          icon={<GamepadIcon />}
          label={t('pcDisplay.specs.peripherals')}
          className="md:col-span-2 2xl:col-span-1"
        >
          <div className="grid gap-x-6 gap-y-0.5 sm:grid-cols-2">
            {hw.peripherals.map((p, i) => (
              <p key={`${p.vendorId}:${p.productId}:${i}`} className="truncate" title={p.name}>
                {p.name}
              </p>
            ))}
          </div>
        </SpecTile>
      )}
    </ul>
  );
}

export function PcSpecsSection(): JSX.Element {
  const { t } = useTranslation();
  const storedPc = useSettingsStore((s) => s.pcInfo);
  const liveMonitors = useSettingsStore((s) => s.kiosk?.monitors);
  const [pc, setPc] = useState<PcInfo | null>(storedPc);
  const [hardware, setHardware] = useState<HardwareInfo | null>(null);
  const [failed, setFailed] = useState(false);
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    let active = true;
    setFailed(false);
    api.system.hardware().then(
      (hw) => active && setHardware(hw),
      () => active && setFailed(true),
    );
    return () => {
      active = false;
    };
  }, [generation]);

  useEffect(() => {
    if (storedPc) {
      setPc(storedPc);
      return undefined;
    }
    let active = true;
    api.system.pcInfo().then(
      (info) => active && setPc(info),
      () => undefined,
    );
    return () => {
      active = false;
    };
  }, [storedPc]);

  const retry = useCallback(() => setGeneration((g) => g + 1), []);
  const monitors = liveMonitors && liveMonitors.length > 0 ? liveMonitors : (hardware?.monitors ?? []);

  return (
    <SettingsSection title={t('pcDisplay.specs.title')} description={t('pcDisplay.specs.description')}>
      {pc && (
        <div className="flex flex-wrap items-center gap-2">
          <Badge tone="accent" size="lg" solid>
            {pc.pc.name}
          </Badge>
          {pc.pc.number > 0 && (
            <Badge tone="neutral" size="lg">
              {t('pcDisplay.specs.seat', { number: pc.pc.number })}
            </Badge>
          )}
          {pc.pc.zone && (
            <Badge tone="neutral" size="lg">
              {t('pcDisplay.specs.zone', { zone: pc.pc.zone })}
            </Badge>
          )}
        </div>
      )}
      {hardware ? (
        <SpecsGrid hardware={hardware} monitors={monitors} />
      ) : failed ? (
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-muted">{t('pcDisplay.specs.unavailable')}</p>
          <Button variant="secondary" size="md" onClick={retry}>
            {t('common.retry')}
          </Button>
        </div>
      ) : (
        <SpecsSkeleton />
      )}
    </SettingsSection>
  );
}

export default PcSpecsSection;
