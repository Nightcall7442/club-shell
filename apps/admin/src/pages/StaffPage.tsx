/**
 * Staff (owner only): who can sign in to the console, with which role; switch access off, reset a PIN, add people.
 */
import { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';
import { clubApi, type StaffMember, type StaffRole } from '@/api';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { Button, Field, Input, Note, PageHeader, Section, Table, Toggle } from '@/ui';

type NoteState = { text: string; tone: 'ok' | 'err' } | null;

const ROLES: { id: StaffRole; label: string }[] = [
  { id: 'cashier', label: 'Кассир' },
  { id: 'owner', label: 'Владелец' },
];

const pinOk = (pin: string): boolean => /^\d{4,8}$/.test(pin);
const digits = (v: string): string => v.replace(/\D/g, '').slice(0, 8);

function PinForm({
  onSave,
  onCancel,
  busy,
}: {
  onSave: (pin: string) => void;
  onCancel: () => void;
  busy: boolean;
}): JSX.Element {
  const [pin, setPin] = useState('');
  return (
    <form
      className="flex items-center justify-end gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        if (pinOk(pin)) onSave(pin);
      }}
    >
      <Input
        autoFocus
        inputMode="numeric"
        type="password"
        placeholder={t('Новый PIN')}
        aria-label={t('Новый PIN')}
        value={pin}
        onChange={(e) => setPin(digits(e.target.value))}
        className="tnum h-8 w-32 text-xs"
      />
      <Button type="submit" size="sm" disabled={busy || !pinOk(pin)}>
        {t('Сохранить')}
      </Button>
      <Button size="sm" variant="ghost" onClick={onCancel}>
        {t('Отмена')}
      </Button>
    </form>
  );
}

export default function StaffPage(): JSX.Element {
  const [items, setItems] = useState<StaffMember[]>([]);
  const [note, setNote] = useState<NoteState>(null);
  const [busy, setBusy] = useState(false);
  const [pinFor, setPinFor] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [role, setRole] = useState<StaffRole>('cashier');
  const [pin, setPin] = useState('');

  const load = useCallback(async (): Promise<void> => {
    try {
      setItems((await clubApi.staff()).items);
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const run = async (fn: () => Promise<string>): Promise<void> => {
    setBusy(true);
    setNote(null);
    try {
      setNote({ text: await fn(), tone: 'ok' });
      await load();
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  const canAdd = name.trim().length > 0 && pinOk(pin) && !busy;

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title={t('Сотрудники')} />
      <Note note={note} />

      <Section title={t('Сотрудники')} bodyClassName="p-2">
        <Table
          rows={items}
          rowKey={(s) => s.id}
          empty={t('Сотрудников нет')}
          columns={[
            {
              key: 'name',
              title: t('Имя'),
              render: (s) => <span className={clsx(!s.active && 'text-muted')}>{s.name}</span>,
            },
            {
              key: 'role',
              title: t('Роль'),
              width: '10rem',
              render: (s) => <span className="label">{s.role === 'owner' ? t('Владелец') : t('Кассир')}</span>,
            },
            {
              key: 'active',
              title: t('Статус'),
              width: '8rem',
              render: (s) => (
                <Toggle
                  checked={s.active}
                  disabled={busy}
                  label={s.active ? t('Активен') : t('Отключён')}
                  onChange={(v) =>
                    void run(async () => {
                      await clubApi.updateStaff(s.id, { active: v });
                      return v
                        ? t('{name}: доступ включён', { name: s.name })
                        : t('{name}: доступ выключен', { name: s.name });
                    })
                  }
                />
              ),
            },
            {
              key: 'pin',
              title: t('PIN'),
              num: true,
              width: '22rem',
              render: (s) =>
                pinFor === s.id ? (
                  <PinForm
                    busy={busy}
                    onCancel={() => setPinFor(null)}
                    onSave={(p) =>
                      void run(async () => {
                        await clubApi.updateStaff(s.id, { pin: p });
                        setPinFor(null);
                        return t('{name}: PIN изменён', { name: s.name });
                      })
                    }
                  />
                ) : (
                  <Button size="sm" variant="ghost" onClick={() => setPinFor(s.id)}>
                    {t('Сменить PIN')}
                  </Button>
                ),
            },
          ]}
        />
      </Section>

      <Section title={t('Добавить сотрудника')}>
        <form
          className="grid grid-cols-1 items-end gap-5 md:grid-cols-[minmax(0,1fr)_auto_12rem_auto]"
          onSubmit={(e) => {
            e.preventDefault();
            if (!canAdd) return;
            void run(async () => {
              await clubApi.addStaff({ name: name.trim(), role, pin });
              const done = t('{name} добавлен', { name: name.trim() });
              setName('');
              setPin('');
              setRole('cashier');
              return done;
            });
          }}
        >
          <Field label={t('Имя')}>
            <Input value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />
          </Field>
          <div className="flex flex-col gap-1.5">
            <span className="label">{t('Роль')}</span>
            <div className="flex gap-1.5" role="radiogroup">
              {ROLES.map((r) => (
                <button
                  key={r.id}
                  type="button"
                  role="radio"
                  aria-checked={role === r.id}
                  onClick={() => setRole(r.id)}
                  className={clsx(
                    'focus-ring choice h-10 rounded-md px-3.5 text-sm font-semibold',
                    role === r.id && 'choice-on',
                  )}
                >
                  {t(r.label)}
                </button>
              ))}
            </div>
          </div>
          <Field label={t('PIN · 4–8 цифр')}>
            <Input
              inputMode="numeric"
              type="password"
              className="tnum"
              value={pin}
              onChange={(e) => setPin(digits(e.target.value))}
            />
          </Field>
          <Button type="submit" variant="primary" disabled={!canAdd}>
            {t('Добавить')}
          </Button>
        </form>
      </Section>
    </div>
  );
}
