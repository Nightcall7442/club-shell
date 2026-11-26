/**
 * «Интерфейс и логотипы» (D-93, D-95, D-105): how the club looks everywhere — its name, logo, wallpaper and colour on
 * the PCs' lock, idle and top bar, on the console's rail and on every printed slip, and the theme the PCs start in —
 * and the player-screen designer: per screen, where the logo stands and how large, the picture behind it with its
 * dimming and blur, which elements show, the owner's texts per language, the headings font and the plates' tint, with
 * a live miniature of each screen. Pictures are uploaded here (kept on the club's server) or given as a link. Saved
 * with the shared save bar, like «Экран игрока».
 */
import { useRef, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import {
  mediaUrl,
  uploadMedia,
  type ClubSettings,
  type LogoPosition,
  type LogoSize,
  type LookText,
  type PcLabelSettings,
  type ScreenLookSettings,
} from '@/api';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { useClubSettings } from '@/settings';
import { Button, Field, Input, Note, PageHeader, SaveBar, Section, Segmented, Tabs, Toggle, inputCls } from '@/ui';
import { OwnerPage } from './ownerKit';
import {
  DEFAULT_DIM,
  FONTS,
  HEX,
  MAX_DIM,
  POSITION_NAMES,
  SHELL_TEXT,
  SIZES,
  TEXT_MAX,
  THEMES,
  PositionPad,
  ScreenPreview,
  contrast,
  resolve,
  safeTint,
  type Lang,
  type Screen,
  type ThemeId,
} from './screenDesigner';

type Branding = ClubSettings['branding'];

const PC_LABEL_DEFAULT: PcLabelSettings = { format: 'nameZone', large: false, lock: true, idle: true, topBar: true };

/** What the PC's label can say, with how it reads for PC-05 in the VIP zone. */
const PC_FORMATS: { id: PcLabelSettings['format']; name: string; sample: string }[] = [
  { id: 'nameZone', name: 'Название и зона', sample: 'PC-05 · VIP' },
  { id: 'name', name: 'Только название', sample: 'PC-05' },
  { id: 'number', name: 'Только номер', sample: 'ПК 05' },
];

const SCREENS: { id: Screen; name: string }[] = [
  { id: 'lock', name: 'Экран входа' },
  { id: 'idle', name: 'Заставка' },
  { id: 'desktop', name: 'Рабочий стол' },
];

const SIZE_NAMES: Record<LogoSize, string> = { s: 'S', m: 'M', l: 'L', xl: 'XL' };
const LANGS: { id: Lang; name: string }[] = [
  { id: 'ru', name: 'RU' },
  { id: 'uz', name: 'UZ' },
  { id: 'en', name: 'EN' },
];

const PICK_TYPES = 'image/png,image/jpeg,image/webp,image/gif';

/** How a headings font reads, Latin and Cyrillic (the PCs show both). */
const FONT_SAMPLE = 'Aa Бб'; // i18n-ignore

/** One picture of the club: the preview, «Загрузить файл», «Убрать» and the link it is stored as. */
function PictureField({
  label,
  hint,
  value,
  kind,
  onChange,
  preview,
  testId,
}: {
  label: string;
  hint: string;
  value: string | null;
  kind: 'logo' | 'wallpaper';
  onChange: (url: string | null) => void;
  preview: (src: string) => ReactNode;
  testId?: string;
}): JSX.Element {
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const shown = mediaUrl(value);

  const pick = async (file: File | undefined): Promise<void> => {
    if (!file) return;
    if (file.size > 5 * 1024 * 1024) {
      setError(t('Файл больше 5 МБ'));
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const r = await uploadMedia(file, kind);
      onChange(r.url);
    } catch (e) {
      setError(describe(e));
    } finally {
      setBusy(false);
      if (input.current) input.current.value = '';
    }
  };

  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid={testId ?? `picture-${kind}`}>
      <span className="label-sm">{label}</span>
      <div className="flex flex-wrap items-center gap-4">
        <div className="flex h-24 w-40 shrink-0 items-center justify-center overflow-hidden rounded-md border border-text/10 bg-bg/60">
          {shown ? preview(shown) : <span className="text-xs text-muted">{t('Нет')}</span>}
        </div>
        <div className="flex min-w-0 flex-1 flex-col gap-2">
          <div className="flex gap-2">
            <Button size="sm" variant="primary" disabled={busy} onClick={() => input.current?.click()}>
              {busy ? t('Загрузка…') : t('Загрузить файл')}
            </Button>
            {value && (
              <Button size="sm" variant="ghost" onClick={() => onChange(null)}>
                {t('Убрать')}
              </Button>
            )}
          </div>
          <span className="text-xs text-muted">{hint}</span>
        </div>
        <input
          ref={input}
          type="file"
          accept={PICK_TYPES}
          aria-label={t('{label}: файл', { label })}
          className="hidden"
          onChange={(e) => void pick(e.target.files?.[0])}
        />
      </div>
      <Field label={t('Или ссылка')}>
        <Input
          value={value ?? ''}
          placeholder="https://"
          onChange={(e) => onChange(e.target.value.trim() ? e.target.value : null)}
        />
      </Field>
      {error && <Note tone="err">{error}</Note>}
    </div>
  );
}

