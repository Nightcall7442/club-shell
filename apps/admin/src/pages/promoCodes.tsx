/**
 * «Промокоды» of «Бонусы и акции»: the club's codes as a list, each created, edited and deleted through a dialog of
 * its own and saved at once (`PATCH /admin/club {promoCodes}` with the list read fresh), not through the page's save bar.
 *
 * What the server does with a code (PromoEndpoints, ClubSettingsEndpoints.SyncPromoCodesAsync): a `bonus` code credits
 * its value to the client's bonus account (D-106, spent on play time only) at the desk or from the player's PC; a client
 * redeems a code once; `usesLeft` (null — no limit) and `used` are server counters, set when the code is created and
 * kept on every later save, so the limit is not edited afterwards; codes compare ignoring case, and a code renamed would
 * be a new code (clients could redeem it again), so the code is not edited either. A `discountPct` code is refused at the
 * desk (no discount by code in v1): the list says so, and the dialog creates bonus codes only.
 */
import { useEffect, useState } from 'react';
import clsx from 'clsx';
import { AdminError, type PromoCode } from '@/api';
import { describe } from '@/errors';
import { moneyParts } from '@/format';
import { dateLocale, t } from '@/i18n';
import { GiftIcon } from '@/icons';
import type { ClubSettingsState } from '@/settings';
import { Badge, Button, EmptyState, Field, Input, MoneyInput, Note, Section, Sheet, Table, inputCls } from '@/ui';

const MAX_CODE = 32;
/** Letters and digits a code is generated from: no 0/O, 1/I/L, so a code read aloud or off a flyer is typed right. */
const CODE_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';

export function generateCode(): string {
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => CODE_ALPHABET[b % CODE_ALPHABET.length]).join('');
}

type Status = 'active' | 'expired' | 'exhausted' | 'inert';

export function promoStatus(p: PromoCode, at = Date.now()): Status {
  if (p.kind !== 'bonus') return 'inert';
  if (p.expiresAt && Date.parse(p.expiresAt) <= at) return 'expired';
  if (p.usesLeft !== null && p.usesLeft <= 0) return 'exhausted';
  return 'active';
}

const STATUS: Record<Status, { label: string; tone: 'live' | 'muted' | 'warn' }> = {
  active: { label: 'Активен', tone: 'live' },
  expired: { label: 'Истёк', tone: 'muted' },
  exhausted: { label: 'Исчерпан', tone: 'warn' },
  inert: { label: 'Не работает', tone: 'warn' },
};

/** `YYYY-MM-DD` of a moment in the console's clock (the date input's value), and the end of such a day back. */
function localDate(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  const p = (n: number): string => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

function endOfDay(date: string): string {
  const [y, m, d] = date.split('-').map(Number);
  return new Date(y!, m! - 1, d!, 23, 59, 59, 999).toISOString();
}

function dateText(iso: string): string {
  return new Date(iso).toLocaleDateString(dateLocale(), { day: 'numeric', month: 'long', year: 'numeric' });
}

/** What the code gives, in words: «+20 000 сум бонусов». */
function gives(p: PromoCode): string {
  return p.kind === 'bonus'
    ? t('+{sum} сум бонусов', { sum: moneyParts(p.value).num })
    : t('Скидка {pct} %', { pct: String(p.value) });
}

/** A code in mono; a click copies it, «Скопировано» for a moment. */
function CopyCode({ code }: { code: string }): JSX.Element {
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!copied) return undefined;
    const id = setTimeout(() => setCopied(false), 1500);
    return () => clearTimeout(id);
  }, [copied]);
  return (
    <span className="inline-flex items-center gap-2">
      <button
        type="button"
        data-promo-code={code}
        title={t('Скопировать')}
        aria-label={t('Скопировать код {code}', { code })}
        onClick={(e) => {
          e.stopPropagation();
          void navigator.clipboard
            ?.writeText(code)
            .then(() => setCopied(true))
            .catch(() => undefined);
        }}
        className="focus-ring -mx-1.5 rounded-sm px-1.5 py-0.5 font-mono text-[14px] font-semibold tracking-[0.08em] text-hi hover:bg-text/[0.05]"
      >
        {code}
      </button>
      {copied && (
        <span role="status" className="text-[11.5px] text-accent">
          {t('Скопировано')}
        </span>
      )}
    </span>
  );
}

