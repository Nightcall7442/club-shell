/**
 * Shop and stock: products with price, remaining quantity and availability; a row opens the edit panel with goods
 * receipt. Edits are owner-only on the server; a cashier sees the server's refusal. Low-stock threshold is a club setting.
 */
import { useCallback, useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import type { Product } from '@clubshell/contracts';
import { clubApi } from '@/api';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { money } from '@/format';
import { useClubSettings } from '@/settings';
import { Button, Field, MoneyInput, Note, NumberInput, PageHeader, SaveBar, Section, Table, Toggle } from '@/ui';

type Filter = 'all' | 'low' | 'out';
type NoteState = { text: string; tone: 'ok' | 'err' } | null;

const FILTERS: { id: Filter; label: string }[] = [
  { id: 'all', label: 'Все' },
  { id: 'low', label: 'Заканчивается' },
  { id: 'out', label: 'Нет в наличии' },
];

const CATEGORY: Record<string, string> = {
  food: 'Еда',
  drink: 'Напитки',
  snack: 'Снеки',
  service: 'Услуги',
  merch: 'Мерч',
  time: 'Пакеты времени',
};

const isLow = (p: Product, lowAt: number): boolean => p.stockQty != null && p.stockQty <= lowAt;

function ProductPanel({ product, onChanged }: { product: Product; onChanged: () => Promise<void> }): JSX.Element {
  const [price, setPrice] = useState(product.price.amount);
  const [qty, setQty] = useState(product.stockQty ?? 0);
  const [tracked, setTracked] = useState(product.stockQty != null);
  const [inStock, setInStock] = useState(product.inStock);
  const [receive, setReceive] = useState(0);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<NoteState>(null);

  useEffect(() => {
    setPrice(product.price.amount);
    setQty(product.stockQty ?? 0);
    setTracked(product.stockQty != null);
    setInStock(product.inStock);
  }, [product]);
  useEffect(() => {
    setReceive(0);
    setNote(null);
  }, [product.id]);

  const run = async (fn: () => Promise<unknown>, ok: string): Promise<void> => {
    setBusy(true);
    setNote(null);
    try {
      await fn();
      await onChanged();
      setNote({ text: ok, tone: 'ok' });
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  const stockQty = tracked ? Math.max(0, Math.round(qty)) : null;
  const changed =
    price !== product.price.amount || inStock !== product.inStock || stockQty !== (product.stockQty ?? null);

  return (
    <div className="flex flex-col gap-5">
      <Section title={product.title}>
        <Field label={t('Цена')}>
          <MoneyInput value={price} onChange={setPrice} />
        </Field>
        <Field label={t('Остаток')}>
          <NumberInput value={qty} min={0} suffix={t('шт')} disabled={!tracked} onChange={setQty} />
        </Field>
        <Toggle label={t('Не вести учёт')} checked={!tracked} onChange={(v) => setTracked(!v)} />
        <Toggle label={t('В наличии')} checked={inStock} onChange={setInStock} />
        <Note note={note} />
        <Button
          variant="primary"
          className="self-end"
          disabled={busy || !changed}
          onClick={() =>
            void run(() => clubApi.updateProduct(product.id, { price, stockQty, inStock }), t('Сохранено'))
          }
        >
          {t('Сохранить')}
        </Button>
      </Section>

      <Section title={t('Приход товара')}>
        <div className="flex items-end gap-2">
          <Field label={t('Количество')} className="flex-1">
            <NumberInput value={receive} min={1} suffix={t('шт')} onChange={setReceive} />
          </Field>
          <Button
            disabled={busy || receive < 1}
            onClick={() =>
              void run(
                async () => {
                  await clubApi.receiveProduct(product.id, Math.round(receive));
                  setReceive(0);
                },
                t('Принято {n} шт', { n: Math.round(receive) }),
              )
            }
          >
            {t('Принять')}
          </Button>
        </div>
      </Section>
    </div>
  );
}

export default function ShopPage(): JSX.Element {
  const [items, setItems] = useState<Product[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<Filter>('all');
  const [selected, setSelected] = useState<string | null>(null);
  const [rowNote, setRowNote] = useState<NoteState>(null);
  const s = useClubSettings();
  const lowAt = s.draft?.stock.lowAt ?? 0;

  const load = useCallback(async () => {
    try {
      setItems((await clubApi.products()).items);
      setError(null);
    } catch (e) {
      setError(describe(e));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const rows = useMemo(
    () =>
      items.filter((p) =>
        filter === 'low' ? isLow(p, lowAt) && p.inStock : filter === 'out' ? !p.inStock || p.stockQty === 0 : true,
      ),
    [items, filter, lowAt],
  );
  const product = items.find((p) => p.id === selected) ?? null;

  const toggleStock = async (p: Product, v: boolean): Promise<void> => {
    setRowNote(null);
    try {
      await clubApi.updateProduct(p.id, { inStock: v });
      await load();
    } catch (e) {
      setRowNote({ text: describe(e), tone: 'err' });
    }
  };

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title={t('Магазин и склад')} />
      {error && <Note note={{ text: error, tone: 'err' }} />}
      {s.error && <Note note={{ text: s.error, tone: 'err' }} />}
      <Note note={rowNote} />

      <div className={clsx('grid grid-cols-1 items-start gap-5', product && 'lg:grid-cols-[minmax(0,1fr)_24rem]')}>
        <div className="flex flex-col gap-5">
          <Section
            title={t('Товары')}
            actions={FILTERS.map((f) => (
              <Button
                key={f.id}
                size="sm"
                className={clsx(filter === f.id && 'choice-on')}
                onClick={() => setFilter(f.id)}
              >
                {t(f.label)}
              </Button>
            ))}
            bodyClassName="p-2"
          >
            <Table
              rows={rows}
              rowKey={(p) => p.id}
              selectedKey={selected}
              onRowClick={(p) => setSelected(p.id === selected ? null : p.id)}
              empty={t('Нет товаров')}
              columns={[
                {
                  key: 'img',
                  title: t('Фото'),
                  width: '4rem',
                  render: (p) => (
                    <span className="block h-10 w-10 overflow-hidden rounded-md border border-line bg-bg">
                      <img
                        src={p.imageUrl}
                        alt=""
                        loading="lazy"
                        onError={(e) => (e.currentTarget.style.visibility = 'hidden')}
                        className="h-full w-full object-cover"
                      />
                    </span>
                  ),
                },
                { key: 'title', title: t('Название'), render: (p) => <span className="font-medium">{p.title}</span> },
                {
                  key: 'cat',
                  title: t('Категория'),
                  render: (p) => <span className="text-muted">{t(CATEGORY[p.category] ?? p.category)}</span>,
                },
                { key: 'price', title: t('Цена'), num: true, render: (p) => money(p.price) },
                {
                  key: 'qty',
                  title: t('Остаток'),
                  num: true,
                  render: (p) =>
                    p.stockQty == null ? (
                      <span className="text-muted">{t('не учитывается')}</span>
                    ) : (
                      <span className={clsx(isLow(p, lowAt) && 'text-danger')}>{p.stockQty}</span>
                    ),
                },
                {
                  key: 'stock',
                  title: t('В наличии'),
                  width: '7rem',
                  render: (p) => (
                    <span onClick={(e) => e.stopPropagation()}>
                      <Toggle checked={p.inStock} onChange={(v) => void toggleStock(p, v)} />
                    </span>
                  ),
                },
              ]}
            />
          </Section>

          <Section title={t('Учёт остатков')}>
            <Field label={t('Порог «заканчивается»')} className="max-w-[12rem]">
              <NumberInput
                value={lowAt}
                min={0}
                suffix={t('шт')}
                disabled={!s.draft}
                onChange={(n) => s.draft && s.set('stock', { ...s.draft.stock, lowAt: Math.max(0, Math.round(n)) })}
              />
            </Field>
          </Section>
          <SaveBar
            dirty={s.dirty}
            saving={s.saving}
            label={t('Порог изменён')}
            onReset={s.reset}
            onSave={() => void s.save()}
          />
        </div>

        {product && <ProductPanel product={product} onChanged={load} />}
      </div>
    </div>
  );
}
