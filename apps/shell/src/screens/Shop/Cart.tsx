import { useState } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { ORDER_MAX_QTY, type Money } from '@clubshell/contracts';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { useLocale } from '@/hooks/useLocale';
import { formatMoney } from '@/lib/format';
import { isShellApiError } from '@/lib/tauri';
import { describeError, useNotificationsStore } from '@/store/notifications';
import { useSettingsStore } from '@/store/settings';
import { selectCartCount, selectCartLines, selectCartTotal, useShopStore } from '@/store/shop';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';
import { selectBalanceAmount, useWalletStore } from '@/store/wallet';
import { ModalBackHandler } from '@/screens/Wallet/TopUpModal';

export interface CartProps {
  /** Called after an `insufficientFunds` rejection (the screen opens the top-up modal). */
  onInsufficientFunds?: () => void;
  className?: string;
}

const TrashIcon = (): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3" />
  </svg>
);

const BagIcon = (): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="1.5"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M6 8h12l1 13H5L6 8zM9 8V6a3 3 0 0 1 6 0v2" />
  </svg>
);

/** Right-hand cart panel: lines with ± quantity, staff note, total, balance-after and the place-order flow. */
export function Cart({ onInsufficientFunds, className }: CartProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const animations = useThemeStore(selectAnimationsEnabled);
  const lines = useShopStore(selectCartLines);
  const total = useShopStore(selectCartTotal);
  const count = useShopStore(selectCartCount);
  const placing = useShopStore((s) => s.placing);
  const setQty = useShopStore((s) => s.setQty);
  const removeFromCart = useShopStore((s) => s.removeFromCart);
  const clearCart = useShopStore((s) => s.clearCart);
  const placeOrder = useShopStore((s) => s.placeOrder);
  const balance = useWalletStore(selectBalanceAmount);
  const pcName = useSettingsStore((s) => s.pcInfo?.pc.name ?? '');
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [note, setNote] = useState('');
  const [confirming, setConfirming] = useState(false);

  const after: Money = { amount: balance.amount - total.amount, currency: total.currency };
  const enough = after.amount >= 0;
  const empty = lines.length === 0;

  const submit = async (): Promise<void> => {
    setConfirming(false);
    try {
      await placeOrder(note.trim() || undefined);
      setNote('');
      push({
        title: t('shop.orderPlaced'),
        body: pcName ? t('shop.orderPlacedHint', { pc: pcName }) : '',
        level: 'success',
      });
    } catch (e) {
      if (isShellApiError(e) && e.code === 'insufficientFunds') {
        push({ title: t('shop.insufficientFunds'), body: describeError(e), level: 'error' });
        onInsufficientFunds?.();
      } else {
        pushError(e, t('shop.placeOrder'));
      }
    }
  };

  return (
    <section
      aria-label={t('shop.cart')}
      className={clsx('glass flex min-h-0 flex-col rounded-xl', className)}
      data-nav-scope="cart"
    >
      <header className="flex items-center justify-between gap-3 px-5 pt-5">
        <h2 className="flex items-center gap-2 text-xl font-bold text-text">
          <span className="inline-flex h-6 w-6 text-primary" aria-hidden="true">
            <BagIcon />
          </span>
          {t('shop.cart')}
          {count > 0 && (
            <span className="tnum rounded-full bg-primary px-2 py-0.5 text-sm font-semibold text-white">{count}</span>
          )}
        </h2>
        {!empty && (
          <Button size="md" variant="ghost" icon={<TrashIcon />} onClick={clearCart} disabled={placing}>
            {t('shop.clearCart')}
          </Button>
        )}
      </header>

      <ul role="list" className="themed-scrollbar min-h-0 flex-1 overflow-y-auto px-5 py-4" aria-live="polite">
        {empty ? (
          <li className="flex h-full min-h-[10rem] flex-col items-center justify-center gap-2 text-center">
            <span className="inline-flex h-12 w-12 text-muted/60" aria-hidden="true">
              <BagIcon />
            </span>
            <p className="text-lg font-semibold text-text">{t('shop.emptyCart')}</p>
            <p className="text-base text-muted">{t('shop.emptyCartHint')}</p>
          </li>
        ) : (
          <AnimatePresence initial={false}>
            {lines.map(({ product, qty }) => {
              const maxQty = Math.min(ORDER_MAX_QTY, product.stockQty ?? ORDER_MAX_QTY);
              return (
                <motion.li
                  key={product.id}
                  layout={animations}
                  initial={animations ? { opacity: 0, x: 16 } : false}
                  animate={{ opacity: 1, x: 0 }}
                  exit={animations ? { opacity: 0, x: 16, transition: { duration: 0.15 } } : undefined}
                  className="flex items-center gap-3 border-b border-text/10 py-3 last:border-b-0"
                >
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-base font-semibold text-text">{product.title}</p>
                    <p className="tnum text-sm text-muted">
                      {formatMoney(product.price, locale)} × {qty}
                    </p>
                  </div>
                  <div
                    className="flex items-center gap-1"
                    role="group"
                    aria-label={`${t('shop.qty')}: ${product.title}`}
                  >
                    <Button
                      size="md"
                      variant="ghost"
                      iconOnly
                      className="h-9 w-9 rounded-full"
                      aria-label={qty === 1 ? t('shop.removeFromCart') : t('shop.decrease')}
                      disabled={placing}
                      onClick={() => (qty === 1 ? removeFromCart(product.id) : setQty(product.id, qty - 1))}
                    >
                      −
                    </Button>
                    <span className="tnum min-w-[2ch] text-center text-base font-bold">{qty}</span>
                    <Button
                      size="md"
                      variant="ghost"
                      iconOnly
                      className="h-9 w-9 rounded-full"
                      aria-label={t('shop.increase')}
                      disabled={placing || qty >= maxQty}
                      onClick={() => setQty(product.id, qty + 1)}
                    >
                      +
                    </Button>
                  </div>
                  <span className="tnum w-[7.5rem] shrink-0 text-right text-base font-bold text-text">
                    {formatMoney({ amount: product.price.amount * qty, currency: product.price.currency }, locale)}
                  </span>
                </motion.li>
              );
            })}
          </AnimatePresence>
        )}
      </ul>

      <footer className="flex flex-col gap-3 border-t border-text/10 px-5 py-4">
        <Input
          label={t('shop.note')}
          placeholder={t('shop.notePlaceholder')}
          value={note}
          maxLength={200}
          disabled={empty || placing}
          onChange={(e) => setNote(e.target.value)}
        />
        <dl className="flex flex-col gap-1 text-base">
          <div className="flex items-center justify-between">
            <dt className="text-muted">{t('shop.total')}</dt>
            <dd className="tnum text-2xl font-black text-text">{formatMoney(total, locale)}</dd>
          </div>
          <div className="flex items-center justify-between text-sm">
            <dt className="text-muted">{t('shop.balanceAfter')}</dt>
            <dd className={clsx('tnum font-semibold', enough ? 'text-muted' : 'text-danger')}>
              {formatMoney(after, locale)}
            </dd>
          </div>
        </dl>
        {!empty && !enough && <p className="text-sm text-danger">{t('shop.topUpFirst')}</p>}
        <Button size="lg" block loading={placing} disabled={empty} onClick={() => setConfirming(true)}>
          {placing ? t('shop.placing') : t('shop.placeOrder')}
        </Button>
      </footer>

      <Modal
        open={confirming}
        onClose={() => setConfirming(false)}
        title={t('shop.confirmTitle')}
        description={t('shop.confirmText', { total: formatMoney(total, locale) })}
        size="sm"
        footer={
          <>
            <Button variant="secondary" size="lg" onClick={() => setConfirming(false)}>
              {t('common.cancel')}
            </Button>
            <Button size="lg" onClick={() => void submit()}>
              {t('shop.payWithBalance')}
            </Button>
          </>
        }
      >
        <ModalBackHandler onBack={() => setConfirming(false)} />
        <ul role="list" className="flex flex-col gap-1 text-base text-text">
          {lines.map(({ product, qty }) => (
            <li key={product.id} className="flex justify-between gap-3">
              <span className="truncate">
                {product.title} × {qty}
              </span>
              <span className="tnum shrink-0">
                {formatMoney({ amount: product.price.amount * qty, currency: product.price.currency }, locale)}
              </span>
            </li>
          ))}
        </ul>
        {pcName && <p className="mt-3 text-sm text-muted">{t('shop.deliverTo', { pc: pcName })}</p>}
      </Modal>
    </section>
  );
}
