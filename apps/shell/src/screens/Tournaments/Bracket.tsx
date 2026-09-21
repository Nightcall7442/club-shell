import { useMemo } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import type { Bracket as BracketData, BracketMatch } from '@clubshell/contracts';

export interface BracketProps {
  bracket: BracketData;
  meId: string | null;
  /** Display name of a player id (leaderboard names, the current user, …). */
  nameOf: (userId: string) => string;
  className?: string;
}

const MATCH_W = 232;
const MATCH_H = 92;
const GAP_X = 64;
const GAP_Y = 18;
const HEADER_H = 40;

interface Placed {
  match: BracketMatch;
  round: number;
  index: number;
  x: number;
  y: number;
}

/** Round title: `Final`, `Semifinal`, `Quarterfinal`, otherwise `Round N`. */
export function roundLabel(
  index: number,
  total: number,
  t: (key: string, vars?: Record<string, unknown>) => string,
): string {
  const fromEnd = total - 1 - index;
  if (fromEnd === 0) {
    return t('tournaments.final');
  }
  if (fromEnd === 1) {
    return t('tournaments.semifinal');
  }
  if (fromEnd === 2) {
    return t('tournaments.quarterfinal');
  }
  return t('tournaments.round', { n: index + 1 });
}

/** Classic single-elimination layout: each round's matches sit centred between the two feeding matches. */
export function layoutBracket(bracket: BracketData): { placed: Placed[]; width: number; height: number } {
  const slot0 = MATCH_H + GAP_Y;
  const placed: Placed[] = [];
  let height = HEADER_H;
  bracket.rounds.forEach((round, r) => {
    const slot = slot0 * 2 ** r;
    round.matches.forEach((match, i) => {
      placed.push({ match, round: r, index: i, x: r * (MATCH_W + GAP_X), y: HEADER_H + i * slot + (slot - slot0) / 2 });
    });
    height = Math.max(height, HEADER_H + round.matches.length * slot - GAP_Y);
  });
  const width = Math.max(0, bracket.rounds.length * (MATCH_W + GAP_X) - GAP_X);
  return { placed, width, height };
}

function splitScore(score: string | null | undefined): [string, string] {
  if (!score) {
    return ['', ''];
  }
  const parts = score.split(/[:–-]/).map((p) => p.trim());
  return [parts[0] ?? '', parts[1] ?? ''];
}

function Player({
  id,
  score,
  winner,
  decided,
  mine,
  nameOf,
  tbd,
}: {
  id: string | null | undefined;
  score: string;
  winner: boolean;
  decided: boolean;
  mine: boolean;
  nameOf: (id: string) => string;
  tbd: string;
}): JSX.Element {
  return (
    <div
      className={clsx(
        'flex h-1/2 items-center gap-2 px-3',
        winner && 'bg-primary/20 text-text',
        decided && !winner && 'text-muted',
        !decided && 'text-text',
        mine && 'shadow-[inset_3px_0_0_rgb(var(--c-accent))]',
      )}
    >
      <span
        className={clsx(
          'min-w-0 flex-1 truncate text-sm',
          winner ? 'font-bold' : 'font-medium',
          !id && 'italic text-muted',
        )}
      >
        {id ? nameOf(id) : tbd}
      </span>
      {score !== '' && (
        <span className={clsx('tnum text-sm', winner ? 'font-bold text-primary' : 'text-muted')}>{score}</span>
      )}
      {winner && (
        <svg viewBox="0 0 24 24" fill="currentColor" className="h-4 w-4 text-primary" aria-hidden="true">
          <path d="M5 3h14v2h2v5a5 5 0 0 1-4.6 4.98A6 6 0 0 1 13 17.9V19h3v2H8v-2h3v-1.1a6 6 0 0 1-3.4-2.92A5 5 0 0 1 3 10V5h2zm0 4v3a3 3 0 0 0 2.06 2.85A6 6 0 0 1 7 12V7zm14 0h-2v5c0 .3-.02.58-.06.85A3 3 0 0 0 19 10z" />
        </svg>
      )}
    </div>
  );
}

