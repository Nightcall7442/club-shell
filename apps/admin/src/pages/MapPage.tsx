/**
 * The counter ("Карта"): the hall map as strict numbered cells per zone with a status legend that counts, and the
 * selected seat on the right. Everything a cashier does at
 * the desk — open time, add time, end a session, top up a wallet, message or lock a PC — is one click from that panel.
 * Polls `/admin/overview` every 2 s (the real console would follow the server's WebSocket).
 */
import { useCallback, useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import type { Session } from '@clubshell/contracts';
import { adminApi, clubApi, type Member, type Overview, type PriceQuote, type Seat } from '@/api';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { duration, minutesLabel, money } from '@/format';

const POLL_MS = 2000;
const MINUTE_PRESETS = [30, 60, 120, 180];
const TOPUP_PRESETS = [20_000, 50_000, 100_000, 200_000];

const STATUS: Record<Seat['pc']['status'], { label: string; short: string; dot: string; cell: string }> = {
  free: { label: 'Свободен', short: 'своб.', dot: 'bg-success', cell: 'border-success/40 text-text' },
  busy: { label: 'Занят', short: 'занят', dot: 'bg-accent', cell: 'border-accent/60 bg-accent/[0.06] text-accent' },
  locked: { label: 'Заблокирован', short: 'блок', dot: 'bg-danger', cell: 'border-danger/60 text-danger' },
  maintenance: {
    label: 'Обслуживание',
    short: 'сервис',
    dot: 'bg-fuchsia-400',
    cell: 'border-fuchsia-400/50 text-fuchsia-300',
  },
  booked: { label: 'Бронь', short: 'бронь', dot: 'bg-amber-300', cell: 'border-amber-300/50 text-amber-200' },
  offline: { label: 'Офлайн', short: 'офлайн', dot: 'bg-muted/40', cell: 'border-line text-muted/50' },
};

const LEGEND_ORDER: Seat['pc']['status'][] = ['free', 'busy', 'booked', 'locked', 'maintenance', 'offline'];

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
        'focus-ring inline-flex h-10 select-none items-center justify-center gap-2 whitespace-nowrap rounded-md px-3 text-sm font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-40',
        variant === 'primary' && 'cut-corners rounded-none text-on-accent hover:brightness-110',
        variant === 'secondary' && 'choice',
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
  const warn = seat.session !== null && left >= 0 && left <= 5 * 60;
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
      title={`${seat.pc.name} · ${t(s.label)}${seat.user ? ` · ${seat.user.displayName}` : ''}`}
      className={clsx(
        'focus-ring relative flex aspect-square flex-col items-center justify-center gap-1.5 rounded-md border bg-bg text-center transition-colors hover:bg-white/[0.04]',
        s.cell,
        selected && 'ring-2 ring-accent ring-offset-2 ring-offset-surface',
      )}
    >
      <span className="num-dot text-[1.6rem] leading-none">{String(seat.pc.number).padStart(2, '0')}</span>
      {seat.session ? (
        <span className={clsx('tnum font-mono text-[0.68rem] leading-none', warn ? 'text-danger' : 'text-muted')}>
          {left < 0 ? '∞' : duration(left)}
        </span>
      ) : (
        <span className="font-mono text-[0.62rem] uppercase leading-none tracking-[0.1em] text-muted">
          {t(s.short)}
        </span>
      )}
      {warn && <span className="absolute right-1.5 top-1.5 h-1.5 w-1.5 animate-pulse rounded-full bg-danger" />}
    </button>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }): JSX.Element {
  return (
    <label className="flex flex-col gap-1.5">
      <span className="label">{label}</span>
      {children}
    </label>
  );
}

