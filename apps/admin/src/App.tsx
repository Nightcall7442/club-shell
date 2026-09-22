/**
 * Club counter: the hall map on the left, the selected seat on the right. Everything a cashier does at the desk —
 * open time, add time, end a session, top up a wallet, message or lock a PC — is one click from the seat panel.
 * Polls `/admin/overview` every 2 s (the real console would follow the server's WebSocket).
 */
import { useCallback, useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import type { Session } from '@clubshell/contracts';
import { adminApi, AdminError, type Member, type Overview, type Seat } from '@/api';
import { duration, minutesLabel, money } from '@/format';

const POLL_MS = 2000;
const MINUTE_PRESETS = [30, 60, 120, 180];
const TOPUP_PRESETS = [20_000, 50_000, 100_000, 200_000];

const STATUS: Record<Seat['pc']['status'], { label: string; short: string; dot: string; ring: string }> = {
  free: { label: 'Свободен', short: 'своб.', dot: 'bg-success', ring: 'border-success/25' },
  busy: { label: 'Занят', short: 'занят', dot: 'bg-accent', ring: 'border-accent/30' },
  locked: { label: 'Заблокирован', short: 'блок', dot: 'bg-danger', ring: 'border-danger/30' },
  maintenance: { label: 'Обслуживание', short: 'сервис', dot: 'bg-danger/70', ring: 'border-line' },
  booked: { label: 'Бронь', short: 'бронь', dot: 'bg-primary', ring: 'border-primary/30' },
  offline: { label: 'Офлайн', short: 'офлайн', dot: 'bg-muted/40', ring: 'border-line' },
};

const ERROR_COPY: Record<string, string> = {
  insufficientFunds: 'Недостаточно средств на балансе',
  sessionAlreadyActive: 'На этом ПК или у клиента уже открыт сеанс',
  policyDenied: 'Действие запрещено политикой',
  notFound: 'Не найдено',
  network: 'Нет связи с сервером. Запущен ли mock-сервер (pnpm mock)?',
};

function describe(e: unknown): string {
  if (e instanceof AdminError) {
    return ERROR_COPY[e.code] ?? e.message;
  }
  return e instanceof Error ? e.message : 'Ошибка';
}

function secondsLeft(s: Session | null): number {
  if (!s) {
    return 0;
  }
  if (s.secondsLeft < 0) {
    return -1;
  }
  return s.endsAt ? Math.max(0, Math.round((Date.parse(s.endsAt) - Date.now()) / 1000)) : s.secondsLeft;
}

// ---------------------------------------------------------------------------------------------------------------------
// Pieces
// ---------------------------------------------------------------------------------------------------------------------

function Button({
  children,
  variant = 'secondary',
  ...rest
}: React.ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: 'primary' | 'secondary' | 'ghost' | 'danger';
}): JSX.Element {
  return (
    <button
      type="button"
      {...rest}
      className={clsx(
        'focus-ring inline-flex h-10 select-none items-center justify-center gap-2 rounded-lg px-3.5 text-sm font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-40',
        variant === 'primary' && 'bg-primary text-on-primary hover:bg-white',
        variant === 'secondary' && 'bg-white/[0.06] text-text hover:bg-white/[0.12]',
        variant === 'ghost' && 'text-muted hover:bg-white/[0.06] hover:text-text',
        variant === 'danger' && 'text-danger hover:bg-danger/10',
        rest.className,
      )}
    >
      {children}
    </button>
  );
}

function SeatTile({
  seat,
  selected,
  onSelect,
  tick,
}: {
  seat: Seat;
  selected: boolean;
  onSelect: () => void;
  tick: number;
}): JSX.Element {
  void tick;
  const s = STATUS[seat.pc.status];
  const left = secondsLeft(seat.session);
  const warn = left >= 0 && left <= 5 * 60;
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
      title={`${seat.pc.name} · ${s.label}${seat.user ? ` · ${seat.user.displayName}` : ''}`}
      className={clsx(
        'focus-ring relative flex aspect-square flex-col items-center justify-center gap-0.5 rounded-lg border text-center transition-colors',
        selected ? 'border-primary bg-primary/10' : `${s.ring} bg-white/[0.02] hover:bg-white/[0.06]`,
      )}
    >
      <span className={clsx('absolute right-1.5 top-1.5 h-1.5 w-1.5 rounded-full', s.dot)} />
      <span className="tnum text-base font-bold leading-none">{seat.pc.number}</span>
      {seat.session ? (
        <span className={clsx('tnum text-[0.65rem] leading-none', warn ? 'text-danger' : 'text-muted')}>
          {left < 0 ? '∞' : duration(left)}
        </span>
      ) : (
        <span className="text-[0.65rem] leading-none text-muted">{s.short}</span>
      )}
    </button>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }): JSX.Element {
  return (
    <label className="flex flex-col gap-1.5">
      <span className="text-xs uppercase tracking-wide text-muted">{label}</span>
      {children}
    </label>
  );
}

