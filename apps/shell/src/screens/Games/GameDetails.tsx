/**
 * `/games/:id` — full game page: hero with Play / Close game, description, tags, account-pool note, minimum spec
 * compared against this PC's hardware, install status and catalogue facts. Escape / B returns to the library.
 */
import type { Game, GameInstallStatus, GameMinSpec, HardwareInfo } from '@clubshell/contracts';
import { useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router-dom';
import { Badge, type BadgeTone } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/Skeleton';
import { useGamepad } from '@/hooks/useGamepad';
import { useLocale } from '@/hooks/useLocale';
import { formatDateTime, formatGb, formatRelativeDay } from '@/lib/format';
import { log } from '@/lib/logger';
import { api } from '@/lib/tauri';
import { selectGame, useGamesStore } from '@/store/games';
import { useNotificationsStore } from '@/store/notifications';
import { useThemeStore } from '@/store/theme';
import { categoryLabel } from './Categories';
import { launcherLabelKey } from './GameCard';
import { GameHero, antiCheatLabel } from './GameHero';
import { LaunchOverlay, launchGame } from './LaunchOverlay';

// ---------------------------------------------------------------------------------------------------------------------
// Spec comparison
// ---------------------------------------------------------------------------------------------------------------------

export type SpecVerdict = 'ok' | 'warn' | 'unknown';

export interface SpecComparison {
  cpu: SpecVerdict;
  gpu: SpecVerdict;
  ram: SpecVerdict;
}

/** Largest 3–5 digit model number in a part name (`i7-13700F` → 13700, `RTX 4070` → 4070); `null` when none. */
export function modelNumber(model: string): number | null {
  const nums = (model.match(/\d{3,5}/g) ?? []).map(Number);
  return nums.length > 0 ? Math.max(...nums) : null;
}

/**
 * RAM is compared exactly. CPU/GPU compare model numbers, which orders parts of the same vendor family well enough
 * for a club PC that is normally far above any minimum.
 * ponytail: model-number heuristic; swap in a benchmark table if cross-vendor accuracy ever matters.
 */
export function compareSpec(minSpec: GameMinSpec | null | undefined, hw: HardwareInfo | null): SpecComparison {
  if (!minSpec || !hw) {
    return { cpu: 'unknown', gpu: 'unknown', ram: 'unknown' };
  }
  const byNumber = (required: string, actual: string | undefined): SpecVerdict => {
    const r = modelNumber(required);
    const a = actual ? modelNumber(actual) : null;
    if (r === null || a === null) {
      return 'unknown';
    }
    return a >= r ? 'ok' : 'warn';
  };
  return {
    cpu: byNumber(minSpec.cpu, hw.cpu.model),
    gpu: byNumber(minSpec.gpu, hw.gpu[0]?.model),
    ram: hw.ramMb >= minSpec.ramMb ? 'ok' : 'warn',
  };
}

const VERDICT_TONE: Record<SpecVerdict, BadgeTone> = { ok: 'success', warn: 'danger', unknown: 'muted' };
const VERDICT_KEY: Record<SpecVerdict, string> = {
  ok: 'games.specOk',
  warn: 'games.specWarn',
  unknown: 'games.specUnknown',
};

// ponytail: one process-wide hardware inventory; it never changes while the shell runs.
let hardwarePromise: Promise<HardwareInfo> | null = null;
function loadHardware(): Promise<HardwareInfo> {
  hardwarePromise ??= api.system.hardware().catch((e: unknown) => {
    hardwarePromise = null;
    throw e;
  });
  return hardwarePromise;
}

function BackIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M15 5l-7 7 7 7" />
    </svg>
  );
}

