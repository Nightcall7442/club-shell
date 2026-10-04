/**
 * Shop and stock: products with price, remaining quantity and availability; a row opens the edit panel with goods
 * receipt. Edits are owner-only on the server; a cashier sees the server's refusal, and no «В наличии» switch. The owner
 * adds a product here («Новый товар») and archives one («В архив»): the bar sells what is listed (cash desk part 3, D-58).
 * A save sends only what changed: a new quantity goes with the one the panel read, so a bar sale meanwhile is never
 * overwritten (409 `stockChanged`: the panel reloads and says so, D-54). Low-stock threshold is a club setting.
 */
import { useCallback, useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import type { Product } from '@clubshell/contracts';
import { clubApi } from '@/api';
import { describe, reasonOf } from '@/errors';
import { t } from '@/i18n';
import { money } from '@/format';
import { PRODUCT_CATEGORY_LABEL } from '@/labels';
import { useClubSettings } from '@/settings';
import {
  Button,
  Field,
  Input,
  MoneyInput,
  Note,
  NumberInput,
  PageHeader,
  SaveBar,
  Section,
  Sheet,
  Table,
  Toggle,
  inputCls,
} from '@/ui';

type Filter = 'all' | 'low' | 'out';
type NoteState = { text: string; tone: 'ok' | 'err' } | null;

const FILTERS: { id: Filter; label: string }[] = [
  { id: 'all', label: 'Все' },
  { id: 'low', label: 'Заканчивается' },
  { id: 'out', label: 'Нет в наличии' },
];

const CATEGORIES = ['drink', 'food', 'snack', 'service', 'merch', 'time'] as const;

const isLow = (p: Product, lowAt: number): boolean => p.stockQty != null && p.stockQty <= lowAt;

/** The keys of a save that changed, and with a new quantity the one the panel read (D-54). */
function changesOf(
  product: Product,
  next: { price: number; stockQty: number | null; inStock: boolean },
  owner: boolean,
): Parameters<typeof clubApi.updateProduct>[1] {
  const out: Parameters<typeof clubApi.updateProduct>[1] = {};
  if (next.price !== product.price.amount) out.price = next.price;
  if (next.stockQty !== (product.stockQty ?? null)) {
    out.stockQty = next.stockQty;
    out.expectedStockQty = product.stockQty ?? null;
  }
  if (owner && next.inStock !== product.inStock) out.inStock = next.inStock;
  return out;
}

function ProductPanel({
  product,
  owner,
  onChanged,
  onArchived,
}: {
  product: Product;
  owner: boolean;
  onChanged: () => Promise<void>;
  onArchived: () => void;
}): JSX.Element {
  const [price, setPrice] = useState(product.price.amount);
  const [qty, setQty] = useState(product.stockQty ?? 0);
  const [tracked, setTracked] = useState(product.stockQty != null);
  const [inStock, setInStock] = useState(product.inStock);
  const [receive, setReceive] = useState(0);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<NoteState>(null);
  const [archiving, setArchiving] = useState(false);

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
      if (reasonOf(e) === 'stockChanged') {
        // The bar sold meanwhile: the panel shows the quantity as it is now; the owner saves again on purpose.
        await onChanged();
        setNote({
          text: t('Остаток изменился, пока вы редактировали (продажи бара) — проверьте и сохраните снова'),
          tone: 'err',
        });
      } else {
        setNote({ text: describe(e), tone: 'err' });
      }
    } finally {
      setBusy(false);
    }
  };

  const stockQty = tracked ? Math.max(0, Math.round(qty)) : null;
  const changes = changesOf(product, { price, stockQty, inStock }, owner);
  const changed = Object.keys(changes).length > 0;

  return (
    <div className="flex flex-col gap-5">
      <Section
        title={product.title}
        actions={
          owner ? (
            <Button variant="ghost" size="sm" disabled={busy} onClick={() => setArchiving(true)}>
              {t('В архив')}
            </Button>
          ) : undefined
        }
      >
        <Field label={t('Цена')}>
          <MoneyInput value={price} onChange={setPrice} />
        </Field>
        <Field label={t('Остаток')}>
          <NumberInput value={qty} min={0} suffix={t('шт')} disabled={!tracked} onChange={setQty} />
        </Field>
        <Toggle label={t('Не вести учёт')} checked={!tracked} onChange={(v) => setTracked(!v)} />
        {owner && <Toggle label={t('В наличии')} checked={inStock} onChange={setInStock} />}
        <Note note={note} />
        <Button
          variant="primary"
          className="self-end"
          disabled={busy || !changed}
          onClick={() => void run(() => clubApi.updateProduct(product.id, changes), t('Сохранено'))}
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

      {archiving && (
        <Sheet title={t('В архив · {title}', { title: product.title })} onClose={() => setArchiving(false)}>
          <p className="text-sm">
            {t('Товар пропадёт из бара и из списка. Прошлые продажи сохранят его название и цену.')}
          </p>
          <Note note={note?.tone === 'err' ? note : null} />
          <div className="flex justify-end gap-2 border-t border-line pt-4">
            <Button variant="ghost" autoFocus onClick={() => setArchiving(false)}>
              {t('Отмена')}
            </Button>
            <Button
              variant="danger"
              className="border border-danger/50"
              disabled={busy}
              onClick={() => {
                setBusy(true);
                clubApi
                  .archiveProduct(product.id)
                  .then(() => {
                    setArchiving(false);
                    onArchived();
                  })
                  .catch((e: unknown) => setNote({ text: describe(e), tone: 'err' }))
                  .finally(() => setBusy(false));
              }}
            >
              {t('В архив')}
            </Button>
          </div>
        </Sheet>
      )}
    </div>
  );
}

