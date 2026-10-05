/**
 * Players' calls at the desk (cash desk part 3, D-63/D-64). The overview carries the inbox (`calls`: open and answered
 * calls of the last 12 hours); the top bar's poll and the map's put it here ({@link setCalls}), and every screen reads
 * it ({@link useCalls}). Calls of one PC are one group: «ПК 05 · Иван · Технический · 2 мин назад · ×3 (снова)».
 *
 * - {@link CallsBell}: «Вызовы N» — the chevron of the KPI strip's amber «Вызовы» card (counter pages) or a bell in the
 *   header (the owner's setup pages), glowing amber while a call rings; its list answers «Иду» (the PC on its socket
 *   shows «Администратор идёт к вам»; an offline one is not told, and the list says so), shows the PC on the map, or
 *   closes the group. Sound, volume and desktop notifications of this console are set there.
 * - {@link CallsRinger}: a short Web Audio beep every 5 s while any call rings (open, not a repeat after «Иду», not a
 *   problem report — those chime once), the tab title flashing «(1) Вызов: ПК 05», an optional desktop notification (a
 *   click shows the PC on the map). Answered or closed on any console, the ringing stops and the notification closes on
 *   the next poll everywhere.
 * - A browser plays sound only after a click or a key on the page: the PIN sign-in unlocks it; after a reload with a
 *   remembered sign-in, {@link AudioUnlockChip} says «Звук выключен — включить» until the first click or key.
 *
 * Volume, mute and notifications are settings of this console (localStorage; a private window keeps them for the tab).
 */
import { useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import clsx from 'clsx';
import { adminApi, type Call } from '@/api';
import { pcLabel } from '@/clientSearch';
import { showPc } from '@/desk';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { CALL_CATEGORY_LABEL, guestDisplayName } from '@/labels';
import { BellIcon, ChevronRightIcon, SpeakerOffIcon } from '@/icons';
import { Button, Toggle } from '@/ui';

const RING_EVERY_MS = 5000;
const FLASH_EVERY_MS = 1000;
const VOLUME_KEY = 'clubshell.admin.calls.volume';
const MUTED_KEY = 'clubshell.admin.calls.muted';
const NOTIFY_KEY = 'clubshell.admin.calls.notify';

// ---------------------------------------------------------------------------------------------------------------------
// The inbox
// ---------------------------------------------------------------------------------------------------------------------

/** null — the server sends no inbox (older than cash desk part 3): no bell. */
let inbox: Call[] | null = null;
/** «Иду» answered per PC: whether the player was told. */
const answers = new Map<string, boolean>();
const listeners = new Set<() => void>();
let version = 0;

function emit(): void {
  version += 1;
  listeners.forEach((l) => l());
}

function subscribe(l: () => void): () => void {
  listeners.add(l);
  return () => listeners.delete(l);
}

/**
 * What this console answered or closed in the last seconds, by PC: a poll that left before the answer and comes back
 * after it must not ring the call again.
 */
const moves = new Map<string, { to: 'acked' | 'resolved'; call: Call; until: number }>();
const STALE_POLL_MS = 15_000;

/** The overview's `calls` (absent from an older server: no inbox). */
export function setCalls(list: Call[] | null | undefined): void {
  inbox = list ?? null;
  const t0 = Date.now();
  for (const [pcId, m] of moves) {
    if (m.until < t0) moves.delete(pcId);
    else apply(m.to, m.call);
  }
  for (const pcId of answers.keys()) {
    if (!inbox?.some((c) => c.pcId === pcId)) answers.delete(pcId);
  }
  emit();
}

export function useCalls(): Call[] | null {
  useSyncExternalStore(subscribe, () => version);
  return inbox;
}

/** Whether the player of `pcId` was told after the last «Иду» on this console (undefined — not answered here). */
export function useAnswer(pcId: string): boolean | undefined {
  useSyncExternalStore(subscribe, () => version);
  return answers.get(pcId);
}

/** The server's rule, echoed at once so the ringing stops before the next poll: older calls of the PC move along. */
function apply(to: 'acked' | 'resolved', call: Call): void {
  if (!inbox) return;
  inbox = inbox.flatMap((c) => {
    if (c.pcId !== call.pcId || c.at > call.at) return [c];
    if (to === 'resolved') return [];
    return c.status === 'open'
      ? [{ ...c, status: 'acked' as const, ackedBy: call.ackedBy, ackedAt: call.ackedAt }]
      : [c];
  });
}

/**
 * «Иду»: the newest open call of the group, with the message to the PC. `notified` null — someone answered first (another
 * console, a stale list): nothing went to the PC from here, and the group says who is on the way instead.
 */
export async function ackCall(call: Call): Promise<boolean | null> {
  const r = await adminApi.callAck(call.id, true);
  // Closed meanwhile on another console: the whole group goes.
  const to = r.call.status === 'resolved' ? 'resolved' : 'acked';
  moves.set(call.pcId, { to, call: r.call, until: Date.now() + STALE_POLL_MS });
  apply(to, r.call);
  const notified = r.notified ?? null;
  if (notified === null) answers.delete(call.pcId);
  else answers.set(call.pcId, notified);
  emit();
  return notified;
}

/** «Закрыть»: the newest call of the group, and so the whole group. */
export async function resolveCall(call: Call): Promise<void> {
  const r = await adminApi.callResolve(call.id);
  moves.set(call.pcId, { to: 'resolved', call: r.call, until: Date.now() + STALE_POLL_MS });
  apply('resolved', r.call);
  answers.delete(call.pcId);
  emit();
}

/** The calls of one PC, newest first, and what the desk needs of them. */
export interface CallGroup {
  pcId: string;
  pcName: string;
  pcNumber: number;
  calls: Call[];
  newest: Call;
  /** The newest open call: what «Иду» answers; null — every call answered. */
  open: Call | null;
  /** Any call of the group rings (open, not a repeat, not a problem report). */
  ringing: boolean;
  repeat: boolean;
  /** The player's name, when a call has one. */
  player: string | null;
  /** When the first call of the group came. */
  since: string;
}

export function ringsNow(c: Call): boolean {
  return c.status === 'open' && !c.repeat && c.category !== 'problem';
}

export function groupCalls(calls: readonly Call[]): CallGroup[] {
  const byPc = new Map<string, Call[]>();
  for (const c of calls) byPc.set(c.pcId, [...(byPc.get(c.pcId) ?? []), c]);
  return [...byPc.values()]
    .map((list) => {
      const sorted = [...list].sort((a, b) => b.at.localeCompare(a.at));
      const newest = sorted[0] as Call;
      // The server's «Гость 5» is said in the console's language, as everywhere else.
      const player = sorted.find((c) => c.user)?.user?.displayName;
      return {
        pcId: newest.pcId,
        pcName: newest.pcName,
        pcNumber: newest.pcNumber,
        calls: sorted,
        newest,
        open: sorted.find((c) => c.status === 'open') ?? null,
        ringing: sorted.some(ringsNow),
        repeat: sorted.some((c) => c.repeat),
        player: player ? guestDisplayName(player) : null,
        since: (sorted.at(-1) as Call).at,
      };
    })
    .sort((a, b) => Number(b.ringing) - Number(a.ringing) || b.newest.at.localeCompare(a.newest.at));
}

/** `только что`, `2 мин назад`, `1 ч назад`. */
export function ago(iso: string, nowMs = Date.now()): string {
  const min = Math.floor((nowMs - Date.parse(iso)) / 60_000);
  if (min < 1) return t('только что');
  if (min < 60) return t('{n} мин назад', { n: min });
  return t('{n} ч назад', { n: Math.floor(min / 60) });
}

/** «ПК 05 · Иван · Технический · «мышь» · 2 мин назад · ×3 (снова)». */
export function groupLine(g: CallGroup, nowMs = Date.now()): string {
  const message = g.calls.find((c) => c.message)?.message;
  return [
    pcLabel(g.pcName),
    g.player ?? t('без входа'),
    t(CALL_CATEGORY_LABEL[g.newest.category] ?? g.newest.category),
    message ? `«${message}»` : null,
    ago(g.since, nowMs),
    g.calls.length > 1 ? `×${g.calls.length}${g.repeat ? ` ${t('(снова)')}` : ''}` : null,
  ]
    .filter(Boolean)
    .join(' · ');
}

// ---------------------------------------------------------------------------------------------------------------------
// Sound
// ---------------------------------------------------------------------------------------------------------------------

type AudioCtor = new () => AudioContext;
let audio: AudioContext | null = null;

function audioCtor(): AudioCtor | null {
  const w = window as unknown as { AudioContext?: AudioCtor; webkitAudioContext?: AudioCtor };
  return w.AudioContext ?? w.webkitAudioContext ?? null;
}

export type AudioState = 'running' | 'locked' | 'unsupported';

function audioState(): AudioState {
  if (!audioCtor()) return 'unsupported';
  return audio?.state === 'running' ? 'running' : 'locked';
}

/** Turns sound on from a click or a key (the only moment a browser allows it). */
export function unlockAudio(): void {
  const Ctor = audioCtor();
  if (!Ctor) return;
  try {
    if (!audio) {
      audio = new Ctor();
      audio.onstatechange = () => emit();
    }
    if (audio.state !== 'running') void audio.resume().then(emit, () => undefined);
  } catch {
    // blocked by the browser: the chip stays
  }
  emit();
}

// Any click or key on the console (the PIN sign-in first of all) unlocks the sound.
if (typeof window !== 'undefined') {
  const onGesture = (): void => {
    if (audioState() === 'locked') unlockAudio();
  };
  window.addEventListener('pointerdown', onGesture, true);
  window.addEventListener('keydown', onGesture, true);
}

export function useAudioState(): AudioState {
  useSyncExternalStore(subscribe, () => version);
  return audioState();
}

/** One short two-tone beep at `volume` (0..1); nothing while the browser has not allowed sound. */
function beep(volume: number): void {
  if (!audio || audio.state !== 'running' || volume <= 0) return;
  try {
    const t0 = audio.currentTime;
    const osc = audio.createOscillator();
    const gain = audio.createGain();
    osc.type = 'sine';
    osc.frequency.setValueAtTime(880, t0);
    osc.frequency.setValueAtTime(660, t0 + 0.18);
    gain.gain.setValueAtTime(0.0001, t0);
    gain.gain.exponentialRampToValueAtTime(Math.max(0.0002, volume), t0 + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, t0 + 0.4);
    osc.connect(gain);
    gain.connect(audio.destination);
    osc.start(t0);
    osc.stop(t0 + 0.42);
  } catch {
    // a closed context: no sound this time
  }
}

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string | null): void {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    // private mode: the setting lasts until the reload
  }
}

