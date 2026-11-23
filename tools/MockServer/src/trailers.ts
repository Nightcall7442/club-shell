/**
 * The club server's trailers (D-97), as the server keeps them, in memory for this run: a stored copy per game, served
 * with byte ranges at `/api/v1/trailers/{id}`; the owner uploads one (MP4/WebM by its bytes, at most 500 MB) or takes
 * it off; «Найти трейлеры» "finds" one for every Steam game without one (a store link, stored after a moment as the
 * bundled sample) and none for the rest. Counter-Strike 2's seed link starts out stored, so its copy plays at once.
 * Agents get the stored copy's path in `videoUrl` and the link it came from in `videoSourceUrl`.
 */
import { readFileSync } from 'node:fs';
import type { FastifyInstance, FastifyReply, FastifyRequest } from 'fastify';
import type { Game } from '@clubshell/contracts';
import { db, errors, findGame, markDirty, uuid } from './db.js';

export const TRAILER_MAX_BYTES = 500 * 1024 * 1024;

/** A small real WebM (3 s, 320×180), what every "downloaded" trailer is in the mock. */
const SAMPLE = readFileSync(new URL('../assets/trailer-sample.webm', import.meta.url));

export interface MockTrailer {
  status: 'searching' | 'downloading' | 'ready' | 'failed';
  origin: 'link' | 'found' | 'upload';
  sourceUrl: string | null;
  url: string | null;
  contentType: string | null;
  size: number | null;
  reason: string | null;
}

const TRAILERS = new Map<string, MockTrailer>();
const FILES = new Map<string, { type: string; bytes: Buffer }>();

function store(bytes: Buffer, type: string): string {
  const id = uuid();
  FILES.set(id, { type, bytes });
  return `/api/v1/trailers/${id}`;
}

function bump(): void {
  db.catalogVersion = `v${Date.now()}`;
  db.configVersion += 1;
  markDirty();
}

/** The trailer's type by its first bytes (as the server): an MP4 `ftyp` box or a WebM EBML header. */
export function sniffVideo(b: Buffer): string | null {
  if (b.length >= 12 && b.subarray(4, 8).toString('latin1') === 'ftyp') return 'video/mp4';
  if (b.length >= 4 && b[0] === 0x1a && b[1] === 0x45 && b[2] === 0xdf && b[3] === 0xa3)
    return b.subarray(0, 64).includes('webm') ? 'video/webm' : null;
  return null;
}

/** The game's trailer for the console, or null for none (a removed one is none). */
export function trailerOf(game: Game): MockTrailer | null {
  let t = TRAILERS.get(game.id);
  // The seed's link of the run starts out stored.
  if (!t && game.videoUrl) {
    t = {
      status: 'ready',
      origin: 'link',
      sourceUrl: game.videoUrl,
      url: store(SAMPLE, 'video/webm'),
      contentType: 'video/webm',
      size: SAMPLE.length,
      reason: null,
    };
    TRAILERS.set(game.id, t);
  }
  return t && t.reason !== 'removed' ? t : null;
}

/** The game as an Agent gets it: the stored copy once ready, the original link as `videoSourceUrl`. */
export function withTrailer(game: Game): Game {
  const t = trailerOf(game);
  if (t?.status === 'ready' && t.url) return { ...game, videoUrl: t.url, videoSourceUrl: t.sourceUrl };
  if (t?.reason === 'notAVideo' || t?.reason === 'streamingFormat') return { ...game, videoUrl: null };
  return t?.origin === 'found' && t.status === 'downloading' ? { ...game, videoUrl: t.sourceUrl } : game;
}

