/**
 * Pricing (owner): tariffs, weekday / holiday percentages, client groups, top-up bonuses and promo codes, happy hours
 * and loyalty levels. Tariffs are saved one by one through `/admin/tariffs`; everything else is part of the club
 * settings document and is saved with the shared save bar.
 */
import { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';
import type { Tariff, TariffTimeWindow } from '@clubshell/contracts';
import {
  clubApi,
  type BonusTier,
  type ClientGroup,
  type ClubSettings,
  type HappyHour,
  type LoyaltyLevel,
  type PromoCode,
  type TariffInput,
} from '@/api';
import { describe } from '@/errors';
import { money } from '@/format';
import { dateLocale, t } from '@/i18n';
import { CloseIcon } from '@/icons';
import { useClubSettings, type ClubSettingsState } from '@/settings';
import {
  Button,
  Chip,
  Field,
  Input,
  MoneyInput,
  Note,
  NumberInput,
  PageHeader,
  SaveBar,
  Section,
  Segmented,
  Table,
  Tabs,
  inputCls,
} from '@/ui';
import { FieldGroup, OwnerPage } from './ownerKit';

type Tab = 'tariffs' | 'days' | 'groups' | 'bonus' | 'happy' | 'loyalty';

const TABS: { id: Tab; title: string }[] = [
  { id: 'tariffs', title: 'Тарифы' },
  { id: 'days', title: 'Цены по дням' },
  { id: 'groups', title: 'Группы клиентов' },
  { id: 'bonus', title: 'Бонусы и промокоды' },
  { id: 'happy', title: 'Счастливые часы' },
  { id: 'loyalty', title: 'Лояльность' },
];

/** Monday-first week for the UI; `idx` is `Date.getDay()` (0 = Sunday), `key` the tariff window wire value. */
const DAYS: { label: string; key: TariffTimeWindow['days'][number]; idx: number }[] = [
  { label: 'Пн', key: 'mon', idx: 1 },
  { label: 'Вт', key: 'tue', idx: 2 },
  { label: 'Ср', key: 'wed', idx: 3 },
  { label: 'Чт', key: 'thu', idx: 4 },
  { label: 'Пт', key: 'fri', idx: 5 },
  { label: 'Сб', key: 'sat', idx: 6 },
  { label: 'Вс', key: 'sun', idx: 0 },
];

type NoteState = { text: string; tone: 'ok' | 'err' } | null;

/** A native colour picker as a 44 px swatch well. */
const COLOR_INPUT =
  'focus-ring h-11 w-12 shrink-0 cursor-pointer rounded-md border border-accent/[0.16] bg-bg/50 p-1 hover:border-accent/[0.26]';

const uid = (): string => Math.random().toString(36).slice(2, 10);

/** A toggle of a set (a weekday, a zone): an outlined chip, `aria-pressed`. `fill` stretches it over its grid cell. */
function Pick({
  on,
  onClick,
  children,
  fill,
}: {
  on: boolean;
  onClick: () => void;
  children: React.ReactNode;
  fill?: boolean;
}): JSX.Element {
  return (
    <Chip tone="outlined" pressed={on} onClick={onClick} className={clsx(fill && 'justify-center !px-0')}>
      {children}
    </Chip>
  );
}

/** The seven weekday toggles of a time window or a happy hour, Monday first. */
function WeekdayPicks({
  on,
  onToggle,
}: {
  on: (d: (typeof DAYS)[number]) => boolean;
  onToggle: (d: (typeof DAYS)[number]) => void;
}): JSX.Element {
  return (
    <div className="grid grid-cols-7 gap-1">
      {DAYS.map((d) => (
        <Pick key={d.key} fill on={on(d)} onClick={() => onToggle(d)}>
          {t(d.label)}
        </Pick>
      ))}
    </div>
  );
}

function RemoveButton({ onClick }: { onClick: () => void }): JSX.Element {
  return (
    <Button variant="ghost" size="sm" onClick={onClick}>
      {t('Удалить')}
    </Button>
  );
}

function toggle<T>(list: T[], v: T): T[] {
  return list.includes(v) ? list.filter((x) => x !== v) : [...list, v];
}

// ---------------------------------------------------------------------------------------------------------------------
// Tariffs
// ---------------------------------------------------------------------------------------------------------------------

interface TariffForm {
  name: string;
  isPackage: boolean;
  pricePerHour: number;
  packageMinutes: number;
  packagePrice: number;
  minMinutes: number;
  maxMinutes: number;
  zones: string[];
  timeWindows: TariffTimeWindow[];
}

const EMPTY_TARIFF: TariffForm = {
  name: '',
  isPackage: false,
  pricePerHour: 0,
  packageMinutes: 180,
  packagePrice: 0,
  minMinutes: 30,
  maxMinutes: 0,
  zones: [],
  timeWindows: [],
};

function formOf(x: Tariff): TariffForm {
  return {
    name: x.name,
    isPackage: x.isPackage,
    pricePerHour: x.pricePerHour.amount,
    packageMinutes: x.packageMinutes ?? 180,
    packagePrice: x.packagePrice?.amount ?? 0,
    minMinutes: x.minMinutes,
    maxMinutes: x.maxMinutes ?? 0,
    zones: [...x.zones],
    timeWindows: x.timeWindows.map((w) => ({ ...w, days: [...w.days] })),
  };
}

function inputOf(f: TariffForm): TariffInput {
  return {
    name: f.name.trim(),
    isPackage: f.isPackage,
    pricePerHour: f.pricePerHour,
    packageMinutes: f.isPackage ? f.packageMinutes : null,
    packagePrice: f.isPackage ? f.packagePrice : null,
    minMinutes: f.minMinutes,
    maxMinutes: f.maxMinutes > 0 ? f.maxMinutes : null,
    zones: f.zones,
    timeWindows: f.timeWindows,
  };
}

function windowLabel(w: TariffTimeWindow): string {
  const days = DAYS.filter((d) => w.days.includes(d.key)).map((d) => t(d.label));
  return `${days.length === 7 || days.length === 0 ? t('Ежедневно') : days.join(' ')} ${w.from}–${w.to}`;
}

/** `pending` — the settings' save bar is on screen (its primary is the page's one then). */
function TariffsTab({ zones, pending }: { zones: string[]; pending: boolean }): JSX.Element {
  const [items, setItems] = useState<Tariff[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<string | 'new' | null>(null);
  const [form, setForm] = useState<TariffForm>(EMPTY_TARIFF);
  const [busy, setBusy] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [note, setNote] = useState<NoteState>(null);

  const load = useCallback(async () => {
    try {
      setItems((await clubApi.tariffs()).items);
      setError(null);
    } catch (e) {
      setError(describe(e));
    }
  }, []);
  useEffect(() => {
    void load();
  }, [load]);

  const open = (x: Tariff | null): void => {
    setEditing(x ? x.id : 'new');
    setForm(x ? formOf(x) : EMPTY_TARIFF);
    setConfirmDelete(false);
    setNote(null);
  };

  const patch = (p: Partial<TariffForm>): void => setForm((f) => ({ ...f, ...p }));
  const patchWindow = (i: number, p: Partial<TariffTimeWindow>): void =>
    setForm((f) => ({ ...f, timeWindows: f.timeWindows.map((w, j) => (j === i ? { ...w, ...p } : w)) }));

  const save = async (): Promise<void> => {
    setBusy(true);
    setNote(null);
    try {
      const input = inputOf(form);
      const r = editing === 'new' ? await clubApi.addTariff(input) : await clubApi.saveTariff(editing ?? '', input);
      await load();
      setEditing(r.tariff.id);
      setNote({ text: t('Тариф сохранён'), tone: 'ok' });
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  const remove = async (): Promise<void> => {
    if (!editing || editing === 'new') return;
    setBusy(true);
    try {
      await clubApi.deleteTariff(editing);
      await load();
      setEditing(null);
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
      setConfirmDelete(false);
    }
  };

  const valid =
    form.name.trim().length > 0 &&
    form.minMinutes >= 5 &&
    (form.isPackage ? form.packageMinutes >= 5 && form.packagePrice > 0 : form.pricePerHour > 0);

  return (
    <div className={clsx('grid grid-cols-1 items-start gap-5', editing && 'lg:grid-cols-[minmax(0,1fr)_400px]')}>
      <Section
        title={t('Тарифы')}
        actions={
          // The page's create action is the primary, unless the editor's «Сохранить» or the save bar's is on screen.
          <Button size="sm" variant={pending || editing ? 'secondary' : 'primary'} onClick={() => open(null)}>
            {t('Новый тариф')}
          </Button>
        }
        bodyClassName="p-2"
      >
        {error && <Note note={{ text: error, tone: 'err' }} />}
        <Table
          rows={items}
          rowKey={(x) => x.id}
          selectedKey={editing}
          onRowClick={open}
          empty={t('Тарифов нет')}
          columns={[
            { key: 'name', title: t('Название'), render: (x) => <span className="font-medium text-hi">{x.name}</span> },
            {
              key: 'kind',
              title: t('Тип'),
              render: (x) => <span className="text-dim">{x.isPackage ? t('Пакет') : t('Почасовой')}</span>,
            },
            {
              key: 'price',
              title: t('Цена'),
              num: true,
              render: (x) =>
                x.isPackage
                  ? `${money(x.packagePrice)} / ${x.packageMinutes ?? 0} ${t('мин')}`
                  : `${money(x.pricePerHour)} / ${t('ч')}`,
            },
            {
              key: 'zones',
              title: t('Зоны'),
              render: (x) => (x.zones.length ? x.zones.join(', ') : <span className="text-muted">{t('Все')}</span>),
            },
            {
              key: 'windows',
              title: t('Окна времени'),
              render: (x) =>
                x.timeWindows.length ? (
                  <span className="tnum text-dim">{x.timeWindows.map(windowLabel).join('; ')}</span>
                ) : (
                  <span className="text-muted">{t('Всегда')}</span>
                ),
            },
            {
              key: 'minmax',
              title: t('Мин / макс, мин'),
              num: true,
              render: (x) => `${x.minMinutes} / ${x.maxMinutes ?? '∞'}`,
            },
          ]}
        />
      </Section>

      {editing && (
        <Section variant="solid" title={editing === 'new' ? t('Новый тариф') : t('Тариф')}>
          <Field label={t('Название')}>
            <Input value={form.name} onChange={(e) => patch({ name: e.target.value })} />
          </Field>
          <FieldGroup label={t('Тип')}>
            <Segmented
              value={form.isPackage ? 'package' : 'hourly'}
              onChange={(v) => patch({ isPackage: v === 'package' })}
              options={[
                { id: 'hourly', label: t('Почасовой') },
                { id: 'package', label: t('Пакет') },
              ]}
            />
          </FieldGroup>
          {form.isPackage ? (
            <div className="grid grid-cols-2 gap-4">
              <Field label={t('Минут в пакете')}>
                <NumberInput
                  value={form.packageMinutes}
                  min={5}
                  onChange={(n) => patch({ packageMinutes: n })}
                  suffix={t('мин')}
                />
              </Field>
              <Field label={t('Цена пакета')}>
                <MoneyInput compact value={form.packagePrice} onChange={(n) => patch({ packagePrice: n })} />
              </Field>
            </div>
          ) : (
            <Field label={t('Цена за час')}>
              <MoneyInput value={form.pricePerHour} onChange={(n) => patch({ pricePerHour: n })} />
            </Field>
          )}
          <div className="grid grid-cols-2 gap-4">
            <Field label={t('Минимум')}>
              <NumberInput
                value={form.minMinutes}
                min={5}
                onChange={(n) => patch({ minMinutes: n })}
                suffix={t('мин')}
              />
            </Field>
            <Field label={t('Максимум')} hint={t('0 — без ограничения')}>
              <NumberInput
                value={form.maxMinutes}
                min={0}
                onChange={(n) => patch({ maxMinutes: n })}
                suffix={t('мин')}
              />
            </Field>
          </div>
          <FieldGroup label={t('Зоны')} hint={t('Ничего не выбрано — все зоны')}>
            <div className="flex flex-wrap gap-1.5">
              {zones.map((z) => (
                <Pick key={z} on={form.zones.includes(z)} onClick={() => patch({ zones: toggle(form.zones, z) })}>
                  {z}
                </Pick>
              ))}
            </div>
          </FieldGroup>
          <FieldGroup
            label={t('Окна времени')}
            aside={
              <Button
                size="xs"
                variant="ghost"
                onClick={() =>
                  patch({
                    timeWindows: [...form.timeWindows, { days: DAYS.map((d) => d.key), from: '10:00', to: '18:00' }],
                  })
                }
              >
                {t('Добавить')}
              </Button>
            }
          >
            {form.timeWindows.length === 0 && <p className="text-[13px] text-muted">{t('Всегда')}</p>}
            {form.timeWindows.map((w, i) => (
              <div key={i} className="well flex flex-col gap-2.5 p-3">
                <WeekdayPicks
                  on={(d) => w.days.includes(d.key)}
                  onToggle={(d) => patchWindow(i, { days: toggle(w.days, d.key) })}
                />
                <div className="grid grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)_auto] items-center gap-2">
                  <input
                    type="time"
                    className={clsx(inputCls, 'tnum')}
                    value={w.from}
                    onChange={(e) => patchWindow(i, { from: e.target.value })}
                  />
                  <span className="text-muted">–</span>
                  <input
                    type="time"
                    className={clsx(inputCls, 'tnum')}
                    value={w.to}
                    onChange={(e) => patchWindow(i, { to: e.target.value })}
                  />
                  <Button
                    variant="ghost"
                    className="w-11 !px-0"
                    aria-label={t('Удалить')}
                    title={t('Удалить')}
                    onClick={() => patch({ timeWindows: form.timeWindows.filter((_, j) => j !== i) })}
                  >
                    <CloseIcon size={16} />
                  </Button>
                </div>
              </div>
            ))}
          </FieldGroup>

          <Note note={note} />

          <div className="flex items-center justify-between gap-2 border-t border-accent/[0.08] pt-4">
            {editing !== 'new' &&
              (confirmDelete ? (
                <span className="flex items-center gap-1">
                  <span className="text-sm text-muted">{t('Удалить?')}</span>
                  <Button variant="danger" size="sm" disabled={busy} onClick={() => void remove()}>
                    {t('Да')}
                  </Button>
                  <Button variant="ghost" size="sm" onClick={() => setConfirmDelete(false)}>
                    {t('Нет')}
                  </Button>
                </span>
              ) : (
                <Button variant="tertiary" onClick={() => setConfirmDelete(true)}>
                  {t('Удалить')}
                </Button>
              ))}
            <span className="ml-auto flex gap-2">
              <Button variant="ghost" onClick={() => setEditing(null)}>
                {t('Закрыть')}
              </Button>
              <Button variant="primary" disabled={busy || !valid} onClick={() => void save()}>
                {busy ? t('Сохраняем…') : t('Сохранить')}
              </Button>
            </span>
          </div>
        </Section>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Settings-document tabs
// ---------------------------------------------------------------------------------------------------------------------

function DaysTab({
  s,
  tariffs,
  set,
}: {
  s: ClubSettings;
  tariffs: Tariff[];
  set: ClubSettingsState['set'];
}): JSX.Element {
  const [date, setDate] = useState('');
  const p = s.pricing;
  const setPct = (idx: number, v: number): void => {
    const weekdayPct = [...p.weekdayPct];
    weekdayPct[idx] = v;
    set('pricing', { ...p, weekdayPct });
  };
  const sample = tariffs.find((x) => !x.isPackage) ?? tariffs[0];
  const samplePrice = sample
    ? Math.round(
        ((sample.isPackage ? (sample.packagePrice?.amount ?? 0) : sample.pricePerHour.amount) *
          (p.weekdayPct[5] ?? 100)) /
          100 /
          100,
      ) * 100
    : 0;
  const holidays = [...p.holidays].sort();

  return (
    <div className="grid grid-cols-1 items-start gap-5 xl:grid-cols-[minmax(0,1fr)_400px]">
      <Section title={t('Цены по дням недели')}>
        <div className="grid grid-cols-7 gap-2">
          {DAYS.map((d) => (
            <Field key={d.key} label={t(d.label)}>
              <NumberInput
                value={p.weekdayPct[d.idx] ?? 100}
                min={0}
                max={500}
                suffix="%"
                onChange={(n) => setPct(d.idx, n)}
              />
            </Field>
          ))}
        </div>
        {sample && (
          <p className="tnum border-t border-accent/[0.08] pt-4 text-[13px] text-dim">
            {sample.isPackage
              ? t('{name} в пятницу', { name: sample.name })
              : t('{name} 1 ч в пятницу', { name: sample.name })}
            {' = '}
            <span className="font-mono text-[13px] font-semibold text-hi">
              {money({ amount: samplePrice, currency: 'UZS' })}
            </span>
          </p>
        )}
      </Section>

      <Section title={t('Праздники')}>
        <Field label={t('Цена в праздник')}>
          <NumberInput
            value={p.holidayPct}
            min={0}
            max={500}
            suffix="%"
            onChange={(n) => set('pricing', { ...p, holidayPct: n })}
          />
        </Field>
        <div className="flex gap-2">
          <input
            type="date"
            className={clsx(inputCls, 'tnum')}
            value={date}
            onChange={(e) => setDate(e.target.value)}
          />
          <Button
            disabled={!date || p.holidays.includes(date)}
            onClick={() => {
              set('pricing', { ...p, holidays: [...p.holidays, date] });
              setDate('');
            }}
          >
            {t('Добавить')}
          </Button>
        </div>
        {holidays.length === 0 ? (
          <p className="text-[13px] text-muted">{t('Праздников нет')}</p>
        ) : (
          <ul className="flex flex-col divide-y divide-accent/[0.07] border-y border-accent/[0.07]">
            {holidays.map((h) => (
              <li key={h} className="flex min-h-11 items-center justify-between py-1">
                <span className="tnum text-[13px]">
                  {new Date(`${h}T00:00:00`).toLocaleDateString(dateLocale(), {
                    day: 'numeric',
                    month: 'long',
                    year: 'numeric',
                  })}
                </span>
                <RemoveButton onClick={() => set('pricing', { ...p, holidays: p.holidays.filter((x) => x !== h) })} />
              </li>
            ))}
          </ul>
        )}
      </Section>
    </div>
  );
}

function GroupsTab({ s, set }: { s: ClubSettings; set: ClubSettingsState['set'] }): JSX.Element {
  const edit = (id: string, p: Partial<ClientGroup>): void =>
    set(
      'groups',
      s.groups.map((g) => (g.id === id ? { ...g, ...p } : g)),
    );
  return (
    <Section
      title={t('Группы клиентов')}
      className="max-w-3xl"
      bodyClassName="p-2"
      actions={
        <Button
          size="sm"
          onClick={() =>
            set('groups', [...s.groups, { id: uid(), name: t('Новая группа'), discountPct: 0, color: '#9ADFFF' }])
          }
        >
          {t('Добавить группу')}
        </Button>
      }
    >
      <Table
        rows={s.groups}
        rowKey={(g) => g.id}
        empty={t('Групп нет')}
        columns={[
          {
            key: 'color',
            title: t('Цвет'),
            width: '5rem',
            render: (g) => (
              <input
                type="color"
                aria-label={t('Цвет')}
                value={g.color}
                onChange={(e) => edit(g.id, { color: e.target.value })}
                className={COLOR_INPUT}
              />
            ),
          },
          {
            key: 'name',
            title: t('Название'),
            render: (g) => <Input value={g.name} onChange={(e) => edit(g.id, { name: e.target.value })} />,
          },
          {
            key: 'discount',
            title: t('Скидка'),
            width: '9rem',
            render: (g) => (
              <NumberInput
                value={g.discountPct}
                min={0}
                max={100}
                suffix="%"
                onChange={(n) => edit(g.id, { discountPct: n })}
              />
            ),
          },
          {
            key: 'rm',
            title: '',
            width: '6rem',
            render: (g) => (
              <RemoveButton
                onClick={() =>
                  set(
                    'groups',
                    s.groups.filter((x) => x.id !== g.id),
                  )
                }
              />
            ),
          },
        ]}
      />
    </Section>
  );
}

function BonusTab({ s, set }: { s: ClubSettings; set: ClubSettingsState['set'] }): JSX.Element {
  const tiers = s.bonusTiers.map((tier, i) => ({ tier, i }));
  const editTier = (i: number, p: Partial<BonusTier>): void =>
    set(
      'bonusTiers',
      s.bonusTiers.map((x, j) => (j === i ? { ...x, ...p } : x)),
    );
  const promos = s.promoCodes.map((promo, i) => ({ promo, i }));
  const editPromo = (i: number, p: Partial<PromoCode>): void =>
    set(
      'promoCodes',
      s.promoCodes.map((x, j) => (j === i ? { ...x, ...p } : x)),
    );

  return (
    <div className="grid grid-cols-1 items-start gap-5 2xl:grid-cols-[28rem_minmax(0,1fr)]">
      <Section
        title={t('Бонус за пополнение')}
        // Two inputs a row: alone on the page it stays a form's width instead of stretching the sum over 1000 px.
        className="max-w-2xl 2xl:max-w-none"
        bodyClassName="p-2"
        actions={
          <Button
            size="sm"
            onClick={() => {
              const last = s.bonusTiers[s.bonusTiers.length - 1];
              set('bonusTiers', [
                ...s.bonusTiers,
                { minAmount: (last?.minAmount ?? 0) + 5_000_000, bonusPct: (last?.bonusPct ?? 0) + 5 },
              ]);
            }}
          >
            {t('Добавить')}
          </Button>
        }
      >
        <Table
          rows={tiers}
          rowKey={(r) => String(r.i)}
          empty={t('Бонусов нет')}
          columns={[
            {
              key: 'min',
              title: t('От суммы'),
              render: (r) => (
                <MoneyInput compact value={r.tier.minAmount} onChange={(n) => editTier(r.i, { minAmount: n })} />
              ),
            },
            {
              key: 'pct',
              title: t('Бонус'),
              width: '8rem',
              render: (r) => (
                <NumberInput
                  value={r.tier.bonusPct}
                  min={0}
                  max={100}
                  suffix="%"
                  onChange={(n) => editTier(r.i, { bonusPct: n })}
                />
              ),
            },
            {
              key: 'rm',
              title: '',
              width: '6rem',
              render: (r) => (
                <RemoveButton
                  onClick={() =>
                    set(
                      'bonusTiers',
                      s.bonusTiers.filter((_, j) => j !== r.i),
                    )
                  }
                />
              ),
            },
          ]}
        />
      </Section>

      <Section
        title={t('Промокоды')}
        bodyClassName="p-2"
        actions={
          <Button
            size="sm"
            onClick={() =>
              set('promoCodes', [
                ...s.promoCodes,
                {
                  code: `PROMO${s.promoCodes.length + 1}`,
                  kind: 'bonus',
                  value: 0,
                  usesLeft: null,
                  expiresAt: null,
                  used: 0,
                },
              ])
            }
          >
            {t('Добавить промокод')}
          </Button>
        }
      >
        <Table
          rows={promos}
          rowKey={(r) => String(r.i)}
          empty={t('Промокодов нет')}
          columns={[
            {
              key: 'code',
              title: t('Код'),
              render: (r) => (
                <Input
                  className="font-mono uppercase"
                  value={r.promo.code}
                  onChange={(e) => editPromo(r.i, { code: e.target.value.toUpperCase().replace(/\s/g, '') })}
                />
              ),
            },
            {
              key: 'kind',
              title: t('Тип'),
              width: '13rem',
              render: (r) => (
                <Segmented
                  size="md"
                  label={t('Тип')}
                  value={r.promo.kind}
                  onChange={(kind) => editPromo(r.i, { kind, value: kind === 'bonus' ? 0 : 10 })}
                  options={[
                    { id: 'bonus', label: t('Бонус') },
                    { id: 'discountPct', label: t('Скидка %') },
                  ]}
                />
              ),
            },
            {
              key: 'value',
              title: t('Значение'),
              width: '10rem',
              render: (r) =>
                r.promo.kind === 'bonus' ? (
                  <MoneyInput compact value={r.promo.value} onChange={(n) => editPromo(r.i, { value: n })} />
                ) : (
                  <NumberInput
                    value={r.promo.value}
                    min={0}
                    max={100}
                    suffix="%"
                    onChange={(n) => editPromo(r.i, { value: n })}
                  />
                ),
            },
            {
              key: 'left',
              title: t('Осталось'),
              width: '7rem',
              render: (r) => (
                <Input
                  inputMode="numeric"
                  className="tnum"
                  placeholder="∞"
                  value={r.promo.usesLeft === null ? '' : String(r.promo.usesLeft)}
                  onChange={(e) => {
                    const d = e.target.value.replace(/\D/g, '');
                    editPromo(r.i, { usesLeft: d === '' ? null : Number(d) });
                  }}
                />
              ),
            },
            {
              key: 'until',
              title: t('До даты'),
              width: '10rem',
              render: (r) => (
                <input
                  type="date"
                  className={clsx(inputCls, 'tnum')}
                  value={r.promo.expiresAt ? r.promo.expiresAt.slice(0, 10) : ''}
                  onChange={(e) =>
                    editPromo(r.i, { expiresAt: e.target.value ? `${e.target.value}T23:59:59.999Z` : null })
                  }
                />
              ),
            },
            {
              key: 'used',
              title: t('Использовано'),
              num: true,
              width: '7rem',
              render: (r) => r.promo.used,
            },
            {
              key: 'rm',
              title: '',
              width: '6rem',
              render: (r) => (
                <RemoveButton
                  onClick={() =>
                    set(
                      'promoCodes',
                      s.promoCodes.filter((_, j) => j !== r.i),
                    )
                  }
                />
              ),
            },
          ]}
        />
      </Section>
    </div>
  );
}

function HappyTab({ s, set }: { s: ClubSettings; set: ClubSettingsState['set'] }): JSX.Element {
  const edit = (id: string, p: Partial<HappyHour>): void =>
    set(
      'happyHours',
      s.happyHours.map((h) => (h.id === id ? { ...h, ...p } : h)),
    );
  return (
    <Section
      title={t('Счастливые часы')}
      bodyClassName="p-2"
      actions={
        <Button
          size="sm"
          onClick={() =>
            set('happyHours', [
              ...s.happyHours,
              {
                id: uid(),
                name: t('Новая акция'),
                days: [1, 2, 3, 4, 5],
                from: '10:00',
                to: '14:00',
                discountPct: 20,
                zones: [],
              },
            ])
          }
        >
          {t('Добавить')}
        </Button>
      }
    >
      <Table
        rows={s.happyHours}
        rowKey={(h) => h.id}
        empty={t('Счастливых часов нет')}
        columns={[
          {
            key: 'name',
            title: t('Название'),
            render: (h) => <Input value={h.name} onChange={(e) => edit(h.id, { name: e.target.value })} />,
          },
          {
            key: 'days',
            title: t('Дни'),
            width: '19rem',
            render: (h) => (
              <WeekdayPicks
                on={(d) => h.days.includes(d.idx)}
                onToggle={(d) => edit(h.id, { days: toggle(h.days, d.idx).sort() })}
              />
            ),
          },
          {
            key: 'time',
            title: t('Время'),
            width: '15rem',
            render: (h) => (
              <div className="flex items-center gap-1.5">
                <input
                  type="time"
                  className={clsx(inputCls, 'tnum')}
                  value={h.from}
                  onChange={(e) => edit(h.id, { from: e.target.value })}
                />
                <span className="text-muted">–</span>
                <input
                  type="time"
                  className={clsx(inputCls, 'tnum')}
                  value={h.to}
                  onChange={(e) => edit(h.id, { to: e.target.value })}
                />
              </div>
            ),
          },
          {
            key: 'pct',
            title: t('Скидка'),
            width: '7rem',
            render: (h) => (
              <NumberInput
                value={h.discountPct}
                min={0}
                max={100}
                suffix="%"
                onChange={(n) => edit(h.id, { discountPct: n })}
              />
            ),
          },
          {
            key: 'zones',
            title: t('Зоны'),
            render: (h) => (
              <div className="flex flex-wrap gap-1">
                <Pick on={h.zones.length === 0} onClick={() => edit(h.id, { zones: [] })}>
                  {t('Все')}
                </Pick>
                {s.zones.map((z) => (
                  <Pick
                    key={z.name}
                    on={h.zones.includes(z.name)}
                    onClick={() => edit(h.id, { zones: toggle(h.zones, z.name) })}
                  >
                    {z.name}
                  </Pick>
                ))}
              </div>
            ),
          },
          {
            key: 'rm',
            title: '',
            width: '6rem',
            render: (h) => (
              <RemoveButton
                onClick={() =>
                  set(
                    'happyHours',
                    s.happyHours.filter((x) => x.id !== h.id),
                  )
                }
              />
            ),
          },
        ]}
      />
    </Section>
  );
}

function LoyaltyTab({ s, set }: { s: ClubSettings; set: ClubSettingsState['set'] }): JSX.Element {
  const rows = [...s.loyalty].sort((a, b) => a.level - b.level);
  const edit = (level: number, p: Partial<LoyaltyLevel>): void =>
    set(
      'loyalty',
      s.loyalty.map((l) => (l.level === level ? { ...l, ...p } : l)),
    );
  return (
    <Section title={t('Уровни лояльности')} className="max-w-4xl" bodyClassName="p-2">
      <Table
        rows={rows}
        rowKey={(l) => String(l.level)}
        columns={[
          {
            key: 'level',
            title: t('Уровень'),
            width: '6rem',
            render: (l) => (
              <span className="tnum font-display text-base font-medium leading-none tracking-[-0.01em] text-hi">
                {String(l.level).padStart(2, '0')}
              </span>
            ),
          },
          {
            key: 'name',
            title: t('Название'),
            render: (l) => <Input value={l.name} onChange={(e) => edit(l.level, { name: e.target.value })} />,
          },
          {
            key: 'min',
            title: t('Потрачено от'),
            width: '14rem',
            render: (l) => <MoneyInput compact value={l.minSpent} onChange={(n) => edit(l.level, { minSpent: n })} />,
          },
          {
            key: 'pct',
            title: t('Скидка'),
            width: '8rem',
            render: (l) => (
              <NumberInput
                value={l.discountPct}
                min={0}
                max={100}
                suffix="%"
                onChange={(n) => edit(l.level, { discountPct: n })}
              />
            ),
          },
        ]}
      />
    </Section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------------------------------------------------

export default function PricingPage(): JSX.Element {
  const [tab, setTab] = useState<Tab>('tariffs');
  const settings = useClubSettings();
  const [tariffs, setTariffs] = useState<Tariff[]>([]);

  useEffect(() => {
    if (tab !== 'days') return;
    clubApi
      .tariffs()
      .then((r) => setTariffs(r.items))
      .catch(() => setTariffs([]));
  }, [tab]);

  const s = settings.draft;

  return (
    <OwnerPage>
      <PageHeader title={t('Тарифы и цены')} caption={t('Настройка клуба')} />

      {/* Page tabs, not a second navigation landmark: the rail is the console's one `<nav>`. */}
      <Tabs
        label={t('Тарифы и цены')}
        value={tab}
        onChange={setTab}
        tabs={TABS.map((x) => ({ id: x.id, label: t(x.title) }))}
      />

      {settings.error && <Note note={{ text: settings.error, tone: 'err' }} />}

      {tab === 'tariffs' && <TariffsTab zones={(s?.zones ?? []).map((z) => z.name)} pending={settings.dirty} />}
      {s && tab === 'days' && <DaysTab s={s} tariffs={tariffs} set={settings.set} />}
      {s && tab === 'groups' && <GroupsTab s={s} set={settings.set} />}
      {s && tab === 'bonus' && <BonusTab s={s} set={settings.set} />}
      {s && tab === 'happy' && <HappyTab s={s} set={settings.set} />}
      {s && tab === 'loyalty' && <LoyaltyTab s={s} set={settings.set} />}

      <SaveBar
        dirty={settings.dirty}
        saving={settings.saving}
        onSave={() => void settings.save()}
        onReset={settings.reset}
        label={t('Есть несохранённые изменения')}
      />
    </OwnerPage>
  );
}
