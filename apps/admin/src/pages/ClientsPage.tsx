/**
 * Clients (cashier + owner): search, the client list with group, loyalty level and money, and a side card (variant F:
 * the client's head on the blueprint grid, balance / bonus / visits / spent wells) to top up the balance (the same
 * «Пополнить» sheet as the search's), edit a profile, bind a club card, reset the sign-in password, redeem a promo code
 * and see the latest wallet entries — or to register a new client with a password and card, so they can sign in on a
 * PC. A password the console generates is shown to the cashier once. Blacklisting is the owner's call: the server
 * answers 403 to a cashier and the panel says. `#/clients/new` (the counter's "+ Новый клиент") opens the registration
 * straight away.
 */
import { useCallback, useEffect, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import type { Transaction } from '@clubshell/contracts';
import { clubApi, type Client, type ClientGroup } from '@/api';
import { describe } from '@/errors';
import { money, moneyParts } from '@/format';
import { dateLocale, t } from '@/i18n';
import { PersonIcon, SearchIcon, UsersIcon } from '@/icons';
import { TopUpSheet } from '@/paybox';
import {
  Badge,
  Button,
  Chip,
  EmptyState,
  Field,
  Input,
  Note,
  PageHeader,
  Section,
  Sum,
  Table,
  Toggle,
  Well,
  inputCls,
} from '@/ui';

type NoteState = { text: string; tone: 'ok' | 'err' } | null;

const nf = new Intl.NumberFormat('ru-RU');

const PASSWORD_CHARS = 'abcdefghjkmnpqrstuvwxyz23456789';

/** A temporary sign-in password: 8 characters without look-alikes (0/o, 1/l/i), easy to read out at the counter. */
function tempPassword(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(8)), (b) => PASSWORD_CHARS[b % PASSWORD_CHARS.length]).join(
    '',
  );
}

/**
 * A generated password, shown once for the cashier to pass on to the client: a status note whose one `.font-mono` child
 * is the password (the E2E reads it there).
 */
function IssuedPassword({ password }: { password: string }): JSX.Element {
  return (
    <Note tone="ok" role="status">
      <span className="block text-[12.5px] leading-5 text-soft">
        {t('Временный пароль — сообщите клиенту, он показывается один раз')}
      </span>
      <span className="mt-1.5 block font-mono text-xl font-semibold leading-7 tracking-[0.12em] text-hi">
        {password}
      </span>
    </Note>
  );
}

function yearOf(text: string): number | null {
  const n = Number(text);
  return text.trim() && Number.isInteger(n) ? n : null;
}

/** Up to two initials of a name. */
function initialsOf(name: string): string {
  return name
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((w) => w.charAt(0).toUpperCase())
    .join('');
}

/** The group's colour (from the club's data) as a dot. */
function ColorDot({ color }: { color: string }): JSX.Element {
  return <span aria-hidden="true" className="h-2 w-2 shrink-0 rounded-full" style={{ background: color }} />;
}

function GroupDot({ group }: { group: ClientGroup | undefined }): JSX.Element {
  return (
    <span className="flex items-center gap-2">
      {group && <ColorDot color={group.color} />}
      {group ? <span className="text-soft">{group.name}</span> : <span className="text-muted">—</span>}
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
    <Chip
      key={id ?? 'none'}
      tone="outlined"
      pressed={value === id}
      icon={color ? <ColorDot color={color} /> : undefined}
      onClick={() => onChange(id)}
    >
      {label}
    </Chip>
  );
  return (
    <div className="flex flex-wrap gap-1.5">
      {chip(null, t('Без группы'))}
      {groups.map((g) => chip(g.id, g.name, g.color))}
    </div>
  );
}

/**
 * The card's head (spec §9, «Клиенты»): 96 px on the blueprint grid with the accent hairline on top, the avatar and the
 * name over its foot.
 */
