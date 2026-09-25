/**
 * Cashier control (owner only): the signals the server finds in the staff journal — cash short at close, sessions
 * refunded minutes after opening, big discounts handed out, the same client topped up again and again, money taken
 * with no shift open — per cashier for 7 / 30 / 90 days, the journal itself, and the thresholds of the rules.
 */
import { useEffect, useState } from 'react';
import clsx from 'clsx';
import {
  clubApi,
  type AuditAction,
  type AuditEntry,
  type ControlFlag,
  type ControlReport,
  type ControlSettings,
  type Severity,
} from '@/api';
import { describe } from '@/errors';
import { dateLocale, t } from '@/i18n';
import { money } from '@/format';
import { useClubSettings } from '@/settings';
import { Field, MoneyInput, Note, NumberInput, PageHeader, SaveBar, Section, Table } from '@/ui';

const PERIODS = [7, 30, 90] as const;

const uzs = (minor: number): string => money({ amount: minor, currency: 'UZS' });

const ACTION_LABEL: Record<AuditAction, string> = {
  shiftOpen: 'Открытие смены',
  shiftClose: 'Закрытие смены',
  topUp: 'Пополнение',
  sessionOpen: 'Открыл время',
  sessionExtend: 'Продлил время',
  sessionEnd: 'Завершил сеанс',
  promoRedeem: 'Промокод',
  clientGroup: 'Группа клиента',
  blacklist: 'Чёрный список',
  stockReceive: 'Приход товара',
  stockEdit: 'Правка остатка',
  pcCommand: 'Команда ПК',
};

const SEVERITY_DOT: Record<Severity, string> = {
  high: 'bg-danger',
  medium: 'bg-warning',
  low: 'bg-muted',
};

const SEVERITY_LABEL: Record<Severity, string> = {
  high: 'Серьёзно',
  medium: 'Внимание',
  low: 'Для сведения',
};

/** Actions that move money: done with no shift open they are a signal of their own. */
const MONEY: ReadonlySet<AuditAction> = new Set(['topUp', 'sessionOpen', 'sessionExtend', 'sessionEnd', 'promoRedeem']);

