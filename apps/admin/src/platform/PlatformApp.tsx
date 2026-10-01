/**
 * Platform administration (server `/api/v1/platform/*`, beyond the contract): the operator of a server that holds several
 * clubs creates clubs, hands out their PC enrollment keys, adds owners, resets a forgotten owner PIN and disables a club.
 * Russian only: this page is for the operator, not for club staff. The platform key lives in sessionStorage, so closing the
 * tab signs out.
 */
import { useCallback, useEffect, useState } from 'react';
import { Button, Field, Input, Note, PageHeader, Section } from '@/ui';

const BASE = (import.meta.env['VITE_ADMIN_API'] as string | undefined) ?? 'http://localhost:8080/api/v1';
const KEY_STORAGE = 'clubshell.platform.key';
const ZONES = ['Asia/Tashkent', 'Asia/Samarkand', 'Asia/Almaty', 'Asia/Bishkek', 'Asia/Dushanbe', 'Europe/Moscow'];

interface Owner {
  id: string;
  name: string;
  active: boolean;
}

interface Club {
  id: string;
  name: string;
  code: string;
  timeZone: string;
  disabled: boolean;
  createdAt: string;
  pcs: number;
  keyFromConfig: boolean;
  owners: Owner[];
}

class PlatformError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly details: Record<string, unknown> | null,
  ) {
    super(message);
  }
}

function readKey(): string {
  try {
    return sessionStorage.getItem(KEY_STORAGE) ?? '';
  } catch {
    return '';
  }
}

function writeKey(value: string): void {
  try {
    if (value) sessionStorage.setItem(KEY_STORAGE, value);
    else sessionStorage.removeItem(KEY_STORAGE);
  } catch {
    // private mode: the key is typed again after a reload
  }
}

