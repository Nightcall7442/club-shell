/**
 * «Обновления ClubShell» (beyond the contract, as the server: server/src/ClubShell.Server/Admin/Updates): a demo release
 * of the stable channel, the owner's «Обновлять автоматически», and «Обновить ClubShell» — an `update` command per
 * component a connected PC is behind on (Shell first), journalled as `pcCommand` · `update`. In memory: a restart forgets
 * the switch.
 */
import type { FastifyInstance } from 'fastify';
import type { UpdateManifest } from '@clubshell/contracts';
import { record } from '../control.js';
import { db, errors, now, openSessionForPc } from '../db.js';
import { isConnected, sendCommand } from '../ws.js';
import { requireStaff } from './club.js';

const RELEASE_VERSION = '1.5.0';
const PUBLISHED_AT = '2026-11-15T10:00:00.000Z';

function manifest(component: 'agent' | 'shell'): UpdateManifest {
  const file = component === 'agent' ? `ClubShell-${RELEASE_VERSION}.msi` : `ClubShell-Shell-${RELEASE_VERSION}.zip`;
  return {
    channel: 'stable',
    component,
    version: RELEASE_VERSION,
    url: `http://localhost:8080/stable/${RELEASE_VERSION}/${file}`,
    sha256: '0'.repeat(64),
    size: 1,
    signature: 'bW9jaw==',
    releaseNotes: 'Демо-выпуск mock-сервера',
    mandatory: false,
    publishedAt: PUBLISHED_AT,
    minAgentVersion: null,
  };
}

let auto = false;

/** Semver order; an unparsable version is older than any. */
function compare(a: string, b: string): number {
  const parse = (v: string): number[] | null => {
    const m = /^(\d+)\.(\d+)\.(\d+)/.exec(v.trim());
    return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
  };
  const x = parse(a);
  const y = parse(b);
  if (!x || !y) return x ? 1 : y ? -1 : 0;
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return (x[i] ?? 0) - (y[i] ?? 0);
  return 0;
}

export function updatesRoutes(app: FastifyInstance): void {
  app.get('/admin/updates', async (req) => {
    requireStaff(req, 'owner');
    return {
      auto,
      releases: [
        {
          channel: 'stable',
          version: RELEASE_VERSION,
          publishedAt: PUBLISHED_AT,
          agent: RELEASE_VERSION,
          shell: RELEASE_VERSION,
          notes: 'Демо-выпуск mock-сервера',
          mandatory: false,
        },
      ],
      pcs: db.pcs.map((p) => ({
        id: p.id,
        name: p.name,
        number: p.number,
        online: isConnected(p.id),
        busy: openSessionForPc(p.id) !== undefined,
        agentVersion: p.agentVersion,
        shellVersion: p.shellVersion,
      })),
    };
  });

  app.patch('/admin/updates', async (req) => {
    requireStaff(req, 'owner');
    const b = (req.body ?? {}) as Record<string, unknown>;
    if (typeof b['auto'] !== 'boolean') throw errors.validation('auto', 'required');
    auto = b['auto'];
    return { ok: true, auto };
  });

  app.post('/admin/updates/apply', async (req) => {
    const staff = requireStaff(req, 'owner');
    const b = (req.body ?? {}) as Record<string, unknown>;
    if (b['channel'] !== undefined && b['channel'] !== 'stable') throw errors.notFound('release');
    const only = Array.isArray(b['pcIds']) ? (b['pcIds'] as unknown[]).map(String) : null;
    const targets = db.pcs.filter((p) => only === null || only.includes(p.id));
    if (only !== null && targets.length !== only.length) throw errors.notFound('pc');
    const results = targets.map((pc) => {
      const needed = (['shell', 'agent'] as const).filter(
        (c) => compare(RELEASE_VERSION, c === 'agent' ? pc.agentVersion : pc.shellVersion) > 0,
      );
      let result: 'sent' | 'afterSession' | 'upToDate' | 'offline';
      if (needed.length === 0) result = 'upToDate';
      else if (!isConnected(pc.id)) result = 'offline';
      else {
        for (const component of needed) {
          void sendCommand(
            pc.id,
            'update',
            { component, applyNow: true, manifest: manifest(component) },
            { issuedBy: staff.name },
          );
        }
        record(staff, 'pcCommand', {
          pcId: pc.id,
          detail: `${pc.name} · update ${RELEASE_VERSION}`,
          meta: { kind: 'update', version: RELEASE_VERSION, at: now() },
        });
        result = openSessionForPc(pc.id) ? 'afterSession' : 'sent';
      }
      return { pcId: pc.id, name: pc.name, result };
    });
    return { channel: 'stable', version: RELEASE_VERSION, results };
  });
}
