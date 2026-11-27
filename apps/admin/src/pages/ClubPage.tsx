/**
 * "Экран игрока": what the kiosk shows to players — branding, the sections players can open, banners, the club rules
 * in three languages, whether players create their own account on the lock screen (D-92, `limits.selfSignup`, off by
 * default) and the limits (minors, auto-extension, postpaid debts of guests and clients, who may take cash out of the
 * drawer). On the right, a live miniature of the kiosk built from the draft.
 */
import { useState } from 'react';
import clsx from 'clsx';
import type { ShellFeatures } from '@clubshell/contracts';
import type { Banner, ClubSettings, ScreensaverSettings } from '@/api';
import { t, type Lang } from '@/i18n';
import { useClubSettings } from '@/settings';
import {
  Button,
  Field,
  Input,
  Note,
  NumberInput,
  PageHeader,
  SaveBar,
  Section,
  Segmented,
  Table,
  Toggle,
  inputCls,
} from '@/ui';
import { OwnerPage } from './ownerKit';

const FEATURES: { key: keyof ShellFeatures; label: string }[] = [
  { key: 'shop', label: 'Магазин' },
  { key: 'chat', label: 'Чат' },
  { key: 'booking', label: 'Бронь' },
  { key: 'tournaments', label: 'Турниры' },
  { key: 'profile', label: 'Профиль' },
  { key: 'topup', label: 'Пополнение' },
  { key: 'apps', label: 'Приложения' },
  // The kiosk's «Позвать администратора» button: its calls ring at the desk (the bell in the top bar).
  { key: 'callAdmin', label: 'Вызов администратора (кнопка на ПК, звонок на кассе)' },
  // Off by default: the panel pauses the kiosk guard while open, and its driver settings stay for the next player.
  { key: 'gpuPanel', label: 'Панель видеокарты (NVIDIA / AMD / Intel)' },
  // On unless switched off (SHELL_CHANGES 93): power without the PIN, only while nobody plays.
  { key: 'lockScreenPower', label: 'Кнопки «Выключить» и «Перезагрузить» на экране входа' },
];

/** Kiosk tabs in their shell order; `Игры` is always there. */
const PREVIEW_TABS: { key: keyof ShellFeatures | null; label: string }[] = [
  { key: null, label: 'Игры' },
  { key: 'apps', label: 'Приложения' },
  { key: 'shop', label: 'Магазин' },
  { key: 'booking', label: 'Бронь' },
  { key: 'tournaments', label: 'Турниры' },
  { key: 'chat', label: 'Чат' },
  { key: 'profile', label: 'Профиль' },
];

const RULE_LANGS: { key: Lang; label: string }[] = [
  { key: 'ru', label: 'RU' },
  { key: 'uz', label: 'UZ' },
  { key: 'en', label: 'EN' },
];

const HEX = /^#[0-9a-fA-F]{6}$/;