function CardBand({ children }: { children: ReactNode }): JSX.Element {
  return (
    <div className="relative flex h-24 shrink-0 items-end overflow-hidden px-4 pb-3.5">
      <span aria-hidden="true" className="hud-grid absolute inset-0" />
      <span
        aria-hidden="true"
        className="absolute inset-0"
        style={{
          background:
            'radial-gradient(360px 140px at 85% 0%, rgb(var(--c-accent) / 0.1), transparent 70%), linear-gradient(180deg, rgb(var(--c-art) / 0) 35%, rgb(var(--c-art) / 0.85) 100%)',
        }}
      />
      <span
        aria-hidden="true"
        className="absolute inset-x-0 top-0 h-px"
        style={{
          background:
            'linear-gradient(90deg, rgb(var(--c-accent) / 0) 0%, rgb(var(--c-accent) / 0.7) 50%, rgb(var(--c-accent) / 0) 100%)',
        }}
      />
      <div className="relative flex min-w-0 items-center gap-3.5">{children}</div>
    </div>
  );
}

/** 48 px round avatar: initials in Unbounded, or an icon. */
function Avatar({ children }: { children: ReactNode }): JSX.Element {
  return (
    <span
      aria-hidden="true"
      className="flex h-12 w-12 shrink-0 items-center justify-center rounded-full border border-accent/[0.22] bg-accent/[0.08] font-display text-base font-semibold text-hi"
    >
      {children}
    </span>
  );
}

