/**
 * "Мой компьютер" in the PC block on Home: which PC this is (name, seat, zone), what it is built from (CPU, graphics
 * card, memory, the primary monitor) and how it is doing right now (CPU / GPU / RAM load with temperatures, FPS while
 * a game reports it). "Все характеристики" opens the full spec sheet. Everything comes from the settings store, which
 * the top bar reads too, so the Agent is asked once.
 */
import { useState, type ReactNode } from 'react';
import type { PcMetrics } from '@clubshell/contracts';
import { useTranslation } from 'react-i18next';
import { SettingsSection } from '@/components/settings/SettingsSection';
import { Button } from '@/components/ui/Button';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Skeleton } from '@/components/ui/Skeleton';
import { useLocale } from '@/hooks/useLocale';
import { selectPrimaryGpu, selectPrimaryMonitor, useSettingsStore } from '@/store/settings';
import { MonitorIcon } from './display/MonitorSection';
import { CpuIcon, GpuIcon, PcIdBadges, PcSpecsModal, RamIcon, SpecsUnavailable } from './display/PcSpecsSection';
import { cpuName, formatMib, formatUnit } from './display/specs';
import { vitalsOf, type Vital } from './vitals';

interface SpecRowProps {
  icon: ReactNode;
  label: string;
  children: ReactNode;
}

/** One part: icon, mono caps label, the model on one line. */
function SpecRow({ icon, label, children }: SpecRowProps): JSX.Element {
  return (
    <li className="flex min-w-0 items-center gap-3">
      <span
        aria-hidden="true"
        className="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-md bg-accent/10 text-accent [&>svg]:h-5 [&>svg]:w-5"
      >
        {icon}
      </span>
      <div className="min-w-0 flex-1">
        <p className="hud-label">{label}</p>
        <p className="truncate text-base font-semibold leading-snug">{children}</p>
      </div>
    </li>
  );
}

function SpecRowsSkeleton(): JSX.Element {
  return (
    <div className="flex flex-col gap-3" aria-hidden="true">
      {Array.from({ length: 4 }, (_, i) => (
        <Skeleton key={i} variant="rect" height="2.5rem" />
      ))}
    </div>
  );
}

interface LoadRowProps {
  label: string;
  /** 0–100. */
  pct: number;
  /** Shown after the percentage (`47°`, `9,6 / 32 ГБ`). */
  detail: string | null;
  hot?: boolean;
}

function LoadRow({ label, pct, detail, hot = false }: LoadRowProps): JSX.Element {
  const { t } = useTranslation();
  const value = Math.round(Math.min(100, Math.max(0, pct)));
  const load = t('pcDisplay.vitals.load', { value });
  return (
    <div className="flex items-center gap-3">
      <span className="hud-label w-10 shrink-0">{label}</span>
      <ProgressBar
        value={value}
        size="sm"
        tone={hot ? 'danger' : 'primary'}
        label={label}
        valueText={detail ? `${load} · ${detail}` : load}
        className="min-w-0 flex-1"
      />
    </div>
  );
}

/** CPU / GPU / RAM bars of the latest `sys.metrics` sample, and the frame rate while a game reports it. */
function LiveLoad({ metrics, ramMb }: { metrics: PcMetrics; ramMb: number }): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const vitals = vitalsOf(metrics);
  const temp = (v: Vital | undefined): string | null =>
    v?.unit === 'temp' ? t('pcDisplay.vitals.temp', { value: v.value }) : null;
  const cpu = vitals.find((v) => v.key === 'cpu');
  const gpu = vitals.find((v) => v.key === 'gpu');
  const fps = vitals.find((v) => v.key === 'fps');
  return (
    <div className="flex flex-col gap-2.5 border-t border-[color:var(--hairline)] pt-4">
      <div className="flex items-baseline justify-between gap-3">
        <p className="hud-label">{t('pcDisplay.vitals.title')}</p>
        {fps && (
          <p className="flex items-baseline gap-1.5">
            <span className="hud-label">{t('pcDisplay.vitals.fps')}</span>
            <span className="num-dot text-xl leading-none text-accent">{fps.value}</span>
          </p>
        )}
      </div>
      <LoadRow label={t('pcDisplay.vitals.cpu')} pct={metrics.cpuPct} detail={temp(cpu)} hot={cpu?.hot} />
      <LoadRow label={t('pcDisplay.vitals.gpu')} pct={metrics.gpuPct} detail={temp(gpu)} hot={gpu?.hot} />
      {ramMb > 0 && (
        <LoadRow
          label={t('pcDisplay.vitals.ram')}
          pct={(metrics.ramUsedMb / ramMb) * 100}
          detail={`${formatMib(metrics.ramUsedMb, locale, t)} / ${formatMib(ramMb, locale, t)}`}
        />
      )}
    </div>
  );
}

export function PcSummaryCard(): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const pc = useSettingsStore((s) => s.pcInfo?.pc ?? null);
  const hardware = useSettingsStore((s) => s.hardware);
  const failed = useSettingsStore((s) => s.hardwareStatus === 'error');
  const gpu = useSettingsStore(selectPrimaryGpu);
  const monitor = useSettingsStore(selectPrimaryMonitor);
  const metrics = useSettingsStore((s) => s.metrics);
  const [specsOpen, setSpecsOpen] = useState(false);

  let parts: JSX.Element;
  if (hardware) {
    parts = (
      <ul className="flex flex-col gap-3">
        <SpecRow icon={<CpuIcon />} label={t('pcDisplay.specs.cpu')}>
          {cpuName(hardware.cpu.model) || '—'}
        </SpecRow>
        <SpecRow icon={<GpuIcon />} label={t('pcDisplay.specs.gpu')}>
          {gpu?.model || '—'}
        </SpecRow>
        <SpecRow icon={<RamIcon />} label={t('pcDisplay.specs.ram')}>
          {hardware.ramMb > 0 ? formatMib(hardware.ramMb, locale, t) : '—'}
        </SpecRow>
        <SpecRow icon={<MonitorIcon />} label={t('pcDisplay.monitor.title')}>
          {monitor && monitor.width > 0 ? (
            <>
              <span className="tnum">{t('pcDisplay.monitor.resolution', { w: monitor.width, h: monitor.height })}</span>
              {monitor.hz > 0 && (
                <>
                  <span aria-hidden="true"> · </span>
                  <span className="text-accent">{formatUnit('hz', monitor.hz, locale, t)}</span>
                </>
              )}
            </>
          ) : (
            '—'
          )}
        </SpecRow>
      </ul>
    );
  } else if (failed) {
    parts = <SpecsUnavailable />;
  } else {
    parts = <SpecRowsSkeleton />;
  }

  return (
    <SettingsSection title={t('pcDisplay.specs.title')} description={t('pcDisplay.specs.description')}>
      {pc && <PcIdBadges pc={pc} />}
      {parts}
      {metrics && <LiveLoad metrics={metrics} ramMb={hardware?.ramMb ?? 0} />}
      <Button variant="secondary" size="md" block onClick={() => setSpecsOpen(true)}>
        {t('pcDisplay.specs.all')}
      </Button>
      <PcSpecsModal open={specsOpen} onClose={() => setSpecsOpen(false)} />
    </SettingsSection>
  );
}