const inputCls =
  'focus-ring h-10 w-full rounded-lg border border-line bg-white/[0.03] px-3 text-sm text-text placeholder:text-muted';

// ---------------------------------------------------------------------------------------------------------------------
// Seat panel
// ---------------------------------------------------------------------------------------------------------------------

function SeatPanel({
  seat,
  members,
  tariffs,
  tick,
  onDone,
}: {
  seat: Seat;
  members: Member[];
  tariffs: Overview['tariffs'];
  tick: number;
  onDone: () => void;
}): JSX.Element {
  const zoneTariffs = useMemo(
    () =>
      tariffs.filter(
        (t) => t.zones.length === 0 || t.zones.some((z) => z.toLowerCase() === seat.pc.zone.toLowerCase()),
      ),
    [tariffs, seat.pc.zone],
  );
  const [userId, setUserId] = useState('');
  const [tariffId, setTariffId] = useState('');
  const [minutes, setMinutes] = useState(60);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [note, setNote] = useState<{ text: string; tone: 'ok' | 'err' } | null>(null);

  useEffect(() => {
    setNote(null);
    setUserId('');
    setMinutes(60);
  }, [seat.pc.id]);
  useEffect(() => {
    if (!tariffId || !zoneTariffs.some((t) => t.id === tariffId)) {
      setTariffId(zoneTariffs[0]?.id ?? '');
    }
  }, [zoneTariffs, tariffId]);

  const run = async (key: string, fn: () => Promise<string>): Promise<void> => {
    setBusy(key);
    setNote(null);
    try {
      setNote({ text: await fn(), tone: 'ok' });
      onDone();
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(null);
    }
  };

  const tariff = zoneTariffs.find((t) => t.id === tariffId);
  const price =
    tariff && !tariff.isPackage
      ? Math.round((tariff.pricePerHour.amount / 60) * minutes)
      : (tariff?.packagePrice?.amount ?? 0);
  const left = secondsLeft(seat.session);
  void tick;

  return (
    <div className="flex h-full flex-col gap-5 overflow-y-auto pr-1">
      <header className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-xl font-bold leading-tight">{seat.pc.name}</h2>
          <p className="text-sm text-muted">
            {seat.pc.zone} · {STATUS[seat.pc.status].label}
          </p>
        </div>
        {seat.session && (
          <div className="shrink-0 text-right">
            <div className="text-[0.7rem] uppercase tracking-wide text-muted">Осталось</div>
            <div className={clsx('tnum text-2xl font-bold leading-none', left >= 0 && left <= 300 && 'text-danger')}>
              {left < 0 ? '∞' : duration(left)}
            </div>
          </div>
        )}
      </header>

      {note && (
        <p
          className={clsx(
            'rounded-lg px-3 py-2 text-sm',
            note.tone === 'ok' ? 'bg-success/10 text-success' : 'bg-danger/10 text-danger',
          )}
        >
          {note.text}
        </p>
      )}

      {seat.session && seat.user ? (
        <>
          <section className="flex flex-col gap-4">
            <div className="flex items-baseline justify-between gap-3">
              <span className="truncate text-base font-semibold">{seat.user.displayName}</span>
              <span className="tnum shrink-0 text-sm text-muted">{money(seat.user.balance)}</span>
            </div>

            <Field label="Добавить время">
              <div className="grid grid-cols-4 gap-1.5">
                {MINUTE_PRESETS.map((m) => (
                  <Button
                    key={m}
                    disabled={busy !== null}
                    onClick={() =>
                      void run(`ext-${m}`, async () => {
                        const r = await adminApi.extend({ pcId: seat.pc.id, minutes: m });
                        return `Добавлено ${minutesLabel(m)} · списано ${money(r.charged)}`;
                      })
                    }
                  >
                    +{minutesLabel(m)}
                  </Button>
                ))}
              </div>
            </Field>

            <Field label="Пополнить баланс">
              <div className="grid grid-cols-4 gap-1.5">
                {TOPUP_PRESETS.map((a) => (
                  <Button
                    key={a}
                    disabled={busy !== null}
                    onClick={() =>
                      void run(`top-${a}`, async () => {
                        const r = await adminApi.topUp({ userId: seat.user?.id ?? '', amount: a * 100 });
                        return `Баланс пополнен · теперь ${money(r.balance)}`;
                      })
                    }
                  >
                    {a / 1000}к
                  </Button>
                ))}
              </div>
            </Field>
          </section>

          <section className="flex flex-col gap-2 border-t border-line pt-4">
            <Button
              variant="danger"
              className="justify-start"
              disabled={busy !== null}
              onClick={() =>
                void run('end', async () => {
                  const r = await adminApi.end({ pcId: seat.pc.id });
                  return r.refunded && r.refunded.amount > 0
                    ? `Сеанс завершён · возврат ${money(r.refunded)}`
                    : 'Сеанс завершён';
                })
              }
            >
              Завершить сеанс
            </Button>
            <div className="grid grid-cols-2 gap-1.5">
              <Button
                variant="ghost"
                disabled={busy !== null}
                onClick={() =>
                  void run('lock', async () => {
                    await adminApi.command(seat.pc.id, { kind: 'lock' });
                    return 'ПК заблокирован';
                  })
                }
              >
                Заблокировать
              </Button>
              <Button
                variant="ghost"
                disabled={busy !== null}
                onClick={() =>
                  void run('unlock', async () => {
                    await adminApi.command(seat.pc.id, { kind: 'unlock' });
                    return 'ПК разблокирован';
                  })
                }
              >
                Разблокировать
              </Button>
            </div>
          </section>
        </>
      ) : (
        <section className="flex flex-col gap-4">
          <Field label="Клиент">
            <select className={inputCls} value={userId} onChange={(e) => setUserId(e.target.value)}>
              <option value="">— выберите клиента —</option>
              {members.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.displayName} · {money(m.balance)}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Тариф">
            <select className={inputCls} value={tariffId} onChange={(e) => setTariffId(e.target.value)}>
              {zoneTariffs.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name} · {money(t.isPackage ? (t.packagePrice ?? t.pricePerHour) : t.pricePerHour)}
                  {t.isPackage ? '' : ' / ч'}
                </option>
              ))}
            </select>
          </Field>
          {!tariff?.isPackage && (
            <Field label="Время">
              <div className="grid grid-cols-4 gap-1.5">
                {MINUTE_PRESETS.map((m) => (
                  <Button key={m} variant={m === minutes ? 'primary' : 'secondary'} onClick={() => setMinutes(m)}>
                    {minutesLabel(m)}
                  </Button>
                ))}
              </div>
            </Field>
          )}
          <div className="flex items-center justify-between gap-3 border-t border-line pt-4">
            <span className="text-sm text-muted">
              К списанию{' '}
              <span className="tnum text-base font-bold text-text">{money({ amount: price, currency: 'UZS' })}</span>
            </span>
            <Button
              variant="primary"
              disabled={busy !== null || !userId || !tariffId || seat.pc.status === 'maintenance'}
              onClick={() =>
                void run('open', async () => {
                  const r = await adminApi.openSession({ pcId: seat.pc.id, userId, tariffId, minutes });
                  return `Сеанс открыт · списано ${money(r.charged)}`;
                })
              }
            >
              Открыть сеанс
            </Button>
          </div>
        </section>
      )}

      <section className="mt-auto flex flex-col gap-2 border-t border-line pt-4">
        <Field label="Сообщение на экран">
          <div className="flex gap-1.5">
            <input
              className={inputCls}
              value={message}
              placeholder="Закрываемся через 20 минут"
              onChange={(e) => setMessage(e.target.value)}
            />
            <Button
              disabled={busy !== null || message.trim().length === 0}
              onClick={() =>
                void run('msg', async () => {
                  await adminApi.command(seat.pc.id, { kind: 'message', text: message.trim() });
                  setMessage('');
                  return 'Сообщение отправлено';
                })
              }
            >
              Отправить
            </Button>
          </div>
        </Field>
        <div className="grid grid-cols-2 gap-1.5">
          <Button
            variant="ghost"
            disabled={busy !== null}
            onClick={() =>
              void run('reboot', async () => {
                await adminApi.command(seat.pc.id, { kind: 'reboot' });
                return 'ПК перезагружается';
              })
            }
          >
            Перезагрузить
          </Button>
          <Button
            variant="ghost"
            disabled={busy !== null}
            onClick={() =>
              void run('shutdown', async () => {
                await adminApi.command(seat.pc.id, { kind: 'shutdown' });
                return 'ПК выключается';
              })
            }
          >
            Выключить
          </Button>
        </div>
      </section>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// App
