/**
 * The console shell: PIN sign-in, then a top bar (club, clock, hall usage, shift, language, staff) and a sidebar of
 * sections the way Senet lays out its club console — the counter first, the owner's configuration below it. Cashiers
 * see the counter, shift, clients and stock; owners see everything. The section lives in the URL hash (`#/tariffs`).
 */
import { lazy, Suspense, useCallback, useEffect, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import { adminApi, clubApi, hasToken, setToken, type Shift, type StaffMember } from '@/api';
import { describe } from '@/errors';
import { LANGS, dateLocale, setLang, t, useLang } from '@/i18n';
import { Button } from '@/ui';

const MapPage = lazy(() => import('@/pages/MapPage'));
const ShiftPage = lazy(() => import('@/pages/ShiftPage'));
const ClientsPage = lazy(() => import('@/pages/ClientsPage'));
const PricingPage = lazy(() => import('@/pages/PricingPage'));
const HallPage = lazy(() => import('@/pages/HallPage'));
const ShopPage = lazy(() => import('@/pages/ShopPage'));
const CatalogPage = lazy(() => import('@/pages/CatalogPage'));
const ClubPage = lazy(() => import('@/pages/ClubPage'));
const AutomationPage = lazy(() => import('@/pages/AutomationPage'));
const IntegrationsPage = lazy(() => import('@/pages/IntegrationsPage'));
const ReportsPage = lazy(() => import('@/pages/ReportsPage'));
const ControlPage = lazy(() => import('@/pages/ControlPage'));
const NetworkPage = lazy(() => import('@/pages/NetworkPage'));
const InsightsPage = lazy(() => import('@/pages/InsightsPage'));
const HealthPage = lazy(() => import('@/pages/HealthPage'));
const StaffPage = lazy(() => import('@/pages/StaffPage'));

interface SectionDef {
  id: string;
  title: string;
  ownerOnly: boolean;
  icon: ReactNode;
  /** Pages get whether the signed-in staff member is the owner (for owner-only parts inside shared pages). */
  page: React.LazyExoticComponent<(props: { isOwner?: boolean }) => JSX.Element>;
}

const svg = (d: string): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth={1.6}
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d={d} />
  </svg>
);

