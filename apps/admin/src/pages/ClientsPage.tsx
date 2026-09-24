/**
 * Clients (cashier + owner): search, the client list with group, loyalty level and money, and a side panel to edit a
 * profile, redeem a promo code and see the latest wallet entries — or to register a new client. Blacklisting is the
 * owner's call: the server answers 403 to a cashier and the panel shows why.
 */
import { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';
import type { Transaction } from '@clubshell/contracts';
import { clubApi, type Client, type ClientGroup } from '@/api';
import { describe } from '@/errors';
import { money } from '@/format';
import { t } from '@/i18n';
import { Button, Field, Input, Note, PageHeader, Section, Table, Toggle, inputCls } from '@/ui';

type NoteState = { text: string; tone: 'ok' | 'err' } | null;

const nf = new Intl.NumberFormat('ru-RU');

function yearOf(text: string): number | null {
  const n = Number(text);
  return text.trim() && Number.isInteger(n) ? n : null;
}

function GroupDot({ group }: { group: ClientGroup | undefined }): JSX.Element {
  return (
    <span className="flex items-center gap-2">
      {group && <span className="h-2.5 w-2.5 shrink-0 rounded-full" style={{ background: group.color }} />}
      {group ? group.name : <span className="text-muted">—</span>}
    </span>
  );
}

function GroupChoice({
  groups,
  value,
  onChange,
}: {
  groups: ClientGroup[];
  value: string | null;
  onChange: (id: string | null) => void;
}): JSX.Element {
  const chip = (id: string | null, label: string, color?: string): JSX.Element => (
    <button
      key={id ?? 'none'}
      type="button"
      aria-pressed={value === id}
      onClick={() => onChange(id)}
      className={clsx(
        'choice focus-ring inline-flex h-8 items-center gap-2 rounded-md px-2.5 text-xs font-medium',
        value === id && 'choice-on',
      )}
    >
      {color && <span className="h-2 w-2 rounded-full" style={{ background: color }} />}
      {label}
    </button>
  );
  return (
    <div className="flex flex-wrap gap-1.5">
      {chip(null, t('Без группы'))}
      {groups.map((g) => chip(g.id, g.name, g.color))}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Detail panel
// ---------------------------------------------------------------------------------------------------------------------

function ClientPanel({
  client,
  groups,
  onSaved,
}: {
  client: Client;
  groups: ClientGroup[];
  onSaved: (c: Client) => void;
}): JSX.Element {
  const [name, setName] = useState(client.displayName);
  const [phone, setPhone] = useState(client.phone);
  const [telegram, setTelegram] = useState(client.telegram);
  const [birth, setBirth] = useState(client.birthYear ? String(client.birthYear) : '');
  const [groupId, setGroupId] = useState(client.groupId);
  const [note, setNote] = useState(client.note);
  const [blacklisted, setBlacklisted] = useState(client.blacklisted);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<NoteState>(null);
  const [promo, setPromo] = useState('');
  const [promoNote, setPromoNote] = useState<NoteState>(null);
  const [txs, setTxs] = useState<Transaction[]>([]);

  const loadTxs = useCallback(async () => {
    try {
      const r = await clubApi.clientTransactions(client.id);
      setTxs([...r.items].sort((a, b) => b.createdAt.localeCompare(a.createdAt)).slice(0, 12));
    } catch {
      setTxs([]);
    }
  }, [client.id]);

  useEffect(() => {
    setName(client.displayName);
    setPhone(client.phone);
    setTelegram(client.telegram);
    setBirth(client.birthYear ? String(client.birthYear) : '');
    setGroupId(client.groupId);
    setNote(client.note);
    setBlacklisted(client.blacklisted);
    setResult(null);
    setPromo('');
    setPromoNote(null);
  }, [client.id]);
  useEffect(() => {
    void loadTxs();
  }, [loadTxs]);

  const save = async (): Promise<void> => {
    setBusy(true);
    setResult(null);
    try {
      const input: Partial<Client> = {
        displayName: name.trim(),
        phone: phone.trim(),
        telegram: telegram.trim(),
        birthYear: yearOf(birth),
        groupId,
        note,
      };
      if (blacklisted !== client.blacklisted) input.blacklisted = blacklisted;
      const r = await clubApi.updateClient(client.id, input);
      onSaved(r.client);
      setResult({ text: t('Сохранено'), tone: 'ok' });
    } catch (e) {
      setResult({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  const redeem = async (): Promise<void> => {
    setBusy(true);
    setPromoNote(null);
    try {
      const r = await clubApi.redeemPromo(client.id, promo.trim());
      setPromo('');
      setPromoNote({ text: t('Промокод применён · баланс {sum}', { sum: money(r.balance) }), tone: 'ok' });
      onSaved({ ...client, balance: r.balance });
      void loadTxs();
    } catch (e) {
      setPromoNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-col gap-5">
      <header className="flex flex-col gap-1">
        <span className="label">
          {client.levelName} · {t('уровень {n}', { n: client.level })}
        </span>
        <h2 className="font-display text-2xl font-normal leading-tight tracking-tight">{client.displayName}</h2>
        <span className="font-mono text-xs text-muted">@{client.username}</span>
      </header>

      <dl className="grid grid-cols-2 divide-x divide-line overflow-hidden rounded-md border border-line bg-bg text-center">
        <div className="flex flex-col gap-1.5 px-3 py-3">
          <dt className="label">{t('Баланс')}</dt>
          <dd className="tnum text-lg font-semibold leading-none">{money(client.balance)}</dd>
        </div>
        <div className="flex flex-col gap-1.5 px-3 py-3">
          <dt className="label">{t('Бонусы')}</dt>
          <dd className="tnum text-lg font-semibold leading-none">{money(client.bonus)}</dd>
        </div>
      </dl>

      <section className="flex flex-col gap-4">
        <Field label={t('Имя')}>
          <Input value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('Телефон')}>
            <Input value={phone} inputMode="tel" onChange={(e) => setPhone(e.target.value)} />
          </Field>
          <Field label={t('Год рождения')}>
            <Input
              value={birth}
              inputMode="numeric"
              className="tnum"
              maxLength={4}
              onChange={(e) => setBirth(e.target.value.replace(/\D/g, ''))}
            />
          </Field>
        </div>
        <Field label={t('Telegram')}>
          <Input value={telegram} placeholder="@username" onChange={(e) => setTelegram(e.target.value)} />
        </Field>
        <div className="flex flex-col gap-1.5">
          <span className="label">{t('Группа')}</span>
          <GroupChoice groups={groups} value={groupId} onChange={setGroupId} />
        </div>
        <Field label={t('Заметка')}>
          <textarea
            rows={3}
            value={note}
            onChange={(e) => setNote(e.target.value)}
            className={clsx(inputCls, 'h-auto resize-y py-2')}
          />
        </Field>
        <Toggle checked={blacklisted} onChange={setBlacklisted} label={t('Чёрный список')} />
        <Note note={result} />
        <Button variant="primary" disabled={busy || !name.trim()} onClick={() => void save()}>
          {t('Сохранить')}
        </Button>
      </section>

      <section className="flex flex-col gap-2 border-t border-line pt-4">
        <Field label={t('Промокод')}>
          <div className="flex gap-1.5">
            <Input
              value={promo}
              className="font-mono uppercase"
              onChange={(e) => setPromo(e.target.value.toUpperCase())}
            />
            <Button disabled={busy || !promo.trim()} onClick={() => void redeem()}>
              {t('Применить')}
            </Button>
          </div>
        </Field>
        <Note note={promoNote} />
      </section>

      <section className="flex flex-col gap-2 border-t border-line pt-4">
        <span className="label">{t('Последние операции')}</span>
        {txs.length === 0 ? (
          <p className="text-sm text-muted">{t('Операций нет')}</p>
        ) : (
          <ul className="flex flex-col divide-y divide-line/60">
            {txs.map((x) => (
              <li key={x.id} className="flex items-baseline justify-between gap-3 py-2">
                <span className="flex min-w-0 flex-col">
                  <span className="truncate text-sm">{x.description}</span>
                  <span className="tnum font-mono text-[0.68rem] text-muted">
                    {new Date(x.createdAt).toLocaleString('ru-RU', {
                      day: '2-digit',
                      month: '2-digit',
                      hour: '2-digit',
                      minute: '2-digit',
                    })}
                  </span>
                </span>
                <span
                  className={clsx(
                    'tnum shrink-0 text-sm font-semibold',
                    x.amount.amount < 0 ? 'text-text' : 'text-success',
                  )}
                >
                  {x.amount.amount > 0 ? '+' : x.amount.amount < 0 ? '−' : ''}
                  {money({ ...x.amount, amount: Math.abs(x.amount.amount) })}
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function NewClientPanel({
  groups,
  onCreated,
  onCancel,
}: {
  groups: ClientGroup[];
  onCreated: (c: Client) => void;
  onCancel: () => void;
}): JSX.Element {
  const [name, setName] = useState('');
  const [login, setLogin] = useState('');
  const [phone, setPhone] = useState('');
  const [birth, setBirth] = useState('');
  const [groupId, setGroupId] = useState<string | null>(null);
  const [telegram, setTelegram] = useState('');
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<NoteState>(null);

  const create = async (): Promise<void> => {
    setBusy(true);
    setResult(null);
    try {
      const r = await clubApi.addClient({
        displayName: name.trim(),
        username: login.trim(),
        phone: phone.trim() || undefined,
        birthYear: yearOf(birth),
        groupId,
        telegram: telegram.trim() || undefined,
      });
      onCreated(r.client);
    } catch (e) {
      setResult({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-col gap-5">
      <h2 className="font-display text-2xl font-normal leading-tight tracking-tight">{t('Новый клиент')}</h2>
      <section className="flex flex-col gap-4">
        <Field label={t('Имя')}>
          <Input value={name} autoFocus onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t('Логин')}>
          <Input
            value={login}
            className="font-mono"
            onChange={(e) => setLogin(e.target.value.toLowerCase().replace(/\s/g, ''))}
          />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('Телефон')}>
            <Input value={phone} inputMode="tel" onChange={(e) => setPhone(e.target.value)} />
          </Field>
          <Field label={t('Год рождения')}>
            <Input
              value={birth}
              inputMode="numeric"
              className="tnum"
              maxLength={4}
              onChange={(e) => setBirth(e.target.value.replace(/\D/g, ''))}
            />
          </Field>
        </div>
        <Field label={t('Telegram')}>
          <Input value={telegram} placeholder="@username" onChange={(e) => setTelegram(e.target.value)} />
        </Field>
        <div className="flex flex-col gap-1.5">
          <span className="label">{t('Группа')}</span>
          <GroupChoice groups={groups} value={groupId} onChange={setGroupId} />
        </div>
        <Note note={result} />
        <div className="flex justify-end gap-2 border-t border-line pt-4">
          <Button variant="ghost" onClick={onCancel}>
            {t('Отменить')}
          </Button>
          <Button variant="primary" disabled={busy || !name.trim() || !login.trim()} onClick={() => void create()}>
            {t('Создать')}
          </Button>
        </div>
      </section>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------------------------------------------------

export default function ClientsPage(): JSX.Element {
  const [q, setQ] = useState('');
  const [items, setItems] = useState<Client[]>([]);
  const [groups, setGroups] = useState<ClientGroup[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | 'new' | null>(null);

  const load = useCallback(async (query: string) => {
    try {
      setItems((await clubApi.clients(query)).items);
      setError(null);
    } catch (e) {
      setError(describe(e));
    }
  }, []);

  useEffect(() => {
    const timer = setTimeout(() => void load(q.trim()), 300);
    return () => clearTimeout(timer);
  }, [q, load]);

  useEffect(() => {
    clubApi
      .settings()
      .then((s) => setGroups(s.groups))
      .catch(() => setGroups([]));
  }, []);

  const client = items.find((c) => c.id === selected) ?? null;
  const groupOf = (id: string | null): ClientGroup | undefined => groups.find((g) => g.id === id);
  const replace = (c: Client): void => setItems((list) => list.map((x) => (x.id === c.id ? c : x)));

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title={t('Клиенты')}
        actions={
          <>
            <Input
              type="search"
              className="w-72"
              value={q}
              placeholder={t('Имя, логин или телефон')}
              onChange={(e) => setQ(e.target.value)}
            />
            <Button variant="primary" onClick={() => setSelected('new')}>
              {t('Новый клиент')}
            </Button>
          </>
        }
      />

      {error && <Note note={{ text: error, tone: 'err' }} />}

      <div className="grid grid-cols-1 items-start gap-5 lg:grid-cols-[minmax(0,1fr)_24rem]">
        <Section bodyClassName="p-2">
          <Table
            rows={items}
            rowKey={(c) => c.id}
            selectedKey={selected}
            onRowClick={(c) => setSelected(c.id)}
            empty={q ? t('Никого не нашли') : t('Клиентов нет')}
            columns={[
              {
                key: 'name',
                title: t('Имя'),
                render: (c) => (
                  <span className="flex items-center gap-2">
                    <span className={clsx('font-medium', c.blacklisted && 'text-danger')}>{c.displayName}</span>
                    {c.blacklisted && (
                      <span className="rounded border border-danger/50 px-1.5 py-0.5 font-mono text-[0.62rem] uppercase tracking-[0.1em] text-danger">
                        {t('ЧС')}
                      </span>
                    )}
                  </span>
                ),
              },
              {
                key: 'login',
                title: t('Логин'),
                render: (c) => <span className="font-mono text-xs text-muted">{c.username}</span>,
              },
              {
                key: 'phone',
                title: t('Телефон'),
                render: (c) =>
                  c.phone ? <span className="tnum">{c.phone}</span> : <span className="text-muted">—</span>,
              },
              { key: 'group', title: t('Группа'), render: (c) => <GroupDot group={groupOf(c.groupId)} /> },
              { key: 'level', title: t('Уровень'), render: (c) => c.levelName },
              { key: 'balance', title: t('Баланс'), num: true, render: (c) => money(c.balance) },
              { key: 'visits', title: t('Визиты'), num: true, render: (c) => nf.format(c.visits) },
              {
                key: 'spent',
                title: t('Потрачено'),
                num: true,
                render: (c) => money({ amount: c.spent, currency: 'UZS' }),
              },
            ]}
          />
        </Section>

        <aside className="panel p-5">
          {selected === 'new' ? (
            <NewClientPanel
              groups={groups}
              onCancel={() => setSelected(null)}
              onCreated={(c) => {
                setItems((list) => [c, ...list.filter((x) => x.id !== c.id)]);
                setSelected(c.id);
              }}
            />
          ) : client ? (
            <ClientPanel client={client} groups={groups} onSaved={replace} />
          ) : (
            <p className="py-10 text-center text-sm text-muted">{t('Выберите клиента')}</p>
          )}
        </aside>
      </div>
    </div>
  );
}