interface SoundSettings {
  volume: number;
  muted: boolean;
  notify: boolean;
}

function readSound(): SoundSettings {
  const v = Number(read(VOLUME_KEY));
  return {
    volume: read(VOLUME_KEY) !== null && Number.isFinite(v) ? Math.min(1, Math.max(0, v)) : 0.6,
    muted: read(MUTED_KEY) === '1',
    notify: read(NOTIFY_KEY) === '1',
  };
}

let sound = readSound();

function setSound(next: Partial<SoundSettings>): void {
  sound = { ...sound, ...next };
  write(VOLUME_KEY, String(sound.volume));
  write(MUTED_KEY, sound.muted ? '1' : null);
  write(NOTIFY_KEY, sound.notify ? '1' : null);
  emit();
}

function useSound(): SoundSettings {
  useSyncExternalStore(subscribe, () => version);
  return sound;
}

function notificationsAllowed(): boolean {
  return typeof Notification !== 'undefined' && Notification.permission === 'granted';
}

/** The desktop notifications this console raised and has not closed yet, by call. */
const desktop = new Map<string, Notification>();

// ---------------------------------------------------------------------------------------------------------------------
// Ringer (mounted once in the console)
// ---------------------------------------------------------------------------------------------------------------------

export function CallsRinger(): null {
  const calls = useCalls();
  const settings = useSound();
  const groups = useMemo(() => groupCalls(calls ?? []), [calls]);
  const ringing = groups.filter((g) => g.ringing);
  const ring = ringing.length > 0;
  const first = ringing[0];
  const title = first ? `(${ringing.length}) ${t('Вызов: {pc}', { pc: pcLabel(first.pcName) })}` : '';

  // A beep now and every 5 s while anything rings.
  useEffect(() => {
    if (!ring || settings.muted) return undefined;
    beep(settings.volume);
    const id = window.setInterval(() => beep(settings.volume), RING_EVERY_MS);
    return () => window.clearInterval(id);
  }, [ring, settings.muted, settings.volume]);

  // The tab title flashes while anything rings.
  useEffect(() => {
    if (!ring) return undefined;
    const base = document.title;
    let on = false;
    const id = window.setInterval(() => {
      on = !on;
      document.title = on ? title : base;
    }, FLASH_EVERY_MS);
    return () => {
      window.clearInterval(id);
      document.title = base;
    };
  }, [ring, title]);

  // A new repeat or problem report chimes once; a new ringing call may raise a desktop notification.
  const seen = useRef<Set<string> | null>(null);
  useEffect(() => {
    if (!calls) return;
    if (seen.current === null) {
      seen.current = new Set(calls.map((c) => c.id));
      return;
    }
    const known = seen.current;
    const fresh = calls.filter((c) => !known.has(c.id));
    fresh.forEach((c) => known.add(c.id));
    if (fresh.some((c) => c.status !== 'resolved' && !ringsNow(c)) && !settings.muted) beep(settings.volume);
    if (settings.notify && notificationsAllowed()) {
      for (const c of fresh.filter(ringsNow)) {
        try {
          const n = new Notification(t('Вызов: {pc}', { pc: pcLabel(c.pcName) }), {
            body: [t(CALL_CATEGORY_LABEL[c.category] ?? c.category), c.message].filter(Boolean).join(' · '),
            tag: c.id,
            requireInteraction: true,
          });
          // A click brings the console up on the map with that PC.
          n.onclick = () => {
            window.focus();
            window.location.hash = '/map';
            showPc(c.pcId);
            n.close();
          };
          desktop.set(c.id, n);
        } catch {
          // notifications blocked by the system
        }
      }
    }
  }, [calls, settings.muted, settings.notify, settings.volume]);

  // Answered or closed on any console (or signed out): its desktop notification goes too.
  useEffect(() => {
    for (const [id, n] of desktop) {
      if (calls?.some((c) => c.id === id && c.status === 'open')) continue;
      n.close();
      desktop.delete(id);
    }
  }, [calls]);

  return null;
}