const inputCls =
  'focus-ring h-10 w-full rounded-md border border-line bg-bg px-3 text-sm text-text placeholder:text-muted';

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

  const tariff = zoneTariffs.find((x) => x.id === tariffId);
  // The server's quote: weekday / holiday price and the best discount (group, loyalty level, happy hour).
  const [priceQuote, setPriceQuote] = useState<PriceQuote | null>(null);
  useEffect(() => {
    if (!tariffId) return undefined;
    let alive = true;
    clubApi
      .quote({ tariffId, pcId: seat.pc.id, minutes, userId: userId || null })
      .then((q) => alive && setPriceQuote(q))
      .catch(() => alive && setPriceQuote(null));
    return () => {
      alive = false;
    };
  }, [tariffId, minutes, userId, seat.pc.id]);
  const price = priceQuote?.total.amount ?? 0;
  const left = secondsLeft(seat.session);
  void tick;

  return (
    <div className="flex h-full flex-col gap-5 overflow-y-auto pr-1">
      <header className="flex flex-col gap-1">
        <span className="label flex items-center gap-2">
          <span className={clsx('h-2 w-2 rounded-full', STATUS[seat.pc.status].dot)} />
          {seat.pc.zone} · {t(STATUS[seat.pc.status].label)}
        </span>
        <h2 className="font-display text-2xl font-normal leading-tight tracking-tight">
          {seat.user ? seat.user.displayName : seat.pc.name}
        </h2>
        {seat.user && <span className="font-mono text-xs text-muted">{seat.pc.name}</span>}
      </header>

      {seat.session && seat.user && (
        <dl className="grid grid-cols-2 divide-x divide-line overflow-hidden rounded-md border border-line bg-bg text-center">
          <div className="flex flex-col gap-1.5 px-3 py-3">
            <dt className="label">{t('Осталось')}</dt>
            <dd className={clsx('num-dot text-2xl leading-none', left >= 0 && left <= 300 && 'text-danger')}>
              {left < 0 ? '∞' : duration(left)}
            </dd>
          </div>
          <div className="flex flex-col gap-1.5 px-3 py-3">
            <dt className="label">{t('Баланс')}</dt>
            <dd className="tnum text-lg font-semibold leading-none">{money(seat.user.balance)}</dd>
          </div>
        </dl>
      )}

      {note && (
        <p
          className={clsx(
            'rounded-md px-3 py-2 text-sm',
            note.tone === 'ok' ? 'bg-success/10 text-success' : 'bg-danger/10 text-danger',
          )}
        >
          {note.text}
        </p>
      )}

      {seat.session && seat.user ? (
        <>
          <section className="flex flex-col gap-4">
            <Field label={t('Добавить время')}>
              <div className="grid grid-cols-4 gap-1.5">
                {MINUTE_PRESETS.map((m) => (
                  <Button
                    key={m}
                    disabled={busy !== null}
                    onClick={() =>
                      void run(`ext-${m}`, async () => {
                        const r = await adminApi.extend({ pcId: seat.pc.id, minutes: m });
                        return t('Добавлено {time} · списано {sum}', { time: minutesLabel(m), sum: money(r.charged) });
                      })
                    }
                  >
                    <span className="whitespace-nowrap">+{minutesLabel(m)}</span>
                  </Button>
                ))}
              </div>
            </Field>

            <Field label={t('Пополнить баланс')}>
              <div className="grid grid-cols-4 gap-1.5">
                {TOPUP_PRESETS.map((a) => (
                  <Button
                    key={a}
                    disabled={busy !== null}
                    onClick={() =>
                      void run(`top-${a}`, async () => {
                        const r = await adminApi.topUp({ userId: seat.user?.id ?? '', amount: a * 100 });
                        return t('Баланс пополнен · теперь {sum}', { sum: money(r.balance) });
                      })
                    }
                  >
                    {t('{n}к', { n: a / 1000 })}
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
                    ? t('Сеанс завершён · возврат {sum}', { sum: money(r.refunded) })
                    : t('Сеанс завершён');
                })
              }
            >
              {t('Завершить сеанс')}
            </Button>
            <div className="grid grid-cols-2 gap-1.5">
              <Button
                variant="ghost"
                disabled={busy !== null}
                onClick={() =>
                  void run('lock', async () => {
                    await adminApi.command(seat.pc.id, { kind: 'lock' });
                    return t('ПК заблокирован');
                  })
                }
              >
                {t('Заблокировать')}
              </Button>
              <Button
                variant="ghost"
                disabled={busy !== null}
                onClick={() =>
                  void run('unlock', async () => {
                    await adminApi.command(seat.pc.id, { kind: 'unlock' });
                    return t('ПК разблокирован');
                  })
                }
              >
                {t('Разблокировать')}
              </Button>
            </div>
          </section>
        </>
      ) : (
        <section className="flex flex-col gap-4">
          <Field label={t('Клиент')}>
            <select className={inputCls} value={userId} onChange={(e) => setUserId(e.target.value)}>
              <option value="">{t('— выберите клиента —')}</option>
              {members.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.displayName} · {money(m.balance)}
                </option>
              ))}
            </select>
          </Field>
          <Field label={t('Тариф')}>
            <select className={inputCls} value={tariffId} onChange={(e) => setTariffId(e.target.value)}>
              {zoneTariffs.map((tf) => (
                <option key={tf.id} value={tf.id}>
                  {tf.name} · {money(tf.isPackage ? (tf.packagePrice ?? tf.pricePerHour) : tf.pricePerHour)}
                  {tf.isPackage ? '' : t(' / ч')}
                </option>
              ))}
            </select>
          </Field>
          {!tariff?.isPackage && (
            <Field label={t('Время')}>
              <div className="grid grid-cols-4 gap-1.5">
                {MINUTE_PRESETS.map((m) => (
                  <Button key={m} className={clsx(m === minutes && 'choice-on')} onClick={() => setMinutes(m)}>
                    {minutesLabel(m)}
                  </Button>
                ))}
              </div>
            </Field>
          )}
          <div className="flex items-center justify-between gap-3 border-t border-line pt-4">
            <span className="flex flex-col gap-1">
              <span className="label">{t('К списанию')}</span>
              <span className="tnum text-lg font-semibold text-text">{money({ amount: price, currency: 'UZS' })}</span>
              {priceQuote && priceQuote.discountPct > 0 && (
                <span className="text-xs text-success">
                  −{priceQuote.discountPct}% · {priceQuote.discountReason}
                </span>
              )}
              {priceQuote && priceQuote.dayPct !== 100 && (
                <span className="text-xs text-muted">
                  {t('Цена дня')}: {priceQuote.dayPct}%
                </span>
              )}
            </span>
            <Button
              variant="primary"
              disabled={busy !== null || !userId || !tariffId || seat.pc.status === 'maintenance'}
              onClick={() =>
                void run('open', async () => {
                  const r = await adminApi.openSession({ pcId: seat.pc.id, userId, tariffId, minutes });
                  return t('Сеанс открыт · списано {sum}', { sum: money(r.charged) });
                })
              }
            >
              {t('Открыть сеанс')}
            </Button>
          </div>
        </section>
      )}

      <section className="mt-auto flex flex-col gap-2 border-t border-line pt-4">
        <Field label={t('Сообщение на экран')}>
          <div className="flex gap-1.5">
            <input
              className={inputCls}
              value={message}
              placeholder={t('Закрываемся через 20 минут')}
              onChange={(e) => setMessage(e.target.value)}
            />
            <Button
              disabled={busy !== null || message.trim().length === 0}
              onClick={() =>
                void run('msg', async () => {
                  await adminApi.command(seat.pc.id, { kind: 'message', text: message.trim() });
                  setMessage('');
                  return t('Сообщение отправлено');
                })
              }
            >
              {t('Отправить')}
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
                return t('ПК перезагружается');
              })
            }
          >
            {t('Перезагрузить')}
          </Button>
          <Button
            variant="ghost"
            disabled={busy !== null}
            onClick={() =>
              void run('shutdown', async () => {
                await adminApi.command(seat.pc.id, { kind: 'shutdown' });
                return t('ПК выключается');
              })
            }
          >
            {t('Выключить')}
          </Button>
        </div>
      </section>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// App