function dateTime(iso: string): string {
  return new Date(iso).toLocaleString(dateLocale(), {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

/** The flag in the console language; the server sends the facts, not the sentence. */
function flagText(f: ControlFlag): string {
  const p = f.params;
  const detail = p['detail'] ? ` · ${p['detail']}` : '';
  switch (f.kind) {
    case 'shortfall':
      return t('Недостача в кассе при закрытии смены: {sum}', { sum: uzs(Number(p['short'] ?? f.amount)) });
    case 'earlyEnds':
      return t('{count} сеанса за смену закрыты с возвратом вскоре после открытия', { count: p['count'] ?? 0 });
    case 'earlyEnd':
      return t('Сеанс закрыт через {min} мин с возвратом', { min: p['minutes'] ?? 0 }) + detail;
    case 'discount':
      return (
        t('Клиент переведён в группу «{group}» со скидкой {pct}%', { group: p['group'] ?? '', pct: p['pct'] ?? 0 }) +
        detail
      );
    case 'sameClient':
      return t('{count} пополнения одному клиенту за смену', { count: p['count'] ?? 0 }) + detail;
    case 'noShift':
      return (
        t('Операция без открытой смены: {action}', { action: t(ACTION_LABEL[p['action'] as AuditAction] ?? '') }) +
        detail
      );
    case 'bigCash':
      return t('Крупное пополнение наличными') + detail;
    default:
      return f.kind;
  }
}

/** What the journal row says after the action; shift rows spell out the cash, the rest show the server's detail. */
function journalDetail(e: AuditEntry): string {
  if (e.action === 'shiftClose') {
    return t('ожидалось {expected} · посчитано {counted}', {
      expected: uzs(Number(e.meta['expected'] ?? 0)),
      counted: uzs(Number(e.meta['counted'] ?? 0)),
    });
  }
  if (e.action === 'shiftOpen') return t('наличные на начало');
  return e.detail;
}

function Dot({ severity }: { severity: Severity }): JSX.Element {
  return (
    <span aria-hidden="true" className={clsx('inline-block h-2 w-2 shrink-0 rounded-full', SEVERITY_DOT[severity])} />
  );
}

function FlagCounts({ flags }: { flags: Record<Severity, number> }): JSX.Element {
  const total = flags.high + flags.medium + flags.low;
  if (total === 0) return <span className="text-success">{t('Чисто')}</span>;
  return (
    <span className="flex items-center justify-end gap-3">
      {(['high', 'medium', 'low'] as const).map((s) =>
        flags[s] > 0 ? (
          <span key={s} className="flex items-center gap-1.5" title={t(SEVERITY_LABEL[s])}>
            <Dot severity={s} />
            <span className="tnum">{flags[s]}</span>
          </span>
        ) : null,
      )}
    </span>
  );
}

function Thresholds(): JSX.Element | null {
  const st = useClubSettings();
  const c = st.draft?.control;
  if (!c) return null;
  const set = (patch: Partial<ControlSettings>): void => st.set('control', { ...c, ...patch });
  return (
    <>
      <Section title={t('Пороги')}>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
          <Field label={t('Раннее закрытие — в первые, мин')}>
            <NumberInput value={c.earlyEndMinutes} min={1} max={120} onChange={(v) => set({ earlyEndMinutes: v })} />
          </Field>
          <Field label={t('Ранних закрытий за смену — тревога от')}>
            <NumberInput value={c.earlyEndsPerShift} min={1} max={50} onChange={(v) => set({ earlyEndsPerShift: v })} />
          </Field>
          <Field label={t('Скидка группы — отмечать от, %')}>
            <NumberInput value={c.discountPct} min={1} max={100} onChange={(v) => set({ discountPct: v })} />
          </Field>
          <Field label={t('Пополнений одному клиенту за смену — от')}>
            <NumberInput value={c.sameClientTopups} min={2} max={50} onChange={(v) => set({ sameClientTopups: v })} />
          </Field>
          <Field label={t('Недостача в кассе — тревога от')}>
            <MoneyInput value={c.shortfallFrom} onChange={(v) => set({ shortfallFrom: v })} />
          </Field>
        </div>
        <p className="mt-4 text-sm text-muted">
          {t('Серьёзные сигналы сразу приходят в Telegram, если включено событие «Подозрительная операция».')}
        </p>
      </Section>
      <SaveBar
        dirty={st.dirty}
        saving={st.saving}
        onSave={() => void st.save()}
        onReset={st.reset}
        label={t('Есть несохранённые изменения')}
      />
    </>
  );
}

export default function ControlPage(): JSX.Element {
  const [days, setDays] = useState<number>(7);
  const [staffId, setStaffId] = useState<string | null>(null);
  const [data, setData] = useState<ControlReport | null>(null);
  const [note, setNote] = useState<{ text: string; tone: 'ok' | 'err' } | null>(null);

  useEffect(() => {
    let alive = true;
    clubApi
      .control(days, staffId)
      .then((r) => {
        if (!alive) return;
        setData(r);
        setNote(null);
      })
      .catch((e: unknown) => alive && setNote({ text: describe(e), tone: 'err' }));
    return () => {
      alive = false;
    };
  }, [days, staffId]);

  const who = staffId ? data?.staff.find((s) => s.staffId === staffId)?.staffName : null;

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title={t('Контроль кассиров')}
        actions={PERIODS.map((p) => (
          <button
            key={p}
            type="button"
            aria-pressed={days === p}
            onClick={() => setDays(p)}
            className={clsx(
              'focus-ring choice h-10 rounded-md px-3.5 text-sm font-semibold transition-colors',
              days === p && 'choice-on',
            )}
          >
            {t('{n} дней', { n: p })}
          </button>
        ))}
      />
      <Note note={note} />

      {data && (
        <>
          <Section title={t('Кассиры')} bodyClassName="p-2">
            <Table
              rows={data.staff}
              rowKey={(s) => s.staffId}
              selectedKey={staffId}
              onRowClick={(s) => setStaffId(s.staffId === staffId ? null : s.staffId)}
              empty={t('За период действий не было')}
              columns={[
                {
                  key: 'who',
                  title: t('Сотрудник'),
                  render: (s) => <span className="font-medium">{s.staffName}</span>,
                },
                { key: 'ops', title: t('Операций'), num: true, render: (s) => s.operations },
                { key: 'topups', title: t('Пополнения'), num: true, render: (s) => uzs(s.topUps) },
                { key: 'refunds', title: t('Возвраты'), num: true, render: (s) => uzs(s.refunds) },
                { key: 'early', title: t('Ранние закрытия'), num: true, render: (s) => s.earlyEnds },
                { key: 'disc', title: t('Скидки'), num: true, render: (s) => s.discounts },
                {
                  key: 'short',
                  title: t('Недостача'),
                  num: true,
                  render: (s) => <span className={clsx(s.shortfall > 0 && 'text-danger')}>{uzs(s.shortfall)}</span>,
                },
                { key: 'flags', title: t('Сигналы'), num: true, render: (s) => <FlagCounts flags={s.flags} /> },
              ]}
            />
          </Section>

          <Section
            title={who ? t('Сигналы · {name}', { name: who }) : t('Сигналы')}
            actions={
              staffId ? (
                <button
                  type="button"
                  className="focus-ring rounded px-2 py-1 text-sm text-muted hover:text-text"
                  onClick={() => setStaffId(null)}
                >
                  {t('Все сотрудники')}
                </button>
              ) : undefined
            }
          >
            {data.flags.length === 0 ? (
              <p className="flex items-center gap-2 text-sm text-success">
                <span aria-hidden="true" className="h-2 w-2 rounded-full bg-success" />
                {t('Подозрительного не найдено')}
              </p>
            ) : (
              <ul className="flex flex-col divide-y divide-line/60">
                {data.flags.map((f) => (
                  <li key={f.id} className="flex items-start gap-3 py-3 first:pt-0 last:pb-0">
                    <span className="mt-1.5">
                      <Dot severity={f.severity} />
                    </span>
                    <div className="min-w-0 flex-1">
                      <p className="text-sm">{flagText(f)}</p>
                      <p className="mt-0.5 text-xs text-muted">
                        <span className="tnum">{dateTime(f.at)}</span> · {f.staffName} · {t(SEVERITY_LABEL[f.severity])}
                      </p>
                    </div>
                    {f.amount > 0 && <span className="tnum shrink-0 text-sm">{uzs(f.amount)}</span>}
                  </li>
                ))}
              </ul>
            )}
          </Section>

          <Section title={t('Журнал действий')} bodyClassName="p-2">
            <Table<AuditEntry>
              rows={data.log}
              rowKey={(e) => e.id}
              empty={t('Записей нет')}
              columns={[
                {
                  key: 'at',
                  title: t('Время'),
                  width: '9rem',
                  render: (e) => <span className="tnum">{dateTime(e.at)}</span>,
                },
                { key: 'who', title: t('Сотрудник'), width: '11rem', render: (e) => e.staffName },
                {
                  key: 'what',
                  title: t('Действие'),
                  render: (e) => (
                    <span className="flex flex-wrap items-center gap-2">
                      <span>{t(ACTION_LABEL[e.action])}</span>
                      <span className="text-muted">{journalDetail(e)}</span>
                      {MONEY.has(e.action) && e.shiftId === null && (
                        <span className="rounded bg-warning/15 px-1.5 py-0.5 text-[0.7rem] text-warning">
                          {t('без смены')}
                        </span>
                      )}
                    </span>
                  ),
                },
                {
                  key: 'amount',
                  title: t('Сумма'),
                  num: true,
                  width: '9rem',
                  render: (e) => (e.amount > 0 ? uzs(e.amount) : '—'),
                },
              ]}
            />
          </Section>
        </>
      )}

      <Thresholds />
    </div>
  );
}
