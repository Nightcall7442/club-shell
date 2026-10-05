/**
 * Game catalogue of the player shell: the club's games (add, edit, delete — saved at once), and their order, featured
 * and hidden marks — a draft of `catalog` in the club settings, saved with the save bar.
 */
import { useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import { clubApi, type AdminGame, type ClubSettings, type GameInput } from '@/api';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { CheckIcon, ChevronDownIcon, GamepadIcon, SearchIcon } from '@/icons';
import { useClubSettings } from '@/settings';
import { Button, Chip, Field, Input, inputCls, Note, PageHeader, SaveBar, Section, Table, Toggle } from '@/ui';
import { FieldGroup, OwnerPage } from './ownerKit';

const LAUNCHER: Record<string, string> = {
  steam: 'Steam',
  epic: 'Epic Games',
  riot: 'Riot',
  battlenet: 'Battle.net',
  battleNet: 'Battle.net',
  exe: 'Отдельно',
  ea: 'EA',
  ubisoft: 'Ubisoft',
  standalone: 'Отдельно',
};

/** Launchers the owner can pick for a game, in the order of the form. */
const LAUNCHERS: { key: string; label: string }[] = [
  { key: 'exe', label: 'Программа (.exe)' },
  { key: 'steam', label: 'Steam' },
  { key: 'epic', label: 'Epic Games' },
  { key: 'riot', label: 'Riot' },
  { key: 'battleNet', label: 'Battle.net' },
  { key: 'ea', label: 'EA' },
  { key: 'ubisoft', label: 'Ubisoft' },
];

/** Category keys the player shell names (its `games.cat.*`), with the console's labels. */
const CATEGORIES: { key: string; label: string }[] = [
  { key: 'shooter', label: 'Шутер' },
  { key: 'moba', label: 'MOBA' },
  { key: 'battleRoyale', label: 'Королевская битва' },
  { key: 'action', label: 'Экшен' },
  { key: 'strategy', label: 'Стратегия' },
  { key: 'racing', label: 'Гонки' },
  { key: 'sports', label: 'Спорт' },
  { key: 'rpg', label: 'RPG' },
  { key: 'sandbox', label: 'Песочница' },
  { key: 'survival', label: 'Выживание' },
  { key: 'simulation', label: 'Симулятор' },
  { key: 'fighting', label: 'Файтинг' },
  { key: 'horror', label: 'Хоррор' },
  { key: 'casual', label: 'Казуальные' },
  { key: 'coop', label: 'Кооператив' },
  { key: 'multiplayer', label: 'Мультиплеер' },
  { key: 'singleplayer', label: 'Одиночная' },
  { key: 'competitive', label: 'Соревновательные' },
  { key: 'arcade', label: 'Аркады' },
  { key: 'hero', label: 'Геройские' },
  { key: 'openWorld', label: 'Открытый мир' },
];

const MAX_CATEGORIES = 5;

/** A full Windows path to an .exe: a drive (`G:\`) or a share (`\\nas\games\`), as the server checks it. */
const EXE_PATH = /^(?:[A-Za-z]:\\|\\\\[^\\/:*?"<>|]+\\[^\\/:*?"<>|]+\\)[^/:*?"<>|]*\.exe$/i;

interface GameForm {
  title: string;
  launcher: string;
  exePath: string;
  args: string;
  launcherAppId: string;
  coverUrl: string;
  category: string[];
  description: string;
  videoUrl: string;
}

const EMPTY_FORM: GameForm = {
  title: '',
  launcher: 'exe',
  exePath: '',
  args: '',
  launcherAppId: '',
  coverUrl: '',
  category: [],
  description: '',
  videoUrl: '',
};

/** A trailer link the server takes (https only); whether it is a playable file shows in the preview. */
const VIDEO_URL = /^https:\/\/\S+$/i;

function formOf(g: AdminGame): GameForm {
  const steamCover = g.launcher === 'steam' && g.coverUrl === steamCoverUrl(g.launcherAppId ?? '');
  return {
    title: g.title,
    launcher: g.launcher === 'battlenet' ? 'battleNet' : g.launcher,
    exePath: g.exePath ?? '',
    args: g.args ?? '',
    launcherAppId: g.launcherAppId ?? '',
    // The store art of a Steam game follows its id by itself.
    coverUrl: steamCover ? '' : (g.coverUrl ?? ''),
    category: g.category,
    description: g.description ?? '',
    videoUrl: g.videoUrl ?? '',
  };
}

function steamCoverUrl(appId: string): string {
  return `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/library_600x900.jpg`;
}

/** Explorer's "Copy as path" wraps the path in quotes. */
const unquote = (s: string): string => s.trim().replace(/^"+|"+$/g, '');

/** A Steam store link turns into its app id (`store.steampowered.com/app/730/…` → `730`). */
const steamId = (s: string): string => /store\.steampowered\.com\/app\/(\d+)/i.exec(s)?.[1] ?? s.trim();

/** The form's problem in the console's words, or null when it can be saved. */
function problem(f: GameForm): string | null {
  if (!f.title.trim()) return t('Введите название');
  if (f.launcher === 'exe') {
    if (!EXE_PATH.test(unquote(f.exePath)))
      return t('Укажите полный путь к .exe, например G:\\Games\\CS 1.6\\cstrike.exe');
  } else if (f.launcher === 'steam') {
    if (!/^\d{1,10}$/.test(f.launcherAppId.trim())) return t('Укажите номер игры в Steam');
  } else if (!f.launcherAppId.trim()) {
    return t('Укажите код игры в лаунчере');
  }
  const cover = f.coverUrl.trim();
  if (cover && !/^https?:\/\/\S+$/i.test(cover)) return t('Обложка — ссылка на картинку (https://…)');
  const video = f.videoUrl.trim();
  if (video && !VIDEO_URL.test(video)) return t('Ролик — ссылка https://… на файл .mp4 или .webm');
  return null;
}

function inputOf(f: GameForm): GameInput {
  const exe = f.launcher === 'exe';
  return {
    title: f.title.trim(),
    launcher: f.launcher,
    exePath: exe ? unquote(f.exePath) : null,
    launcherAppId: exe ? null : f.launcherAppId.trim(),
    args: f.args.trim() || null,
    coverUrl: f.coverUrl.trim() || null,
    category: f.category,
    description: f.description.trim() || null,
    videoUrl: f.videoUrl.trim() || null,
  };
}

function Arrow({ up }: { up: boolean }): JSX.Element {
  return <ChevronDownIcon size={16} className={clsx(up && 'rotate-180')} />;
}

/**
 * A game's cover as the catalogue shows it: the picture plain (no art scrims, unlike `GameArt`, which is made for text
 * on top), on the art surface; with no link, or a link that does not load, the blueprint grid and a gamepad.
 */
function Cover({ src, className }: { src: string | null | undefined; className?: string }): JSX.Element {
  const [failed, setFailed] = useState<string | null>(null);
  const shown = src && failed !== src ? src : null;
  return (
    <span
      aria-hidden="true"
      className={clsx('relative block shrink-0 overflow-hidden border border-line bg-art', className)}
    >
      {shown ? (
        <img
          key={shown}
          src={shown}
          alt=""
          loading="lazy"
          decoding="async"
          referrerPolicy="no-referrer"
          onError={() => setFailed(shown)}
          className="h-full w-full object-cover"
        />
      ) : (
        <span className="absolute inset-0 flex items-center justify-center text-muted/70">
          <span className="hud-grid absolute inset-0" />
          <GamepadIcon size={16} className="relative" />
        </span>
      )}
    </span>
  );
}

/** The trailer as the PCs play it (muted, looped); a link that is not a playable file says so before it is saved. */
function TrailerPreview({ url }: { url: string }): JSX.Element {
  const [src, setSrc] = useState(url);
  const [broken, setBroken] = useState(false);

  // A typed link settles first: no request per keystroke.
  useEffect(() => {
    const timer = window.setTimeout(() => {
      setSrc(url);
      setBroken(false);
    }, 500);
    return () => window.clearTimeout(timer);
  }, [url]);

  return (
    <div aria-live="polite">
      {broken ? (
        <span className="text-xs font-medium text-warning">
          {t('Ролик не открывается: нужна прямая ссылка на файл .mp4 или .webm')}
        </span>
      ) : (
        <video
          key={src}
          src={src}
          muted
          loop
          autoPlay
          playsInline
          controls
          preload="metadata"
          aria-label={t('Ролик')}
          onError={() => setBroken(true)}
          className="aspect-video w-56 rounded-md border border-line bg-art object-cover"
        />
      )}
    </div>
  );
}

/** Add or edit one game of the club; saves straight to the server. */
function GameEditor({
  game,
  onDone,
  onCancel,
}: {
  game: AdminGame | null;
  onDone: () => void;
  onCancel: () => void;
}): JSX.Element {
  const [form, setForm] = useState<GameForm>(() => (game ? formOf(game) : EMPTY_FORM));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const set = (p: Partial<GameForm>): void => setForm((f) => ({ ...f, ...p }));
  const issue = problem(form);
  const exe = form.launcher === 'exe';
  const steam = form.launcher === 'steam';
  const cover =
    form.coverUrl.trim() ||
    (steam && /^\d+$/.test(form.launcherAppId.trim()) ? steamCoverUrl(form.launcherAppId.trim()) : '');
  const chips = [
    ...CATEGORIES,
    ...form.category.filter((c) => !CATEGORIES.some((k) => k.key === c)).map((c) => ({ key: c, label: c })),
  ];

  const run = async (action: () => Promise<unknown>): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      await action();
      onDone();
    } catch (e) {
      setError(describe(e));
    } finally {
      setBusy(false);
      setConfirmDelete(false);
    }
  };

  const save = (): Promise<void> =>
    run(() => (game ? clubApi.saveGame(game.id, inputOf(form)) : clubApi.addGame(inputOf(form))));

  return (
    <Section variant="solid" title={game ? t('Игра · {title}', { title: game.title }) : t('Новая игра')}>
      <div className="grid grid-cols-1 gap-6 md:grid-cols-[minmax(0,1fr)_8.5rem]">
        <div className="grid grid-cols-1 items-start gap-x-5 gap-y-4 lg:grid-cols-2">
          <Field label={t('Название')}>
            <Input value={form.title} maxLength={100} onChange={(e) => set({ title: e.target.value })} />
          </Field>
          <Field label={t('Как запускается')}>
            <select className={inputCls} value={form.launcher} onChange={(e) => set({ launcher: e.target.value })}>
              {LAUNCHERS.map((l) => (
                <option key={l.key} value={l.key}>
                  {t(l.label)}
                </option>
              ))}
            </select>
          </Field>
          {exe ? (
            <>
              <Field
                label={t('Путь к .exe на игровых ПК')}
                hint={t(
                  'Полный путь, одинаковый на всех ПК. В проводнике: Shift + правый клик по файлу → «Копировать как путь».',
                )}
              >
                <Input
                  className="font-mono"
                  placeholder="G:\Games\Counter Strike 1.6\cstrike.exe"
                  value={form.exePath}
                  onChange={(e) => set({ exePath: e.target.value })}
                />
              </Field>
              <Field label={t('Параметры запуска')} hint={t('Необязательно, например -console -novid')}>
                <Input className="font-mono" value={form.args} onChange={(e) => set({ args: e.target.value })} />
              </Field>
            </>
          ) : (
            <Field
              label={steam ? t('Номер игры в Steam') : t('Код игры в лаунчере')}
              hint={
                steam
                  ? t('Число из ссылки магазина: store.steampowered.com/app/730 → 730. Можно вставить всю ссылку.')
                  : undefined
              }
            >
              <Input
                className="font-mono"
                value={form.launcherAppId}
                onChange={(e) => set({ launcherAppId: steam ? steamId(e.target.value) : e.target.value })}
              />
            </Field>
          )}
          <Field
            label={t('Обложка')}
            hint={
              steam
                ? t('Ссылка на картинку. Пусто — обложка из Steam.')
                : t('Ссылка на картинку (вертикальная, 2:3). Необязательно.')
            }
          >
            <Input placeholder="https://…" value={form.coverUrl} onChange={(e) => set({ coverUrl: e.target.value })} />
          </Field>
          <div className="flex flex-col gap-2">
            <Field
              label={t('Ролик (ссылка на .mp4/.webm)')}
              hint={t(
                'Необязательно. Прямая ссылка на сам файл: ролик без звука идёт за картинкой игры на ПК. Ссылка на YouTube не подойдёт.',
              )}
            >
              <Input
                inputMode="url"
                autoComplete="off"
                spellCheck={false}
                placeholder="https://…/trailer.mp4"
                value={form.videoUrl}
                onChange={(e) => set({ videoUrl: e.target.value })}
              />
            </Field>
            {VIDEO_URL.test(form.videoUrl.trim()) && <TrailerPreview url={form.videoUrl.trim()} />}
          </div>
          <FieldGroup label={t('Категории')} hint={t('До {n}', { n: MAX_CATEGORIES })} className="lg:col-span-2">
            <div className="flex flex-wrap gap-1.5">
              {chips.map((c) => {
                const on = form.category.includes(c.key);
                return (
                  <Chip
                    key={c.key}
                    tone="outlined"
                    pressed={on}
                    onClick={() =>
                      set({
                        category: on
                          ? form.category.filter((x) => x !== c.key)
                          : form.category.length < MAX_CATEGORIES
                            ? [...form.category, c.key]
                            : form.category,
                      })
                    }
                  >
                    {t(c.label)}
                  </Chip>
                );
              })}
            </div>
          </FieldGroup>
          <Field label={t('Описание')} hint={t('Необязательно')} className="lg:col-span-2">
            <textarea
              className={clsx(inputCls, 'h-auto min-h-20 resize-y py-2.5 leading-relaxed')}
              maxLength={1000}
              value={form.description}
              onChange={(e) => set({ description: e.target.value })}
            />
          </Field>
        </div>
        <div className="flex flex-col gap-2">
          <span className="label-sm">{t('Обложка')}</span>
          <Cover src={cover || null} className="aspect-[2/3] w-full rounded-md" />
        </div>
      </div>

      {error && <Note note={{ text: error, tone: 'err' }} />}

      <div className="mt-2 flex flex-wrap items-center justify-between gap-2 border-t border-accent/[0.08] pt-4">
        {game &&
          (confirmDelete ? (
            <span className="flex items-center gap-1">
              <span className="text-sm text-muted">{t('Удалить игру?')}</span>
              <Button
                variant="danger"
                size="sm"
                disabled={busy}
                onClick={() => void run(() => clubApi.deleteGame(game.id))}
              >
                {t('Да')}
              </Button>
              <Button variant="ghost" size="sm" onClick={() => setConfirmDelete(false)}>
                {t('Нет')}
              </Button>
            </span>
          ) : (
            <Button variant="tertiary" disabled={busy} onClick={() => setConfirmDelete(true)}>
              {t('Удалить')}
            </Button>
          ))}
        <span className="ml-auto flex items-center gap-3">
          {issue && <span className="text-xs text-dim">{issue}</span>}
          <Button variant="ghost" disabled={busy} onClick={onCancel}>
            {t('Закрыть')}
          </Button>
          <Button variant="primary" disabled={busy || issue !== null} onClick={() => void save()}>
            {busy ? t('Сохраняем…') : game ? t('Сохранить') : t('Добавить')}
          </Button>
        </span>
      </div>
    </Section>
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
  const [editing, setEditing] = useState<{ id: string; text: string } | null>(null);
  const [savingPaths, setSavingPaths] = useState(false);
  /** The game open in the editor: `new` — a new one. */
  const [gameEdit, setGameEdit] = useState<string | null>(null);
  const s = useClubSettings();

  const savePaths = async (): Promise<void> => {
    if (!editing) return;
    setSavingPaths(true);
    try {
      const paths = editing.text
        .split('\n')
        .map((l) => l.trim())
        .filter(Boolean);
      const r = await clubApi.saveGameSettingsPaths(editing.id, paths);
      setGames((list) => list.map((g) => (g.id === editing.id ? { ...g, settingsPaths: r.settingsPaths } : g)));
      setEditing(null);
    } catch (e) {
      setError(describe(e));
    } finally {
      setSavingPaths(false);
    }
  };

  const load = (): Promise<void> =>
    clubApi
      .games()
      .then((r) => setGames(r.items))
      .catch((e: unknown) => setError(describe(e)));

  useEffect(() => {
    void load();
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
  const openGame = (id: string): void => {
    setEditing(null);
    setGameEdit(id);
  };

  return (
    <OwnerPage>
      <PageHeader
        title={t('Каталог игр')}
        caption={t('Настройка клуба')}
        actions={
          <div className="flex items-center gap-2">
            <span className="relative">
              <SearchIcon
                size={16}
                className="pointer-events-none absolute left-3.5 top-1/2 -translate-y-1/2 text-muted"
              />
              <Input
                type="search"
                className="w-72 pl-10"
                placeholder={t('Поиск')}
                value={query}
                onChange={(e) => setQuery(e.target.value)}
              />
            </span>
            <Button variant="primary" onClick={() => openGame('new')}>
              {t('Добавить игру')}
            </Button>
          </div>
        }
      />
      {error && <Note note={{ text: error, tone: 'err' }} />}
      {s.error && <Note note={{ text: s.error, tone: 'err' }} />}

      {gameEdit && (
        <GameEditor
          key={gameEdit}
          game={gameEdit === 'new' ? null : (byId.get(gameEdit) ?? null)}
          onCancel={() => setGameEdit(null)}
          onDone={() => {
            setGameEdit(null);
            void load();
          }}
        />
      )}

      {editing && (
        <Section
          variant="solid"
          title={t('Настройки игрока · {title}', { title: byId.get(editing.id)?.title ?? '' })}
          actions={
            <div className="flex gap-2">
              <Button variant="ghost" onClick={() => setEditing(null)} disabled={savingPaths}>
                {t('Отменить')}
              </Button>
              <Button variant="primary" onClick={() => void savePaths()} disabled={savingPaths}>
                {savingPaths ? t('Сохраняем…') : t('Сохранить')}
              </Button>
            </div>
          }
        >
          <p className="max-w-[880px] text-[13px] leading-5 text-dim">
            {t(
              'Файлы и папки, где игра хранит бинды, чувствительность и графику игрока. Агент сохраняет их после игры и возвращает игроку на любом ПК. По одному пути в строке; можно {installPath}, %LOCALAPPDATA%, %APPDATA%, %USERPROFILE%.',
            )}
          </p>
          <textarea
            aria-label={t('Пути к настройкам игрока')}
            className={clsx(inputCls, 'h-auto min-h-32 resize-y py-2.5 font-mono leading-relaxed')}
            value={editing.text}
            onChange={(e) => setEditing({ ...editing, text: e.target.value })}
          />
        </Section>
      )}

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
              render: (g) => <Cover src={g.coverUrl} className="h-12 w-8 rounded-[6px]" />,
            },
            {
              key: 'title',
              title: t('Название'),
              render: (g) => (
                <button
                  type="button"
                  className={clsx(
                    'focus-ring -mx-1.5 rounded-md px-1.5 py-1 text-left font-medium hover:bg-text/[0.04]',
                    catalog.hidden.includes(g.id) ? 'text-muted' : 'text-hi',
                  )}
                  title={t('Изменить')}
                  onClick={() => openGame(g.id)}
                >
                  {g.title}
                  {g.exePath && (
                    <span className="mt-0.5 block font-mono text-[11.5px] font-normal text-muted">{g.exePath}</span>
                  )}
                </button>
              ),
            },
            {
              key: 'launcher',
              title: t('Лаунчер'),
              render: (g) => <span className="text-dim">{t(LAUNCHER[g.launcher] ?? g.launcher)}</span>,
            },
            {
              key: 'installed',
              title: t('Установлена'),
              render: (g) =>
                g.installed ? (
                  <span className="inline-flex items-center gap-1.5 text-text">
                    <CheckIcon size={15} strokeWidth={2} className="text-accent" />
                    {t('Да')}
                  </span>
                ) : (
                  <span className="text-muted">{t('Нет')}</span>
                ),
            },
            {
              key: 'settings',
              title: t('Настройки игрока'),
              render: (g) => (
                <button
                  type="button"
                  className="focus-ring -mx-2 inline-flex h-9 items-center gap-1.5 rounded-md px-2 text-[13px] font-medium hover:bg-text/[0.04]"
                  onClick={() => {
                    setGameEdit(null);
                    setEditing({ id: g.id, text: g.settingsPaths.join('\n') });
                  }}
                >
                  {g.settingsPaths.length > 0 ? (
                    <>
                      <CheckIcon size={15} strokeWidth={2} className="text-accent" />
                      <span className="text-accent">{t('Переносятся')}</span>
                    </>
                  ) : (
                    <span className="text-muted">{t('Не заданы')}</span>
                  )}
                </button>
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
              width: '6.5rem',
              render: (g) => {
                const i = order.indexOf(g.id);
                return (
                  <div className="flex gap-1">
                    <Button
                      variant="ghost"
                      size="sm"
                      className="w-9 !px-0"
                      aria-label={t('Выше')}
                      disabled={!s.draft || i <= 0}
                      onClick={() => move(g.id, -1)}
                    >
                      <Arrow up />
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      className="w-9 !px-0"
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
            {
              key: 'edit',
              title: '',
              width: '6.5rem',
              render: (g) => (
                <Button variant="ghost" size="sm" onClick={() => openGame(g.id)}>
                  {t('Изменить')}
                </Button>
              ),
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
    </OwnerPage>
  );
}
