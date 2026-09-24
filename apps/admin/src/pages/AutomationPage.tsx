/**
 * "Автоматизация": the club's "if → then" rules as readable sentences, with an editor on the right and four ready
 * templates. Rules are part of the settings document; the server's rule engine (`club.ts`) runs them.
 */
import { useState } from 'react';
import clsx from 'clsx';
import type { AutomationRule, RuleAction, RuleTrigger } from '@/api';
import { t } from '@/i18n';
import { money } from '@/format';
import { useClubSettings } from '@/settings';
import { Button, Field, Input, MoneyInput, NumberInput, PageHeader, SaveBar, Section, Toggle, inputCls } from '@/ui';

type TriggerKind = RuleTrigger['kind'];
type ActionKind = RuleAction['kind'];

const TRIGGERS: { kind: TriggerKind; label: string }[] = [
  { kind: 'minutesLeft', label: 'Осталось минут до конца сеанса' },
  { kind: 'pcIdleMinutes', label: 'ПК свободен N минут' },
  { kind: 'visitCount', label: 'Каждый N-й визит клиента' },
  { kind: 'topupAtLeast', label: 'Пополнение от суммы' },
  { kind: 'sessionStarted', label: 'Начало сеанса' },
];

const ACTIONS: { kind: ActionKind; label: string }[] = [
  { kind: 'message', label: 'Сообщение на экран' },
  { kind: 'bonus', label: 'Бонус на баланс' },
  { kind: 'lockPc', label: 'Заблокировать ПК' },
  { kind: 'shutdownPc', label: 'Выключить ПК' },
  { kind: 'notifyOwner', label: 'Уведомить владельца' },
];

interface Draft {
  id: string | null;
  name: string;
  trigger: TriggerKind;
  value: number;
  action: ActionKind;
  text: string;
  amount: number;
}

const EMPTY: Draft = {
  id: null,
  name: '',
  trigger: 'minutesLeft',
  value: 5,
  action: 'message',
  text: '',
  amount: 1_000_000,
};

const TEMPLATES: { label: string; draft: Omit<Draft, 'id'> }[] = [
  {
    label: '5 минут до конца → сообщение',
    draft: {
      name: '5 минут до конца — предложить продлить',
      trigger: 'minutesLeft',
      value: 5,
      action: 'message',
      text: 'Осталось 5 минут. Добавьте время в кошельке, чтобы не прерывать игру.',
      amount: 0,
    },
  },
  {
    label: 'ПК свободен 30 мин → выключить',
    draft: {
      name: 'Свободный ПК 30 минут — выключить',
      trigger: 'pcIdleMinutes',
      value: 30,
      action: 'shutdownPc',
      text: '',
      amount: 0,
    },
  },
  {
    label: 'Каждый 10-й визит → бонус 10 000',
    draft: {
      name: 'Каждый 10-й визит — бонус 10 000',
      trigger: 'visitCount',
      value: 10,
      action: 'bonus',
      text: '',
      amount: 1_000_000,
    },
  },
  {
    label: 'Пополнение от 200 000 → бонус 20 000',
    draft: {
      name: 'Пополнение от 200 000 — бонус 20 000',
      trigger: 'topupAtLeast',
      value: 20_000_000,
      action: 'bonus',
      text: '',
      amount: 2_000_000,
    },
  },
];

const uzs = (minor: number): string => money({ amount: minor, currency: 'UZS' });

function triggerText(tr: RuleTrigger): string {
  switch (tr.kind) {
    case 'minutesLeft':
      return t('до конца сеанса осталось {n} мин', { n: tr.value });
    case 'pcIdleMinutes':
      return t('ПК свободен {n} мин', { n: tr.value });
    case 'visitCount':
      return t('каждый {n}-й визит клиента', { n: tr.value });
    case 'topupAtLeast':
      return t('пополнение от {sum}', { sum: uzs(tr.value) });
    case 'sessionStarted':
      return t('начался сеанс');
  }
}

function actionText(a: RuleAction): string {
  switch (a.kind) {
    case 'message':
      return t('сообщение на экран: «{text}»', { text: a.text });
    case 'bonus':
      return t('бонус {sum} на баланс', { sum: uzs(a.amount) });
    case 'lockPc':
      return t('заблокировать ПК');
    case 'shutdownPc':
      return t('выключить ПК');
    case 'notifyOwner':
      return t('уведомить владельца: «{text}»', { text: a.text });
  }
}

function toDraft(r: AutomationRule): Draft {
  return {
    id: r.id,
    name: r.name,
    trigger: r.trigger.kind,
    value: r.trigger.kind === 'sessionStarted' ? 0 : r.trigger.value,
    action: r.action.kind,
    text: r.action.kind === 'message' || r.action.kind === 'notifyOwner' ? r.action.text : '',
    amount: r.action.kind === 'bonus' ? r.action.amount : 0,
  };
}

