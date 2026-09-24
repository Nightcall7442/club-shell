/**
 * "Экран игрока": what the kiosk shows to players — branding, the sections players can open, banners, the club rules
 * in three languages and the minor limits. On the right, a live miniature of the kiosk built from the draft.
 */
import { useState } from 'react';
import clsx from 'clsx';
import type { ShellFeatures } from '@clubshell/contracts';
import type { Banner, ClubSettings } from '@/api';
import { t, type Lang } from '@/i18n';
import { useClubSettings } from '@/settings';
import { Button, Field, Input, NumberInput, PageHeader, SaveBar, Section, Table, Toggle, inputCls } from '@/ui';

const FEATURES: { key: keyof ShellFeatures; label: string }[] = [
  { key: 'shop', label: 'Магазин' },
  { key: 'chat', label: 'Чат' },
  { key: 'booking', label: 'Бронь' },
  { key: 'tournaments', label: 'Турниры' },
  { key: 'profile', label: 'Профиль' },
  { key: 'topup', label: 'Пополнение' },
  { key: 'apps', label: 'Приложения' },
  { key: 'callAdmin', label: 'Вызов администратора' },
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
      className="relative aspect-[16/10] overflow-hidden rounded-md border border-line bg-bg bg-cover bg-center"
      style={wallpaper ? { backgroundImage: `url("${wallpaper}")` } : undefined}
    >
      <div className="absolute inset-0 bg-gradient-to-b from-bg/70 via-bg/40 to-bg/90" />
      <div className="relative flex h-full flex-col">
        <div className="flex items-center gap-3 border-b border-white/10 bg-bg/70 px-3">
          <div className="flex min-w-0 items-center gap-1.5 py-2">
            {s.branding.logoUrl?.trim() ? (
              <img src={s.branding.logoUrl} alt="" className="h-4 w-4 shrink-0 rounded-sm object-cover" />
            ) : (
              <span className="h-3 w-3 shrink-0 rounded-sm" style={{ background: accent }} />
            )}
            <span className="truncate font-display text-[0.62rem] font-medium">{s.branding.clubName || '—'}</span>
          </div>
          <nav className="flex min-w-0 flex-1 gap-2.5 overflow-hidden">
            {tabs.map((x, i) => (
              <span
                key={x.label}
                className={clsx(
                  'whitespace-nowrap border-b-2 py-2 text-[0.55rem]',
                  i === 0 ? 'text-text' : 'border-transparent text-muted',
                )}
                style={i === 0 ? { borderColor: accent } : undefined}
              >
                {t(x.label)}
              </span>
            ))}
          </nav>
          <span className="tnum shrink-0 font-mono text-[0.55rem] text-muted">45 000 сум</span>
        </div>
        <div className="flex flex-1 flex-col justify-end gap-2 p-3">
          {banner && (
            <div
              className="h-10 w-2/3 rounded-sm border border-white/10 bg-cover bg-center"
              style={{ backgroundImage: `url("${banner.imageUrl}")` }}
            />
          )}
          <span className="font-display text-sm font-light leading-tight">Counter-Strike 2</span>
          <div className="flex items-center gap-1.5">
            <span
              className="cut-corners px-3 py-1 text-[0.55rem] font-semibold text-on-accent"
              style={{ ['--fill' as string]: accent }}
            >
              {t('Играть')}
            </span>
            {s.features.callAdmin && (
              <span className="rounded-sm border border-white/15 px-2 py-1 text-[0.55rem] text-muted">
                {t('Вызов администратора')}
              </span>
            )}
          </div>
          <div className="grid grid-cols-5 gap-1.5 pt-1">
            {[0, 1, 2, 3, 4].map((i) => (
              <span
                key={i}
                className={clsx('aspect-[3/4] rounded-sm border bg-white/[0.04]', i === 0 ? '' : 'border-white/10')}
                style={i === 0 ? { borderColor: accent } : undefined}
              />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

export default function ClubPage(): JSX.Element {
  const st = useClubSettings();
  const [rulesLang, setRulesLang] = useState<Lang>('ru');
  const s = st.draft;

  if (!s) {
    return (
      <div className="flex flex-col gap-5">
        <PageHeader title={t('Экран игрока')} />
        {st.error && <p className="text-sm text-danger">{st.error}</p>}
      </div>
    );
  }

  const branding = (patch: Partial<ClubSettings['branding']>): void => st.set('branding', { ...s.branding, ...patch });
  const banner = (id: string, patch: Partial<Banner>): void =>
    st.set(
      'banners',
      s.banners.map((b) => (b.id === id ? { ...b, ...patch } : b)),
    );
  const accentValid = HEX.test(s.branding.accent);

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title={t('Экран игрока')} />
      {st.error && <p className="rounded-md bg-danger/10 px-3 py-2 text-sm text-danger">{st.error}</p>}

      <div className="grid items-start gap-5 xl:grid-cols-[minmax(0,1fr)_28rem]">
        <div className="flex min-w-0 flex-col gap-5">
          <Section title={t('Бренд')}>
            <div className="grid gap-4 md:grid-cols-2">
              <Field label={t('Название клуба')}>
                <Input value={s.branding.clubName} onChange={(e) => branding({ clubName: e.target.value })} />
              </Field>
              <Field label={t('Акцентный цвет')}>
                <div className="flex gap-2">
                  <input
                    type="color"
                    aria-label={t('Акцентный цвет')}
                    value={accentValid ? s.branding.accent : '#9ADFFF'}
                    onChange={(e) => branding({ accent: e.target.value.toUpperCase() })}
                    className="focus-ring h-10 w-12 shrink-0 cursor-pointer rounded-md border border-line bg-bg p-1"
                  />
                  <Input
                    value={s.branding.accent}
                    maxLength={7}
                    onChange={(e) => branding({ accent: e.target.value })}
                    className={clsx('font-mono uppercase', !accentValid && 'border-danger/60')}
                  />
                </div>
              </Field>
              <Field label={t('Логотип (URL)')}>
                <Input
                  value={s.branding.logoUrl ?? ''}
                  placeholder="https://"
                  onChange={(e) => branding({ logoUrl: e.target.value.trim() ? e.target.value : null })}
                />
              </Field>
              <Field label={t('Обои (URL)')}>
                <Input
                  value={s.branding.wallpaperUrl ?? ''}
                  placeholder="https://"
                  onChange={(e) => branding({ wallpaperUrl: e.target.value.trim() ? e.target.value : null })}
                />
              </Field>
            </div>
          </Section>

          <Section title={t('Разделы для игроков')}>
            <div className="grid gap-x-6 gap-y-4 sm:grid-cols-2 2xl:grid-cols-4">
              {FEATURES.map((f) => (
                <Toggle
                  key={f.key}
                  label={t(f.label)}
                  checked={s.features[f.key]}
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
                  render: (b) => <Toggle checked={b.enabled} onChange={(v) => banner(b.id, { enabled: v })} />,
                },
                {
                  key: 'del',
                  title: '',
                  width: '6.5rem',
                  render: (b) => (
                    <Button
                      variant="danger"
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
              <div className="flex gap-1.5">
                {RULE_LANGS.map((l) => (
                  <Button
                    key={l.key}
                    size="sm"
                    aria-pressed={rulesLang === l.key}
                    className={clsx('w-11 font-mono', rulesLang === l.key && 'choice-on')}
                    onClick={() => setRulesLang(l.key)}
                  >
                    {l.label}
                  </Button>
                ))}
              </div>
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

          <Section title={t('Ограничения')}>
            <div className="grid gap-4 sm:grid-cols-2 md:max-w-lg">
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
          </Section>
        </div>

        <Section title={t('Предпросмотр')} className="xl:sticky xl:top-5">
          <Preview s={s} />
        </Section>
      </div>

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
