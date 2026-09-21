import { useEffect } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { Badge } from '@/components/ui/Badge';
import { Tooltip } from '@/components/ui/Tooltip';
import { useLocale } from '@/hooks/useLocale';
import { formatGb } from '@/lib/format';
import { api } from '@/lib/tauri';
import { useNotificationsStore } from '@/store/notifications';
import { useSettingsStore } from '@/store/settings';

export interface PcInfoBadgeProps {
  /** Hide the CPU/GPU/RAM mini row. */
  hideMetrics?: boolean;
  className?: string;
}

/** Link state of this PC as one dot: agent + server online → success, server offline → accent, agent gone → danger. */
export function PcInfoBadge({ hideMetrics = false, className }: PcInfoBadgeProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const pcInfo = useSettingsStore((s) => s.pcInfo);
  const metrics = useSettingsStore((s) => s.metrics);
  const setMetrics = useSettingsStore((s) => s.setMetrics);
  const shellVersion = useSettingsStore((s) => s.kiosk?.version ?? s.pcInfo?.shellVersion ?? null);
  const agentConnected = useNotificationsStore((s) => s.agentConnected);
  const serverOnline = useNotificationsStore((s) => s.serverConnectivity === 'online');

  // One sample right away; `agent://sys.metrics` keeps it fresh afterwards.
  useEffect(() => {
    if (metrics !== null || hideMetrics) {
      return;
    }
    let active = true;
    api.system.metrics().then(
      (m) => {
        if (active) {
          setMetrics(m);
        }
      },
      () => undefined,
    );
    return () => {
      active = false;
    };
  }, [metrics, hideMetrics, setMetrics]);

  const tone = !agentConnected ? 'danger' : serverOnline ? 'success' : 'accent';
  const linkLabel = !agentConnected
    ? t('lock.agentOffline')
    : serverOnline
      ? t('lock.agentOnline')
      : t('lock.serverOffline');
  const pc = pcInfo?.pc ?? null;

  return (
    <div
      role="group"
      aria-label={t('lock.pcInfo')}
      className={clsx('glass flex flex-col gap-2 rounded-xl px-5 py-4 text-sm text-muted', className)}
    >
      <div className="flex items-center gap-3">
        <Tooltip content={linkLabel} placement="top">
          <span
            role="img"
            aria-label={linkLabel}
            tabIndex={0}
            data-nav="true"
            className="focus-ring inline-flex h-6 w-6 items-center justify-center rounded-full"
          >
            <span
              aria-hidden="true"
              className={clsx(
                'h-3 w-3 rounded-full',
                tone === 'success' && 'bg-success',
                tone === 'accent' && 'bg-accent',
                tone === 'danger' && 'bg-danger anim-live-dot',
              )}
            />
          </span>
        </Tooltip>
        <span className="text-lg font-bold text-text">{pc?.name ?? t('lock.pc')}</span>
        {pc && (
          <Badge tone="primary" size="sm">
            {t('lock.zone')}: {pc.zone} · {t('lock.seat')} {pc.number}
          </Badge>
        )}
      </div>
      {pcInfo && (
        <p className="tnum">
          {t('lock.version', { shell: shellVersion ?? pcInfo.shellVersion, agent: pcInfo.agentVersion })}
        </p>
      )}
      {!hideMetrics && metrics && (
        <p className="tnum flex flex-wrap gap-x-4 gap-y-1">
          <span>
            {t('lock.cpu')} <span className="text-text">{Math.round(metrics.cpuPct)}%</span>
          </span>
          <span>
            {t('lock.gpu')} <span className="text-text">{Math.round(metrics.gpuPct)}%</span>
          </span>
          <span>
            {t('lock.ram')} <span className="text-text">{formatGb(metrics.ramUsedMb / 1024, locale)}</span>
          </span>
        </p>
      )}
    </div>
  );
}

export default PcInfoBadge;
