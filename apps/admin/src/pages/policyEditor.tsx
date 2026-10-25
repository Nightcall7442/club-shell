/**
 * «Политика ПК» on the hall page (owner, SHELL_CHANGES 27): the document every Agent of the club enforces — the process
 * deny-list, USB, the web filter and its DNS servers, the explorer lockdown, power — edited as JSON. Saving makes it the
 * club's own, so the server's seed no longer replaces it at startup; «Вернуть по умолчанию» goes back to the seed. A
 * connected PC applies a change at once, the others at their next heartbeat.
 */
import { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';
import type { Policy } from '@clubshell/contracts';
import { clubApi, type ClubPolicy } from '@/api';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { Badge, Button, Note, Section, inputCls, type Tone } from '@/ui';

const pretty = (policy: Policy): string => JSON.stringify(policy, null, 2);

export function PolicyEditor(): JSX.Element {
  const [current, setCurrent] = useState<ClubPolicy | null>(null);
  const [text, setText] = useState('');
  const [note, setNote] = useState<{ text: string; tone: Tone } | null>(null);
  const [busy, setBusy] = useState(false);
  // «Вернуть по умолчанию» asks once (danger «Вернуть», «Отмена»): it drops everything the owner changed.
  const [asking, setAsking] = useState(false);

  const show = useCallback((p: ClubPolicy) => {
    setCurrent(p);
    setText(pretty(p.policy));
  }, []);

  useEffect(() => {
    clubApi.policy().then(show, (e: unknown) => setNote({ text: describe(e), tone: 'err' }));
  }, [show]);

  const dirty = current !== null && text !== pretty(current.policy);

  const run = async (action: () => Promise<ClubPolicy>, done: (version: number) => string): Promise<void> => {
    setBusy(true);
    setNote(null);
    try {
      const next = await action();
      show(next);
      setNote({ text: done(next.policy.version), tone: 'ok' });
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
      setAsking(false);
    }
  };

  const save = (): void => {
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      setNote({ text: t('Это не JSON: проверьте скобки, кавычки и запятые'), tone: 'err' });
      return;
    }
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      setNote({ text: t('Политика — это объект JSON в фигурных скобках'), tone: 'err' });
      return;
    }
    void run(
      () => clubApi.savePolicy(parsed as Policy),
      (v) => t('Политика сохранена, версия {v}', { v }),
    );
  };

  return (
    <Section
      title={t('Политика ПК')}
      actions={
        current && (
          <Badge tone={current.custom ? 'accent' : 'muted'}>
            {current.custom ? t('Своя политика клуба') : t('Политика по умолчанию')}
          </Badge>
        )
      }
    >
      <p className="text-[13px] leading-5 text-muted">
        {t(
          'Что агент делает на каждом ПК клуба: запрещённые процессы, флешки, веб-фильтр и DNS, ограничения Windows. Пустой список dnsServers — DNS остаётся от DHCP. После сохранения обновление с сервера эту политику не заменяет.',
        )}
      </p>
      <textarea
        aria-label={t('Политика ПК (JSON)')}
        spellCheck={false}
        disabled={current === null || busy}
        value={text}
        onChange={(e) => setText(e.target.value)}
        className={clsx(inputCls, 'h-auto min-h-80 resize-y py-2.5 font-mono text-[12px] leading-relaxed')}
      />
      {note && <Note note={note} role="status" />}
      <div className="flex flex-wrap items-center justify-between gap-2">
        {current?.custom &&
          (asking ? (
            <span className="flex items-center gap-2">
              <span className="text-sm text-muted">{t('Вернуть политику по умолчанию?')}</span>
              <Button
                variant="danger"
                disabled={busy}
                onClick={() =>
                  void run(
                    () => clubApi.resetPolicy(),
                    (v) => t('Возвращена политика по умолчанию, версия {v}', { v }),
                  )
                }
              >
                {t('Вернуть')}
              </Button>
              <Button variant="ghost" onClick={() => setAsking(false)}>
                {t('Отмена')}
              </Button>
            </span>
          ) : (
            <Button variant="tertiary" disabled={busy} onClick={() => setAsking(true)}>
              {t('Вернуть по умолчанию')}
            </Button>
          ))}
        <span className="ml-auto flex items-center gap-2">
          <Button variant="ghost" disabled={!dirty || busy} onClick={() => current && setText(pretty(current.policy))}>
            {t('Отменить правки')}
          </Button>
          <Button variant="primary" disabled={!dirty || busy} onClick={save}>
            {t('Сохранить политику')}
          </Button>
        </span>
      </div>
    </Section>
  );
}