function ramLabel(mb: number, locale: string): string {
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(mb / 1024)} GB`;
}

interface FactProps {
  label: string;
  value: string | null | undefined;
}

function Fact({ label, value }: FactProps): JSX.Element | null {
  if (!value) {
    return null;
  }
  return (
    <div className="flex items-baseline justify-between gap-4 border-b border-text/10 py-2 last:border-b-0">
      <dt className="shrink-0 text-muted">{label}</dt>
      <dd className="truncate text-right font-medium text-text" title={value}>
        {value}
      </dd>
    </div>
  );
}

export interface SpecTableProps {
  game: Game;
  hardware: HardwareInfo | null;
}

/** Minimum spec vs. this PC, row per component with an ok / warn verdict. */
export function SpecTable({ game, hardware }: SpecTableProps): JSX.Element {
  const { t } = useTranslation();
  const { tag } = useLocale();
  const verdict = compareSpec(game.minSpec, hardware);
  const spec = game.minSpec;
  if (!spec) {
    return <p className="text-muted">{t('games.specUnknown')}</p>;
  }
  const rows: { key: keyof SpecComparison; label: string; required: string; actual: string }[] = [
    { key: 'cpu', label: t('games.cpu'), required: spec.cpu, actual: hardware?.cpu.model ?? '—' },
    { key: 'gpu', label: t('games.gpu'), required: spec.gpu, actual: hardware?.gpu[0]?.model ?? '—' },
    {
      key: 'ram',
      label: t('games.ram'),
      required: ramLabel(spec.ramMb, tag),
      actual: hardware ? ramLabel(hardware.ramMb, tag) : '—',
    },
  ];
  return (
    <table className="w-full border-collapse text-base">
      <thead>
        <tr className="text-left text-sm text-muted">
          <th scope="col" className="py-2 pr-3 font-semibold" />
          <th scope="col" className="py-2 pr-3 font-semibold">
            {t('common.required')}
          </th>
          <th scope="col" className="py-2 pr-3 font-semibold">
            {t('games.thisPc')}
          </th>
          <th scope="col" className="py-2 font-semibold">
            {t('common.status')}
          </th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.key} className="border-t border-text/10 align-top">
            <th scope="row" className="py-2 pr-3 text-left font-semibold text-text">
              {r.label}
            </th>
            <td className="py-2 pr-3 text-text/90">{r.required}</td>
            <td className="py-2 pr-3 text-text/90">{r.actual}</td>
            <td className="py-2">
              <Badge tone={VERDICT_TONE[verdict[r.key]]} size="sm" dot>
                {t(VERDICT_KEY[verdict[r.key]])}
              </Badge>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export default function GameDetails(): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const { id = '' } = useParams<{ id: string }>();
  const selector = useMemo(() => selectGame(id), [id]);
  const game = useGamesStore(selector);
  const get = useGamesStore((s) => s.get);
  const select = useGamesStore((s) => s.select);
  const running = useGamesStore((s) => s.running);
  const launching = useGamesStore((s) => s.launching);
  const kill = useGamesStore((s) => s.kill);
  const pushError = useNotificationsStore((s) => s.pushError);
  const animations = useThemeStore((s) => s.theme.animations);
  const [missing, setMissing] = useState(false);
  const [hardware, setHardware] = useState<HardwareInfo | null>(null);
  const [install, setInstall] = useState<GameInstallStatus | null>(null);

  useEffect(() => {
    let active = true;
    setMissing(false);
    document.getElementById('main')?.scrollTo({ top: 0 });
    select(id);
    void get(id).then((g) => {
      if (active && !g) {
        setMissing(true);
      }
    });
    return () => {
      active = false;
    };
  }, [id, get, select]);

  useEffect(() => {
    let active = true;
    loadHardware().then(
      (hw) => {
        if (active) {
          setHardware(hw);
        }
      },
      (e: unknown) => log.debug('sys.hardware failed', e),
    );
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    let active = true;
    setInstall(null);
    if (!game?.installed) {
      return undefined;
    }
    api.games.installStatus(id).then(
      (s) => {
        if (active) {
          setInstall(s);
        }
      },
      (e: unknown) => log.debug('games.installStatus failed', e),
    );
    return () => {
      active = false;
    };
  }, [id, game?.installed]);

  const back = (): void => navigate('/games');
  useGamepad({ onBack: back });

  const isRunning = running.some((r) => r.gameId === id);
  const duration = animations ? 0.25 : 0;

  const closeGame = async (g: Game): Promise<void> => {
    try {
      await kill(g.id);
    } catch (e) {
      pushError(e, t('games.killTitle'));
    }
  };

  return (
    <motion.div
      initial={{ opacity: 0, y: 12 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration, ease: 'easeOut' }}
      className="flex min-h-full w-full flex-col gap-[var(--gap)]"
    >
      <div>
        <Button variant="ghost" size="md" icon={<BackIcon />} onClick={back}>
          {t('games.backToGames')}
        </Button>
      </div>

      {missing ? (
        <div className="glass flex flex-col items-center justify-center gap-4 rounded-xl px-6 py-16 text-center">
          <p className="text-xl font-semibold text-text">{t('errors.notFound')}</p>
          <Button variant="primary" size="lg" onClick={back}>
            {t('games.backToGames')}
          </Button>
        </div>
      ) : (
        <>
          <GameHero
            game={game}
            loading={!game}
            size="lg"
            showDetails={false}
            autoFocus
            running={isRunning}
            launching={launching?.gameId === id}
            onPlay={(g) => void launchGame(g.id)}
            onKill={(g) => void closeGame(g)}
          />

          {game ? (
            <div className="grid grid-cols-1 gap-[var(--gap)] lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
              <section className="glass flex flex-col gap-5 rounded-xl p-[var(--gutter)]">
                <h2 className="text-2xl font-bold text-text">{t('games.description')}</h2>
                <p
                  className={clsx(
                    'whitespace-pre-line text-lg leading-relaxed',
                    game.description ? 'text-text/90' : 'text-muted',
                  )}
                >
                  {game.description || t('games.noDescription')}
                </p>
                {game.tags.length > 0 && (
                  <div className="flex flex-col gap-2">
                    <h3 className="text-sm font-semibold text-muted">{t('games.tags')}</h3>
                    <ul className="flex flex-wrap gap-2" aria-label={t('games.tags')}>
                      {game.tags.map((tag) => (
                        <li key={tag}>
                          <Badge tone="neutral" size="md">
                            {tag}
                          </Badge>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
                {game.requiresAccount && (
                  <div className="flex items-start gap-3 rounded-lg bg-accent/10 p-4 text-text">
                    <Badge tone="accent" size="sm" className="mt-0.5">
                      {t('games.requiresAccount')}
                    </Badge>
                    <p className="text-base">{t('games.requiresAccountHint')}</p>
                  </div>
                )}
                {game.ageRating >= 16 && (
                  <p role="note" className="rounded-lg bg-danger/10 p-4 text-base font-medium text-danger">
                    {t('games.ageWarning', { age: game.ageRating })}
                  </p>
                )}
                <div className="flex flex-col gap-2">
                  <h3 className="text-sm font-semibold text-muted">{t('games.minSpec')}</h3>
                  <SpecTable game={game} hardware={hardware} />
                </div>
              </section>

              <aside className="glass flex flex-col gap-5 rounded-xl p-[var(--gutter)]">
                <h2 className="text-2xl font-bold text-text">{t('games.details')}</h2>
                <dl className="flex flex-col text-base">
                  <Fact label={t('games.launcher')} value={t(launcherLabelKey(game.launcher))} />
                  <Fact label={t('games.category')} value={game.category.map((c) => categoryLabel(t, c)).join(', ')} />
                  <Fact
                    label={t('games.antiCheat')}
                    value={game.antiCheat === 'none' ? t('games.antiCheatNone') : antiCheatLabel(game.antiCheat)}
                  />
                  <Fact label={t('games.size')} value={formatGb(game.sizeGb, locale)} />
                  <Fact label={t('games.version')} value={game.version ?? install?.version ?? null} />
                  <Fact
                    label={t('games.lastPlayed')}
                    value={game.lastPlayedAt ? formatRelativeDay(game.lastPlayedAt, locale) : t('games.neverPlayed')}
                  />
                </dl>
                <div className="flex flex-col gap-2">
                  <h3 className="text-sm font-semibold text-muted">{t('games.installStatus')}</h3>
                  <div className="flex flex-wrap items-center gap-2">
                    <Badge tone={game.installed ? 'success' : 'muted'} dot>
                      {game.installed ? t('games.installed') : t('games.notInstalled')}
                    </Badge>
                    {install && (
                      <Badge tone={install.launcherReady ? 'success' : 'danger'} dot>
                        {install.launcherReady ? t('games.launcherReady') : t('games.launcherNotReady')}
                      </Badge>
                    )}
                  </div>
                  {game.installed && !install && <Skeleton variant="text" lines={2} />}
                  {install && (
                    <dl className="flex flex-col text-sm">
                      <Fact label={t('games.installPath')} value={install.installPath ?? game.installPath ?? null} />
                      <Fact
                        label={t('games.verifiedAt')}
                        value={install.verifiedAt ? formatDateTime(install.verifiedAt, locale) : null}
                      />
                      <Fact
                        label={t('games.size')}
                        value={install.sizeGb > 0 ? formatGb(install.sizeGb, locale) : null}
                      />
                    </dl>
                  )}
                </div>
              </aside>
            </div>
          ) : (
            <div className="grid grid-cols-1 gap-[var(--gap)] lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
              <Skeleton variant="rect" className="h-72 rounded-xl" />
              <Skeleton variant="rect" className="h-72 rounded-xl" />
            </div>
          )}
        </>
      )}

      <LaunchOverlay />
    </motion.div>
  );
}