/** Grouped like the work: the counter, then the club's setup, then the business. */
const GROUPS: { title: string; items: SectionDef[] }[] = [
  {
    title: 'Касса',
    items: [
      {
        id: 'map',
        title: 'Карта',
        ownerOnly: false,
        icon: svg('M3 4h7v7H3zM14 4h7v7h-7zM3 15h7v5H3zM14 15h7v5h-7z'),
        page: MapPage,
      },
      {
        id: 'shift',
        title: 'Смена',
        ownerOnly: false,
        icon: svg('M12 7v5l3 2M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0z'),
        page: ShiftPage,
      },
      {
        id: 'clients',
        title: 'Клиенты',
        ownerOnly: false,
        icon: svg(
          'M16 20v-1a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v1M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM22 20v-1a4 4 0 0 0-3-3.9M16 3.1a4 4 0 0 1 0 7.8',
        ),
        page: ClientsPage,
      },
      {
        id: 'shop',
        title: 'Магазин и склад',
        ownerOnly: false,
        icon: svg('M4 7h16l-1.2 11a2 2 0 0 1-2 1.8H7.2a2 2 0 0 1-2-1.8L4 7zM8 10V6a4 4 0 0 1 8 0v4'),
        page: ShopPage,
      },
      {
        id: 'health',
        title: 'Состояние ПК',
        ownerOnly: false,
        icon: svg('M3 12h4l3-8 4 16 3-8h4'),
        page: HealthPage,
      },
    ],
  },
  {
    title: 'Настройка клуба',
    items: [
      {
        id: 'pricing',
        title: 'Тарифы и цены',
        ownerOnly: true,
        icon: svg('M20.6 13.4 13.4 20.6a2 2 0 0 1-2.8 0L3 13V3h10l7.6 7.6a2 2 0 0 1 0 2.8zM7.5 7.5h.01'),
        page: PricingPage,
      },
      {
        id: 'hall',
        title: 'Зал и устройства',
        ownerOnly: true,
        icon: svg('M3 5h18v11H3zM8 20h8M12 16v4'),
        page: HallPage,
      },
      {
        id: 'catalog',
        title: 'Игры',
        ownerOnly: true,
        icon: svg(
          'M6 8h12a4 4 0 0 1 4 4v3a3 3 0 0 1-5.4 1.8L15 15H9l-1.6 1.8A3 3 0 0 1 2 15v-3a4 4 0 0 1 4-4zM7 11v3M5.5 12.5h3',
        ),
        page: CatalogPage,
      },
      {
        id: 'club',
        title: 'Экран игрока',
        ownerOnly: true,
        icon: svg('M12 3a9 9 0 1 0 9 9M12 3v9l6-6'),
        page: ClubPage,
      },
      {
        id: 'automation',
        title: 'Автоматизация',
        ownerOnly: true,
        icon: svg('M13 2 3 14h9l-1 8 10-12h-9l1-8z'),
        page: AutomationPage,
      },
      {
        id: 'integrations',
        title: 'Уведомления и API',
        ownerOnly: true,
        icon: svg('M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0'),
        page: IntegrationsPage,
      },
    ],
  },
  {
    title: 'Бизнес',
    items: [
      {
        id: 'insights',
        title: 'Подсказки',
        ownerOnly: true,
        icon: svg('M9 18h6M10 22h4M12 2a7 7 0 0 0-4 12.7V17h8v-2.3A7 7 0 0 0 12 2z'),
        page: InsightsPage,
      },
      {
        id: 'network',
        title: 'Сеть клубов',
        ownerOnly: true,
        icon: svg('M12 3v6M5 21v-6h14v6M12 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM5 15v-3h14v3'),
        page: NetworkPage,
      },
      { id: 'reports', title: 'Отчёты', ownerOnly: true, icon: svg('M3 3v18h18M7 15l4-4 3 3 5-6'), page: ReportsPage },
      {
        id: 'control',
        title: 'Контроль',
        ownerOnly: true,
        icon: svg('M12 3 4 6v6c0 5 3.4 8.5 8 9 4.6-.5 8-4 8-9V6l-8-3zM9 12l2 2 4-4'),
        page: ControlPage,
      },
      {
        id: 'staff',
        title: 'Персонал',
        ownerOnly: true,
        icon: svg('M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM4 21a8 8 0 0 1 16 0'),
        page: StaffPage,
      },
    ],
  },
];

const ALL = GROUPS.flatMap((g) => g.items);