/** «Новый товар» (owner): a name, a category, a price, and a quantity when it is counted. */
function NewProductSheet({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: (p: Product) => void;
}): JSX.Element {
  const [title, setTitle] = useState('');
  const [category, setCategory] = useState<string>('drink');
  const [price, setPrice] = useState(0);
  const [tracked, setTracked] = useState(true);
  const [qty, setQty] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const name = title.trim();
  const ready = name.length > 0 && name.length <= 80 && !busy;
  const create = async (): Promise<void> => {
    if (!ready) return;
    setBusy(true);
    setError(null);
    try {
      const r = await clubApi.createProduct({
        title: name,
        category,
        price,
        stockQty: tracked ? Math.max(0, Math.round(qty)) : null,
        inStock: true,
      });
      onCreated(r.product);
    } catch (e) {
      setError(describe(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Sheet title={t('Новый товар')} onClose={onClose}>
      <div
        className="flex flex-col gap-4"
        onKeyDown={(e) => {
          if (e.key === 'Enter' && !(e.target instanceof HTMLButtonElement)) {
            e.preventDefault();
            if (!e.repeat) void create();
          }
        }}
      >
        <Field label={t('Название')}>
          <Input value={title} maxLength={80} autoFocus onChange={(e) => setTitle(e.target.value)} />
        </Field>
        <Field label={t('Категория')}>
          <select className={inputCls} value={category} onChange={(e) => setCategory(e.target.value)}>
            {CATEGORIES.map((c) => (
              <option key={c} value={c}>
                {t(PRODUCT_CATEGORY_LABEL[c] ?? c)}
              </option>
            ))}
          </select>
        </Field>
        <Field label={t('Цена')}>
          <MoneyInput value={price} onChange={setPrice} />
        </Field>
        <Toggle label={t('Не вести учёт')} checked={!tracked} onChange={(v) => setTracked(!v)} />
        {tracked && (
          <Field label={t('Остаток')}>
            <NumberInput value={qty} min={0} suffix={t('шт')} onChange={setQty} />
          </Field>
        )}
        <Note note={error ? { text: error, tone: 'err' } : null} />
        <div className="flex justify-end gap-2 border-t border-line pt-4">
          <Button variant="ghost" onClick={onClose}>
            {t('Отмена')}
          </Button>
          <Button variant="primary" disabled={!ready} onClick={() => void create()}>
            {busy ? '…' : t('Добавить')}
          </Button>
        </div>
      </div>
    </Sheet>
  );
}

/** Product photo; the first letter of the name when there is none or it fails to load. */
function ProductThumb({ url, title }: { url: string | null | undefined; title: string }): JSX.Element {
  const [failed, setFailed] = useState(false);
  return (
    <span className="flex h-10 w-10 items-center justify-center overflow-hidden rounded-md border border-line bg-bg">
      {url && !failed ? (
        <img src={url} alt="" loading="lazy" onError={() => setFailed(true)} className="h-full w-full object-cover" />
      ) : (
        <span aria-hidden="true" className="font-display text-sm text-muted">
          {title.trim().charAt(0).toUpperCase()}
        </span>
      )}
    </span>
  );
}

export default function ShopPage({ isOwner = false }: { isOwner?: boolean }): JSX.Element {
  const [items, setItems] = useState<Product[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<Filter>('all');
  const [selected, setSelected] = useState<string | null>(null);
  const [rowNote, setRowNote] = useState<NoteState>(null);
  const [creating, setCreating] = useState(false);
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
      <PageHeader
        title={t('Магазин и склад')}
        actions={
          isOwner ? (
            <Button variant="primary" onClick={() => setCreating(true)}>
              {t('Новый товар')}
            </Button>
          ) : undefined
        }
      />
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
                  render: (p) => <ProductThumb url={p.imageUrl} title={p.title} />,
                },
                { key: 'title', title: t('Название'), render: (p) => <span className="font-medium">{p.title}</span> },
                {
                  key: 'cat',
                  title: t('Категория'),
                  render: (p) => (
                    <span className="text-muted">{t(PRODUCT_CATEGORY_LABEL[p.category] ?? p.category)}</span>
                  ),
                },
                { key: 'price', title: t('Цена'), num: true, render: (p) => money(p.price) },
                {
                  key: 'qty',
                  title: t('Остаток'),
                  num: true,
                  render: (p) =>
                    p.stockQty == null ? (
                      <span className="text-muted">{t('не учитывается')}</span>
                    ) : p.stockQty === 0 ? (
                      <span className="text-danger">{t('нет на складе')}</span>
                    ) : (
                      <span className={clsx(isLow(p, lowAt) && 'text-danger')}>{p.stockQty}</span>
                    ),
                },
                // The switch is the owner's (the server refuses a cashier's): a cashier sees no switch.
                ...(isOwner
                  ? [
                      {
                        key: 'stock',
                        title: t('В наличии'),
                        width: '7rem',
                        render: (p: Product) => (
                          <span onClick={(e) => e.stopPropagation()}>
                            <Toggle checked={p.inStock} onChange={(v) => void toggleStock(p, v)} />
                          </span>
                        ),
                      },
                    ]
                  : []),
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

        {product && (
          <ProductPanel
            product={product}
            owner={isOwner}
            onChanged={load}
            onArchived={() => {
              setSelected(null);
              setRowNote({ text: t('Товар в архиве'), tone: 'ok' });
              void load();
            }}
          />
        )}
      </div>

      {creating && (
        <NewProductSheet
          onClose={() => setCreating(false)}
          onCreated={(p) => {
            setCreating(false);
            setRowNote({ text: t('Товар добавлен · {title}', { title: p.title }), tone: 'ok' });
            void load().then(() => setSelected(p.id));
          }}
        />
      )}
    </div>
  );
}