// ---------------------------------------------------------------------------------------------------------------------

export function App(): JSX.Element {
  const [data, setData] = useState<Overview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [tick, setTick] = useState(0);

  const load = useCallback(async () => {
    try {
      setData(await adminApi.overview());
      setError(null);
    } catch (e) {
      setError(describe(e));
    }
  }, []);

  useEffect(() => {
    void load();
    const poll = setInterval(() => void load(), POLL_MS);
    const clock = setInterval(() => setTick((n) => n + 1), 1000);
    return () => {
      clearInterval(poll);
      clearInterval(clock);
    };
  }, [load]);

  const seats = data?.seats ?? [];
  const seat = seats.find((s) => s.pc.id === selected) ?? null;
  const zones = useMemo(() => {
    const out = new Map<string, Seat[]>();
    for (const s of seats) {
      out.set(s.pc.zone, [...(out.get(s.pc.zone) ?? []), s]);
    }
    return [...out.entries()];
  }, [seats]);

  return (
    <div className="mx-auto flex h-screen max-w-[1800px] flex-col gap-4 p-5">
      <header className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-xl font-bold tracking-tight">
            Касса <span className="text-muted">· CyberArena Tashkent</span>
          </h1>
          <p className="text-sm text-muted">
            {data ? `Свободно ${data.club.free} из ${data.club.total}` : 'Загрузка…'}
          </p>
        </div>
        {error && <p className="rounded-xl bg-danger/10 px-3 py-2 text-sm text-danger">{error}</p>}
      </header>

      <div className="grid min-h-0 flex-1 grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_26rem]">
        <div className="panel min-h-0 overflow-y-auto p-4">
          {zones.map(([zone, list]) => (
            <section key={zone} className="mb-6 last:mb-0">
              <h2 className="mb-2 flex items-baseline gap-2 text-xs font-semibold uppercase tracking-[0.18em] text-muted">
                {zone}
                <span className="tnum font-normal normal-case tracking-normal">
                  {list.filter((s) => s.pc.status === 'free').length} / {list.length} свободно
                </span>
              </h2>
              <div className="grid grid-cols-[repeat(auto-fill,minmax(3.25rem,1fr))] gap-1.5">
                {list.map((s) => (
                  <SeatTile
                    key={s.pc.id}
                    seat={s}
                    tick={tick}
                    selected={s.pc.id === selected}
                    onSelect={() => setSelected(s.pc.id)}
                  />
                ))}
              </div>
            </section>
          ))}
          {seats.length === 0 && !error && <p className="text-sm text-muted">Нет данных о ПК</p>}
        </div>

        <aside className="panel min-h-0 p-4">
          {seat && data ? (
            <SeatPanel seat={seat} members={data.users} tariffs={data.tariffs} tick={tick} onDone={() => void load()} />
          ) : (
            <div className="flex h-full flex-col items-center justify-center gap-2 text-center">
              <p className="text-lg font-semibold">Выберите место</p>
              <p className="max-w-[22rem] text-sm text-muted">
                Откройте время, пополните баланс, продлите или завершите сеанс, отправьте сообщение на экран.
              </p>
            </div>
          )}
        </aside>
      </div>
    </div>
  );
}

export default App;