// ---------------------------------------------------------------------------------------------------------------------
// The chip and the bell
// ---------------------------------------------------------------------------------------------------------------------

/**
 * «Звук выключен — включить»: the browser has not allowed sound yet (after a reload with a remembered sign-in). `kpi` —
 * a small ghost button in the «Вызовы» card (the crossed-out speaker alone); `header` — beside the compact bell on the
 * owner's setup pages (the words from 1700 px). The words are always its name and tooltip.
 */
export function AudioUnlockChip({ variant = 'header' }: { variant?: 'kpi' | 'header' }): JSX.Element | null {
  const state = useAudioState();
  if (state !== 'locked') return null;
  const label = t('Звук выключен — включить');
  return (
    <button
      type="button"
      onClick={unlockAudio}
      aria-label={label}
      title={label}
      className={clsx(
        'focus-ring flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-md text-xs font-medium text-warning',
        variant === 'kpi'
          ? 'h-7 px-2 hover:bg-warning/[0.08]'
          : 'h-9 border border-warning/40 bg-warning/[0.08] px-2.5 hover:bg-warning/[0.14]',
      )}
    >
      <SpeakerOffIcon size={16} />
      {/* In the «Вызовы» card the speaker alone: the card's words come first. */}
      {variant === 'header' && <span className="hidden min-[1700px]:inline">{label}</span>}
    </button>
  );
}

