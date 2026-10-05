/**
 * "Уведомления и API": outbound webhooks per club event, the big top-up threshold and the club API key (accepted as
 * an owner token on `/api/v1/admin/*`, so it comes from the owner-only `GET /admin/club/api-key`, not the settings
 * document). No Telegram: it is banned in the product, and Telegram fields an older server still sends are ignored.
 */
import { useEffect, useState } from 'react';
import clsx from 'clsx';
import { clubApi, type ClubEvent, type Webhook } from '@/api';
import { describe } from '@/errors';
import { dateLocale, t } from '@/i18n';
import { useClubSettings } from '@/settings';
import {
  Button,
  Chip,
  Field,
  Input,
  MoneyInput,
  Note,
  PageHeader,
  SaveBar,
  Section,
  StatusDot,
  Table,
  Toggle,
} from '@/ui';
import { FieldGroup, OwnerPage } from './ownerKit';

type NoteState = { text: string; tone: 'ok' | 'err' } | null;

const EVENT_LABEL: Record<ClubEvent, string> = {
  shiftClosed: 'Смена закрыта',
  pcOffline: 'ПК не в сети',
  bigTopup: 'Крупное пополнение',
  lowStock: 'Товар заканчивается',
  ruleFired: 'Сработало правило',
  sessionOpened: 'Открыт сеанс',
  suspicious: 'Подозрительная операция',
  hardware: 'Нужен ремонт ПК',
};
const ALL_EVENTS = Object.keys(EVENT_LABEL) as ClubEvent[];