async function call<T>(key: string, method: string, path: string, body?: unknown): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${BASE}/platform${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${key}`,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new PlatformError(0, 'Нет связи с сервером', null);
  }
  const text = await res.text();
  const json = text ? (JSON.parse(text) as Record<string, unknown>) : {};
  if (!res.ok) {
    const error = (json['error'] ?? {}) as { message?: string; details?: Record<string, unknown> };
    throw new PlatformError(res.status, error.message ?? `HTTP ${res.status}`, error.details ?? null);
  }
  return json as T;
}

/** Plain-language text of a failed call. */
function explain(e: unknown): string {
  if (!(e instanceof PlatformError)) return e instanceof Error ? e.message : 'Ошибка';
  if (e.status === 401) return 'Неверный ключ суперадмина';
  if (e.status === 404 && !e.details?.['what']) return 'Суперадминка выключена на сервере: задайте Platform__AdminKey';
  const field = e.details?.['field'];
  const reason = e.details?.['reason'];
  if (field === 'code' && reason === 'taken') return 'Такой код клуба уже занят';
  if (field === 'code') return 'Код клуба: от 3 до 12 латинских букв и цифр';
  if (field === 'timeZone') return 'Неизвестный часовой пояс';
  if ((field === 'pin' || field === 'ownerPin') && reason === 'taken') return 'Этот PIN уже занят в этом клубе';
  if (field === 'pin' || field === 'ownerPin') return 'PIN: от 4 до 8 цифр';
  if (field) return `Заполните поле «${String(field)}»`;
  if (reason === 'keyFromConfig') return 'Ключ этого клуба задаётся на Railway (Club__EnrollmentKey), а не здесь';
  return e.message;
}

export function PlatformApp(): JSX.Element {
  const [key, setKey] = useState(readKey);
  const [clubs, setClubs] = useState<Club[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(
    async (k: string): Promise<void> => {
      try {
        const r = await call<{ items: Club[] }>(k, 'GET', '/clubs');
        setClubs(r.items);
        setError(null);
      } catch (e) {
        setClubs(null);
        setError(explain(e));
        if (e instanceof PlatformError && e.status === 401) {
          writeKey('');
          setKey('');
        }
      }
    },
    [setClubs],
  );

  useEffect(() => {
    if (key) void load(key);
  }, [key, load]);

  if (!key || clubs === null) {
    return (
      <SignIn
        error={error}
        onKey={(k) => {
          writeKey(k);
          setKey(k);
          void load(k);
        }}
      />
    );
  }

  return (
    <main className="mx-auto flex max-w-5xl flex-col gap-6 p-6">
      <PageHeader
        title="Суперадмин · клубы"
        actions={
          <Button
            variant="ghost"
            onClick={() => {
              writeKey('');
              setKey('');
              setClubs(null);
            }}
          >
            Выйти
          </Button>
        }
      />
      <CreateClub apiKey={key} onCreated={() => void load(key)} />
      {clubs.map((club) => (
        <ClubCard key={club.id} club={club} apiKey={key} onChanged={() => void load(key)} />
      ))}
    </main>
  );
}

function SignIn({ error, onKey }: { error: string | null; onKey: (key: string) => void }): JSX.Element {
  const [value, setValue] = useState('');
  return (
    <div className="flex h-screen items-center justify-center p-6">
      <form
        className="panel flex w-[24rem] flex-col gap-5 p-8"
        onSubmit={(e) => {
          e.preventDefault();
          if (value.trim()) onKey(value.trim());
        }}
      >
        <div className="flex items-center gap-3">
          <span aria-hidden="true" className="h-6 w-6 rotate-45 border border-accent/70" />
          <span className="font-display text-lg tracking-tight">ClubShell · суперадмин</span>
        </div>
        <Field label="Ключ суперадмина" hint="Значение Platform__AdminKey на Railway">
          <Input type="password" autoComplete="off" value={value} onChange={(e) => setValue(e.target.value)} />
        </Field>
        <Button type="submit" variant="primary" disabled={!value.trim()}>
          Войти
        </Button>
        {error && <p className="text-center text-sm text-danger">{error}</p>}
      </form>
    </div>
  );
}

/** A secret shown once, with a copy button. */
function Secret({ title, value, hint }: { title: string; value: string; hint: string }): JSX.Element {
  const [copied, setCopied] = useState(false);
  return (
    <div className="flex flex-col gap-2 rounded-md bg-success/10 p-4">
      <span className="label text-success">{title}</span>
      <div className="flex flex-wrap items-center gap-3">
        <code className="select-all break-all font-mono text-lg">{value}</code>
        <Button
          size="sm"
          onClick={() => {
            void navigator.clipboard?.writeText(value).then(() => setCopied(true));
          }}
        >
          {copied ? 'Скопировано' : 'Скопировать'}
        </Button>
      </div>
      <span className="text-xs text-muted">{hint}</span>
    </div>
  );
}

function CreateClub({ apiKey, onCreated }: { apiKey: string; onCreated: () => void }): JSX.Element {
  const [name, setName] = useState('');
  const [code, setCode] = useState('');
  const [timeZone, setTimeZone] = useState(ZONES[0] as string);
  const [ownerName, setOwnerName] = useState('');
  const [ownerPin, setOwnerPin] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [created, setCreated] = useState<{ club: Club; enrollmentKey: string } | null>(null);

  const submit = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const r = await call<{ club: Club; enrollmentKey: string }>(apiKey, 'POST', '/clubs', {
        name,
        ...(code ? { code } : {}),
        timeZone,
        ownerName,
        ownerPin,
      });
      setCreated(r);
      setName('');
      setCode('');
      setOwnerName('');
      setOwnerPin('');
      onCreated();
    } catch (e) {
      setError(explain(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Section title="Новый клуб">
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Название клуба">
          <Input value={name} maxLength={80} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Код клуба" hint="Пусто — придумает сервер. Владелец вводит его на кассе вместе с PIN.">
          <Input
            value={code}
            maxLength={12}
            className="font-mono uppercase"
            onChange={(e) => setCode(e.target.value.replace(/[^0-9a-z]/gi, '').toUpperCase())}
          />
        </Field>
        <Field label="Часовой пояс">
          <select
            className="focus-ring h-10 rounded-md border border-line bg-bg px-3 text-sm"
            value={timeZone}
            onChange={(e) => setTimeZone(e.target.value)}
          >
            {ZONES.map((z) => (
              <option key={z} value={z}>
                {z}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Имя владельца">
          <Input value={ownerName} maxLength={64} onChange={(e) => setOwnerName(e.target.value)} />
        </Field>
        <Field label="PIN владельца" hint="4–8 цифр. Сообщите его владельцу лично.">
          <Input
            value={ownerPin}
            inputMode="numeric"
            maxLength={8}
            onChange={(e) => setOwnerPin(e.target.value.replace(/\D/g, ''))}
          />
        </Field>
      </div>
      <div>
        <Button
          variant="primary"
          disabled={busy || !name.trim() || !ownerName.trim() || ownerPin.length < 4}
          onClick={() => void submit()}
        >
          Создать клуб
        </Button>
      </div>
      <Note note={error ? { text: error, tone: 'err' } : null} />
      {created && (
        <Secret
          title={`Клуб «${created.club.name}» создан · код ${created.club.code}`}
          value={created.enrollmentKey}
          hint="Ключ для ПК этого клуба: его вшивают в установщик ClubShell. Показывается один раз — сохраните его сейчас."
        />
      )}
    </Section>
  );
}

function ClubCard({ club, apiKey, onChanged }: { club: Club; apiKey: string; onChanged: () => void }): JSX.Element {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<{ text: string; tone: 'ok' | 'err' } | null>(null);
  const [newKey, setNewKey] = useState<string | null>(null);
  const [pinFor, setPinFor] = useState<Owner | 'new' | null>(null);
  const [pin, setPin] = useState('');
  const [ownerName, setOwnerName] = useState('');

  const run = async (action: () => Promise<void>, ok?: string): Promise<void> => {
    setBusy(true);
    setNote(null);
    try {
      await action();
      if (ok) setNote({ text: ok, tone: 'ok' });
      onChanged();
    } catch (e) {
      setNote({ text: explain(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <Section
      title={`${club.name} · ${club.code || 'без кода'}`}
      actions={
        <span className={club.disabled ? 'text-sm text-danger' : 'text-sm text-success'}>
          {club.disabled ? 'выключен' : 'работает'}
        </span>
      }
    >
      <div className="flex flex-wrap gap-x-8 gap-y-2 text-sm text-muted">
        <span>ПК: {club.pcs}</span>
        <span>Пояс: {club.timeZone}</span>
        <span>Создан: {new Date(club.createdAt).toLocaleDateString('ru-RU')}</span>
      </div>

      <div className="flex flex-col gap-2">
        <span className="label">Владельцы</span>
        {club.owners.length === 0 && <span className="text-sm text-muted">Нет владельцев</span>}
        {club.owners.map((o) => (
          <div key={o.id} className="flex flex-wrap items-center gap-3 text-sm">
            <span className={o.active ? '' : 'text-muted line-through'}>{o.name}</span>
            <Button size="sm" variant="ghost" onClick={() => setPinFor(o)}>
              Новый PIN
            </Button>
          </div>
        ))}
      </div>

      {pinFor && (
        <div className="flex flex-wrap items-end gap-3">
          {pinFor === 'new' && (
            <Field label="Имя нового владельца">
              <Input value={ownerName} maxLength={64} onChange={(e) => setOwnerName(e.target.value)} />
            </Field>
          )}
          <Field label={pinFor === 'new' ? 'PIN' : `Новый PIN для «${pinFor.name}»`}>
            <Input
              value={pin}
              inputMode="numeric"
              maxLength={8}
              onChange={(e) => setPin(e.target.value.replace(/\D/g, ''))}
            />
          </Field>
          <Button
            variant="primary"
            disabled={busy || pin.length < 4 || (pinFor === 'new' && !ownerName.trim())}
            onClick={() =>
              void run(
                async () => {
                  if (pinFor === 'new')
                    await call(apiKey, 'POST', `/clubs/${club.id}/owners`, { name: ownerName, pin });
                  else await call(apiKey, 'POST', `/clubs/${club.id}/owners/${pinFor.id}/pin`, { pin });
                  setPinFor(null);
                  setPin('');
                  setOwnerName('');
                },
                pinFor === 'new' ? 'Владелец добавлен' : 'PIN изменён, старые входы владельца закрыты',
              )
            }
          >
            Сохранить
          </Button>
          <Button variant="ghost" onClick={() => setPinFor(null)}>
            Отмена
          </Button>
        </div>
      )}

      <div className="flex flex-wrap gap-2">
        <Button size="sm" onClick={() => setPinFor('new')}>
          Добавить владельца
        </Button>
        <Button
          size="sm"
          disabled={busy || club.keyFromConfig}
          title={club.keyFromConfig ? 'Ключ этого клуба задаётся на Railway (Club__EnrollmentKey)' : undefined}
          onClick={() => {
            if (!window.confirm('Выдать новый ключ для ПК? Старый продолжит работать до следующей смены.')) return;
            void run(async () => {
              const r = await call<{ enrollmentKey: string }>(apiKey, 'POST', `/clubs/${club.id}/enrollment-key`);
              setNewKey(r.enrollmentKey);
            });
          }}
        >
          Новый ключ для ПК
        </Button>
        <Button
          size="sm"
          variant={club.disabled ? 'secondary' : 'danger'}
          disabled={busy}
          onClick={() => {
            if (
              !club.disabled &&
              !window.confirm(`Выключить «${club.name}»? Сотрудники не смогут войти, новые ПК не подключатся.`)
            )
              return;
            void run(() =>
              call(apiKey, 'PATCH', `/clubs/${club.id}`, { disabled: !club.disabled }).then(() => undefined),
            );
          }}
        >
          {club.disabled ? 'Включить клуб' : 'Выключить клуб'}
        </Button>
      </div>
      <Note note={note} />
      {newKey && (
        <Secret
          title="Новый ключ для ПК"
          value={newKey}
          hint="Показывается один раз. Соберите с ним новый установщик."
        />
      )}
    </Section>
  );
}