function Preview({ s }: { s: ClubSettings }): JSX.Element {
  const accent = HEX.test(s.branding.accent) ? s.branding.accent : '#9ADFFF';
  const tabs = PREVIEW_TABS.filter((x) => x.key === null || s.features[x.key]);
  const wallpaper = s.branding.wallpaperUrl?.trim();
  const banner = s.banners.find((b) => b.enabled && b.imageUrl.trim());
  return (
    <div
      className="relative aspect-[16/10] overflow-hidden rounded-md border border-line-strong bg-bg bg-cover bg-center shadow-float"
      style={wallpaper ? { backgroundImage: `url("${wallpaper}")` } : undefined}
    >
      <div className="absolute inset-0 bg-gradient-to-b from-bg/70 via-bg/40 to-bg/90" />
      <div className="relative flex h-full flex-col">
        <div className="border-b border-line bg-bg/70 px-3">
          <div className="flex items-center justify-between gap-3 pt-2">
            <div className="flex min-w-0 items-center gap-1.5">
              {s.branding.logoUrl?.trim() ? (
                <img src={s.branding.logoUrl} alt="" className="h-4 w-4 shrink-0 rounded-sm object-cover" />
              ) : (
                <span className="h-3 w-3 shrink-0 rounded-sm" style={{ background: accent }} />
              )}
              <span className="truncate font-display text-[10px] font-medium">{s.branding.clubName || '—'}</span>
            </div>
            <span className="tnum shrink-0 font-mono text-[9px] text-muted">{t('{n} сум', { n: '45 000' })}</span>
          </div>
          {/* The kiosk's tab row, drawn: a picture of the kiosk, not a navigation of this console. */}
          <div className="flex flex-wrap gap-x-3">
            {tabs.map((x, i) => (
              <span
                key={x.label}
                className={clsx(
                  'whitespace-nowrap border-b-2 py-1.5 text-[9.5px]',
                  i === 0 ? 'text-text' : 'border-transparent text-muted',
                )}
                style={i === 0 ? { borderColor: accent } : undefined}
              >
                {t(x.label)}
              </span>
            ))}
          </div>
        </div>
        <div className="flex flex-1 flex-col justify-end gap-2 p-3">
          {banner && (
            <div
              className="h-10 w-2/3 rounded-sm border border-line bg-cover bg-center"
              style={{ backgroundImage: `url("${banner.imageUrl}")` }}
            />
          )}
          <span className="font-display text-sm font-medium leading-tight">Counter-Strike 2</span>
          <div className="flex items-center gap-1.5">
            <span
              className="cut-corners px-3 py-1 text-[9px] font-semibold text-on-accent"
              style={{ ['--fill' as string]: accent }}
            >
              {t('Играть')}
            </span>
            {s.features.callAdmin && (
              <span className="rounded-sm border border-text/[0.12] px-2 py-1 text-[9px] text-muted">
                {t('Вызов администратора')}
              </span>
            )}
          </div>
          <div className="grid grid-cols-5 gap-1.5 pt-1">
            {[0, 1, 2, 3, 4].map((i) => (
              <span
                key={i}
                className={clsx('aspect-[3/4] rounded-sm border bg-text/[0.04]', i === 0 ? '' : 'border-line')}
                style={i === 0 ? { borderColor: accent } : undefined}
              />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

/**
 * A debt cap in sums: empty (stored `null`; 0 reads the same) is no cap at all — the server's meaning for
 * `guestDebtLimit` and `memberDebtLimit` alike.
 */
function DebtLimitInput({
  value,
  onChange,
}: {
  /** Tiyin; `null` or 0 — no cap. */
  value: number | null;
  onChange: (tiyin: number | null) => void;
}): JSX.Element {
  const sums = value !== null && value > 0 ? Math.round(value / 100) : null;
  return (
    <div className="relative">
      <input
        type="number"
        min={0}
        max={10_000_000}
        inputMode="numeric"
        className={clsx(inputCls, 'tnum pr-10')}
        placeholder={t('без ограничения')}
        value={sums ?? ''}
        onChange={(e) => {
          const n = Math.floor(Number(e.target.value));
          onChange(e.target.value.trim() === '' || !Number.isFinite(n) || n <= 0 ? null : n * 100);
        }}
      />
      <span className="pointer-events-none absolute right-3.5 top-1/2 -translate-y-1/2 text-xs text-muted">
        {t('сум')}
      </span>
    </div>
  );
}

export default function ClubPage(): JSX.Element {
  const st = useClubSettings();
  const [rulesLang, setRulesLang] = useState<Lang>('ru');
  const s = st.draft;

  if (!s) {
    return (
      <OwnerPage>
        <PageHeader title={t('Экран игрока')} caption={t('Настройка клуба')} />
        {st.error && <Note tone="err">{st.error}</Note>}
      </OwnerPage>
    );
  }

  // Desk postpaid is on unless the owner turned it off (a club saved before it was a setting has no value).
  const guestPostpaid = s.limits.guestPostpaid ?? true;
  const banner = (id: string, patch: Partial<Banner>): void =>
    st.set(
      'banners',
      s.banners.map((b) => (b.id === id ? { ...b, ...patch } : b)),
    );

  return (
    <OwnerPage>
      <PageHeader title={t('Экран игрока')} caption={t('Настройка клуба')} />
      {st.error && <Note tone="err">{st.error}</Note>}

      <div className="grid items-start gap-5 xl:grid-cols-[minmax(0,1fr)_400px]">
        <div className="flex min-w-0 flex-col gap-5">
          <Section title={t('Название, логотип и тема')}>
            <p className="flex flex-wrap items-center justify-between gap-3 text-sm text-muted">
              <span>{t('Название, логотип, обои, цвет и тема ПК — в разделе «Интерфейс и логотипы».')}</span>
              <a href="#/interface" className="focus-ring rounded-sm font-medium text-accent hover:underline">
                {t('Открыть')}
              </a>
            </p>
          </Section>

          <Section title={t('Разделы для игроков')}>
            <div className="grid gap-x-6 gap-y-4 sm:grid-cols-2">
              {FEATURES.map((f) => (
                <Toggle
                  key={f.key}
                  label={t(f.label)}
                  checked={
                    f.key === 'lockScreenPower' ? s.features.lockScreenPower !== false : s.features[f.key] === true
                  }
                  onChange={(v) => st.set('features', { ...s.features, [f.key]: v })}
                />
              ))}
            </div>
          </Section>

          <Section
            title={t('Баннеры')}
            bodyClassName="p-2"
            actions={
              <Button
                size="sm"
                onClick={() =>
                  st.set('banners', [
                    ...s.banners,
                    { id: crypto.randomUUID(), title: '', imageUrl: '', from: null, to: null, enabled: true },
                  ])
                }
              >
                {t('Добавить баннер')}
              </Button>
            }
          >
            <Table
              rows={s.banners}
              rowKey={(b) => b.id}
              empty={t('Баннеров нет')}
              columns={[
                {
                  key: 'title',
                  title: t('Заголовок'),
                  width: '22%',
                  render: (b) => <Input value={b.title} onChange={(e) => banner(b.id, { title: e.target.value })} />,
                },
                {
                  key: 'image',
                  title: t('Изображение (URL)'),
                  render: (b) => (
                    <Input
                      value={b.imageUrl}
                      placeholder="https://"
                      onChange={(e) => banner(b.id, { imageUrl: e.target.value })}
                    />
                  ),
                },
                {
                  key: 'from',
                  title: t('С'),
                  width: '9.5rem',
                  render: (b) => (
                    <Input
                      type="date"
                      className="tnum"
                      value={b.from ?? ''}
                      onChange={(e) => banner(b.id, { from: e.target.value || null })}
                    />
                  ),
                },
                {
                  key: 'to',
                  title: t('По'),
                  width: '9.5rem',
                  render: (b) => (
                    <Input
                      type="date"
                      className="tnum"
                      value={b.to ?? ''}
                      onChange={(e) => banner(b.id, { to: e.target.value || null })}
                    />
                  ),
                },
                {
                  key: 'on',
                  title: t('Показ'),
                  width: '4.5rem',
                  render: (b) => (
                    <Toggle ariaLabel={t('Показ')} checked={b.enabled} onChange={(v) => banner(b.id, { enabled: v })} />
                  ),
                },
                {
                  key: 'del',
                  title: '',
                  width: '6.5rem',
                  render: (b) => (
                    <Button
                      variant="tertiary"
                      size="sm"
                      onClick={() =>
                        st.set(
                          'banners',
                          s.banners.filter((x) => x.id !== b.id),
                        )
                      }
                    >
                      {t('Удалить')}
                    </Button>
                  ),
                },
              ]}
            />
          </Section>

          <Section
            title={t('Правила клуба')}
            actions={
              <Segmented
                size="md"
                full={false}
                label={t('Язык правил')}
                value={rulesLang}
                onChange={setRulesLang}
                options={RULE_LANGS.map((l) => ({
                  id: l.key,
                  label: <span className="font-mono text-[10.5px] uppercase tracking-[0.08em]">{l.label}</span>,
                }))}
              />
            }
          >
            <textarea
              aria-label={t('Правила клуба')}
              rows={6}
              value={s.rulesText[rulesLang]}
              onChange={(e) => st.set('rulesText', { ...s.rulesText, [rulesLang]: e.target.value })}
              className={clsx(inputCls, 'h-auto resize-y py-2.5 leading-relaxed')}
            />
          </Section>

          {s.screensaver && <ScreensaverSection value={s.screensaver} onChange={(v) => st.set('screensaver', v)} />}

          <Section title={t('Регистрация на ПК')}>
            <div className="flex flex-col gap-3 md:max-w-xl">
              <Toggle
                label={t('Игроки создают аккаунт сами')}
                checked={s.limits.selfSignup === true}
                onChange={(v) => st.set('limits', { ...s.limits, selfSignup: v })}
              />
              <p className="-mt-1 pl-14 text-[11.5px] leading-4 text-muted">
                {t(
                  'На экране входа появится «Создать аккаунт»: имя, логин или телефон и пароль. Если заданы правила клуба, игрок соглашается с ними. Новый аккаунт — обычный клиент с нулевым балансом; с одного ПК — не больше 5 аккаунтов в час.',
                )}
              </p>
            </div>
          </Section>

          <Section title={t('Ограничения')}>
            <div className="grid gap-x-5 gap-y-4 sm:grid-cols-2 md:max-w-xl">
              <Field label={t('Несовершеннолетние — младше')}>
                <NumberInput
                  value={s.limits.minorAge}
                  min={0}
                  max={99}
                  suffix={t('лет')}
                  onChange={(n) => st.set('limits', { ...s.limits, minorAge: n })}
                />
              </Field>
              <Field label={t('Комендантский час с')}>
                <Input
                  type="time"
                  className="tnum"
                  value={s.limits.minorCurfew}
                  onChange={(e) => st.set('limits', { ...s.limits, minorCurfew: e.target.value })}
                />
              </Field>
            </div>
            <div className="mt-1 flex flex-col gap-3 border-t border-accent/[0.08] pt-5 md:max-w-xl">
              <Toggle
                label={t('Автопродление сеанса')}
                checked={(s.limits.autoExtendMinutes ?? 0) > 0}
                onChange={(v) => st.set('limits', { ...s.limits, autoExtendMinutes: v ? 30 : 0 })}
              />
              <p className="-mt-1 pl-14 text-[11.5px] leading-4 text-muted">
                {t('Когда оплаченное время кончается, а на балансе клиента хватает денег, сеанс продлевается сам.')}
              </p>
              {(s.limits.autoExtendMinutes ?? 0) > 0 && (
                <Field label={t('Продлевать на')}>
                  <NumberInput
                    value={s.limits.autoExtendMinutes ?? 30}
                    min={5}
                    max={720}
                    suffix={t('мин')}
                    onChange={(n) => st.set('limits', { ...s.limits, autoExtendMinutes: Math.max(5, n) })}
                  />
                </Field>
              )}
              {/* Postpaid is the desk's: on a PC the player buys time in advance (a member signing in with money
                  plays pay as you go from the balance until it runs out — that start is not capped here). */}
              <p className="text-[11.5px] leading-4 text-muted">
                {t(
                  'Постоплата — только при посадке с кассы: на ПК игрок сам покупает время заранее. По умолчанию постоплата на кассе включена для клиентов и гостей.',
                )}
              </p>
              <Toggle
                label={t('Постоплата для гостей')}
                checked={guestPostpaid}
                onChange={(v) => st.set('limits', { ...s.limits, guestPostpaid: v })}
              />
              <p className="-mt-1 pl-14 text-[11.5px] leading-4 text-muted">
                {t(
                  'Кассир сажает гостя без предоплаты, гость платит на кассе после сеанса. Неоплаченные счета видны на карте зала.',
                )}
              </p>
              {guestPostpaid && (
                <Field
                  label={t('Наибольший долг гостя')}
                  hint={t('Пусто — без ограничения. Ограничение действует только на сеансы с кассы.')}
                >
                  <DebtLimitInput
                    value={s.limits.guestDebtLimit ?? null}
                    onChange={(v) => st.set('limits', { ...s.limits, guestDebtLimit: v })}
                  />
                </Field>
              )}
              <Field
                label={t('Наибольший долг клиента на постоплате')}
                hint={t(
                  'Насколько клиент с кассовой постоплатой может уйти в минус. Пусто — без ограничения. Ограничение действует только на сеансы с кассы.',
                )}
              >
                <DebtLimitInput
                  value={s.limits.memberDebtLimit ?? null}
                  onChange={(v) => st.set('limits', { ...s.limits, memberDebtLimit: v })}
                />
              </Field>
              <Toggle
                label={t('Изъятие из кассы — только владелец')}
                checked={s.limits.cashOutOwnerOnly ?? false}
                onChange={(v) => st.set('limits', { ...s.limits, cashOutOwnerOnly: v })}
              />
              <p className="-mt-1 pl-14 text-[11.5px] leading-4 text-muted">
                {t('Кассир сможет только вносить деньги в кассу; изымать — владелец.')}
              </p>
            </div>
          </Section>
        </div>

        <Section variant="side" title={t('Предпросмотр')} className="xl:sticky xl:top-0">
          <Preview s={s} />
        </Section>
      </div>

      <SaveBar
        dirty={st.dirty}
        saving={st.saving}
        onSave={() => void st.save()}
        onReset={() => st.reset()}
        label={t('Есть несохранённые изменения')}
      />
    </OwnerPage>
  );
}

/**
 * «Заставка на свободных ПК» (D-87): on a PC nobody is signed in to, after the minutes without input the shell plays
 * these pictures and clips (none — its built-in slides with the prices) until someone touches it.
 */
function ScreensaverSection({
  value,
  onChange,
}: {
  value: ScreensaverSettings;
  onChange: (v: ScreensaverSettings) => void;
}): JSX.Element {
  const set = (p: Partial<ScreensaverSettings>): void => onChange({ ...value, ...p });
  const editItem = (i: number, p: Partial<ScreensaverSettings['items'][number]>): void =>
    set({ items: value.items.map((x, j) => (j === i ? { ...x, ...p } : x)) });
  const rows = value.items.map((item, i) => ({ item, i }));
  return (
    <Section
      title={t('Заставка на свободных ПК')}
      bodyClassName="flex flex-col gap-4"
      actions={<Toggle checked={value.enabled} onChange={(v) => set({ enabled: v })} label={t('Заставка включена')} />}
    >
      <p className="text-sm text-muted">
        {t(
          'Когда за ПК никого нет, после бездействия на экране идут ваши картинки и ролики. Касание — снова экран входа. Без файлов — встроенные слайды с ценами.',
        )}
      </p>
      <Field label={t('Через сколько минут без действий')} className="max-w-[16rem]">
        <NumberInput
          value={value.afterMinutes}
          min={1}
          max={120}
          suffix={t('мин')}
          onChange={(n) => set({ afterMinutes: n })}
        />
      </Field>
      <div data-testid="screensaver-items">
        <Table
          rows={rows}
          rowKey={(r) => String(r.i)}
          empty={t('Файлов нет — будут встроенные слайды')}
          columns={[
            {
              key: 'url',
              title: t('Ссылка на картинку или видео'),
              render: (r) => (
                <Input
                  aria-label={t('Ссылка на картинку или видео')}
                  value={r.item.url}
                  placeholder="https://"
                  onChange={(e) => {
                    const url = e.target.value;
                    // A clip by its extension: the owner rarely thinks of the switch.
                    const video = /\.(mp4|webm|mov|m4v)(\?|$)/i.test(url.trim());
                    editItem(r.i, {
                      url,
                      type: video ? 'video' : r.item.type === 'video' && url.trim() === '' ? 'image' : r.item.type,
                    });
                  }}
                />
              ),
            },
            {
              key: 'type',
              title: t('Тип'),
              width: '12rem',
              render: (r) => (
                <Segmented
                  size="md"
                  label={t('Тип')}
                  value={r.item.type}
                  onChange={(v) => editItem(r.i, { type: v })}
                  options={[
                    { id: 'image', label: t('Картинка') },
                    { id: 'video', label: t('Видео') },
                  ]}
                />
              ),
            },
            {
              key: 'sec',
              title: t('Секунд'),
              width: '7.5rem',
              render: (r) => (
                <NumberInput
                  value={r.item.durationSec}
                  min={3}
                  max={600}
                  disabled={r.item.type === 'video'}
                  onChange={(n) => editItem(r.i, { durationSec: n })}
                />
              ),
            },
            {
              key: 'rm',
              title: '',
              width: '6rem',
              render: (r) => (
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => set({ items: value.items.filter((_, j) => j !== r.i) })}
                >
                  {t('Удалить')}
                </Button>
              ),
            },
          ]}
        />
      </div>
      <div>
        <Button
          size="sm"
          disabled={value.items.length >= 20}
          onClick={() => set({ items: [...value.items, { url: '', type: 'image', durationSec: 10 }] })}
        >
          {t('Добавить картинку или видео')}
        </Button>
      </div>
    </Section>
  );
}
