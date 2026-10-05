/**
 * The console shell, variant F «Командный центр»: PIN sign-in, then the club's dimmed wallpaper behind everything, an
 * 88 px rail of sections on the left (the counter first, the owner's configuration and business below it; the staff
 * member and «Выйти» at its foot), and on the right a 44 px header (club, clock, the shift chip, client search,
 * language), the KPI strip on the counter pages (`kpi.tsx`: occupancy, today's money, the drawer with its «Внесение и
 * изъятие» menu, the players' calls with their bell and sound) and the page. Cashiers see the counter, shift, clients
 * and stock; owners see everything; on the owner's setup pages (no strip) the drawer's «±» and the bell sit in the
 * header beside the chip.
 * The section lives in the URL hash (`#/tariffs`, `#/clients/new`). Without an open shift the console asks to open one
 * (`shift.tsx`); "/" jumps to the client search, whose rows top up a client from anywhere or show their PC on the map.
 * The club's name, limits, wallpaper and the signed-in staff member reach the pages through `ClubContext` (`club.ts`).
 */
import {
  lazy,
  Suspense,
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import clsx from 'clsx';
import {
  AdminError,
  adminApi,
  clubApi,
  getClubCode,
  hasToken,
  setClubCode,
  setToken,
  type ClientHit,
  type ClubSettings,
  type StaffMember,
} from '@/api';
import { Wallpaper } from '@/art';
import { AudioUnlockChip, CallsBell, CallsRinger, setCalls } from '@/calls';
import { ClubContext, type ClubState } from '@/club';
import { GlobalSearch } from '@/clientSearch';
import { isTyping, sheetOpen, showPc, signedOut } from '@/desk';
import { describe } from '@/errors';
import { LANGS, currentLang, dateLocale, setLang, t, useLang } from '@/i18n';
import {
  BoltIcon,
  BoxIcon,
  ChartIcon,
  ClockIcon,
  CupIcon,
  DownloadIcon,
  GamepadIcon,
  LogoMark,
  LogoutIcon,
  MapIcon,
  MonitorIcon,
  NetworkIcon,
  PersonIcon,
  PulseIcon,
  ScreenIcon,
  ShieldIcon,
  TagIcon,
  UsersIcon,
  pathIcon,
} from '@/icons';
import { KpiStrip, type HallCounts } from '@/kpi';
import { TopUpSheet } from '@/paybox';
import { useInstall } from '@/pwa';
import { CashMenu, ShiftChip, ShiftProvider } from '@/shift';
import { Button, inputCls } from '@/ui';

/** «3 зоны»: the hall's zone count in the header's caption, with the word's plural form of the console language. */
function zonesWord(n: number): string {
  if (currentLang() !== 'ru') return t(n === 1 ? '{n} зона' : '{n} зоны', { n });
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return t('{n} зона', { n });
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return t('{n} зоны', { n });
  return t('{n} зон', { n });
}

/** How often the counter re-reads the club settings (limits the owner may change meanwhile). */
const SETTINGS_POLL_MS = 60_000;

const MapPage = lazy(() => import('@/pages/MapPage'));
const BarPage = lazy(() => import('@/pages/BarPage'));
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

const BellRuleIcon = pathIcon('M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0', 'BellRuleIcon');

/** Grouped like the work: the counter, then the club's setup, then the business. The first group is «Касса». */
const GROUPS: { title: string; items: SectionDef[] }[] = [
  {
    title: 'Касса',
    items: [
      { id: 'map', title: 'Карта', ownerOnly: false, icon: <MapIcon />, page: MapPage },
      { id: 'bar', title: 'Бар', ownerOnly: false, icon: <CupIcon />, page: BarPage },
      { id: 'shift', title: 'Смена', ownerOnly: false, icon: <ClockIcon />, page: ShiftPage },
      { id: 'clients', title: 'Клиенты', ownerOnly: false, icon: <UsersIcon />, page: ClientsPage },
      { id: 'shop', title: 'Магазин и склад', ownerOnly: false, icon: <BoxIcon />, page: ShopPage },
      { id: 'health', title: 'Состояние ПК', ownerOnly: false, icon: <PulseIcon />, page: HealthPage },
    ],
  },
  {
    title: 'Настройка клуба',
    items: [
      { id: 'pricing', title: 'Тарифы и цены', ownerOnly: true, icon: <TagIcon />, page: PricingPage },
      { id: 'hall', title: 'Зал и устройства', ownerOnly: true, icon: <MonitorIcon />, page: HallPage },
      { id: 'catalog', title: 'Игры', ownerOnly: true, icon: <GamepadIcon />, page: CatalogPage },
      { id: 'club', title: 'Экран игрока', ownerOnly: true, icon: <ScreenIcon />, page: ClubPage },
      { id: 'automation', title: 'Автоматизация', ownerOnly: true, icon: <BoltIcon />, page: AutomationPage },
      {
        id: 'integrations',
        title: 'Уведомления и API',
        ownerOnly: true,
        icon: <BellRuleIcon />,
        page: IntegrationsPage,
      },
    ],
  },
  {
    title: 'Бизнес',
    items: [
      { id: 'network', title: 'Сеть клубов', ownerOnly: true, icon: <NetworkIcon />, page: NetworkPage },
      { id: 'reports', title: 'Отчёты', ownerOnly: true, icon: <ChartIcon />, page: ReportsPage },
      { id: 'control', title: 'Контроль', ownerOnly: true, icon: <ShieldIcon />, page: ControlPage },
      { id: 'staff', title: 'Персонал', ownerOnly: true, icon: <PersonIcon />, page: StaffPage },
    ],
  },
];

const ALL = GROUPS.flatMap((g) => g.items);
/** The counter's pages: these get the KPI strip. */
const COUNTER = new Set((GROUPS[0]?.items ?? []).map((s) => s.id));

function useHashSection(): [string, (id: string) => void] {
  // `#/clients/new` is the Клиенты section; the page reads the rest itself.
  const read = (): string => window.location.hash.replace(/^#\/?/, '').split('/')[0] || 'map';
  const [id, setId] = useState(read);
  useEffect(() => {
    const on = (): void => setId(read());
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  return [id, (next) => (window.location.hash = `/${next}`)];
}

/** «Кассир Азиз» → «АЗ»: the first two letters of the last word, for the rail's avatar. */
function initials(name: string): string {
  const last = name.trim().split(/\s+/).at(-1) ?? '';
  return last.slice(0, 2).toUpperCase() || '·';
}

// ---------------------------------------------------------------------------------------------------------------------
// Sign-in
// ---------------------------------------------------------------------------------------------------------------------

function Login({ onDone }: { onDone: (staff: StaffMember) => void }): JSX.Element {
  useLang();
  const [pin, setPin] = useState('');
  const [clubCode, setClubCodeState] = useState(getClubCode);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (value: string): Promise<void> => {
    setBusy(true);
    setError(null);
    const code = clubCode.trim().toUpperCase();
    try {
      const r = await clubApi.login(value, code || undefined);
      setClubCode(code);
      setToken(r.token);
      onDone(r.staff);
    } catch (e) {
      const needsCode = e instanceof AdminError && e.details?.['field'] === 'clubCode';
      setError(needsCode ? t('Введите код клуба') : describe(e));
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
      // Typing the club code must not feed the PIN.
      if (e.target instanceof HTMLInputElement) return;
      if (/^\d$/.test(e.key)) press(e.key);
      else if (e.key === 'Backspace') setPin((p) => p.slice(0, -1));
      else if (e.key === 'Enter' && pin.length >= 4) void submit(pin);
    };
    window.addEventListener('keydown', on);
    return () => window.removeEventListener('keydown', on);
  });

  const key = 'focus-ring choice h-14 rounded-md font-display text-[22px] font-medium leading-none';
  return (
    <div className="relative isolate flex h-screen items-center justify-center overflow-y-auto p-6">
      {/* Before sign-in the club is not known yet: the console's own wallpaper, let through a little more. */}
      <Wallpaper url={null} strong />
      <div className="panel-solid edge-top relative flex w-[360px] max-w-full flex-col items-center gap-6 rounded-xl px-8 pb-7 pt-8">
        <div className="flex items-center gap-3">
          <LogoMark size={30} />
          <span className="font-display text-lg font-medium tracking-[-0.01em] text-hi">ClubShell</span>
        </div>
        <label className="flex w-full flex-col gap-2">
          <span className="label-sm">{t('Код клуба')}</span>
          <input
            className={clsx(
              inputCls,
              'text-center font-mono uppercase tracking-widest placeholder:font-sans placeholder:normal-case placeholder:tracking-normal',
            )}
            value={clubCode}
            maxLength={12}
            autoComplete="off"
            spellCheck={false}
            placeholder={t('если клубов несколько')}
            onChange={(e) => setClubCodeState(e.target.value.replace(/[^0-9a-z]/gi, ''))}
            onKeyDown={(e) => {
              if (e.key === 'Enter') e.currentTarget.blur();
            }}
          />
        </label>
        <div className="flex flex-col items-center gap-3">
          <span className="label-sm">{t('Введите PIN')}</span>
          <div className="flex h-6 items-center gap-3" aria-live="polite">
            {Array.from({ length: Math.max(4, pin.length) }, (_, i) => (
              <span
                key={i}
                className={clsx(
                  'h-2.5 w-2.5 rounded-full border transition-colors',
                  i < pin.length
                    ? 'border-accent bg-accent shadow-[0_0_10px_rgb(var(--c-accent)/0.8)]'
                    : 'border-muted/70',
                )}
              />
            ))}
          </div>
        </div>
        <div className="grid w-full grid-cols-3 gap-2">
          {['1', '2', '3', '4', '5', '6', '7', '8', '9'].map((d) => (
            <button key={d} type="button" className={key} onClick={() => press(d)}>
              {d}
            </button>
          ))}
          <button type="button" className="btn-ghost focus-ring h-14 rounded-md text-[13px]" onClick={() => setPin('')}>
            {t('Сброс')}
          </button>
          <button type="button" className={key} onClick={() => press('0')}>
            0
          </button>
          <button
            type="button"
            className="btn-ghost focus-ring h-14 rounded-md text-lg"
            aria-label={t('Стереть')}
            onClick={() => setPin((p) => p.slice(0, -1))}
          >
            ⌫
          </button>
        </div>
        <Button
          variant="primary"
          size="lg"
          className="w-full"
          disabled={pin.length < 4 || busy}
          onClick={() => void submit(pin)}
        >
          {t('Войти')}
        </Button>
        {error && <p className="text-center text-sm font-medium text-danger-ink">{error}</p>}
        {/* The demo PINs exist only on the mock and the dev seed; a production build talks to a real club. */}
        {import.meta.env.DEV && (
          <p className="text-center text-xs text-muted">{t('Демо: владелец 0000, кассир 1111')}</p>
        )}
        <InstallButton variant="login" />
      </div>
    </div>
  );
}

/**
 * The browser's install offer as the console's own button; nothing when there is none (installed, or no such browser).
 * `rail` — an icon item at the foot of the rail; `login` — a quiet line under the PIN pad.
 */
function InstallButton({ variant }: { variant: 'rail' | 'login' }): JSX.Element | null {
  const { canInstall, install } = useInstall();
  if (!canInstall) return null;
  const label = t('Установить приложение');
  return variant === 'rail' ? (
    <button
      type="button"
      onClick={() => void install()}
      aria-label={label}
      title={label}
      className="focus-ring flex h-10 w-10 items-center justify-center rounded-md text-muted transition-colors hover:bg-text/[0.04] hover:text-text"
    >
      <DownloadIcon size={18} />
    </button>
  ) : (
    <button
      type="button"
      onClick={() => void install()}
      className="btn-ghost focus-ring flex h-9 items-center justify-center gap-2 rounded-md px-3 text-[13px]"
    >
      <DownloadIcon size={18} />
      {label}
    </button>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Shell
// ---------------------------------------------------------------------------------------------------------------------

/** A 1×28 hairline between the header's blocks. */
function Separator(): JSX.Element {
  return <span aria-hidden="true" className="h-7 w-px shrink-0 bg-accent/[0.12]" />;
}

export function App(): JSX.Element {
  const lang = useLang();
  const [staff, setStaff] = useState<StaffMember | null>(null);
  const [checking, setChecking] = useState(hasToken());
  const [section, go] = useHashSection();
  const [hall, setHall] = useState<HallCounts | null>(null);
  const [now, setNow] = useState(new Date());
  // The club this console is signed in to (a server may hold several): its display name, the limits the seat panel
  // follows and the wallpaper behind the console, from the club settings.
  const [clubName, setClubName] = useState<string | null>(null);
  const [limits, setLimits] = useState<ClubSettings['limits'] | null>(null);
  const [wallpaperUrl, setWallpaperUrl] = useState<string | null>(null);
  // A client being topped up from the header's search (on any page, with or without a PC).
  const [topUpFor, setTopUpFor] = useState<ClientHit | null>(null);
  const search = useRef<HTMLInputElement>(null);
  // The rail scrolls on a short screen (the owner has 16 sections): the active one is always brought into view.
  const railNav = useRef<HTMLElement>(null);
  const railActive = useRef<HTMLButtonElement>(null);
  useLayoutEffect(() => {
    const nav = railNav.current;
    const item = railActive.current;
    if (!nav || !item) return;
    const n = nav.getBoundingClientRect();
    const b = item.getBoundingClientRect();
    // Clear of the rail's faded edges.
    const pad = 12;
    if (b.top < n.top + pad) nav.scrollTop -= n.top + pad - b.top;
    else if (b.bottom > n.bottom - pad) nav.scrollTop += b.bottom - (n.bottom - pad);
  }, [section, staff]);
  // A sheet left open by whoever signed out does not greet the next one.
  useEffect(() => setTopUpFor(null), [staff]);

  useEffect(() => {
    if (!hasToken()) return;
    clubApi
      .me()
      .then((r) => setStaff(r.staff))
      .catch(() => setToken(null))
      .finally(() => setChecking(false));
  }, []);

  // Whoever signs out takes the inbox, the bar's cart and buyer and any pending desk action with them: the next one
  // starts from the next poll, with an empty cart.
  useEffect(() => {
    if (staff) return;
    setCalls(null);
    signedOut();
  }, [staff]);

  useEffect(() => {
    const out = (): void => setStaff(null);
    window.addEventListener('admin:signed-out', out);
    return () => window.removeEventListener('admin:signed-out', out);
  }, []);

  const refreshTop = useCallback(async () => {
    try {
      const o = await adminApi.overview();
      // Occupied: seats with a session (an offline, locked or serviced PC without one is not).
      setHall({
        occupied: o.seats.filter((s) => s.session).length,
        free: o.club.free,
        total: o.club.total,
        zones: new Set(o.seats.map((s) => s.pc.zone)).size,
      });
      // The players' calls ride on the same poll (the map's own poll refreshes them faster).
      setCalls(o.calls);
    } catch {
      // the pages show their own errors
    }
  }, []);

  // "/" — the client search, from anywhere but a field being typed in or a sheet on top. The same key on the Russian
  // layout types "." (`code` Slash), so the key counts, not the character.
  useEffect(() => {
    if (!staff) return undefined;
    const on = (e: KeyboardEvent): void => {
      const slash = e.key === '/' || (e.code === 'Slash' && !e.shiftKey);
      if (!slash || isTyping(e) || sheetOpen() || e.ctrlKey || e.metaKey || e.altKey) return;
      e.preventDefault();
      search.current?.focus();
    };
    window.addEventListener('keydown', on);
    return () => window.removeEventListener('keydown', on);
  }, [staff]);

  const signOut = (): void => {
    // Revokes the token on the server (sent before it is dropped below); signing out does not wait for it.
    void clubApi.logout().catch(() => undefined);
    setToken(null);
    setStaff(null);
  };

  const loadSettings = useCallback(() => {
    clubApi
      .settings()
      .then((s) => {
        setClubName(s.branding.clubName);
        setLimits(s.limits);
        setWallpaperUrl(s.branding.wallpaperUrl || null);
      })
      .catch(() => undefined);
  }, []);

  useEffect(() => {
    if (!staff) {
      setClubName(null);
      setLimits(null);
      setWallpaperUrl(null);
      setHall(null);
      return undefined;
    }
    loadSettings();
    const id = setInterval(loadSettings, SETTINGS_POLL_MS);
    return () => clearInterval(id);
  }, [staff, loadSettings]);

  const club = useMemo<ClubState>(
    () => ({ clubName, limits, wallpaperUrl, staff, reload: loadSettings }),
    [clubName, limits, wallpaperUrl, staff, loadSettings],
  );

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
  if (!staff) return <Login onDone={setStaff} />;

  const owner = staff.role === 'owner';
  const allowed = ALL.filter((s) => !s.ownerOnly || owner);
  const current = allowed.find((s) => s.id === section) ?? allowed[0];
  const Page = current?.page ?? MapPage;
  const counter = current ? COUNTER.has(current.id) : true;
  const locale = dateLocale();

  const shell = (
    <ShiftProvider key={staff.id} staff={staff} onSignOut={signOut}>
      <div className="relative isolate h-screen overflow-hidden">
        <Wallpaper url={wallpaperUrl} />
        <div className="relative grid h-full grid-cols-[88px_minmax(0,1fr)]">
          {/* Rail: the logo, the sections, then who is signed in and «Выйти». */}
          <aside className="glass-rail relative z-30 flex min-h-0 flex-col pb-4 pt-5">
            <div className="mb-3 flex shrink-0 justify-center">
              <LogoMark size={30} />
            </div>
            <nav
              ref={railNav}
              aria-label={t('Разделы')}
              className="no-scrollbar flex min-h-0 flex-1 flex-col overflow-y-auto py-2 [mask-image:linear-gradient(180deg,transparent_0,#000_8px,#000_calc(100%-8px),transparent_100%)]"
            >
              {GROUPS.map((g, gi) => {
                const items = g.items.filter((s) => !s.ownerOnly || owner);
                if (items.length === 0) return null;
                return (
                  <div key={g.title} className={clsx('flex flex-col', gi > 0 && 'mt-2')}>
                    <span
                      className={clsx(
                        'px-1.5 text-center font-mono text-[9px] font-medium uppercase leading-[11px] tracking-[0.14em] text-muted',
                        owner ? 'pb-1 pt-0.5' : 'pb-1.5 pt-1',
                      )}
                    >
                      {t(g.title)}
                    </span>
                    {items.map((s) => {
                      const active = s.id === current?.id;
                      return (
                        <button
                          key={s.id}
                          ref={active ? railActive : undefined}
                          type="button"
                          onClick={() => go(s.id)}
                          aria-current={active ? 'page' : undefined}
                          className={clsx(
                            'focus-ring-inset relative flex w-full shrink-0 flex-col items-center px-0.5 text-center outline-none transition-colors',
                            // The owner's sixteen sections sit tighter than the cashier's six.
                            owner ? 'gap-1 py-2' : 'gap-[7px] py-3',
                            active
                              ? 'bg-[linear-gradient(90deg,rgb(var(--c-accent)/0.13),rgb(var(--c-accent)/0))] text-hi'
                              : 'text-muted hover:bg-text/[0.03] hover:text-text',
                          )}
                        >
                          {active && (
                            <span
                              aria-hidden="true"
                              className="absolute bottom-2.5 left-0 top-2.5 w-0.5 bg-accent shadow-[0_0_10px_rgb(var(--c-accent)/0.8)]"
                            />
                          )}
                          <span aria-hidden="true" className={clsx('flex', active && 'text-accent')}>
                            {s.icon}
                          </span>
                          <span
                            className={clsx(
                              // Long words hyphenate in the console's language («Автомати-зация»), never spill out.
                              'hyphens-auto text-[10.5px] leading-[13px] [overflow-wrap:anywhere]',
                              active ? 'font-semibold' : 'font-medium',
                            )}
                          >
                            {t(s.title)}
                          </span>
                        </button>
                      );
                    })}
                  </div>
                );
              })}
            </nav>
            <span aria-hidden="true" className="mx-2.5 h-px shrink-0 bg-accent/[0.08]" />
            <div className="flex shrink-0 flex-col items-center gap-2 px-1.5 pt-3">
              <InstallButton variant="rail" />
              <span
                aria-hidden="true"
                className="flex h-9 w-9 items-center justify-center rounded-full border border-accent/[0.22] bg-accent/[0.08] font-display text-xs font-semibold text-text"
              >
                {initials(staff.name)}
              </span>
              <span className="line-clamp-2 text-center text-[10.5px] font-medium leading-[13px] text-text [overflow-wrap:anywhere]">
                {staff.name}
              </span>
            </div>
            <button
              type="button"
              onClick={signOut}
              className="focus-ring-inset mx-2.5 mt-3 flex shrink-0 flex-col items-center gap-1.5 border-t border-accent/[0.08] px-1 pt-2.5 text-muted transition-colors hover:text-text"
            >
              <LogoutIcon size={18} />
              <span className="text-[10.5px] font-medium leading-[13px]">{t('Выйти')}</span>
            </button>
          </aside>

          <div
            className={clsx(
              'grid min-h-0 min-w-0 gap-3.5 px-6 pb-4 pt-[18px]',
              counter ? 'grid-rows-[auto_auto_minmax(0,1fr)]' : 'grid-rows-[auto_minmax(0,1fr)]',
            )}
          >
            <header className="relative z-30 flex h-11 min-w-0 items-center gap-[18px]">
              <div className="flex min-w-0 max-w-[240px] shrink flex-col gap-1.5">
                <span className="truncate font-display text-[15px] font-medium leading-none tracking-[-0.01em] text-text">
                  {clubName ?? 'ClubShell'}
                </span>
                <span className="label-sm whitespace-nowrap">
                  {hall
                    ? `${t(owner ? 'Владелец · {n} ПК' : 'Касса · {n} ПК', { n: hall.total })}${hall.zones > 0 ? ` · ${zonesWord(hall.zones)}` : ''}`
                    : owner
                      ? t('Владелец')
                      : t('Касса')}
                </span>
              </div>
              <Separator />
              <div className="flex shrink-0 items-baseline gap-3">
                <span className="num-dot text-[26px] leading-none tracking-[0.02em] text-text">
                  {now.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })}
                </span>
                <span
                  className={clsx(
                    'hidden whitespace-nowrap text-[13px] leading-4 text-dim',
                    // The setup pages hold the drawer's «±» and the bell in the header too: the date needs more room.
                    counter ? 'min-[1400px]:inline' : 'min-[1700px]:inline',
                  )}
                >
                  {now.toLocaleDateString(locale, { weekday: 'long', day: 'numeric', month: 'long' })}
                </span>
              </div>
              <Separator />
              <ShiftChip onClick={() => go('shift')} />
              {!counter && (
                // The setup pages have no strip: the drawer's «±» and the bell sit by the chip (one of each per page).
                <div className="flex shrink-0 items-center gap-2">
                  <CashMenu variant="compact" />
                  <AudioUnlockChip variant="header" />
                  <CallsBell variant="compact" />
                </div>
              )}
              <div className="min-w-0 flex-1" />
              <GlobalSearch
                ref={search}
                compact={!counter}
                onTopUp={setTopUpFor}
                onShowPc={(hit) => {
                  if (!hit.playing) return;
                  go('map');
                  showPc(hit.playing.pcId);
                }}
              />
              <div className="flex h-10 shrink-0 items-center gap-0.5 rounded-md border border-line bg-surface/50 p-1">
                {LANGS.map((l) => (
                  <button
                    key={l}
                    type="button"
                    aria-pressed={l === lang}
                    onClick={() => setLang(l)}
                    className={clsx(
                      'focus-ring h-[30px] rounded-seg px-[9px] font-mono text-[10.5px] uppercase leading-none tracking-[0.08em]',
                      l === lang
                        ? 'bg-accent/[0.12] font-semibold text-accent'
                        : 'font-medium text-muted hover:text-text',
                    )}
                  >
                    {l}
                  </button>
                ))}
              </div>
            </header>

            {counter && <KpiStrip hall={hall} />}

            <main className="thin-scrollbar -mx-2 -mb-2 min-h-0 overflow-y-auto px-2 pb-2">
              <Suspense fallback={null}>
                <Page key={`${current?.id}-${lang}`} isOwner={owner} />
              </Suspense>
            </main>
          </div>
        </div>
      </div>
      {topUpFor && <TopUpSheet payee={topUpFor} onClose={() => setTopUpFor(null)} onDone={() => void refreshTop()} />}
      <CallsRinger />
    </ShiftProvider>
  );
  return <ClubContext.Provider value={club}>{shell}</ClubContext.Provider>;
}

export default App;