function useHashSection(): [string, (id: string) => void] {
  const read = (): string => window.location.hash.replace(/^#\/?/, '') || 'map';
  const [id, setId] = useState(read);
  useEffect(() => {
    const on = (): void => setId(read());
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  return [id, (next) => (window.location.hash = `/${next}`)];
}

// ---------------------------------------------------------------------------------------------------------------------
// Sign-in
// ---------------------------------------------------------------------------------------------------------------------

function Login({ onDone }: { onDone: (staff: StaffMember, shift: Shift | null) => void }): JSX.Element {
  useLang();
  const [pin, setPin] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (value: string): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const r = await clubApi.login(value);
      setToken(r.token);
      onDone(r.staff, r.shift);
    } catch (e) {
      setError(describe(e));
      setPin('');
    } finally {
      setBusy(false);
    }
  };

  const press = (d: string): void => {
    if (busy) return;
    const next = (pin + d).slice(0, 8);
    setPin(next);
  };

  useEffect(() => {
    const on = (e: KeyboardEvent): void => {
      if (/^\d$/.test(e.key)) press(e.key);
      else if (e.key === 'Backspace') setPin((p) => p.slice(0, -1));
      else if (e.key === 'Enter' && pin.length >= 4) void submit(pin);
    };
    window.addEventListener('keydown', on);
    return () => window.removeEventListener('keydown', on);
  });

  return (
    <div className="flex h-screen items-center justify-center p-6">
      <div className="panel flex w-[22rem] flex-col items-center gap-6 p-8">
        <div className="flex items-center gap-3">
          <span aria-hidden="true" className="h-6 w-6 rotate-45 border border-accent/70" />
          <span className="font-display text-lg tracking-tight">ClubShell</span>
        </div>
        <div className="flex flex-col items-center gap-2">
          <span className="label">{t('Введите PIN')}</span>
          <div className="flex h-10 items-center gap-2" aria-live="polite">
            {Array.from({ length: Math.max(4, pin.length) }, (_, i) => (
              <span
                key={i}
                className={clsx(
                  'h-3 w-3 rounded-full border',
                  i < pin.length ? 'border-accent bg-accent' : 'border-line',
                )}
              />
            ))}
          </div>
        </div>
        <div className="grid w-full grid-cols-3 gap-2">
          {['1', '2', '3', '4', '5', '6', '7', '8', '9'].map((d) => (
            <button
              key={d}
              type="button"
              className="focus-ring choice num-dot h-14 rounded-md text-2xl"
              onClick={() => press(d)}
            >
              {d}
            </button>
          ))}
          <button
            type="button"
            className="focus-ring h-14 rounded-md text-sm text-muted hover:text-text"
            onClick={() => setPin('')}
          >
            {t('Сброс')}
          </button>
          <button
            type="button"
            className="focus-ring choice num-dot h-14 rounded-md text-2xl"
            onClick={() => press('0')}
          >
            0
          </button>
          <button
            type="button"
            className="focus-ring h-14 rounded-md text-sm text-muted hover:text-text"
            aria-label={t('Стереть')}
            onClick={() => setPin((p) => p.slice(0, -1))}
          >
            ⌫
          </button>
        </div>
        <Button variant="primary" className="w-full" disabled={pin.length < 4 || busy} onClick={() => void submit(pin)}>
          {t('Войти')}
        </Button>
        {error && <p className="text-center text-sm text-danger">{error}</p>}
        <p className="text-center text-xs text-muted">{t('Демо: владелец 0000, кассир 1111')}</p>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Shell
// ---------------------------------------------------------------------------------------------------------------------

export function App(): JSX.Element {
  const lang = useLang();
  const [staff, setStaff] = useState<StaffMember | null>(null);
  const [shift, setShift] = useState<Shift | null>(null);
  const [checking, setChecking] = useState(hasToken());
  const [section, go] = useHashSection();
  const [usage, setUsage] = useState<{ busy: number; total: number } | null>(null);
  const [now, setNow] = useState(new Date());

  useEffect(() => {
    if (!hasToken()) return;
    clubApi
      .me()
      .then((r) => {
        setStaff(r.staff);
        setShift(r.shift);
      })
      .catch(() => setToken(null))
      .finally(() => setChecking(false));
  }, []);

  useEffect(() => {
    const out = (): void => setStaff(null);
    window.addEventListener('admin:signed-out', out);
    return () => window.removeEventListener('admin:signed-out', out);
  }, []);

  const refreshTop = useCallback(async () => {
    try {
      const o = await adminApi.overview();
      setUsage({ busy: o.club.total - o.club.free, total: o.club.total });
      const me = await clubApi.me();
      setShift(me.shift);
    } catch {
      // the pages show their own errors
    }
  }, []);

  useEffect(() => {
    if (!staff) return undefined;
    void refreshTop();
    const a = setInterval(() => void refreshTop(), 5000);
    const b = setInterval(() => setNow(new Date()), 1000);
    return () => {
      clearInterval(a);
      clearInterval(b);
    };
  }, [staff, refreshTop]);

  if (checking) return <div className="h-screen" />;
  if (!staff)
    return (
      <Login
        onDone={(s, sh) => {
          setStaff(s);
          setShift(sh);
        }}
      />
    );

  const allowed = ALL.filter((s) => !s.ownerOnly || staff.role === 'owner');
  const current = allowed.find((s) => s.id === section) ?? allowed[0];
  const Page = current?.page ?? MapPage;
  const locale = dateLocale();

  return (
    <div className="grid h-screen grid-cols-[15rem_minmax(0,1fr)] grid-rows-[4rem_minmax(0,1fr)]">
      {/* Brand cell (top left), like Senet's red block — here the club mark in the accent */}
      <div className="flex items-center gap-3 border-b border-r border-line px-5">
        <span aria-hidden="true" className="h-5 w-5 shrink-0 rotate-45 border border-accent/70" />
        <div className="min-w-0 leading-tight">
          <div className="truncate font-display text-sm tracking-tight">CyberArena</div>
          <div className="label">{staff.role === 'owner' ? t('Владелец') : t('Касса')}</div>
        </div>
      </div>

      {/* Top bar */}
      <header className="flex min-w-0 items-center gap-4 border-b border-line px-6 xl:gap-6">
        <div className="flex items-baseline gap-3">
          <span className="num-dot text-2xl leading-none">
            {now.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })}
          </span>
          <span className="hidden whitespace-nowrap text-sm text-muted xl:inline">
            {now.toLocaleDateString(locale, { weekday: 'long', day: 'numeric', month: 'long' })}
          </span>
        </div>
        <span className="h-8 w-px bg-line" />
        <button
          type="button"
          onClick={() => go('shift')}
          className="focus-ring flex min-w-0 items-center gap-2 whitespace-nowrap rounded-md px-2 py-1 text-sm hover:bg-white/[0.04]"
        >
          <span className={clsx('h-2 w-2 shrink-0 rounded-full', shift ? 'bg-success' : 'bg-danger')} />
          {shift ? t('Смена открыта · {name}', { name: shift.staffName }) : t('Смена не открыта')}
        </button>
        <div className="ml-auto flex items-center gap-5">
          {usage && (
            <div className="flex items-baseline gap-3">
              <span className="label hidden whitespace-nowrap xl:inline">{t('Загрузка зала')}</span>
              <span className="num-dot text-2xl leading-none">
                <span className="text-accent">{String(usage.busy).padStart(2, '0')}</span>
                <span className="text-muted">/{String(usage.total).padStart(2, '0')}</span>
              </span>
            </div>
          )}
          <span className="h-8 w-px bg-line" />
          <div className="flex rounded-md border border-line p-0.5">
            {LANGS.map((l) => (
              <button
                key={l}
                type="button"
                aria-pressed={l === lang}
                onClick={() => setLang(l)}
                className={clsx(
                  'focus-ring h-7 rounded px-2 font-mono text-[0.68rem] uppercase tracking-[0.12em]',
                  l === lang ? 'bg-accent/15 text-accent' : 'text-muted hover:text-text',
                )}
              >
                {l}
              </button>
            ))}
          </div>
          <div className="text-right leading-tight">
            <div className="text-sm">{staff.name}</div>
            <button
              type="button"
              className="focus-ring label hover:text-text"
              onClick={() => {
                setToken(null);
                setStaff(null);
              }}
            >
              {t('Выйти')}
            </button>
          </div>
        </div>
      </header>

      {/* Sidebar */}
      <nav aria-label={t('Разделы')} className="flex flex-col gap-5 overflow-y-auto border-r border-line py-4">
        {GROUPS.map((g) => {
          const items = g.items.filter((s) => !s.ownerOnly || staff.role === 'owner');
          if (items.length === 0) return null;
          return (
            <div key={g.title} className="flex flex-col">
              <span className="label px-5 pb-2">{t(g.title)}</span>
              {items.map((s) => {
                const active = s.id === current?.id;
                return (
                  <button
                    key={s.id}
                    type="button"
                    onClick={() => go(s.id)}
                    aria-current={active ? 'page' : undefined}
                    className={clsx(
                      'focus-ring flex h-11 items-center gap-3 border-l-2 px-5 text-left text-sm transition-colors [&>svg]:h-5 [&>svg]:w-5 [&>svg]:shrink-0',
                      active
                        ? 'border-accent bg-accent/[0.08] text-text'
                        : 'border-transparent text-muted hover:bg-white/[0.03] hover:text-text',
                    )}
                  >
                    {s.icon}
                    <span className="truncate">{t(s.title)}</span>
                  </button>
                );
              })}
            </div>
          );
        })}
      </nav>

      <main className="min-h-0 overflow-y-auto p-6">
        <Suspense fallback={null}>
          <Page key={`${current?.id}-${lang}`} isOwner={staff.role === 'owner'} />
        </Suspense>
      </main>
    </div>
  );
}

export default App;
