/**
 * «Интерфейс и логотипы» (D-93): how the club looks everywhere — its name, logo, wallpaper and colour on the PCs' lock,
 * idle and top bar, on the console's rail and on every printed slip, and the theme the PCs start in. Pictures are
 * uploaded here (kept on the club's server) or given as a link. Saved with the shared save bar, like «Экран игрока».
 */
import { useRef, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import { mediaUrl, uploadMedia, type ClubSettings, type PcLabelSettings } from '@/api';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { useClubSettings } from '@/settings';
import { Button, Field, Input, Note, PageHeader, SaveBar, Section, Toggle, inputCls } from '@/ui';
import { OwnerPage } from './ownerKit';

const HEX = /^#[0-9a-fA-F]{6}$/;

type Branding = ClubSettings['branding'];
type ThemeId = NonNullable<Branding['theme']>;

/** The bundled shell themes as swatches: background, panel, text and the accent the theme brings. */
const THEMES: { id: ThemeId; name: string; hint: string; bg: string; panel: string; text: string; accent: string }[] = [
  {
    id: 'default',
    name: 'Obsidian',
    hint: 'Тёмная, по умолчанию',
    bg: '#07090C',
    panel: '#0D1117',
    text: '#E8F1F6',
    accent: '#9ADFFF',
  },
  {
    id: 'light',
    name: 'Daylight',
    hint: 'Светлая, для светлого зала',
    bg: '#EEF2F6',
    panel: '#FFFFFF',
    text: '#0F172A',
    accent: '#0369A1',
  },
  {
    id: 'neon',
    name: 'Neon Night',
    hint: 'Тёмная, неоновая',
    bg: '#07060F',
    panel: '#120F24',
    text: '#FAF5FF',
    accent: '#F0ABFC',
  },
];

const PC_LABEL_DEFAULT: PcLabelSettings = { format: 'nameZone', large: false, lock: true, idle: true, topBar: true };

/** What the PC's label can say, with how it reads for PC-05 in the VIP zone. */
const PC_FORMATS: { id: PcLabelSettings['format']; name: string; sample: string }[] = [
  { id: 'nameZone', name: 'Название и зона', sample: 'PC-05 · VIP' },
  { id: 'name', name: 'Только название', sample: 'PC-05' },
  { id: 'number', name: 'Только номер', sample: 'ПК 05' },
];

const PICK_TYPES = 'image/png,image/jpeg,image/webp,image/gif';

/** One picture of the club: the preview, «Загрузить файл», «Убрать» and the link it is stored as. */
function PictureField({
  label,
  hint,
  value,
  kind,
  onChange,
  preview,
}: {
  label: string;
  hint: string;
  value: string | null;
  kind: 'logo' | 'wallpaper';
  onChange: (url: string | null) => void;
  preview: (src: string) => ReactNode;
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
    <div className="flex min-w-0 flex-col gap-3" data-testid={`picture-${kind}`}>
      <span className="label-sm">{label}</span>
      <div className="flex flex-wrap items-center gap-4">
        <div className="flex h-24 w-40 shrink-0 items-center justify-center overflow-hidden rounded-md border border-text/10 bg-bg/60">
          {shown ? preview(shown) : <span className="text-xs text-muted">{t('Нет')}</span>}
        </div>
        <div className="flex flex-col gap-2">
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

export default function InterfacePage(): JSX.Element {
  const st = useClubSettings();
  const s = st.draft;

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
  const logo = mediaUrl(s.branding.logoUrl);
  const pcLabel: PcLabelSettings = { ...PC_LABEL_DEFAULT, ...s.branding.pcLabel };
  const setPcLabel = (patch: Partial<PcLabelSettings>): void => branding({ pcLabel: { ...pcLabel, ...patch } });
  const pcSample = PC_FORMATS.find((f) => f.id === pcLabel.format)?.sample ?? 'PC-05 · VIP';

  return (
    <OwnerPage>
      <PageHeader title={t('Интерфейс и логотипы')} caption={t('Настройка клуба')} />
      {st.error && <Note tone="err">{st.error}</Note>}

      <div className="grid items-start gap-5 xl:grid-cols-[minmax(0,1fr)_380px]">
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
            </div>
          </Section>

          <Section title={t('Логотип и обои')}>
            <div className="grid gap-6 lg:grid-cols-2">
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

        <Section variant="side" title={t('Так это выглядит')} className="xl:sticky xl:top-0">
          <div
            className="relative flex aspect-video flex-col justify-between overflow-hidden rounded-md p-4"
            style={{
              background: mediaUrl(s.branding.wallpaperUrl)
                ? `center / cover no-repeat url("${mediaUrl(s.branding.wallpaperUrl)}")`
                : (THEMES.find((x) => x.id === theme)?.bg ?? '#07090C'),
            }}
          >
            <div className="flex flex-col gap-2">
              <div className="flex items-center gap-2 rounded-sm bg-black/45 px-2 py-1.5 backdrop-blur-sm">
                {logo ? (
                  <img src={logo} alt="" className="h-6 w-6 object-contain" />
                ) : (
                  <span
                    className="h-6 w-6 rounded-sm"
                    style={{ background: accentValid ? s.branding.accent : '#9ADFFF' }}
                  />
                )}
                <span className="min-w-0 truncate text-sm font-semibold text-white">
                  {s.branding.clubName || t('Название клуба')}
                </span>
              </div>
              {pcLabel.lock &&
                (pcLabel.large ? (
                  <div className="flex items-end gap-2 self-start rounded-sm bg-black/45 px-2 py-1 backdrop-blur-sm">
                    <span className="font-display text-3xl font-light leading-none text-white">05</span>
                    <span className="pb-0.5 text-[10px] uppercase tracking-wider text-white/80">
                      {pcLabel.format === 'number' ? t('ПК') : pcSample}
                    </span>
                  </div>
                ) : (
                  <span className="self-start rounded-sm bg-black/45 px-2 py-0.5 text-[10px] uppercase tracking-wider text-white/85 backdrop-blur-sm">
                    {pcLabel.format === 'number' ? t('ПК 05') : pcSample}
                  </span>
                ))}
            </div>
            <span
              className="self-start rounded-sm px-3 py-1.5 text-xs font-semibold text-black"
              style={{ background: accentValid ? s.branding.accent : '#9ADFFF' }}
            >
              {t('Войти')}
            </span>
          </div>
          <p className="mt-3 text-xs text-muted">{t('ПК получают изменения сразу после сохранения.')}</p>
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