function dateTime(iso: string): string {
  return new Date(iso).toLocaleString(dateLocale(), {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

/**
 * An event code as integrations see it (`shiftClosed`, camel case: not uppercased), its name in the tooltip: a quiet mono
 * tag in a webhook's row, an outlined chip (`aria-pressed`) when picking events for a new one.
 */
function EventChip({ ev, on, onClick }: { ev: ClubEvent; on?: boolean; onClick?: () => void }): JSX.Element {
  if (!onClick) {
    return (
      <span
        title={t(EVENT_LABEL[ev])}
        className="inline-flex h-5 items-center rounded-sm border border-text/[0.12] bg-bg/40 px-1.5 font-mono text-[10.5px] leading-none text-soft"
      >
        {ev}
      </span>
    );
  }
  return (
    <Chip
      tone="outlined"
      pressed={Boolean(on)}
      title={t(EVENT_LABEL[ev])}
      onClick={onClick}
      className="font-mono text-xs"
    >
      {ev}
    </Chip>
  );
}

export default function IntegrationsPage(): JSX.Element {
  const st = useClubSettings();
  const s = st.draft;
  const [hookUrl, setHookUrl] = useState('');
  const [hookEvents, setHookEvents] = useState<ClubEvent[]>([]);
  const [hookError, setHookError] = useState<string | null>(null);
  const [apiKey, setApiKey] = useState<string | null>(null);
  const [confirmRotate, setConfirmRotate] = useState(false);
  const [rotating, setRotating] = useState(false);
  const [keyNote, setKeyNote] = useState<NoteState>(null);

  useEffect(() => {
    // An older server has no such route and still puts the key into the settings document (below).
    clubApi
      .apiKey()
      .then((r) => setApiKey(r.apiKey))
      .catch(() => undefined);
  }, []);

  if (!s) {
    return (
      <OwnerPage>
        <PageHeader title={t('Уведомления и API')} caption={t('Настройка клуба')} />
        {st.error && <Note tone="err">{st.error}</Note>}
      </OwnerPage>
    );
  }

  const hooks = s.webhooks;
  const setHook = (id: string, patch: Partial<Webhook>): void =>
    st.set(
      'webhooks',
      hooks.map((h) => (h.id === id ? { ...h, ...patch } : h)),
    );
  const key = apiKey ?? s.apiKey ?? '';

  const addHook = (): void => {
    const url = hookUrl.trim();
    if (!/^https?:\/\/\S+$/.test(url)) {
      setHookError(t('Укажите адрес, начинающийся с http:// или https://'));
      return;
    }
    if (hookEvents.length === 0) {
      setHookError(t('Выберите хотя бы одно событие'));
      return;
    }
    st.set('webhooks', [
      ...hooks,
      { id: crypto.randomUUID(), url, events: hookEvents, enabled: true, lastStatus: null, lastAt: null },
    ]);
    setHookUrl('');
    setHookEvents([]);
    setHookError(null);
  };

  const copy = async (): Promise<void> => {
    try {
      await navigator.clipboard.writeText(key);
      setKeyNote({ text: t('Ключ скопирован'), tone: 'ok' });
    } catch {
      setKeyNote({ text: t('Не удалось скопировать — выделите ключ вручную'), tone: 'err' });
    }
  };

  const rotate = async (): Promise<void> => {
    setRotating(true);
    setKeyNote(null);
    try {
      const r = await clubApi.rotateApiKey();
      setApiKey(r.apiKey);
      setKeyNote({ text: t('Ключ заменён'), tone: 'ok' });
    } catch (e) {
      setKeyNote({ text: describe(e), tone: 'err' });
    } finally {
      setRotating(false);
      setConfirmRotate(false);
    }
  };

  return (
    <OwnerPage>
      <PageHeader title={t('Уведомления и API')} caption={t('Настройка клуба')} />
      {st.error && <Note tone="err">{st.error}</Note>}

      <Section title={t('Вебхуки')} bodyClassName="p-0 gap-0">
        <div className="p-2">
          <Table
            rows={hooks}
            rowKey={(h) => h.id}
            empty={t('Вебхуков нет')}
            columns={[
              {
                key: 'url',
                title: t('Адрес'),
                render: (h) => <span className="break-all font-mono text-xs text-text">{h.url}</span>,
              },
              {
                key: 'events',
                title: t('События'),
                render: (h) => (
                  <div className="flex flex-wrap gap-1">
                    {h.events.map((ev) => (
                      <EventChip key={ev} ev={ev} />
                    ))}
                  </div>
                ),
              },
              {
                key: 'on',
                title: t('Вкл.'),
                width: '4.5rem',
                render: (h) => <Toggle checked={h.enabled} onChange={(v) => setHook(h.id, { enabled: v })} />,
              },
              {
                key: 'status',
                title: t('Последний ответ'),
                width: '11rem',
                render: (h) =>
                  h.lastStatus === null ? (
                    <span className="text-muted">—</span>
                  ) : (
                    <span className="flex items-center gap-2">
                      <StatusDot tone={h.lastStatus >= 200 && h.lastStatus < 300 ? 'ok' : 'danger'} />
                      <span
                        className={clsx(
                          'tnum font-mono text-xs font-medium',
                          h.lastStatus >= 200 && h.lastStatus < 300 ? 'text-text' : 'text-danger-ink',
                        )}
                      >
                        {h.lastStatus === 0 ? t('нет связи') : h.lastStatus}
                      </span>
                      {h.lastAt && <span className="tnum font-mono text-[11px] text-muted">{dateTime(h.lastAt)}</span>}
                    </span>
                  ),
              },
              {
                key: 'del',
                title: '',
                width: '6.5rem',
                render: (h) => (
                  <Button
                    variant="tertiary"
                    size="sm"
                    onClick={() =>
                      st.set(
                        'webhooks',
                        hooks.filter((x) => x.id !== h.id),
                      )
                    }
                  >
                    {t('Удалить')}
                  </Button>
                ),
              },
            ]}
          />
        </div>
        <div className="flex flex-col gap-4 border-t border-accent/[0.08] p-5">
          <div className="flex flex-wrap items-end gap-3">
            <Field label={t('Новый вебхук')} className="min-w-[18rem] flex-1">
              <Input
                className="font-mono"
                placeholder="https://example.com/hook"
                value={hookUrl}
                onChange={(e) => setHookUrl(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && addHook()}
              />
            </Field>
            <Button onClick={addHook}>{t('Добавить')}</Button>
          </div>
          <FieldGroup label={t('События')}>
            <div className="flex flex-wrap gap-1.5">
              {ALL_EVENTS.map((ev) => (
                <EventChip
                  key={ev}
                  ev={ev}
                  on={hookEvents.includes(ev)}
                  onClick={() => setHookEvents((xs) => (xs.includes(ev) ? xs.filter((x) => x !== ev) : [...xs, ev]))}
                />
              ))}
            </div>
          </FieldGroup>
          {hookError && <p className="text-xs font-medium text-warning">{hookError}</p>}
        </div>
        {s.notifications && (
          <div className="border-t border-accent/[0.08] p-5">
            <Field label={t('Крупное пополнение — от')} className="max-w-xs">
              <MoneyInput
                compact
                value={s.notifications.bigTopupAt}
                onChange={(v) => st.set('notifications', { bigTopupAt: v })}
              />
            </Field>
          </div>
        )}
      </Section>

      <Section title={t('API')}>
        <Note note={keyNote} />
        <Field label={t('Ключ API')}>
          <div className="flex flex-wrap gap-2">
            <Input
              readOnly
              value={key}
              onFocus={(e) => e.currentTarget.select()}
              className="min-w-[18rem] flex-1 font-mono"
            />
            <Button disabled={!key} onClick={() => void copy()}>
              {t('Копировать')}
            </Button>
            {confirmRotate ? (
              <div className="flex items-center gap-1">
                <span className="px-1 text-sm text-muted">{t('Старый ключ перестанет работать')}</span>
                <Button variant="danger" disabled={rotating} onClick={() => void rotate()}>
                  {t('Сменить')}
                </Button>
                <Button variant="ghost" disabled={rotating} onClick={() => setConfirmRotate(false)}>
                  {t('Отмена')}
                </Button>
              </div>
            ) : (
              <Button variant="ghost" onClick={() => setConfirmRotate(true)}>
                {t('Сменить ключ')}
              </Button>
            )}
          </div>
        </Field>
        <pre className="well thin-scrollbar overflow-x-auto px-3.5 py-3 font-mono text-xs leading-relaxed text-muted">
          {'curl -H "Authorization: Bearer '}
          <span className="text-text">{'<key>'}</span>
          {'" http://<server>/api/v1/admin/reports?days=7'}
        </pre>
      </Section>

      <SaveBar
        dirty={st.dirty}
        saving={st.saving}
        onSave={() => void st.save()}
        onReset={st.reset}
        label={t('Есть несохранённые изменения')}
      />
    </OwnerPage>
  );
}