// ---------------------------------------------------------------------------------------------------------------------

export function MapPage(): JSX.Element {
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

  const counts = useMemo(() => {
    const c = new Map<Seat['pc']['status'], number>();
    for (const x of seats) {
      c.set(x.pc.status, (c.get(x.pc.status) ?? 0) + 1);
    }
    return c;
  }, [seats]);
  void tick;

  return (
    <div className="flex h-full min-h-0 flex-col gap-3">
      {error && <p className="rounded-md bg-danger/10 px-3 py-1.5 text-sm text-danger">{error}</p>}
      <div className="grid min-h-0 flex-1 grid-cols-1 gap-5 lg:grid-cols-[minmax(0,1fr)_24rem]">
        <div className="flex min-h-0 flex-col gap-5">
          <div className="panel min-h-0 flex-1 overflow-y-auto p-5">
            <h1 className="mb-5 font-display text-2xl font-light tracking-tight">{t('Карта зала')}</h1>
            <div className="flex flex-col gap-6">
              {zones.map(([zone, list]) => (
                <section key={zone} className="flex flex-col gap-2.5">
                  <h2 className="flex items-baseline justify-between gap-2 border-b border-line pb-2">
                    <span className="label text-text">{zone}</span>
                    <span className="tnum font-mono text-xs text-muted">
                      {t('{free}/{total} свободно', {
                        free: list.filter((x) => x.pc.status === 'free').length,
                        total: list.length,
                      })}
                    </span>
                  </h2>
                  <div className="grid grid-cols-[repeat(auto-fill,minmax(4.5rem,1fr))] gap-2">
                    {list.map((x) => (
                      <SeatTile
                        key={x.pc.id}
                        seat={x}
                        tick={tick}
                        selected={x.pc.id === selected}
                        onSelect={() => setSelected(x.pc.id)}
                      />
                    ))}
                  </div>
                </section>
              ))}
              {seats.length === 0 && !error && <p className="text-sm text-muted">{t('Нет данных о ПК')}</p>}
            </div>
          </div>

          {/* Legend that counts: every status, how many seats are in it right now */}
          <ul className="panel grid shrink-0 grid-cols-3 divide-x divide-line xl:grid-cols-6">
            {LEGEND_ORDER.map((k) => (
              <li key={k} className="flex items-center justify-between gap-3 px-4 py-3">
                <span className="flex items-center gap-2.5 text-sm">
                  <span className={clsx('h-3 w-3 rounded-[3px] border-2 bg-transparent', STATUS[k].cell)} />
                  {t(STATUS[k].label)}
                </span>
                <span className="num-dot text-lg leading-none">{String(counts.get(k) ?? 0).padStart(2, '0')}</span>
              </li>
            ))}
          </ul>
        </div>

        <aside className="panel min-h-0 p-5">
          {seat && data ? (
            <SeatPanel seat={seat} members={data.users} tariffs={data.tariffs} tick={tick} onDone={() => void load()} />
          ) : (
            <div className="flex h-full flex-col items-center justify-center gap-2 text-center">
              <p className="font-display text-lg tracking-tight">{t('Выберите место')}</p>
              <p className="max-w-[20rem] text-sm text-muted">
                {t('Откройте время, пополните баланс, продлите или завершите сеанс, отправьте сообщение на экран.')}
              </p>
            </div>
          )}
        </aside>
      </div>
    </div>
  );
}

export default MapPage;
