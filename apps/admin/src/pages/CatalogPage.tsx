/**
 * Game catalogue of the player shell: order, featured and hidden games. Everything is a draft of `catalog` in the club
 * settings, saved with the save bar.
 */
import { useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import { clubApi, type AdminGame, type ClubSettings } from '@/api';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { useClubSettings } from '@/settings';
import { Button, Input, Note, PageHeader, SaveBar, Section, Table, Toggle } from '@/ui';

const LAUNCHER: Record<string, string> = {
  steam: 'Steam',
  epic: 'Epic Games',
  riot: 'Riot',
  battlenet: 'Battle.net',
  ea: 'EA',
  ubisoft: 'Ubisoft',
  standalone: 'Отдельно',
};

function Arrow({ up }: { up: boolean }): JSX.Element {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.8} className="h-4 w-4">
      <path d={up ? 'M6 15l6-6 6 6' : 'M6 9l6 6 6-6'} strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

/** Full order: the saved ids that still exist, then games the order does not mention yet, in server order. */
function fullOrder(order: string[], games: AdminGame[]): string[] {
  const ids = new Set(games.map((g) => g.id));
  const kept = order.filter((id) => ids.has(id));
  const seen = new Set(kept);
  return [...kept, ...games.map((g) => g.id).filter((id) => !seen.has(id))];
}

export default function CatalogPage(): JSX.Element {
  const [games, setGames] = useState<AdminGame[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const s = useClubSettings();

  useEffect(() => {
    clubApi
      .games()
      .then((r) => setGames(r.items))
      .catch((e: unknown) => setError(describe(e)));
  }, []);

  const catalog: ClubSettings['catalog'] = s.draft?.catalog ?? { order: [], hidden: [], featured: [] };
  const order = useMemo(() => fullOrder(catalog.order, games), [catalog.order, games]);
  const byId = useMemo(() => new Map(games.map((g) => [g.id, g])), [games]);
  const q = query.trim().toLowerCase();
  const rows = order
    .map((id) => byId.get(id))
    .filter((g): g is AdminGame => g !== undefined && (!q || g.title.toLowerCase().includes(q)));

  const setCatalog = (next: Partial<ClubSettings['catalog']>): void => s.set('catalog', { ...catalog, ...next });
  const toggleIn = (list: string[], id: string, on: boolean): string[] =>
    on ? [...list.filter((x) => x !== id), id] : list.filter((x) => x !== id);
  const move = (id: string, delta: number): void => {
    const i = order.indexOf(id);
    const j = i + delta;
    if (i < 0 || j < 0 || j >= order.length) return;
    const next = [...order];
    [next[i], next[j]] = [next[j] as string, next[i] as string];
    setCatalog({ order: next });
  };

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title={t('Каталог игр')}
        actions={
          <Input
            type="search"
            className="w-64"
            placeholder={t('Поиск')}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        }
      />
      {error && <Note note={{ text: error, tone: 'err' }} />}
      {s.error && <Note note={{ text: s.error, tone: 'err' }} />}

      <Section bodyClassName="p-2">
        <Table
          rows={rows}
          rowKey={(g) => g.id}
          empty={t('Игры не найдены')}
          columns={[
            {
              key: 'pos',
              title: t('№'),
              width: '3rem',
              num: true,
              render: (g) => <span className="text-muted">{order.indexOf(g.id) + 1}</span>,
            },
            {
              key: 'cover',
              title: t('Обложка'),
              width: '4rem',
              render: (g) =>
                g.coverUrl ? (
                  <img
                    src={g.coverUrl}
                    alt=""
                    loading="lazy"
                    className="h-12 w-8 rounded-[4px] border border-line object-cover"
                  />
                ) : (
                  <span className="block h-12 w-8 rounded-[4px] border border-line bg-bg" />
                ),
            },
            {
              key: 'title',
              title: t('Название'),
              render: (g) => (
                <span className={clsx('font-medium', catalog.hidden.includes(g.id) && 'text-muted')}>{g.title}</span>
              ),
            },
            {
              key: 'launcher',
              title: t('Лаунчер'),
              render: (g) => <span className="text-muted">{t(LAUNCHER[g.launcher] ?? g.launcher)}</span>,
            },
            {
              key: 'installed',
              title: t('Установлена'),
              render: (g) =>
                g.installed ? (
                  <span className="text-success">{t('Да')}</span>
                ) : (
                  <span className="text-muted">{t('Нет')}</span>
                ),
            },
            {
              key: 'featured',
              title: t('Рекомендуем'),
              width: '8rem',
              render: (g) => (
                <Toggle
                  checked={catalog.featured.includes(g.id)}
                  onChange={(v) => setCatalog({ featured: toggleIn(catalog.featured, g.id, v) })}
                />
              ),
            },
            {
              key: 'hidden',
              title: t('Скрыть'),
              width: '6rem',
              render: (g) => (
                <Toggle
                  checked={catalog.hidden.includes(g.id)}
                  onChange={(v) => setCatalog({ hidden: toggleIn(catalog.hidden, g.id, v) })}
                />
              ),
            },
            {
              key: 'order',
              title: t('Порядок'),
              width: '6rem',
              render: (g) => {
                const i = order.indexOf(g.id);
                return (
                  <div className="flex gap-1">
                    <Button
                      variant="ghost"
                      size="sm"
                      aria-label={t('Выше')}
                      disabled={!s.draft || i <= 0}
                      onClick={() => move(g.id, -1)}
                    >
                      <Arrow up />
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      aria-label={t('Ниже')}
                      disabled={!s.draft || i >= order.length - 1}
                      onClick={() => move(g.id, 1)}
                    >
                      <Arrow up={false} />
                    </Button>
                  </div>
                );
              },
            },
          ]}
        />
      </Section>

      <SaveBar
        dirty={s.dirty}
        saving={s.saving}
        label={t('Каталог изменён')}
        onReset={s.reset}
        onSave={() => void s.save()}
      />
    </div>
  );
}
