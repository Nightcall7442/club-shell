/**
 * Обновления ClubShell (owner only): the release published to the club server, which PCs run an older Agent or Shell,
 * «Обновить» for every PC or one, and «Обновлять автоматически» (the PCs install a new release at night by themselves).
 * A free PC installs at once, a busy one when its session ends; an offline PC gets nothing.
 */
import { useCallback, useEffect, useState } from 'react';
import { clubApi, type UpdatePc, type UpdateRelease, type UpdateResult, type UpdatesState } from '@/api';
import { describe } from '@/errors';
import { dateLocale, t } from '@/i18n';
import { Badge, Button, Note, PageHeader, Section, Table, Toggle } from '@/ui';
import { OwnerPage } from './ownerKit';

type NoteState = { text: string; tone: 'ok' | 'warn' | 'err' } | null;

/** Semver order, as the server compares: an unparsable version ("", "dev") is older than any. */
export function compareVersions(a: string, b: string): number {
  const parse = (v: string): [number, number, number, string | null] | null => {
    const m = /^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/.exec(v.trim());
    return m ? [Number(m[1]), Number(m[2]), Number(m[3]), m[4] ?? null] : null;
  };
  const x = parse(a);
  const y = parse(b);
  if (!x || !y) return x ? 1 : y ? -1 : 0;
  for (let i = 0; i < 3; i++) {
    const d = (x[i] as number) - (y[i] as number);
    if (d !== 0) return d;
  }
  if (x[3] === y[3]) return 0;
  if (x[3] === null) return 1;
  if (y[3] === null) return -1;
  return x[3] < y[3] ? -1 : 1;
}

/** The PC runs an Agent or a Shell older than the release. */
function behind(pc: UpdatePc, release: UpdateRelease): boolean {
  return (
    (release.agent !== null && compareVersions(release.agent, pc.agentVersion) > 0) ||
    (release.shell !== null && compareVersions(release.shell, pc.shellVersion) > 0)
  );
}

function summary(results: { result: UpdateResult }[], version: string): NoteState {
  const count = (r: UpdateResult): number => results.filter((x) => x.result === r).length;
  const sent = count('sent');
  const later = count('afterSession');
  const offline = count('offline');
  const parts = [
    sent > 0 ? t('ставится на {n} ПК', { n: sent }) : null,
    later > 0 ? t('{n} ПК — после конца сеанса', { n: later }) : null,
    offline > 0 ? t('{n} ПК офлайн — не отправлено', { n: offline }) : null,
  ].filter((p): p is string => p !== null);
  if (parts.length === 0) return { text: t('Все ПК уже на версии {version}', { version }), tone: 'ok' };
  return { text: `ClubShell ${version}: ${parts.join(', ')}`, tone: offline > 0 && sent + later === 0 ? 'warn' : 'ok' };
}

export default function UpdatesPage(): JSX.Element {
  const [state, setState] = useState<UpdatesState | null>(null);
  const [note, setNote] = useState<NoteState>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async (): Promise<void> => {
    try {
      setState(await clubApi.updates());
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const release = state?.releases.find((r) => r.channel === 'stable') ?? state?.releases[0] ?? null;
  const pcs = state?.pcs ?? [];
  const outdated = release ? pcs.filter((p) => behind(p, release)) : [];

  const apply = async (pcIds?: string[]): Promise<void> => {
    if (!release) return;
    setBusy(true);
    setNote(null);
    try {
      const res = await clubApi.applyUpdate({ channel: release.channel, ...(pcIds ? { pcIds } : {}) });
      setNote(summary(res.results, res.version));
      await load();
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  const setAuto = async (auto: boolean): Promise<void> => {
    setBusy(true);
    setNote(null);
    try {
      await clubApi.setAutoUpdate(auto);
      setNote({
        text: auto
          ? t('Автообновление включено: свободные ПК поставят новую версию ночью (04:00–07:00)')
          : t('Автообновление выключено: новая версия ставится только кнопкой «Обновить»'),
        tone: 'ok',
      });
      await load();
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <OwnerPage>
      <PageHeader title={t('Обновления ClubShell')} caption={t('Настройка клуба')} />
      <Note note={note} role="status" />

      <Section title={t('Версия на сервере')}>
        {state && !release ? (
          <p className="text-[13px] text-muted">
            {t('На сервер ещё не опубликован ни один выпуск ClubShell. Его публикует разработчик (publish.ps1).')}
          </p>
        ) : release ? (
          <div className="flex flex-wrap items-center justify-between gap-4">
            <div className="min-w-0">
              <p className="font-display text-xl text-hi">
                ClubShell {release.version} {release.channel === 'beta' && <Badge tone="warn">{t('бета')}</Badge>}{' '}
                {release.mandatory && <Badge tone="danger">{t('обязательное')}</Badge>}
              </p>
              <p className="mt-1 text-[13px] text-muted">
                {t('Опубликовано {date}', { date: new Date(release.publishedAt).toLocaleString(dateLocale()) })}
                {' · '}
                {outdated.length > 0
                  ? t('нужно обновить: {n} из {total} ПК', { n: outdated.length, total: pcs.length })
                  : t('все ПК обновлены')}
              </p>
              {release.notes && <p className="mt-2 whitespace-pre-line text-[13px] text-dim">{release.notes}</p>}
            </div>
            <Button variant="primary" disabled={busy || outdated.length === 0} onClick={() => void apply()}>
              {t('Обновить все ПК')}
            </Button>
          </div>
        ) : null}
        <div className="mt-5 border-t border-line pt-4">
          <Toggle
            checked={state?.auto ?? false}
            disabled={busy || !state}
            onChange={(v) => void setAuto(v)}
            label={t('Обновлять автоматически ночью (04:00–07:00), когда за ПК никого нет')}
          />
        </div>
      </Section>

      <Section title={t('Компьютеры')} bodyClassName="p-2">
        <Table
          rows={pcs}
          rowKey={(p) => p.id}
          empty={t('Компьютеров нет')}
          columns={[
            { key: 'name', title: t('ПК'), render: (p) => <span className="font-medium text-hi">{p.name}</span> },
            {
              key: 'agent',
              title: t('Агент'),
              width: '8rem',
              render: (p) => <span className="tnum">{p.agentVersion || '—'}</span>,
            },
            {
              key: 'shell',
              title: t('Оболочка'),
              width: '8rem',
              render: (p) => <span className="tnum">{p.shellVersion || '—'}</span>,
            },
            {
              key: 'state',
              title: t('Состояние'),
              width: '11rem',
              render: (p) =>
                !release || !behind(p, release) ? (
                  <Badge tone="live">{t('Актуально')}</Badge>
                ) : !p.online ? (
                  <Badge tone="muted">{t('Офлайн')}</Badge>
                ) : p.busy ? (
                  <Badge tone="warn">{t('Играют')}</Badge>
                ) : (
                  <Badge tone="accent">{t('Есть обновление')}</Badge>
                ),
            },
            {
              key: 'action',
              title: '',
              num: true,
              width: '10rem',
              render: (p) =>
                release && behind(p, release) ? (
                  <Button size="sm" variant="ghost" disabled={busy || !p.online} onClick={() => void apply([p.id])}>
                    {t('Обновить')}
                  </Button>
                ) : null,
            },
          ]}
        />
      </Section>
    </OwnerPage>
  );
}