/** The amber bell of a calling PC (its map tile, its panel); glowing while the call rings. */
export function CallMark({ ringing }: { ringing: boolean }): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      role="img"
      aria-label={t('Вызов администратора')}
      className={clsx('h-3.5 w-3.5 text-warning', ringing && '[filter:drop-shadow(0_0_4px_rgb(var(--c-warning)/0.9))]')}
      fill="none"
      stroke="currentColor"
      strokeWidth="2.2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15z" />
      <path d="M10 20.5a2 2 0 0 0 4 0" />
    </svg>
  );
}

/**
 * «Иду» / «Закрыть» of one group with its outcome: «Администратор идёт» told to the player, or that the PC is not on
 * the line; used by the bell's list and the seat panel's banner. `onShowPc` adds «Показать ПК».
 */
export function CallGroupActions({
  group,
  onShowPc,
  compact,
}: {
  group: CallGroup;
  onShowPc?: () => void;
  compact?: boolean;
}): JSX.Element {
  const answered = useAnswer(group.pcId);
  const [busy, setBusy] = useState<'ack' | 'resolve' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const act = (kind: 'ack' | 'resolve'): void => {
    setBusy(kind);
    setError(null);
    const run = kind === 'ack' && group.open ? ackCall(group.open) : resolveCall(group.newest);
    run
      .catch((e: unknown) => setError(describe(e)))
      .finally(() => {
        setBusy(null);
      });
  };
  const size = compact ? 'xs' : 'sm';
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex flex-wrap items-center gap-1.5">
        {group.open && (
          <Button variant="warn" size={size} disabled={busy !== null} onClick={() => act('ack')}>
            {busy === 'ack' ? '…' : t('Иду')}
          </Button>
        )}
        {onShowPc && (
          <Button variant="secondary" size={size} onClick={onShowPc}>
            {t('Показать ПК')}
          </Button>
        )}
        <Button variant="ghost" size={size} disabled={busy !== null} onClick={() => act('resolve')}>
          {busy === 'resolve' ? '…' : t('Закрыть вызов')}
        </Button>
      </div>
      {!group.open && group.newest.ackedBy && (
        <span className="text-xs text-muted">
          {t('Идёт: {name}', { name: group.newest.ackedBy })}
          {answered === true && ` · ${t('игрок получил сообщение')}`}
        </span>
      )}
      {answered === false && (
        <span role="status" className="text-xs text-warning">
          {t('ПК не на связи — игрок не получил сообщение')}
        </span>
      )}
      {error && (
        <span role="alert" className="text-xs text-danger-ink">
          {error}
        </span>
      )}
    </div>
  );
}

/**
 * «Вызовы N»: the inbox by PC, sound and notification settings of this console; nothing while nobody calls. `kpi` — the
 * 44 px amber chevron of the «Вызовы» card on the counter pages; `compact` — a bell with the count in the header of the
 * owner's setup pages. Either way it is named «Вызовы N» and opens the «Вызовы игроков» list.
 */
