/**
 * What a PC's Agent reports about the machine itself, for «Состояние ПК» and the owner's «Зал и устройства»: the games
 * disk of its last heartbeat (D-73) and its hardware inventory (sent at registration and again on every change).
 *
 * Variant F: a dropped games disk is amber (act now), a connected one the green "online" dot, one the diskless helper
 * mounts muted; the inventory is a quiet well of caption / value rows.
 */
import type { DiskType, HardwareInfo, HeartbeatGamesVolume, PcStatus } from '@clubshell/contracts';
import clsx from 'clsx';
import { dateLocale, t } from '@/i18n';
import { StatusDot } from '@/ui';

/** `12:40` today, `09.10 12:40` on another day. */
function sinceText(iso: string): string {
  const at = new Date(iso);
  const time = at.toLocaleTimeString(dateLocale(), { hour: '2-digit', minute: '2-digit' });
  if (at.toDateString() === new Date().toDateString()) return time;
  return `${at.toLocaleDateString(dateLocale(), { day: '2-digit', month: '2-digit' })} ${time}`;
}

/**
 * The games disk in words with its tone, or null when there is nothing to say: the PC never reported one (an older
 * Agent, no heartbeat yet), it has no network games disk (`owner: none`, local disks only), or it is off (its last
 * report is stale).
 */
export function gamesDisk(
  volume: HeartbeatGamesVolume | null | undefined,
  status: PcStatus,
): { text: string; tone: 'ok' | 'warn' | 'muted' } | null {
  if (!volume || status === 'offline') return null;
  // ClubDisklessHelper reports the volume to its own server; the Agent cannot tell whether it is mounted.
  if (volume.owner === 'disklessHelper') return { text: t('подключает ClubDiskless'), tone: 'muted' };
  if (volume.owner !== 'agent' || typeof volume.mounted !== 'boolean') return null;
  const at = volume.since ? sinceText(volume.since) : null;
  return volume.mounted
    ? { text: at ? t('подключён с {time}', { time: at }) : t('подключён'), tone: 'ok' }
    : { text: at ? t('не подключён с {time}', { time: at }) : t('не подключён'), tone: 'warn' };
}

/** The games disk as a status line (dot and words); nothing when {@link gamesDisk} has nothing to say. */
export function GamesDisk({
  volume,
  status,
}: {
  volume: HeartbeatGamesVolume | null | undefined;
  status: PcStatus;
}): JSX.Element | null {
  const disk = gamesDisk(volume, status);
  if (!disk) return null;
  return (
    <span className="flex items-center gap-2.5 whitespace-nowrap text-[13px] leading-5">
      <StatusDot tone={disk.tone} />
      <span
        className={clsx(
          disk.tone === 'warn' ? 'font-medium text-warning' : disk.tone === 'muted' ? 'text-muted' : 'text-dim',
        )}
      >
        {disk.text}
      </span>
    </span>
  );
}

const DISK_TYPE: Partial<Record<DiskType, string>> = { nvme: 'NVMe', ssd: 'SSD', hdd: 'HDD' };

const gb = (mb: number): number => Math.round(mb / 1024);

/** One part of the machine: the item in the text colour, its details muted on a line of their own. */
function Item({ main, sub }: { main: string; sub?: string | null }): JSX.Element {
  return (
    <span className="flex min-w-0 flex-col">
      <span className="break-words">{main}</span>
      {sub && <span className="text-xs leading-4 text-muted">{sub}</span>}
    </span>
  );
}

/**
 * The PC's hardware as its Agent reported it: processor, video cards, memory, disks, monitors (the main one first) and
 * Windows; a part the inventory has nothing for is left out.
 */
export function HardwareList({ hardware }: { hardware: HardwareInfo }): JSX.Element {
  const { cpu, os } = hardware;
  const gpus = hardware.gpu ?? [];
  const disks = hardware.disks ?? [];
  const monitors = [...(hardware.monitors ?? [])].sort(
    (a, b) => Number(b.primary) - Number(a.primary) || a.index - b.index,
  );
  const rows: { label: string; items: JSX.Element[] }[] = [
    {
      label: t('Процессор'),
      items: cpu?.model
        ? [
            <Item
              key="cpu"
              main={cpu.model}
              sub={
                cpu.cores > 0
                  ? t('ядер: {cores}, потоков: {threads}', { cores: cpu.cores, threads: cpu.threads })
                  : null
              }
            />,
          ]
        : [],
    },
    {
      label: t('Видеокарта'),
      items: gpus.map((g, i) => (
        <Item key={i} main={g.model} sub={g.vramMb >= 1024 ? t('{n} ГБ', { n: gb(g.vramMb) }) : null} />
      )),
    },
    {
      label: t('Память'),
      items: hardware.ramMb > 0 ? [<Item key="ram" main={t('{n} ГБ', { n: gb(hardware.ramMb) })} />] : [],
    },
    {
      label: t('Диски'),
      items: disks.map((d) => (
        <Item
          key={d.mount}
          main={`${d.mount} ${t('{n} ГБ', { n: Math.round(d.totalGb) })}`}
          sub={[
            t('свободно {n} ГБ', { n: Math.round(d.freeGb) }),
            d.type === 'network' ? t('сетевой') : DISK_TYPE[d.type],
          ]
            .filter(Boolean)
            .join(' · ')}
        />
      )),
    },
    {
      label: t('Мониторы'),
      items: monitors.map((m) => (
        <Item
          key={m.index}
          main={m.hz > 0 ? `${m.width}×${m.height} · ${t('{n} Гц', { n: m.hz })}` : `${m.width}×${m.height}`}
        />
      )),
    },
    {
      label: t('Система'),
      items: os?.version ? [<Item key="os" main={os.version} sub={os.build} />] : [],
    },
  ];
  return (
    <dl aria-label={t('Железо')} className="well flex flex-col divide-y divide-accent/[0.07] text-[13px] leading-5">
      {rows
        .filter((r) => r.items.length > 0)
        .map((r) => (
          <div key={r.label} className="grid grid-cols-[6.5rem_minmax(0,1fr)] items-baseline gap-3 px-3.5 py-2">
            <dt className="label-sm">{r.label}</dt>
            <dd className="tnum flex min-w-0 flex-col gap-1.5 text-text">{r.items}</dd>
          </div>
        ))}
    </dl>
  );
}