export function PromoCodesSection({
  codes,
  commit,
}: {
  codes: PromoCode[];
  commit: ClubSettingsState['commit'];
}): JSX.Element {
  // The dialog: a new code, or the code (as listed) being edited.
  const [editing, setEditing] = useState<'new' | PromoCode | null>(null);
  const [removing, setRemoving] = useState<PromoCode | null>(null);
  const [note, setNote] = useState<string | null>(null);
  useEffect(() => {
    if (!note) return undefined;
    const id = setTimeout(() => setNote(null), 4000);
    return () => clearTimeout(id);
  }, [note]);

  const create = (
    <Button variant="secondary" size="sm" onClick={() => setEditing('new')}>
      {t('Новый промокод')}
    </Button>
  );

  return (
    <Section title={t('Промокоды')} actions={codes.length > 0 ? create : undefined} bodyClassName="p-2">
      <div className="flex flex-col gap-3" data-testid="promo-codes">
        <p className="px-3 pt-1 text-[13px] leading-5 text-dim">
          {t(
            'Клиент называет код на кассе или вводит его на своём ПК — бонус зачисляется на бонусный счёт и тратится только на игровое время. Каждый клиент применяет код один раз. Изменения сохраняются сразу.',
          )}
        </p>
        {note && (
          <Note tone="ok" role="status" className="mx-3">
            {note}
          </Note>
        )}
        {codes.length === 0 ? (
          <div className="flex flex-col items-center gap-5 py-10">
            <EmptyState
              icon={<GiftIcon size={22} />}
              title={t('Промокодов пока нет')}
              text={t('Создайте код — например, для новых гостей или турнира.')}
            />
            <Button variant="primary" onClick={() => setEditing('new')}>
              {t('Создать промокод')}
            </Button>
          </div>
        ) : (
          <Table
            rows={codes}
            rowKey={(p) => p.code.toUpperCase()}
            columns={[
              { key: 'code', title: t('Код'), render: (p) => <CopyCode code={p.code} /> },
              {
                key: 'gives',
                title: t('Что даёт'),
                render: (p) => (
                  <span className="flex flex-col">
                    <span className={clsx('whitespace-nowrap', p.kind === 'bonus' ? 'text-text' : 'text-muted')}>
                      {gives(p)}
                    </span>
                    {p.kind !== 'bonus' && (
                      <span className="text-[11.5px] text-muted">{t('Скидки по коду не поддерживаются')}</span>
                    )}
                  </span>
                ),
              },
              {
                key: 'uses',
                title: t('Использовано'),
                width: '10rem',
                render: (p) => (
                  <span className="tnum whitespace-nowrap">
                    {p.usesLeft === null ? (
                      <>
                        {p.used} <span className="text-muted">· {t('без лимита')}</span>
                      </>
                    ) : (
                      <>
                        {p.used} <span className="text-muted">/ {p.used + p.usesLeft}</span>
                      </>
                    )}
                  </span>
                ),
              },
              {
                key: 'until',
                title: t('Действует до'),
                width: '11rem',
                render: (p) =>
                  p.expiresAt ? (
                    <span className="tnum whitespace-nowrap">{dateText(p.expiresAt)}</span>
                  ) : (
                    <span className="text-muted">{t('без срока')}</span>
                  ),
              },
              {
                key: 'status',
                title: t('Статус'),
                width: '8rem',
                render: (p) => {
                  const s = STATUS[promoStatus(p)];
                  return (
                    <Badge tone={s.tone} className="!normal-case">
                      {t(s.label)}
                    </Badge>
                  );
                },
              },
              {
                key: 'actions',
                title: '',
                width: '13rem',
                render: (p) => (
                  <span className="flex justify-end gap-1">
                    <Button
                      variant="ghost"
                      size="sm"
                      aria-label={t('Изменить {code}', { code: p.code })}
                      onClick={() => setEditing(p)}
                    >
                      {t('Изменить')}
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      className="hover:text-danger-ink"
                      aria-label={t('Удалить {code}', { code: p.code })}
                      onClick={() => setRemoving(p)}
                    >
                      {t('Удалить')}
                    </Button>
                  </span>
                ),
              },
            ]}
          />
        )}
      </div>

      {editing && (
        <PromoDialog
          promo={editing === 'new' ? null : editing}
          codes={codes}
          commit={commit}
          onClose={() => setEditing(null)}
          onSaved={(code, created) => {
            setEditing(null);
            setNote(created ? t('Промокод {code} создан', { code }) : t('Промокод {code} сохранён', { code }));
          }}
        />
      )}
      {removing && (
        <RemoveDialog
          promo={removing}
          commit={commit}
          onClose={() => setRemoving(null)}
          onRemoved={(code) => {
            setRemoving(null);
            setNote(t('Промокод {code} удалён', { code }));
          }}
        />
      )}
    </Section>
  );
}