export function CallsBell({ variant = 'compact' }: { variant?: 'kpi' | 'compact' }): JSX.Element | null {
  const calls = useCalls();
  const settings = useSound();
  const [open, setOpen] = useState(false);
  const [nowMs, setNowMs] = useState(Date.now());
  const box = useRef<HTMLDivElement>(null);
  const bell = useRef<HTMLButtonElement>(null);
  const groups = useMemo(() => groupCalls(calls ?? []), [calls]);
  const ringing = groups.some((g) => g.ringing);
  /** Esc closes the list and stays here: the page behind (the map's selection, the bar's search) never sees it. */
  const onEscape = (e: React.KeyboardEvent): void => {
    if (e.key !== 'Escape' || !open) return;
    e.stopPropagation();
    setOpen(false);
    bell.current?.focus();
  };
  useEffect(() => {
    if (!open) return undefined;
    const close = (e: MouseEvent): void => {
      if (box.current && !box.current.contains(e.target as Node)) setOpen(false);
    };
    const tick = window.setInterval(() => setNowMs(Date.now()), 30_000);
    window.addEventListener('mousedown', close);
    return () => {
      window.removeEventListener('mousedown', close);
      window.clearInterval(tick);
    };
  }, [open]);
  useEffect(() => {
    if (groups.length === 0) setOpen(false);
  }, [groups.length]);
  if (!calls || groups.length === 0) return null;
  const label = t('Вызовы {n}', { n: groups.length });
  return (
    <div ref={box} className="relative">
      <button
        ref={bell}
        type="button"
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-label={label}
        title={label}
        onClick={() => {
          setNowMs(Date.now());
          setOpen((v) => !v);
        }}
        onKeyDown={onEscape}
        className={clsx(
          'btn-warn focus-ring flex shrink-0 items-center justify-center rounded-md',
          variant === 'kpi' ? 'h-11 w-11' : 'h-9 gap-1.5 whitespace-nowrap px-2.5 text-[13px] font-semibold',
          // In the KPI card the card itself glows.
          ringing && variant === 'compact' && 'motion-safe:anim-warn-glow',
        )}
      >
        {variant === 'kpi' ? (
          <ChevronRightIcon size={18} strokeWidth={1.8} />
        ) : (
          <>
            <BellIcon size={16} strong />
            <span className="tnum">{groups.length}</span>
          </>
        )}
      </button>
      {open && (
        <div
          role="dialog"
          aria-label={t('Вызовы игроков')}
          className="panel-solid anim-rise absolute right-0 top-[calc(100%+8px)] z-40 flex w-[min(30rem,calc(100vw-2rem))] flex-col gap-3 p-4"
          onKeyDown={onEscape}
        >
          <ul
            aria-label={t('Вызовы игроков')}
            className="thin-scrollbar flex max-h-[24rem] flex-col divide-y divide-line overflow-y-auto"
          >
            {groups.map((g) => (
              <li key={g.pcId} data-call-pc={g.pcId} className="flex flex-col gap-2 py-2.5">
                <span className={clsx('text-sm', g.ringing ? 'font-semibold text-warning' : 'text-text')}>
                  {groupLine(g, nowMs)}
                </span>
                <CallGroupActions
                  group={g}
                  onShowPc={() => {
                    setOpen(false);
                    window.location.hash = '/map';
                    showPc(g.pcId);
                  }}
                />
              </li>
            ))}
          </ul>
          <SoundSettingsRow settings={settings} />
        </div>
      )}
    </div>
  );
}

/** Volume, mute and desktop notifications of this console. */
function SoundSettingsRow({ settings }: { settings: SoundSettings }): JSX.Element {
  const [denied, setDenied] = useState(false);
  return (
    <div className="flex flex-col gap-2 border-t border-line pt-3">
      <div className="flex items-center gap-3">
        <Toggle label={t('Звук')} checked={!settings.muted} onChange={(v) => setSound({ muted: !v })} />
        <input
          type="range"
          min={0}
          max={100}
          step={5}
          aria-label={t('Громкость')}
          disabled={settings.muted}
          value={Math.round(settings.volume * 100)}
          onChange={(e) => setSound({ volume: Number(e.target.value) / 100 })}
          className="min-w-0 flex-1 disabled:opacity-40"
        />
        <button
          type="button"
          className="focus-ring choice h-7 rounded-md px-2.5 text-xs font-semibold"
          disabled={settings.muted}
          onClick={() => {
            unlockAudio();
            beep(settings.volume);
          }}
        >
          {t('Проверить')}
        </button>
      </div>
      {typeof Notification !== 'undefined' && (
        <Toggle
          label={t('Уведомления на рабочем столе')}
          checked={settings.notify && Notification.permission === 'granted'}
          onChange={(v) => {
            if (!v) {
              setSound({ notify: false });
              return;
            }
            void Notification.requestPermission().then((p) => {
              setDenied(p !== 'granted');
              setSound({ notify: p === 'granted' });
            });
          }}
        />
      )}
      {denied && (
        <p className="text-xs text-warning">{t('Браузер запретил уведомления — разрешите их в настройках сайта')}</p>
      )}
    </div>
  );
}
