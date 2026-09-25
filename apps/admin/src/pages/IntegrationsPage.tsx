/**
 * "Уведомления и API": Telegram notifications to the owner, outbound webhooks per club event and the club API key
 * (accepted as an owner token on `/api/v1/admin/*`). The bot token comes back masked as `••••` and is kept unless edited.
 */
import { useState } from 'react';
import clsx from 'clsx';
import { clubApi, type ClubEvent, type Webhook } from '@/api';
import { describe } from '@/errors';
import { dateLocale, t } from '@/i18n';
import { useClubSettings } from '@/settings';
import { Button, Field, Input, MoneyInput, Note, PageHeader, SaveBar, Section, Table, Toggle } from '@/ui';

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

function EventChip({ ev, on, onClick }: { ev: ClubEvent; on?: boolean; onClick?: () => void }): JSX.Element {
  const cls = 'rounded border px-1.5 py-0.5 font-mono text-[0.68rem] leading-none';
  if (!onClick) {
    return (
      <span title={t(EVENT_LABEL[ev])} className={clsx(cls, 'border-line bg-white/[0.03] text-muted')}>
        {ev}
      </span>
    );
  }
  return (
    <button
      type="button"
      title={t(EVENT_LABEL[ev])}
      aria-pressed={on}
      onClick={onClick}
      className={clsx(cls, 'choice focus-ring h-8 px-2.5', on ? 'choice-on text-text' : 'text-muted')}
    >
      {ev}
    </button>
  );
}

export default function IntegrationsPage(): JSX.Element {
  const st = useClubSettings();
  const s = st.draft;
  const [note, setNote] = useState<NoteState>(null);
  const [testing, setTesting] = useState(false);
  const [hookUrl, setHookUrl] = useState('');
  const [hookEvents, setHookEvents] = useState<ClubEvent[]>([]);
  const [hookError, setHookError] = useState<string | null>(null);
  const [apiKey, setApiKey] = useState<string | null>(null);
  const [confirmRotate, setConfirmRotate] = useState(false);
  const [rotating, setRotating] = useState(false);
  const [keyNote, setKeyNote] = useState<NoteState>(null);

  if (!s) {
    return (
      <div className="flex flex-col gap-5">
        <PageHeader title={t('Уведомления и API')} />
        {st.error && <p className="text-sm text-danger">{st.error}</p>}
      </div>
    );
  }

  const n = s.notifications;
  const notify = (patch: Partial<typeof n>): void => st.set('notifications', { ...n, ...patch });
  const hooks = s.webhooks;
  const setHook = (id: string, patch: Partial<Webhook>): void =>
    st.set(
      'webhooks',
      hooks.map((h) => (h.id === id ? { ...h, ...patch } : h)),
    );
  const key = apiKey ?? s.apiKey;

  const test = async (): Promise<void> => {
    const saved = st.data?.notifications;
    if (!saved?.telegramBotToken || !saved.telegramChatId) {
      setNote({ text: t('Сначала сохраните токен бота и chat id'), tone: 'err' });
      return;
    }
    setTesting(true);
    setNote(null);
    try {
      await clubApi.testNotification();
      setNote({ text: t('Тестовое уведомление отправлено'), tone: 'ok' });
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setTesting(false);
    }
  };

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
    <div className="flex flex-col gap-5">
      <PageHeader title={t('Уведомления и API')} />
      {st.error && <p className="rounded-md bg-danger/10 px-3 py-2 text-sm text-danger">{st.error}</p>}

      <Section
        title={t('Telegram')}
        actions={
          <Button size="sm" disabled={testing} onClick={() => void test()}>
            {testing ? t('Отправляем…') : t('Отправить тест')}
          </Button>
        }
      >
        <Note note={note} />
        <div className="grid gap-4 md:grid-cols-3">
          <Field label={t('Токен бота')}>
            <Input
              type="password"
              autoComplete="off"
              className="font-mono"
              value={n.telegramBotToken}
              placeholder="123456:ABC…"
              onFocus={(e) => e.currentTarget.select()}
              onChange={(e) => notify({ telegramBotToken: e.target.value })}
            />
          </Field>
          <Field label={t('Chat ID')}>
            <Input
              className="font-mono"
              value={n.telegramChatId}
              placeholder="-1001234567890"
              onChange={(e) => notify({ telegramChatId: e.target.value.trim() })}
            />
          </Field>
          <Field label={t('Крупное пополнение — от')}>
            <MoneyInput value={n.bigTopupAt} onChange={(v) => notify({ bigTopupAt: v })} />
          </Field>
        </div>
        <div className="flex flex-col gap-3 border-t border-line pt-4">
          <span className="label">{t('События')}</span>
          <div className="grid gap-x-6 gap-y-4 sm:grid-cols-2 xl:grid-cols-3">
            {ALL_EVENTS.map((ev) => (
              <Toggle
                key={ev}
                label={t(EVENT_LABEL[ev])}
                checked={n.events[ev] ?? false}
                onChange={(v) => notify({ events: { ...n.events, [ev]: v } })}
              />
            ))}
          </div>
        </div>
      </Section>

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
                render: (h) => <span className="break-all font-mono text-xs">{h.url}</span>,
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
                      <span
                        className={clsx(
                          'tnum font-mono text-xs',
                          h.lastStatus >= 200 && h.lastStatus < 300 ? 'text-success' : 'text-danger',
                        )}
                      >
                        {h.lastStatus === 0 ? t('нет связи') : h.lastStatus}
                      </span>
                      {h.lastAt && <span className="tnum text-xs text-muted">{dateTime(h.lastAt)}</span>}
                    </span>
                  ),
              },
              {
                key: 'del',
                title: '',
                width: '6.5rem',
                render: (h) => (
                  <Button
                    variant="danger"
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
        <div className="flex flex-col gap-3 border-t border-line p-5">
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
          {hookError && <p className="text-sm text-danger">{hookError}</p>}
        </div>
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
            <Button onClick={() => void copy()}>{t('Копировать')}</Button>
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
        <pre className="overflow-x-auto rounded-md border border-line bg-bg px-3 py-2.5 font-mono text-xs leading-relaxed text-muted">
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
    </div>
  );
}