/** A refusal of the save in words: a code taken by another console in the meantime gets its own line. */
function saveError(e: unknown): string {
  if (e instanceof AdminError && e.code === 'validation' && JSON.stringify(e.details ?? '').includes('taken')) {
    return t('Такой код уже есть');
  }
  return describe(e);
}

function PromoDialog({
  promo,
  codes,
  commit,
  onClose,
  onSaved,
}: {
  promo: PromoCode | null;
  codes: PromoCode[];
  commit: ClubSettingsState['commit'];
  onClose: () => void;
  onSaved: (code: string, created: boolean) => void;
}): JSX.Element {
  const isNew = promo === null;
  const [code, setCode] = useState(() => promo?.code ?? generateCode());
  // A discount code is refused at the desk: editing one turns it into a bonus code, so the sum starts empty.
  const [value, setValue] = useState(promo?.kind === 'bonus' ? promo.value : isNew ? 1_000_000 : 0);
  const [limit, setLimit] = useState('');
  const [until, setUntil] = useState(localDate(promo?.expiresAt ?? null));
  const [tried, setTried] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const today = localDate(new Date().toISOString());
  const errors: Partial<Record<'code' | 'value' | 'limit' | 'until', string>> = {};
  if (isNew) {
    if (code.length === 0) errors.code = t('Введите код');
    else if (code.length > MAX_CODE) errors.code = t('Код — не длиннее 32 символов');
    else if (!/^[A-Z0-9_-]+$/.test(code)) errors.code = t('Только латинские буквы, цифры, «-» и «_»');
    else if (codes.some((p) => p.code.toUpperCase() === code)) errors.code = t('Такой код уже есть');
    if (limit !== '' && Number(limit) < 1) errors.limit = t('Не меньше 1 — или оставьте пустым');
  }
  if (value <= 0) errors.value = t('Укажите сумму бонуса');
  if (until !== '' && until < today && until !== localDate(promo?.expiresAt ?? null))
    errors.until = t('Дата уже прошла');
  const valid = Object.keys(errors).length === 0;
  const shown = (k: keyof typeof errors): string | undefined => (tried ? errors[k] : undefined);

  const save = async (): Promise<void> => {
    setTried(true);
    if (!valid) return;
    setBusy(true);
    setError(null);
    const expiresAt = until ? endOfDay(until) : null;
    try {
      let missing = false;
      await commit('promoCodes', (current): PromoCode[] => {
        if (isNew) {
          if (current.some((p) => p.code.toUpperCase() === code)) {
            throw new AdminError('validation', 'taken', { field: 'promoCodes.code', reason: 'taken' }, 400);
          }
          return [
            ...current,
            { code, kind: 'bonus', value, usesLeft: limit === '' ? null : Number(limit), expiresAt, used: 0 },
          ];
        }
        const key = promo.code.toUpperCase();
        missing = !current.some((p) => p.code.toUpperCase() === key);
        if (missing) return current;
        return current.map((p) => (p.code.toUpperCase() === key ? { ...p, kind: 'bonus', value, expiresAt } : p));
      });
      if (missing) {
        setError(t('Этого промокода уже нет — его удалили с другой кассы'));
        return;
      }
      onSaved(code, isNew);
    } catch (e) {
      setError(saveError(e));
    } finally {
      setBusy(false);
    }
  };

  const left = promo && promo.usesLeft !== null ? promo.usesLeft : null;
  return (
    <Sheet
      title={isNew ? t('Новый промокод') : t('Промокод {code}', { code: promo.code })}
      caption={t('Бонусы и акции')}
      onClose={busy ? undefined : onClose}
    >
      <form
        noValidate
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          void save();
        }}
      >
        {isNew ? (
          <Field label={t('Код')} hint={shown('code') ? undefined : t('Латинские буквы и цифры, до 32 символов')}>
            <span className="flex gap-2">
              <Input
                autoFocus
                className="font-mono uppercase tracking-[0.08em]"
                maxLength={MAX_CODE}
                value={code}
                aria-invalid={Boolean(shown('code'))}
                onChange={(e) => setCode(e.target.value.toUpperCase().replace(/\s/g, ''))}
              />
              <Button variant="tertiary" onClick={() => setCode(generateCode())}>
                {t('Сгенерировать')}
              </Button>
            </span>
          </Field>
        ) : (
          <div className="flex flex-col gap-2">
            <span className="label-sm">{t('Код')}</span>
            <span className="font-mono text-base font-semibold tracking-[0.08em] text-hi">{promo.code}</span>
            <span className="text-[11.5px] leading-4 text-muted">
              {t('Код не меняется: новый код — это новый промокод. Использовано: {used}.', {
                used: String(promo.used),
              })}
            </span>
          </div>
        )}
        <FieldError text={shown('code')} />

        {!isNew && promo.kind !== 'bonus' && (
          <Note tone="warn">
            {t('Скидки по промокоду не поддерживаются: код станет бонусным — укажите сумму бонуса.')}
          </Note>
        )}

        <Field
          label={t('Бонус клиенту')}
          hint={t('Зачисляется на бонусный счёт: тратится только на игровое время, не на бар.')}
        >
          <MoneyInput compact value={value} onChange={setValue} />
        </Field>
        <FieldError text={shown('value')} />

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <div className="flex flex-col gap-2">
            {isNew ? (
              <Field label={t('Сколько раз можно применить')} hint={t('Пусто — без лимита')}>
                <Input
                  inputMode="numeric"
                  className="tnum"
                  placeholder={t('без лимита')}
                  value={limit}
                  aria-invalid={Boolean(shown('limit'))}
                  onChange={(e) => setLimit(e.target.value.replace(/\D/g, '').slice(0, 6))}
                />
              </Field>
            ) : (
              <Field label={t('Сколько раз можно применить')} hint={t('Лимит задаётся при создании кода')}>
                <Input
                  readOnly
                  className="tnum"
                  value={left === null ? t('без лимита') : t('осталось {n}', { n: String(left) })}
                />
              </Field>
            )}
            <FieldError text={shown('limit')} />
          </div>
          <div className="flex flex-col gap-2">
            <Field label={t('Действует до')} hint={t('Включительно. Пусто — без срока')}>
              <input
                type="date"
                min={today}
                className={clsx(inputCls, 'tnum')}
                value={until}
                aria-invalid={Boolean(shown('until'))}
                onChange={(e) => setUntil(e.target.value)}
              />
            </Field>
            <FieldError text={shown('until')} />
          </div>
        </div>

        {error && (
          <Note tone="err" role="alert">
            {error}
          </Note>
        )}

        <div className="flex justify-end gap-2 border-t border-accent/[0.08] pt-4">
          <Button variant="ghost" disabled={busy} onClick={onClose}>
            {t('Отмена')}
          </Button>
          <Button variant="primary" type="submit" disabled={busy}>
            {busy ? t('Сохраняем…') : isNew ? t('Создать') : t('Сохранить')}
          </Button>
        </div>
      </form>
    </Sheet>
  );
}

