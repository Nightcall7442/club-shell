import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { ProductCategory, type ProductCategory as ProductCategoryValue } from '@clubshell/contracts';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Skeleton } from '@/components/ui/Skeleton';
import { Tabs, type TabItem } from '@/components/ui/Tabs';
import { focusElement, useGamepad } from '@/hooks/useGamepad';
import { useNotificationsStore } from '@/store/notifications';
import { selectFilteredProducts, useShopStore } from '@/store/shop';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';
import { Cart } from './Cart';
import { OrderStatusStrip } from './OrderStatus';
import { ProductCard } from './ProductCard';
import { TopUpModal } from '@/screens/Wallet/TopUpModal';

type CategoryKey = ProductCategoryValue | 'all';

/** Category tab order. */
export const SHOP_CATEGORIES: readonly ProductCategoryValue[] = [
  ProductCategory.Food,
  ProductCategory.Drink,
  ProductCategory.Snack,
  ProductCategory.Service,
  ProductCategory.Merch,
  ProductCategory.Time,
];

const SearchIcon = (): JSX.Element => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
    <circle cx="11" cy="11" r="7" />
    <path d="M20 20l-3.5-3.5" />
  </svg>
);

/** Shop: category tabs + search, product grid, live order strip and the cart panel on the right. */
export function ShopScreen(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const animations = useThemeStore(selectAnimationsEnabled);
  const status = useShopStore((s) => s.status);
  const error = useShopStore((s) => s.error);
  const category = useShopStore((s) => s.category);
  const search = useShopStore((s) => s.search);
  const cart = useShopStore((s) => s.cart);
  const products = useShopStore(selectFilteredProducts);
  const load = useShopStore((s) => s.load);
  const loadOrders = useShopStore((s) => s.loadOrders);
  const setCategory = useShopStore((s) => s.setCategory);
  const setSearch = useShopStore((s) => s.setSearch);
  const setQty = useShopStore((s) => s.setQty);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [topUpOpen, setTopUpOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const focused = useRef(false);

  useEffect(() => {
    void load();
    void loadOrders(true);
  }, [load, loadOrders]);

  useEffect(() => {
    if (status === 'error' && error) {
      pushError(error, t('shop.title'));
    }
  }, [status, error, pushError, t]);

  // Initial focus: the active category tab, once the catalogue is in.
  useEffect(() => {
    if (status !== 'ready' || focused.current) {
      return;
    }
    focused.current = true;
    const first =
      root.current?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]') ??
      root.current?.querySelector<HTMLElement>('[data-nav]');
    if (first) {
      focusElement(first);
    }
  }, [status]);

  useGamepad({
    onBack: () => {
      if (!document.documentElement.dataset['modalOpen']) {
        navigate('/home');
      }
    },
  });

  const tabs = useMemo<TabItem<CategoryKey>[]>(
    () => [
      { key: 'all', label: t('shop.category.all') },
      ...SHOP_CATEGORIES.map((c) => ({ key: c, label: t(`shop.category.${c}`) })),
    ],
    [t],
  );

  const onTab = useCallback((key: CategoryKey) => setCategory(key === 'all' ? null : key), [setCategory]);
  const onInsufficientFunds = useCallback(() => setTopUpOpen(true), []);

  const loading = status === 'loading' || status === 'idle';

  return (
    <motion.div
      ref={root}
      className="grid h-full min-h-0 grid-cols-[minmax(0,1fr)_clamp(320px,23vw,440px)] gap-[var(--gap)]"
      initial={animations ? { opacity: 0, y: 12 } : false}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.2, ease: 'easeOut' }}
    >
      <div className="flex min-h-0 min-w-0 flex-col gap-[var(--gap)]">
        <header className="flex flex-wrap items-end justify-between gap-4">
          <div>
            <h1 className="text-[length:var(--fs-2xl)] font-semibold tracking-tight text-text">{t('shop.title')}</h1>
            <p className="text-base text-muted">{t('shop.subtitle')}</p>
          </div>
          <Input
            aria-label={t('shop.search')}
            placeholder={t('shop.searchPlaceholder')}
            leading={<SearchIcon />}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            wrapperClassName="w-[clamp(220px,18vw,340px)]"
            type="search"
          />
        </header>

        <Tabs<CategoryKey>
          items={tabs}
          value={category ?? 'all'}
          onChange={onTab}
          label={t('common.category')}
          size="lg"
          idPrefix="shop"
        />

        <OrderStatusStrip />

        <div
          id={`shop-panel-${category ?? 'all'}`}
          role="tabpanel"
          aria-labelledby={`shop-tab-${category ?? 'all'}`}
          className="themed-scrollbar min-h-0 flex-1 overflow-y-auto overflow-x-hidden pb-4 pr-1"
        >
          {loading ? (
            <div className="grid grid-cols-[repeat(auto-fill,minmax(clamp(170px,11.5vw,240px),1fr))] gap-[var(--gap)]">
              {Array.from({ length: 12 }, (_, i) => (
                <div key={i} className="glass flex flex-col gap-3 rounded-lg p-3">
                  <Skeleton className="aspect-[4/3] w-full" />
                  <Skeleton variant="text" lines={2} />
                  <Skeleton height="2.75rem" />
                </div>
              ))}
            </div>
          ) : products.length === 0 ? (
            <div className="flex h-full min-h-[16rem] flex-col items-center justify-center gap-3 text-center">
              <p className="text-xl font-semibold text-text">
                {status === 'error' ? t('common.error') : search ? t('common.noResults') : t('shop.noProducts')}
              </p>
              {status === 'error' ? (
                <Button variant="secondary" size="lg" onClick={() => void load(true)}>
                  {t('common.retry')}
                </Button>
              ) : (
                <p className="text-base text-muted">{t('common.tryAgain')}</p>
              )}
            </div>
          ) : (
            <ul
              role="list"
              className="grid grid-cols-[repeat(auto-fill,minmax(clamp(170px,11.5vw,240px),1fr))] gap-[var(--gap)]"
            >
              {products.map((p) => (
                <li key={p.id}>
                  <ProductCard
                    product={p}
                    qty={cart.get(p.id) ?? 0}
                    onChange={(qty) => setQty(p.id, qty)}
                    className="h-full"
                  />
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>

      <Cart onInsufficientFunds={onInsufficientFunds} className="min-h-0" />

      <TopUpModal open={topUpOpen} onClose={() => setTopUpOpen(false)} />
    </motion.div>
  );
}

export default ShopScreen;