/** A small titled group inside the designer: a mono caption over its controls. */
function Group({ title, children, aside }: { title: string; children: ReactNode; aside?: ReactNode }): JSX.Element {
  return (
    <div className="flex flex-col gap-3 border-t border-line pt-5 first:border-t-0 first:pt-0">
      <div className="flex items-baseline justify-between gap-3">
        <span className="label-sm text-text">{title}</span>
        {aside}
      </div>
      {children}
    </div>
  );
}

/** The owner's line in the three player languages, the shell's own as the placeholder of each. */
function TextField({
  label,
  value,
  lang,
  fallback,
  max,
  onChange,
}: {
  label: string;
  value: LookText | undefined;
  lang: Lang;
  fallback: string;
  max: number;
  onChange: (v: LookText) => void;
}): JSX.Element {
  const current = value?.[lang] ?? '';
  return (
    <Field label={label} aside={`${current.length}/${max}`}>
      <Input
        value={current}
        maxLength={max}
        lang={lang}
        placeholder={fallback}
        onChange={(e) => onChange({ ...value, [lang]: e.target.value })}
      />
    </Field>
  );
}

export default function InterfacePage(): JSX.Element {
  const st = useClubSettings();
  const s = st.draft;
  const [screen, setScreen] = useState<Screen>('lock');
  const [lang, setLang] = useState<Lang>('ru');

  if (!s) {
    return (
      <OwnerPage>
        <PageHeader title={t('Интерфейс и логотипы')} caption={t('Настройка клуба')} />
        {st.error && <Note tone="err">{st.error}</Note>}
      </OwnerPage>
    );
  }

  const branding = (patch: Partial<Branding>): void => st.set('branding', { ...s.branding, ...patch });
  const accentValid = HEX.test(s.branding.accent);
  const theme: ThemeId = s.branding.theme ?? 'default';
  const swatch = THEMES.find((x) => x.id === theme) ?? THEMES[0]!;
  const pcLabel: PcLabelSettings = { ...PC_LABEL_DEFAULT, ...s.branding.pcLabel };
  const setPcLabel = (patch: Partial<PcLabelSettings>): void => branding({ pcLabel: { ...pcLabel, ...patch } });

  const look: ScreenLookSettings = s.branding[screen] ?? {};
  const setLook = (patch: Partial<ScreenLookSettings>): void => branding({ [screen]: { ...look, ...patch } });
  const r = resolve(s.branding, screen);
  const setLogo = (patch: NonNullable<ScreenLookSettings['logo']>): void =>
    setLook({ logo: { ...look.logo, ...patch } });

  // Colour checks: the accent against the theme's background (3:1 for controls and focus), the tint against its text.
  const accentContrast = accentValid ? contrast(s.branding.accent, swatch.bg) : null;
  const tintValid = !s.branding.tint || HEX.test(s.branding.tint);
  const tintSafe = safeTint(s.branding.tint, swatch.text);
  const tintAdjusted = Boolean(s.branding.tint && tintSafe && tintSafe.toUpperCase() !== s.branding.tint.toUpperCase());
  const words = SHELL_TEXT[lang];

  const positionDisabled: Partial<Record<LogoPosition, string>> =
    screen === 'idle' && r.prices ? { bottomLeft: t('Там тарифы — выключите их, чтобы поставить логотип') } : {};

  return (
    <OwnerPage wide>
      <PageHeader title={t('Интерфейс и логотипы')} caption={t('Настройка клуба')} />
      {st.error && <Note tone="err">{st.error}</Note>}

      <div className="grid items-start gap-5 xl:grid-cols-[minmax(0,1fr)_minmax(0,1.05fr)]">
        <div className="flex min-w-0 flex-col gap-5">
          <Section title={t('Название и цвет')}>
            <div className="grid gap-x-5 gap-y-4 md:grid-cols-2">
              <Field label={t('Название клуба')}>
                <Input value={s.branding.clubName} onChange={(e) => branding({ clubName: e.target.value })} />
              </Field>
              <Field label={t('Акцентный цвет')} hint={t('Кнопки, выделение и подсветка на ПК')}>
                <div className="flex gap-2">
                  <input
                    type="color"
                    aria-label={t('Акцентный цвет')}
                    value={accentValid ? s.branding.accent : '#9ADFFF'}
                    onChange={(e) => branding({ accent: e.target.value.toUpperCase() })}
                    className="focus-ring h-11 w-12 shrink-0 cursor-pointer rounded-md border border-accent/[0.16] bg-bg/50 p-1"
                  />
                  <input
                    className={clsx(inputCls, 'font-mono uppercase', !accentValid && 'border-warning/60')}
                    value={s.branding.accent}
                    maxLength={7}
                    aria-label={t('Код цвета')}
                    onChange={(e) => branding({ accent: e.target.value })}
                  />
                </div>
              </Field>
              <Field
                label={t('Тон плашек')}
                hint={t('Подложка под названием, часами и тарифами на экране входа и заставке. Пусто — фон темы.')}
              >
                <div className="flex gap-2">
                  <input
                    type="color"
                    aria-label={t('Тон плашек')}
                    value={s.branding.tint && HEX.test(s.branding.tint) ? s.branding.tint : swatch.bg}
                    onChange={(e) => branding({ tint: e.target.value.toUpperCase() })}
                    className="focus-ring h-11 w-12 shrink-0 cursor-pointer rounded-md border border-accent/[0.16] bg-bg/50 p-1"
                  />
                  <input
                    className={clsx(inputCls, 'font-mono uppercase', !tintValid && 'border-warning/60')}
                    value={s.branding.tint ?? ''}
                    maxLength={7}
                    placeholder={t('Как в теме')}
                    aria-label={t('Код тона')}
                    onChange={(e) => branding({ tint: e.target.value.trim() ? e.target.value.trim() : null })}
                  />
                  {s.branding.tint && (
                    <Button size="sm" variant="ghost" className="h-11" onClick={() => branding({ tint: null })}>
                      {t('Сбросить')}
                    </Button>
                  )}
                </div>
              </Field>
              <div className="flex flex-col gap-2">
                <span className="label-sm">{t('Шрифт заголовков')}</span>
                <div role="radiogroup" aria-label={t('Шрифт заголовков')} className="grid grid-cols-3 gap-2">
                  {FONTS.map((f) => {
                    const on = (s.branding.font ?? 'unbounded') === f.id;
                    return (
                      <button
                        key={f.id}
                        type="button"
                        role="radio"
                        aria-checked={on}
                        aria-label={f.name}
                        onClick={() => branding({ font: f.id })}
                        className={clsx(
                          'focus-ring flex h-11 flex-col items-center justify-center rounded-md border px-2 transition-colors',
                          on
                            ? 'border-accent/70 bg-accent/[0.08] text-text'
                            : 'border-text/10 text-dim hover:border-text/25',
                        )}
                      >
                        <span className="truncate text-[15px] leading-none" style={{ fontFamily: f.stack }}>
                          {FONT_SAMPLE}
                        </span>
                        <span className="mt-1 truncate text-[10px] text-muted">{f.name}</span>
                      </button>
                    );
                  })}
                </div>
                <span className="text-[11.5px] leading-4 text-muted">
                  {t('Только встроенные шрифты: ПК не качают их из интернета.')}
                </span>
              </div>
            </div>
            <div className="mt-4 flex flex-col gap-2" aria-live="polite">
              {accentContrast !== null && accentContrast < 3 && (
                <Note tone="warn">
                  {t(
                    'Акцент почти сливается с фоном темы ({ratio}:1, нужно от 3:1): выделение и ссылки на ПК будут читаться плохо. Возьмите цвет {direction}.',
                    {
                      ratio: accentContrast.toFixed(1),
                      direction: swatch.id === 'light' ? t('темнее') : t('светлее'),
                    },
                  )}
                </Note>
              )}
              {!tintValid && <Note tone="warn">{t('Тон — цвет в виде #RRGGBB.')}</Note>}
              {tintAdjusted && tintSafe && (
                <Note tone="warn">
                  <span className="flex flex-wrap items-center gap-x-3 gap-y-2">
                    <span>
                      {t(
                        'На этом тоне текст темы не читается. ПК покажет его {how} — до {color}, как в предпросмотре.',
                        {
                          how: swatch.id === 'light' ? t('светлее') : t('темнее'),
                          color: tintSafe,
                        },
                      )}
                    </span>
                    <Button size="sm" variant="ghost" onClick={() => branding({ tint: tintSafe })}>
                      {t('Взять {color}', { color: tintSafe })}
                    </Button>
                  </span>
                </Note>
              )}
            </div>
          </Section>

          <Section title={t('Логотип и обои')}>
            <div className="grid gap-6 lg:grid-cols-2 xl:grid-cols-1">
              <PictureField
                label={t('Логотип')}
                hint={t(
                  'PNG с прозрачным фоном, квадратный, до 5 МБ. Виден на экране входа, заставке, верхней панели ПК, в кассе и на чеках.',
                )}
                value={s.branding.logoUrl}
                kind="logo"
                onChange={(url) => branding({ logoUrl: url })}
                preview={(src) => <img src={src} alt="" className="max-h-20 max-w-36 object-contain" />}
              />
              <PictureField
                label={t('Обои')}
                hint={t('Картинка 1920×1080, до 5 МБ. За экраном входа и заставкой на ПК и в кассе.')}
                value={s.branding.wallpaperUrl}
                kind="wallpaper"
                onChange={(url) => branding({ wallpaperUrl: url })}
                preview={(src) => <img src={src} alt="" className="h-full w-full object-cover" />}
              />
            </div>
          </Section>

          <Section title={t('Экраны игрока')} className="scroll-mt-4" bodyClassName="flex flex-col gap-5">
            <Tabs
              label={t('Экран')}
              tabs={SCREENS.map((x) => ({ id: x.id, label: t(x.name) }))}
              value={screen}
              onChange={setScreen}
            />

            <Group title={t('Логотип')}>
              <div className="grid gap-5 sm:grid-cols-[auto_minmax(0,1fr)]">
                <div className="flex flex-col gap-2">
                  {screen === 'desktop' ? (
                    <p className="max-w-[15rem] text-xs text-muted">
                      {t('В верхней панели логотип всегда слева: в центре разделы, справа часы и меню.')}
                    </p>
                  ) : (
                    <PositionPad
                      screen={screen}
                      value={r.position}
                      onChange={(position) => setLogo({ position })}
                      disabled={positionDisabled}
                      label={t('Где логотип')}
                      names={
                        Object.fromEntries(Object.entries(POSITION_NAMES).map(([k, v]) => [k, t(v)])) as Record<
                          LogoPosition,
                          string
                        >
                      }
                    />
                  )}
                  {screen !== 'desktop' && <span className="text-xs text-muted">{t(POSITION_NAMES[r.position])}</span>}
                </div>
                <div className="flex min-w-0 flex-col gap-4">
                  <Toggle checked={r.show} onChange={(v) => setLogo({ show: v })} label={t('Показывать логотип')} />
                  <Toggle
                    checked={r.name}
                    onChange={(v) => setLogo({ name: v })}
                    label={
                      screen === 'desktop' ? t('Показывать название в верхней панели') : t('Показывать название рядом')
                    }
                  />
                  <div className="flex flex-col gap-2">
                    <span className="label-sm">{t('Размер')}</span>
                    <Segmented
                      label={t('Размер логотипа')}
                      value={r.size}
                      options={SIZES[screen].map((id) => ({ id, label: SIZE_NAMES[id] }))}
                      onChange={(size) => setLogo({ size })}
                      size="md"
                      className="max-w-[16rem]"
                    />
                  </div>
                </div>
              </div>
            </Group>

            <Group title={t('Фон')}>
              <PictureField
                label={
                  screen === 'idle'
                    ? t('Своя картинка заставки')
                    : screen === 'lock'
                      ? t('Своя картинка экрана входа')
                      : t('Своя картинка рабочего стола')
                }
                hint={
                  screen === 'idle'
                    ? t('Без неё идут слайды заставки. Свои слайды из «Экран игрока» важнее картинки.')
                    : t('Без неё — общие обои клуба.')
                }
                value={look.wallpaperUrl ?? null}
                kind="wallpaper"
                testId={`picture-${screen}`}
                onChange={(url) => setLook({ wallpaperUrl: url })}
                preview={(src) => <img src={src} alt="" className="h-full w-full object-cover" />}
              />
              <div className="grid gap-5 sm:grid-cols-2">
                <Field
                  label={t('Затемнение')}
                  aside={r.dim === null ? t('как сейчас') : `${r.dim}%`}
                  hint={t('Сильнее — текст читается на любой картинке. Не больше 80%.')}
                >
                  <div className="flex items-center gap-3">
                    <input
                      type="range"
                      min={0}
                      max={MAX_DIM}
                      step={5}
                      value={r.dim ?? DEFAULT_DIM[screen]}
                      onChange={(e) => setLook({ dim: Number(e.target.value) })}
                      className="h-11 w-full accent-[rgb(var(--c-accent))]"
                    />
                    {r.dim !== null && (
                      <Button size="sm" variant="ghost" onClick={() => setLook({ dim: null })}>
                        {t('Сбросить')}
                      </Button>
                    )}
                  </div>
                </Field>
                <div className="flex flex-col gap-2">
                  <span className="label-sm">{t('Размытие')}</span>
                  <Segmented
                    label={t('Размытие')}
                    value={r.blur === null ? 'theme' : r.blur ? 'on' : 'off'}
                    options={[
                      { id: 'theme', label: t('Как в теме') },
                      { id: 'on', label: t('Размыть') },
                      { id: 'off', label: t('Чётко') },
                    ]}
                    onChange={(v) => setLook({ blur: v === 'theme' ? null : v === 'on' })}
                    size="md"
                  />
                </div>
              </div>
            </Group>

            {screen !== 'desktop' && (
              <Group title={t('Что показывать')}>
                <div className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
                  <Toggle checked={r.clock} onChange={(v) => setLook({ clock: v })} label={t('Часы')} />
                  {screen === 'lock' && (
                    <>
                      <Toggle
                        checked={r.language}
                        onChange={(v) => setLook({ language: v })}
                        label={t('Выбор языка')}
                      />
                      <Toggle
                        checked={s.features.callAdmin !== false}
                        onChange={(v) => st.set('features', { ...s.features, callAdmin: v })}
                        label={t('«Позвать администратора»')}
                      />
                      <Toggle
                        checked={s.features.lockScreenPower !== false}
                        onChange={(v) => st.set('features', { ...s.features, lockScreenPower: v })}
                        label={t('«Выключить» и «Перезагрузить»')}
                      />
                    </>
                  )}
                  {screen === 'idle' && (
                    <>
                      <Toggle checked={r.prices} onChange={(v) => setLook({ prices: v })} label={t('Тарифы')} />
                      <Toggle
                        checked={r.freeBadge}
                        onChange={(v) => setLook({ freeBadge: v })}
                        label={t('«Это место свободно»')}
                      />
                    </>
                  )}
                </div>
                {screen === 'lock' && (
                  <p className="text-xs text-muted">
                    {t('Кнопки администратора и питания — те же переключатели, что в «Экран игрока».')}
                  </p>
                )}
              </Group>
            )}

            {screen !== 'desktop' && (
              <Group
                title={t('Тексты')}
                aside={
                  <Segmented
                    label={t('Язык текста')}
                    value={lang}
                    options={LANGS.map((l) => ({ id: l.id, label: l.name }))}
                    onChange={setLang}
                    size="md"
                    full={false}
                  />
                }
              >
                <p className="text-xs text-muted">
                  {t('Пустое поле — встроенный текст на языке игрока (он в подсказке поля).')}
                </p>
                {screen === 'lock' ? (
                  <div className="grid gap-4">
                    <TextField
                      label={t('Заголовок')}
                      value={look.title}
                      lang={lang}
                      fallback={words.title!}
                      max={TEXT_MAX.title}
                      onChange={(title) => setLook({ title })}
                    />
                    <TextField
                      label={t('Подзаголовок')}
                      value={look.subtitle}
                      lang={lang}
                      fallback={words.subtitle!}
                      max={TEXT_MAX.subtitle}
                      onChange={(subtitle) => setLook({ subtitle })}
                    />
                  </div>
                ) : (
                  <TextField
                    label={t('Призыв')}
                    value={look.cta}
                    lang={lang}
                    fallback={words.cta!}
                    max={TEXT_MAX.cta}
                    onChange={(cta) => setLook({ cta })}
                  />
                )}
              </Group>
            )}
          </Section>

          <Section title={t('Тема на ПК')}>
            <p className="mb-4 text-sm text-muted">
              {t('С этой темой ПК встречают игрока. Игрок может выбрать свою — она сохранится в его аккаунте.')}
            </p>
            <div role="radiogroup" aria-label={t('Тема на ПК')} className="grid gap-3 sm:grid-cols-3">
              {THEMES.map((th) => (
                <button
                  key={th.id}
                  type="button"
                  role="radio"
                  aria-checked={theme === th.id}
                  onClick={() => branding({ theme: th.id })}
                  className={clsx(
                    'focus-ring flex flex-col gap-3 rounded-md border p-3 text-left transition-colors',
                    theme === th.id ? 'border-accent/70 bg-accent/[0.08]' : 'border-text/10 hover:border-text/25',
                  )}
                >
                  <span
                    className="flex h-16 overflow-hidden rounded-sm"
                    style={{ background: th.bg }}
                    aria-hidden="true"
                  >
                    <span className="m-2 flex flex-1 flex-col gap-1.5 rounded-sm p-2" style={{ background: th.panel }}>
                      <span className="h-1.5 w-2/3 rounded-full" style={{ background: th.text }} />
                      <span className="h-1.5 w-1/3 rounded-full opacity-60" style={{ background: th.text }} />
                      <span className="mt-auto h-2 w-1/2 rounded-full" style={{ background: th.accent }} />
                    </span>
                  </span>
                  <span className="flex flex-col">
                    <span className="text-sm font-semibold text-text">{th.name}</span>
                    <span className="text-xs text-muted">{t(th.hint)}</span>
                  </span>
                </button>
              ))}
            </div>
          </Section>

          <Section title={t('Номер ПК на экране')}>
            <p className="mb-4 text-sm text-muted">
              {t(
                'Как ПК подписан для игрока: на экране входа, заставке и верхней панели. Игрок называет его кассиру и видит, куда принесут заказ.',
              )}
            </p>
            <div role="radiogroup" aria-label={t('Что написано')} className="grid gap-3 sm:grid-cols-3">
              {PC_FORMATS.map((f) => (
                <button
                  key={f.id}
                  type="button"
                  role="radio"
                  aria-checked={pcLabel.format === f.id}
                  onClick={() => setPcLabel({ format: f.id })}
                  className={clsx(
                    'focus-ring flex flex-col gap-1 rounded-md border p-3 text-left transition-colors',
                    pcLabel.format === f.id
                      ? 'border-accent/70 bg-accent/[0.08]'
                      : 'border-text/10 hover:border-text/25',
                  )}
                >
                  <span className="font-mono text-sm text-text">{f.id === 'number' ? t('ПК 05') : f.sample}</span>
                  <span className="text-xs text-muted">{t(f.name)}</span>
                </button>
              ))}
            </div>
            <div className="mt-5 flex flex-col gap-3">
              <Toggle
                checked={pcLabel.large}
                onChange={(v) => setPcLabel({ large: v })}
                label={t('Крупный номер на экране входа и заставке — видно через зал')}
              />
              <span className="label-sm mt-2">{t('Где показывать')}</span>
              <div className="flex flex-wrap gap-x-6 gap-y-3">
                <Toggle checked={pcLabel.lock} onChange={(v) => setPcLabel({ lock: v })} label={t('Экран входа')} />
                <Toggle checked={pcLabel.idle} onChange={(v) => setPcLabel({ idle: v })} label={t('Заставка')} />
                <Toggle
                  checked={pcLabel.topBar}
                  onChange={(v) => setPcLabel({ topBar: v })}
                  label={t('Верхняя панель')}
                />
              </div>
            </div>
            <p className="mt-4 text-xs text-muted">
              {t('Название и номер каждого ПК меняются в разделе')}{' '}
              <a href="#/hall" className="focus-ring rounded-sm font-medium text-accent hover:underline">
                {t('Зал и устройства')}
              </a>
              .
            </p>
          </Section>
        </div>

        <Section
          variant="side"
          title={t('Так это выглядит')}
          className="xl:sticky xl:top-0"
          actions={
            <Segmented
              label={t('Язык предпросмотра')}
              value={lang}
              options={LANGS.map((l) => ({ id: l.id, label: l.name }))}
              onChange={setLang}
              size="md"
              full={false}
            />
          }
        >
          <Segmented
            label={t('Экран в предпросмотре')}
            value={screen}
            options={SCREENS.map((x) => ({ id: x.id, label: t(x.name) }))}
            onChange={setScreen}
            className="mb-4"
          />
          <ScreenPreview
            screen={screen}
            branding={s.branding}
            features={s.features}
            pcLabel={pcLabel}
            lang={lang}
            label={t('Предпросмотр: {screen}', { screen: t(SCREENS.find((x) => x.id === screen)!.name) })}
          />
          <p className="mt-3 text-xs text-muted">
            {screen === 'idle' && !r.wallpaper
              ? t('Без своей картинки здесь идут слайды заставки. ПК получают изменения сразу после сохранения.')
              : t('ПК получают изменения сразу после сохранения.')}
          </p>
        </Section>
      </div>

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