function serve(req: FastifyRequest<{ Params: { id: string } }>, reply: FastifyReply): FastifyReply {
  const file = FILES.get(req.params.id);
  if (!file) throw errors.notFound('trailer');
  const size = file.bytes.length;
  reply
    .header('Accept-Ranges', 'bytes')
    .header('Cache-Control', 'public, max-age=31536000, immutable')
    .header('X-Content-Type-Options', 'nosniff')
    .type(file.type);
  const range = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range ?? '');
  if (!range || (range[1] === '' && range[2] === '')) return reply.send(file.bytes);
  let start = range[1] === '' ? Math.max(0, size - Number(range[2])) : Number(range[1]);
  let end = range[1] === '' || range[2] === '' ? size - 1 : Math.min(Number(range[2]), size - 1);
  if (start >= size || start > end) return reply.code(416).header('Content-Range', `bytes */${size}`).send();
  start = Math.max(0, start);
  end = Math.max(start, end);
  return reply
    .code(206)
    .header('Content-Range', `bytes ${start}-${end}/${size}`)
    .send(file.bytes.subarray(start, end + 1));
}

/**
 * `requireOwner` is the console's staff check (`requireStaff(req, 'owner')`), passed in so this module does not depend
 * on the club routes.
 */
export function trailerRoutes(app: FastifyInstance, requireOwner: (req: FastifyRequest) => void): void {
  app.get<{ Params: { id: string } }>('/trailers/:id', async (req, reply) => serve(req, reply));

  app.post<{ Params: { id: string } }>(
    '/admin/games/:id/trailer',
    { bodyLimit: TRAILER_MAX_BYTES + 1024 * 1024 },
    async (req, reply) => {
      requireOwner(req);
      const game = findGame(req.params.id);
      if (!game) throw errors.notFound('game');
      const bytes = Buffer.isBuffer(req.body) ? req.body : Buffer.alloc(0);
      if (bytes.length === 0) throw errors.validation('file', 'required');
      if (bytes.length > TRAILER_MAX_BYTES) throw errors.validation('file', 'max');
      const type = sniffVideo(bytes);
      if (!type) throw errors.validation('file', 'format');
      game.videoUrl = null;
      const t: MockTrailer = {
        status: 'ready',
        origin: 'upload',
        sourceUrl: null,
        url: store(bytes, type),
        contentType: type,
        size: bytes.length,
        reason: null,
      };
      TRAILERS.set(game.id, t);
      bump();
      reply.code(201);
      return { trailer: t };
    },
  );

  app.delete<{ Params: { id: string } }>('/admin/games/:id/trailer', async (req) => {
    requireOwner(req);
    const game = findGame(req.params.id);
    if (!game) throw errors.notFound('game');
    game.videoUrl = null;
    TRAILERS.set(game.id, {
      status: 'failed',
      origin: 'found',
      sourceUrl: null,
      url: null,
      contentType: null,
      size: null,
      reason: 'removed',
    });
    bump();
    return { ok: true };
  });

  app.post('/admin/games/trailers/find', async (req) => {
    requireOwner(req);
    const queued: string[] = [];
    for (const game of db.games) {
      const t = trailerOf(game) ?? TRAILERS.get(game.id) ?? null;
      const searchable = !t || (t.origin === 'found' && (t.status === 'failed' || t.status === 'searching'));
      if (game.videoUrl || !searchable || t?.reason === 'removed') continue;
      queued.push(game.id);
      const steam = game.launcher === 'steam' && game.launcherAppId;
      const found: MockTrailer = {
        status: steam ? 'downloading' : 'failed',
        origin: 'found',
        sourceUrl: steam ? `https://cdn.akamai.steamstatic.com/steam/apps/${game.launcherAppId}/movie_max.mp4` : null,
        url: null,
        contentType: null,
        size: null,
        reason: steam ? null : 'notFound',
      };
      TRAILERS.set(game.id, found);
      if (steam) {
        // The "download": a moment later the copy is there.
        setTimeout(() => {
          if (TRAILERS.get(game.id) !== found) return;
          TRAILERS.set(game.id, {
            ...found,
            status: 'ready',
            url: store(SAMPLE, 'video/webm'),
            contentType: 'video/webm',
            size: SAMPLE.length,
          });
          bump();
        }, 1500);
      }
    }
    return { queued: queued.length, gameIds: queued };
  });
}
