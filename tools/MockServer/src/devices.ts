/**
 * What the console shows of a PC's machine (D-73): the hardware its Agent reported (registration, telemetry,
 * `hardwareChanged`) and the games disk of its last heartbeat (`gamesVolume`), as `GET /admin/pcs` and
 * `GET /admin/health` return them.
 *
 * Mock only: the seeded demo hall has no Agent, so its PCs get stable demo hardware by zone and a games disk the Agent
 * maps (PC-05's dropped a few minutes before the mock started), and the dev console shows both; PC-22 never reported
 * anything (the empty state). A registered PC shows only what its Agent sent; the real server never invents either.
 */
import type { HardwareInfo, HeartbeatGamesVolume } from '@clubshell/contracts';
import type { PcRecord } from './db.js';

/** When this mock started: the demo disks' times count from it, so they stay put across polls. */
const STARTED = Date.now();

const SPECS: Record<string, Pick<HardwareInfo, 'cpu' | 'gpu' | 'ramMb' | 'monitors'> & { disk: number }> = {
  Standard: {
    cpu: { model: 'AMD Ryzen 5 5600', cores: 6, threads: 12 },
    gpu: [{ model: 'NVIDIA GeForce RTX 3060', vramMb: 12288, driver: '566.36' }],
    ramMb: 16384,
    monitors: [{ index: 0, width: 1920, height: 1080, hz: 165, primary: true }],
    disk: 476.9,
  },
  VIP: {
    cpu: { model: 'Intel Core i7-14700K', cores: 20, threads: 28 },
    gpu: [{ model: 'NVIDIA GeForce RTX 4070 Ti SUPER', vramMb: 16384, driver: '566.36' }],
    ramMb: 32768,
    monitors: [
      { index: 0, width: 2560, height: 1440, hz: 240, primary: true },
      { index: 1, width: 1920, height: 1080, hz: 60, primary: false },
    ],
    disk: 953.9,
  },
  Bootcamp: {
    cpu: { model: 'Intel Core i5-13600KF', cores: 14, threads: 20 },
    gpu: [{ model: 'NVIDIA GeForce RTX 4060', vramMb: 8192, driver: '566.36' }],
    ramMb: 16384,
    monitors: [{ index: 0, width: 1920, height: 1080, hz: 240, primary: true }],
    disk: 476.9,
  },
};

/** A seeded demo PC: no Agent ever registered on it. */
const demo = (pc: PcRecord): boolean => !pc.registered && pc.hwid === null;

/** The PC's hardware: its Agent's, or the demo spec of its zone; null — never reported (PC-22 of the demo hall). */
export function hardwareOf(pc: PcRecord): HardwareInfo | null {
  if (!demo(pc)) return pc.hardware;
  const spec = SPECS[pc.zone];
  if (!spec || pc.number === 22) return null;
  return {
    cpu: spec.cpu,
    gpu: spec.gpu,
    ramMb: spec.ramMb,
    disks: [
      {
        mount: 'C:',
        totalGb: spec.disk,
        freeGb: Math.round(spec.disk * (0.3 + (pc.number % 5) / 20) * 10) / 10,
        type: 'nvme',
      },
      // The games disk, mapped by the Agent (gone on PC-05, whose disk dropped).
      ...(pc.number === 5 ? [] : [{ mount: 'G:', totalGb: 3725.9, freeGb: 812.4, type: 'network' as const }]),
    ],
    monitors: spec.monitors,
    network: { mac: pc.macAddress ?? '02:42:AC:11:00:00', ip: pc.ipAddress, adapter: 'Realtek PCIe 2.5GbE' },
    os: { version: 'Windows 11 Pro', build: '26100.4061' },
    peripherals: [],
  };
}

/** The games disk of the PC's last heartbeat: its Agent's, or the demo one; null — never reported. */
export function gamesVolumeOf(pc: PcRecord): HeartbeatGamesVolume | null {
  if (!demo(pc)) return pc.gamesVolume ?? null;
  if (pc.number === 22) return null;
  const dropped = pc.number === 5;
  return {
    owner: 'agent',
    mounted: !dropped,
    driveLetter: 'G',
    since: new Date(STARTED - (dropped ? 4 * 60_000 : 3 * 3_600_000)).toISOString(),
  };
}