/** A block of the card under a hairline, with a mono caption. */
function CardBlock({
  label,
  children,
  className,
}: {
  label?: string;
  children: ReactNode;
  className?: string;
}): JSX.Element {
  return (
    <section className={clsx('flex flex-col gap-3 border-t border-accent/[0.08] pt-4', className)}>
      {label && <span className="label text-text">{label}</span>}
      {children}
    </section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Detail panel
// ---------------------------------------------------------------------------------------------------------------------

function ClientPanel({
  client,
  groups,
  issuedPassword,
  onSaved,
  onPasswordIssued,
  onToppedUp,
}: {
  client: Client;
  groups: ClientGroup[];
  issuedPassword: string | null;
  onSaved: (c: Client) => void;
  onPasswordIssued: (password: string) => void;
  /** A top-up went through: the list reloads the balance. */
  onToppedUp: () => void;
}): JSX.Element {
  const [name, setName] = useState(client.displayName);
  const [phone, setPhone] = useState(client.phone);
  const [card, setCard] = useState(client.cardId ?? '');
  const [loginNote, setLoginNote] = useState<NoteState>(null);
  const [birth, setBirth] = useState(client.birthYear ? String(client.birthYear) : '');
  const [groupId, setGroupId] = useState(client.groupId);
  const [note, setNote] = useState(client.note);
  const [blacklisted, setBlacklisted] = useState(client.blacklisted);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<NoteState>(null);
  const [promo, setPromo] = useState('');
  const [promoNote, setPromoNote] = useState<NoteState>(null);
  const [txs, setTxs] = useState<Transaction[]>([]);
  const [topUp, setTopUp] = useState(false);

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
    setCard(client.cardId ?? '');
    setLoginNote(null);
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

  const bindCard = async (): Promise<void> => {
    setBusy(true);
    setLoginNote(null);
    try {
      const r = await clubApi.bindCard(client.id, card.trim() || null);
      onSaved(r.client);
      setLoginNote({ text: r.client.cardId ? t('Карта привязана') : t('Карта отвязана'), tone: 'ok' });
    } catch (e) {
      setLoginNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  const resetPassword = async (): Promise<void> => {
    const password = tempPassword();
    setBusy(true);
    setLoginNote(null);
    try {
      await clubApi.setClientPassword(client.id, password);
      onPasswordIssued(password);
    } catch (e) {
      setLoginNote({ text: describe(e), tone: 'err' });
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

  const group = groups.find((g) => g.id === client.groupId);
  const tail = client.phone.replace(/\D/g, '').slice(-4);
  const balance = moneyParts(client.balance.amount);
  const bonus = moneyParts(client.bonus.amount);
  const spent = moneyParts(client.spent);

  return (
    <div className="flex flex-col">
      <CardBand>
        <Avatar>{initialsOf(client.displayName)}</Avatar>
        <div className="flex min-w-0 flex-col gap-1.5">
          <h2 className="truncate font-display text-[22px] font-medium leading-7 tracking-[-0.01em] text-hi">
            {client.displayName}
          </h2>
          <span className="truncate font-mono text-xs leading-4 text-dim">
            @{client.username}
            {tail && ` · ••${tail}`}
          </span>
        </div>
      </CardBand>

      <div className="flex flex-col gap-4 px-4 pb-5 pt-3.5">
        <div className="flex flex-wrap gap-1.5">
          <Badge tone="accent">
            {client.levelName} · {t('уровень {n}', { n: client.level })}
          </Badge>
          {group && (
            <Badge tone="neutral">
              <ColorDot color={group.color} />
              {group.name}
            </Badge>
          )}
          {client.blacklisted && <Badge tone="danger">{t('Чёрный список')}</Badge>}
        </div>

        <div className="grid grid-cols-2 gap-2">
          <Well label={t('Баланс')} value={balance.num} unit={balance.unit} size={22} />
          <Well label={t('Бонусы')} value={bonus.num} unit={bonus.unit} size={22} tone="accent" />
          <Well label={t('Визиты')} value={nf.format(client.visits)} size={18} />
          <Well label={t('Потрачено')} value={spent.num} unit={spent.unit} size={18} />
        </div>

        {/* Guests are never topped up (D-36): their debts go through the map's settle sheet. */}
        {client.role !== 'guest' && (
          <Button variant="primary" size="lg" className="w-full" onClick={() => setTopUp(true)}>
            {t('Пополнить')}
          </Button>
        )}

        <CardBlock className="gap-4">
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
          <div className="flex flex-col gap-2">
            <span className="label-sm">{t('Группа')}</span>
            <GroupChoice groups={groups} value={groupId} onChange={setGroupId} />
          </div>
          <Field label={t('Заметка')}>
            <textarea
              rows={3}
              value={note}
              onChange={(e) => setNote(e.target.value)}
              className={clsx(inputCls, 'h-auto min-h-[88px] resize-y py-2.5 leading-5')}
            />
          </Field>
          <Toggle checked={blacklisted} onChange={setBlacklisted} label={t('Чёрный список')} />
          <Note note={result} />
          <Button className="self-end" disabled={busy || !name.trim()} onClick={() => void save()}>
            {t('Сохранить')}
          </Button>
        </CardBlock>

        <CardBlock label={t('Вход на ПК')}>
          <Field label={t('Номер карты')}>
            <div className="flex gap-2">
              <Input value={card} className="font-mono" onChange={(e) => setCard(e.target.value)} />
              <Button
                className="shrink-0"
                disabled={busy || card.trim() === (client.cardId ?? '')}
                onClick={() => void bindCard()}
              >
                {card.trim() ? t('Привязать карту') : t('Отвязать карту')}
              </Button>
            </div>
          </Field>
          <Note note={loginNote} />
          {issuedPassword && <IssuedPassword password={issuedPassword} />}
          <Button variant="tertiary" className="self-start" disabled={busy} onClick={() => void resetPassword()}>
            {t('Сбросить пароль')}
          </Button>
        </CardBlock>

        <CardBlock>
          <Field label={t('Промокод')}>
            <div className="flex gap-2">
              <Input
                value={promo}
                className="font-mono uppercase"
                onChange={(e) => setPromo(e.target.value.toUpperCase())}
              />
              <Button className="shrink-0" disabled={busy || !promo.trim()} onClick={() => void redeem()}>
                {t('Применить')}
              </Button>
            </div>
          </Field>
          <Note note={promoNote} />
        </CardBlock>

        <CardBlock label={t('Последние операции')} className="gap-1.5">
          {txs.length === 0 ? (
            <p className="text-[13px] text-muted">{t('Операций нет')}</p>
          ) : (
            <ul className="flex flex-col">
              {txs.map((x) => {
                const sum = moneyParts(Math.abs(x.amount.amount));
                return (
                  <li
                    key={x.id}
                    className="flex items-baseline justify-between gap-3 border-t border-accent/[0.07] py-2 first:border-t-0"
                  >
                    <span className="flex min-w-0 flex-col gap-0.5">
                      <span className="truncate text-[12.5px] font-medium leading-4 text-text">{x.description}</span>
                      <span className="tnum font-mono text-[10.5px] leading-4 text-muted">
                        {new Date(x.createdAt).toLocaleString(dateLocale(), {
                          day: '2-digit',
                          month: '2-digit',
                          hour: '2-digit',
                          minute: '2-digit',
                        })}
                      </span>
                    </span>
                    {/* Money is never coloured by sign: «+» in the body colour, a real «−» dimmed. */}
                    <span
                      className={clsx(
                        'tnum shrink-0 whitespace-nowrap font-mono text-[12px] font-semibold',
                        x.amount.amount < 0 ? 'text-dim' : 'text-text',
                      )}
                    >
                      {x.amount.amount > 0 ? '+' : x.amount.amount < 0 ? '−' : ''}
                      {sum.num} <span className="font-sans font-medium text-muted">{sum.unit}</span>
                    </span>
                  </li>
                );
              })}
            </ul>
          )}
        </CardBlock>
      </div>

      {topUp && (
        <TopUpSheet
          payee={{ id: client.id, displayName: client.displayName, balance: client.balance, bonus: client.bonus }}
          onClose={() => setTopUp(false)}
          onDone={() => {
            onToppedUp();
            void loadTxs();
          }}
        />
      )}
    </div>
  );
}

function NewClientPanel({
  groups,
  onCreated,
  onCancel,
}: {
  groups: ClientGroup[];
  /** `password` is the generated one to show once; `null` when the cashier typed it. */
  onCreated: (c: Client, password: string | null) => void;
  onCancel: () => void;
}): JSX.Element {
  const [name, setName] = useState('');
  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [card, setCard] = useState('');
  const [phone, setPhone] = useState('');
  const [birth, setBirth] = useState('');
  const [groupId, setGroupId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<NoteState>(null);

  const create = async (): Promise<void> => {
    setBusy(true);
    setResult(null);
    const sent = password || tempPassword();
    try {
      const r = await clubApi.addClient({
        displayName: name.trim(),
        username: login.trim(),
        password: sent,
        cardId: card.trim() || null,
        phone: phone.trim() || undefined,
        birthYear: yearOf(birth),
        groupId,
      });
      onCreated(r.client, password ? null : sent);
    } catch (e) {
      setResult({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-col">
      <CardBand>
        <Avatar>
          <PersonIcon size={20} className="text-accent" />
        </Avatar>
        <h2 className="truncate font-display text-[22px] font-medium leading-7 tracking-[-0.01em] text-hi">
          {t('Новый клиент')}
        </h2>
      </CardBand>
      <section className="flex flex-col gap-4 px-4 pb-5 pt-4">
        <div className="grid grid-cols-2 items-start gap-x-3 gap-y-4">
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
          <Field label={t('Пароль')} hint={t('Не короче 4 символов. Пусто — выдадим временный')}>
            <Input
              type="password"
              autoComplete="new-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </Field>
          <Field label={t('Номер карты')}>
            <Input value={card} className="font-mono" onChange={(e) => setCard(e.target.value)} />
          </Field>
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
        <div className="flex flex-col gap-2">
          <span className="label-sm">{t('Группа')}</span>
          <GroupChoice groups={groups} value={groupId} onChange={setGroupId} />
        </div>
        <Note note={result} />
        <div className="flex justify-end gap-2 border-t border-accent/[0.08] pt-4">
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
  // A generated password stays on screen only until the cashier moves to another client.
  const [issued, setIssued] = useState<{ id: string; password: string } | null>(null);
  const select = (id: string | 'new' | null): void => {
    setSelected(id);
    setIssued(null);
  };

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

  // "+ Новый клиент" from the client search: open the registration, then drop the suffix so a reload does not.
  useEffect(() => {
    const check = (): void => {
      if (!/^#\/?clients\/new$/.test(window.location.hash)) return;
      setSelected('new');
      setIssued(null);
      window.history.replaceState(null, '', '#/clients');
    };
    check();
    window.addEventListener('hashchange', check);
    return () => window.removeEventListener('hashchange', check);
  }, []);

  const client = items.find((c) => c.id === selected) ?? null;
  const groupOf = (id: string | null): ClientGroup | undefined => groups.find((g) => g.id === id);
  const replace = (c: Client): void => setItems((list) => list.map((x) => (x.id === c.id ? c : x)));

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title={t('Клиенты')}
        actions={
          <>
            <div className="relative w-72 max-w-full">
              <SearchIcon
                size={16}
                className="pointer-events-none absolute left-3.5 top-1/2 -translate-y-1/2 text-muted"
              />
              <Input
                type="search"
                className="pl-10"
                value={q}
                placeholder={t('Имя, логин или телефон')}
                onChange={(e) => setQ(e.target.value)}
              />
            </div>
            <Button variant="primary" onClick={() => select('new')}>
              {t('Новый клиент')}
            </Button>
          </>
        }
      />

      {error && <Note note={{ text: error, tone: 'err' }} />}

      <div className="grid grid-cols-1 items-start gap-4 lg:grid-cols-[minmax(0,1fr)_400px]">
        <Section bodyClassName="p-2">
          <Table
            rows={items}
            rowKey={(c) => c.id}
            selectedKey={selected}
            onRowClick={(c) => select(c.id)}
            empty={q ? t('Никого не нашли') : t('Клиентов нет')}
            columns={[
              {
                key: 'name',
                title: t('Имя'),
                render: (c) => (
                  <span className="flex items-center gap-2">
                    <span className="font-medium text-text">{c.displayName}</span>
                    {c.blacklisted && <Badge tone="danger">{t('ЧС')}</Badge>}
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
                  c.phone ? <span className="tnum text-dim">{c.phone}</span> : <span className="text-muted">—</span>,
              },
              { key: 'group', title: t('Группа'), render: (c) => <GroupDot group={groupOf(c.groupId)} /> },
              { key: 'level', title: t('Уровень'), render: (c) => <span className="text-dim">{c.levelName}</span> },
              { key: 'balance', title: t('Баланс'), num: true, render: (c) => <Sum minor={c.balance.amount} /> },
              { key: 'visits', title: t('Визиты'), num: true, render: (c) => nf.format(c.visits) },
              { key: 'spent', title: t('Потрачено'), num: true, render: (c) => <Sum minor={c.spent} /> },
            ]}
          />
        </Section>

        <aside className="panel-solid min-w-0 overflow-hidden">
          {selected === 'new' ? (
            <NewClientPanel
              groups={groups}
              onCancel={() => select(null)}
              onCreated={(c, password) => {
                setItems((list) => [c, ...list.filter((x) => x.id !== c.id)]);
                setSelected(c.id);
                setIssued(password ? { id: c.id, password } : null);
              }}
            />
          ) : client ? (
            <ClientPanel
              client={client}
              groups={groups}
              issuedPassword={issued?.id === client.id ? issued.password : null}
              onSaved={replace}
              onPasswordIssued={(password) => setIssued({ id: client.id, password })}
              onToppedUp={() => void load(q.trim())}
            />
          ) : (
            <EmptyState className="px-6 py-14" icon={<UsersIcon size={22} />} title={t('Выберите клиента')} />
          )}
        </aside>
      </div>
    </div>
  );
}