function buildTrigger(d: Draft): RuleTrigger {
  return d.trigger === 'sessionStarted' ? { kind: 'sessionStarted' } : { kind: d.trigger, value: d.value };
}

function buildAction(d: Draft): RuleAction {
  switch (d.action) {
    case 'message':
    case 'notifyOwner':
      return { kind: d.action, text: d.text.trim() };
    case 'bonus':
      return { kind: 'bonus', amount: d.amount };
    default:
      return { kind: d.action };
  }
}

function problem(d: Draft): string | null {
  if (!d.name.trim()) return t('Укажите название');
  if (d.trigger !== 'sessionStarted' && !(d.value > 0)) return t('Укажите значение условия');
  if ((d.action === 'message' || d.action === 'notifyOwner') && !d.text.trim()) return t('Укажите текст');
  if (d.action === 'bonus' && !(d.amount > 0)) return t('Укажите сумму бонуса');
  return null;
}

function dateTime(iso: string): string {
  return new Date(iso).toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
}

function RuleCard({
  rule,
  selected,
  onToggle,
  onEdit,
  onDelete,
}: {
  rule: AutomationRule;
  selected: boolean;
  onToggle: (v: boolean) => void;
  onEdit: () => void;
  onDelete: () => void;
}): JSX.Element {
  return (
    <article
      className={clsx(
        'panel flex flex-wrap items-center gap-x-6 gap-y-3 px-5 py-4',
        selected && 'border-accent/60',
        !rule.enabled && 'opacity-60',
      )}
    >
      <Toggle checked={rule.enabled} onChange={onToggle} />
      <div className="flex min-w-0 flex-1 flex-col gap-1.5">
        <h3 className="truncate text-sm font-semibold">{rule.name}</h3>
        <p className="text-sm leading-relaxed text-muted">
          <span className="label mr-1.5">{t('Если')}</span>
          <span className="text-text">{triggerText(rule.trigger)}</span>
          <span className="mx-2 text-accent">→</span>
          <span className="label mr-1.5">{t('то')}</span>
          <span className="text-text">{actionText(rule.action)}</span>
        </p>
      </div>
      <div className="flex flex-col items-end gap-1 text-right">
        <span className="tnum text-sm">{t('Сработало: {n}', { n: rule.fired })}</span>
        <span className="tnum font-mono text-xs text-muted">
          {rule.lastFiredAt ? dateTime(rule.lastFiredAt) : '—'}
        </span>
      </div>
      <div className="flex gap-1">
        <Button variant="ghost" size="sm" onClick={onEdit}>
          {t('Изменить')}
        </Button>
        <Button variant="danger" size="sm" onClick={onDelete}>
          {t('Удалить')}
        </Button>
      </div>
    </article>
  );
}