function FieldError({ text }: { text: string | undefined }): JSX.Element | null {
  if (!text) return null;
  return (
    <span role="alert" className="-mt-2 text-[12.5px] text-danger-ink">
      {text}
    </span>
  );
}

function RemoveDialog({
  promo,
  commit,
  onClose,
  onRemoved,
}: {
  promo: PromoCode;
  commit: ClubSettingsState['commit'];
  onClose: () => void;
  onRemoved: (code: string) => void;
}): JSX.Element {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const remove = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const key = promo.code.toUpperCase();
      await commit('promoCodes', (current) => current.filter((p) => p.code.toUpperCase() !== key));
      onRemoved(promo.code);
    } catch (e) {
      setError(describe(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Sheet title={t('Удалить промокод {code}?', { code: promo.code })} onClose={busy ? undefined : onClose}>
      <p className="text-sm leading-5 text-dim">
        {t('Клиенты больше не смогут его применить. Бонусы, уже начисленные по нему, останутся у клиентов.')}
      </p>
      {error && (
        <Note tone="err" role="alert">
          {error}
        </Note>
      )}
      <div className="flex justify-end gap-2">
        <Button variant="ghost" autoFocus disabled={busy} onClick={onClose}>
          {t('Отмена')}
        </Button>
        <Button variant="danger" disabled={busy} onClick={() => void remove()}>
          {busy ? t('Удаляем…') : t('Удалить')}
        </Button>
      </div>
    </Sheet>
  );
}