/** Single-elimination bracket: absolutely placed match cards with SVG elbow connectors; scrolls inside its box. */
export function Bracket({ bracket, meId, nameOf, className }: BracketProps): JSX.Element {
  const { t } = useTranslation();
  const { placed, width, height } = useMemo(() => layoutBracket(bracket), [bracket]);
  const total = bracket.rounds.length;
  const byPos = useMemo(() => new Map(placed.map((p) => [`${p.round}:${p.index}`, p])), [placed]);

  if (total === 0) {
    return <p className={clsx('py-8 text-center text-muted', className)}>{t('tournaments.noBracket')}</p>;
  }

  return (
    <div
      className={clsx('themed-scrollbar overflow-auto', className)}
      role="group"
      aria-label={t('tournaments.bracket')}
    >
      <div className="relative" style={{ width, height, minWidth: width }}>
        <svg className="pointer-events-none absolute inset-0" width={width} height={height} aria-hidden="true">
          {placed.map((p) => {
            const next = byPos.get(`${p.round + 1}:${Math.floor(p.index / 2)}`);
            if (!next) {
              return null;
            }
            const x1 = p.x + MATCH_W;
            const y1 = p.y + MATCH_H / 2;
            const x2 = next.x;
            const y2 = next.y + MATCH_H / 2;
            const mx = x1 + GAP_X / 2;
            const won = Boolean(p.match.winner);
            return (
              <path
                key={p.match.id}
                d={`M${x1},${y1} H${mx} V${y2} H${x2}`}
                fill="none"
                strokeWidth={2}
                className={won ? 'stroke-primary' : 'stroke-text/20'}
                strokeLinecap="round"
              />
            );
          })}
        </svg>
        {bracket.rounds.map((round, r) => (
          <div
            key={r}
            className="absolute top-0 text-center text-xs font-semibold uppercase tracking-widest text-muted"
            style={{ left: r * (MATCH_W + GAP_X), width: MATCH_W }}
          >
            {roundLabel(r, total, t)} · {t('tournaments.matches')} {round.matches.length}
          </div>
        ))}
        {placed.map((p) => {
          const m = p.match;
          const decided = Boolean(m.winner);
          const [sa, sb] = splitScore(m.score);
          const mineA = meId !== null && m.a === meId;
          const mineB = meId !== null && m.b === meId;
          const bye = (m.a && !m.b) || (!m.a && m.b);
          return (
            <div
              key={m.id}
              data-nav="true"
              tabIndex={0}
              role="group"
              aria-label={`${roundLabel(p.round, total, t)}: ${m.a ? nameOf(m.a) : t('tournaments.tbd')} ${t('tournaments.vs')} ${m.b ? nameOf(m.b) : t('tournaments.tbd')}${m.score ? ` ${m.score}` : ''}`}
              style={{ left: p.x, top: p.y, width: MATCH_W, height: MATCH_H }}
              className={clsx(
                'focus-ring glass absolute flex flex-col divide-y divide-text/10 overflow-hidden rounded-xl',
                decided ? 'border-primary/40' : 'border-text/10',
                (mineA || mineB) && !decided && 'shadow-[var(--shadow-glow)]',
              )}
            >
              <Player
                id={m.a}
                score={sa}
                winner={decided && m.winner === m.a}
                decided={decided}
                mine={mineA}
                nameOf={nameOf}
                tbd={bye && !m.a ? t('tournaments.bye') : t('tournaments.tbd')}
              />
              <Player
                id={m.b}
                score={sb}
                winner={decided && m.winner === m.b}
                decided={decided}
                mine={mineB}
                nameOf={nameOf}
                tbd={bye && !m.b ? t('tournaments.bye') : t('tournaments.tbd')}
              />
            </div>
          );
        })}
      </div>
    </div>
  );
}