function Editor({
  initial,
  onApply,
  onClose,
}: {
  initial: Draft;
  onApply: (d: Draft) => void;
  onClose: () => void;
}): JSX.Element {
  const [d, setD] = useState<Draft>(initial);
  const [tried, setTried] = useState(false);
  const up = (patch: Partial<Draft>): void => setD((x) => ({ ...x, ...patch }));
  const err = problem(d);

  const pickTrigger = (kind: TriggerKind): void => {
    const value =
      kind === d.trigger
        ? d.value
        : kind === 'minutesLeft'
          ? 5
          : kind === 'pcIdleMinutes'
            ? 30
            : kind === 'visitCount'
              ? 10
              : kind === 'topupAtLeast'
                ? 20_000_000
                : 0;
    up({ trigger: kind, value });
  };

  return (
    <Section
      title={d.id ? t('Правило') : t('Новое правило')}
      className="xl:sticky xl:top-5"
      actions={
        <Button variant="ghost" size="sm" onClick={onClose}>
          {t('Закрыть')}
        </Button>
      }
    >
      {!d.id && (
        <Field label={t('Шаблоны')}>
          <div className="grid grid-cols-2 gap-1.5">
            {TEMPLATES.map((tpl) => (
              <button
                key={tpl.label}
                type="button"
                onClick={() => {
                  setD({ id: null, ...tpl.draft, name: t(tpl.draft.name), text: tpl.draft.text && t(tpl.draft.text) });
                  setTried(false);
                }}
                className={clsx(
                  'choice focus-ring rounded-md px-3 py-2 text-left text-xs leading-snug',
                  d.name === t(tpl.draft.name) && 'choice-on',
                )}
              >
                {t(tpl.label)}
              </button>
            ))}
          </div>
        </Field>
      )}

      <Field label={t('Название')}>
        <Input value={d.name} onChange={(e) => up({ name: e.target.value })} />
      </Field>

      <Field label={t('Если')}>
        <div className="flex flex-col gap-1.5">
          {TRIGGERS.map((x) => (
            <button
              key={x.kind}
              type="button"
              aria-pressed={d.trigger === x.kind}
              onClick={() => pickTrigger(x.kind)}
              className={clsx(
                'choice focus-ring h-9 rounded-md px-3 text-left text-sm',
                d.trigger === x.kind && 'choice-on',
              )}
            >
              {t(x.label)}
            </button>
          ))}
        </div>
      </Field>

      {d.trigger === 'topupAtLeast' && (
        <Field label={t('Сумма пополнения от')}>
          <MoneyInput value={d.value} onChange={(v) => up({ value: v })} />
        </Field>
      )}
      {(d.trigger === 'minutesLeft' || d.trigger === 'pcIdleMinutes') && (
        <Field label={d.trigger === 'minutesLeft' ? t('Осталось минут') : t('Свободен минут')}>
          <NumberInput value={d.value} min={1} max={1440} suffix={t('мин')} onChange={(v) => up({ value: v })} />
        </Field>
      )}
      {d.trigger === 'visitCount' && (
        <Field label={t('Каждый N-й визит')}>
          <NumberInput value={d.value} min={1} max={1000} onChange={(v) => up({ value: v })} />
        </Field>
      )}

      <Field label={t('То')}>
        <div className="flex flex-col gap-1.5">
          {ACTIONS.map((x) => (
            <button
              key={x.kind}
              type="button"
              aria-pressed={d.action === x.kind}
              onClick={() => up({ action: x.kind })}
              className={clsx(
                'choice focus-ring h-9 rounded-md px-3 text-left text-sm',
                d.action === x.kind && 'choice-on',
              )}
            >
              {t(x.label)}
            </button>
          ))}
        </div>
      </Field>

      {(d.action === 'message' || d.action === 'notifyOwner') && (
        <Field label={t('Текст')}>
          <textarea
            rows={3}
            value={d.text}
            onChange={(e) => up({ text: e.target.value })}
            className={clsx(inputCls, 'h-auto resize-y py-2.5 leading-relaxed')}
          />
        </Field>
      )}
      {d.action === 'bonus' && (
        <Field label={t('Сумма бонуса')}>
          <MoneyInput value={d.amount} onChange={(v) => up({ amount: v })} />
        </Field>
      )}

      {tried && err && <p className="text-sm text-danger">{err}</p>}

      <div className="flex justify-end border-t border-line pt-4">
        <Button
          variant="primary"
          onClick={() => {
            setTried(true);
            if (!err) onApply(d);
          }}
        >
          {d.id ? t('Применить') : t('Добавить правило')}
        </Button>
      </div>
    </Section>
  );
}

export default function AutomationPage(): JSX.Element {
  const st = useClubSettings();
  const [editing, setEditing] = useState<Draft | null>(null);
  const [editorKey, setEditorKey] = useState(0);
  const rules = st.draft?.automation ?? [];

  const open = (d: Draft): void => {
    setEditing(d);
    setEditorKey((k) => k + 1);
  };

  const apply = (d: Draft): void => {
    const trigger = buildTrigger(d);
    const action = buildAction(d);
    const name = d.name.trim();
    if (d.id) {
      st.set(
        'automation',
        rules.map((r) => (r.id === d.id ? { ...r, name, trigger, action } : r)),
      );
    } else {
      st.set('automation', [
        ...rules,
        { id: crypto.randomUUID(), name, enabled: true, trigger, action, fired: 0, lastFiredAt: null },
      ]);
    }
    setEditing(null);
  };

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title={t('Автоматизация')}
        actions={
          st.draft && (
            <Button onClick={() => open(EMPTY)} disabled={editing !== null && editing.id === null}>
              {t('Новое правило')}
            </Button>
          )
        }
      />
      {st.error && <p className="rounded-md bg-danger/10 px-3 py-2 text-sm text-danger">{st.error}</p>}

      {st.draft && (
        <div
          className={clsx('grid items-start gap-5', editing && 'xl:grid-cols-[minmax(0,1fr)_26rem]')}
        >
          <div className="flex min-w-0 flex-col gap-2">
            {rules.map((r) => (
              <RuleCard
                key={r.id}
                rule={r}
                selected={editing?.id === r.id}
                onToggle={(v) =>
                  st.set(
                    'automation',
                    rules.map((x) => (x.id === r.id ? { ...x, enabled: v } : x)),
                  )
                }
                onEdit={() => open(toDraft(r))}
                onDelete={() => {
                  st.set(
                    'automation',
                    rules.filter((x) => x.id !== r.id),
                  );
                  if (editing?.id === r.id) setEditing(null);
                }}
              />
            ))}
            {rules.length === 0 && (
              <p className="panel px-5 py-10 text-center text-sm text-muted">{t('Правил пока нет')}</p>
            )}
          </div>
          {editing && <Editor key={editorKey} initial={editing} onApply={apply} onClose={() => setEditing(null)} />}
        </div>
      )}

      <SaveBar
        dirty={st.dirty}
        saving={st.saving}
        onSave={() => void st.save()}
        onReset={() => {
          st.reset();
          setEditing(null);
        }}
        label={t('Есть несохранённые изменения')}
      />
    </div>
  );
}
