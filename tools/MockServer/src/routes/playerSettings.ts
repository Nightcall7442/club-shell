/**
 * Player game settings (SERVER_API.md §4.4, "Player game settings"): a player's own binds / sensitivity / graphics per
 * game, zipped by the Agent from `Game.settingsPaths`, so they follow the player to any PC. The mock keeps the zips in
 * memory next to the store (base64 in `db.playerSettings`), behind a pre-signed-style upload URL like the save uploads.
 */
import { createHash, randomBytes } from 'node:crypto';
import type { FastifyInstance } from 'fastify';
import type { PlayerSettingsBundle, PlayerSettingsItem } from '@clubshell/contracts';
import { body, db, errors, findGame, inSec, int, markDirty, now, requireAgent, requireUser, str } from '../db.js';

const MAX_BYTES = 16 * 1024 * 1024;
const UPLOAD_TTL_MS = 10 * 60_000;

interface StoredSettings {
  userId: string;
  gameId: string;
  sha256: string;
  sizeBytes: number;
  updatedAt: string;
  /** The zip, base64. */
  data: string;
}

type WithSettings = typeof db & { playerSettings?: Record<string, StoredSettings> };

function store(): Record<string, StoredSettings> {
  const s = db as WithSettings;
  s.playerSettings ??= {};
  return s.playerSettings;
}

const keyOf = (userId: string, gameId: string): string => `${userId}:${gameId}`;

/** Uploads received but not yet committed: token → bytes. */
const pending = new Map<string, { bytes: Buffer | null; at: number; userId: string; gameId: string }>();

function base(req: { protocol: string; hostname: string }): string {
  return process.env['MOCK_PUBLIC_URL'] ?? `${req.protocol}://${req.hostname}`;
}

function bundleOf(s: StoredSettings, origin: string): PlayerSettingsBundle {
  return {
    gameId: s.gameId,
    url: `${origin}/api/v1/mock/player-settings/${encodeURIComponent(keyOf(s.userId, s.gameId))}`,
    sha256: s.sha256,
    sizeBytes: s.sizeBytes,
    updatedAt: s.updatedAt,
  };
}

function listFor(userId: string): PlayerSettingsItem[] {
  return Object.values(store())
    .filter((s) => s.userId === userId)
    .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
    .map((s) => ({
      gameId: s.gameId,
      title: findGame(s.gameId)?.title ?? s.gameId,
      sizeBytes: s.sizeBytes,
      updatedAt: s.updatedAt,
    }));
}

export function playerSettingsRoutes(app: FastifyInstance): void {
  app.get<{ Params: { userId: string } }>('/users/:userId/game-settings', async (req) => {
    requireUser(req, req.params.userId);
    return { items: listFor(req.params.userId) };
  });

  app.get<{ Params: { userId: string; gameId: string } }>(
    '/users/:userId/game-settings/:gameId',
    async (req, reply) => {
      requireAgent(req);
      const s = store()[keyOf(req.params.userId, req.params.gameId)];
      if (!s) return reply.code(204).send();
      return bundleOf(s, base(req));
    },
  );

  app.post<{ Params: { userId: string; gameId: string } }>(
    '/users/:userId/game-settings/:gameId/upload-target',
    async (req) => {
      requireAgent(req);
      if (!findGame(req.params.gameId)) throw errors.notFound('game');
      const token = randomBytes(16).toString('hex');
      pending.set(token, { bytes: null, at: Date.now(), userId: req.params.userId, gameId: req.params.gameId });
      return {
        uploadUrl: `${base(req)}/api/v1/mock/player-settings/upload/${token}`,
        expiresAt: inSec(UPLOAD_TTL_MS / 1000),
        maxBytes: MAX_BYTES,
      };
    },
  );

  // The pre-signed PUT target: keeps the bytes until the Agent commits them.
  app.put<{ Params: { token: string } }>('/mock/player-settings/upload/:token', async (req) => {
    const bytes = Buffer.isBuffer(req.body) ? req.body : Buffer.from(req.rawBody ?? '', 'utf8');
    if (bytes.length > MAX_BYTES) throw errors.validation('body', 'tooLarge');
    for (const [k, v] of pending) if (Date.now() - v.at > UPLOAD_TTL_MS) pending.delete(k);
    const slot = pending.get(req.params.token);
    if (!slot) throw errors.notFound('uploadTarget');
    slot.bytes = bytes;
    return { ok: true, bytes: bytes.length };
  });

  app.put<{ Params: { userId: string; gameId: string } }>('/users/:userId/game-settings/:gameId', async (req) => {
    requireAgent(req);
    const b = body(req);
    const uploadUrl = str(b, 'uploadUrl', 500);
    const sha256 = str(b, 'sha256', 64).toLowerCase();
    const sizeBytes = int(b, 'sizeBytes', 1, MAX_BYTES);
    const token = uploadUrl.split('/').pop() ?? '';
    const upload = pending.get(token);
    // The token was issued for this player and game only: an upload cannot be committed under someone else.
    if (!upload?.bytes || upload.userId !== req.params.userId || upload.gameId !== req.params.gameId)
      throw errors.validation('uploadUrl', 'unknown');
    const actual = createHash('sha256').update(upload.bytes).digest('hex');
    if (actual !== sha256 || upload.bytes.length !== sizeBytes) throw errors.validation('sha256', 'mismatch');
    pending.delete(token);
    const s: StoredSettings = {
      userId: req.params.userId,
      gameId: req.params.gameId,
      sha256,
      sizeBytes,
      updatedAt: now(),
      data: upload.bytes.toString('base64'),
    };
    store()[keyOf(s.userId, s.gameId)] = s;
    markDirty();
    return bundleOf(s, base(req));
  });

  app.delete<{ Params: { userId: string; gameId: string } }>(
    '/users/:userId/game-settings/:gameId',
    async (req, reply) => {
      requireUser(req, req.params.userId);
      const key = keyOf(req.params.userId, req.params.gameId);
      if (!store()[key]) throw errors.notFound('settings');
      delete store()[key];
      markDirty();
      return reply.code(204).send();
    },
  );

  app.get<{ Params: { key: string } }>('/mock/player-settings/:key', async (req, reply) => {
    const s = store()[decodeURIComponent(req.params.key)];
    if (!s) throw errors.notFound('settings');
    return reply.type('application/zip').send(Buffer.from(s.data, 'base64'));
  });
}
