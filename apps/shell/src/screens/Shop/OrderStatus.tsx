import { useState } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { OrderStatus as OrderStatusEnum, type Order, type OrderStatus as OrderStatusValue } from '@clubshell/contracts';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { useLocale } from '@/hooks/useLocale';
import { formatMoney, formatTime } from '@/lib/format';
import { api } from '@/lib/tauri';
import { useNotificationsStore } from '@/store/notifications';
import { selectActiveOrders, useShopStore } from '@/store/shop';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';

/** Happy-path steps of an order, in order. */
export const ORDER_STEPS: readonly OrderStatusValue[] = [
  OrderStatusEnum.Pending,
  OrderStatusEnum.Accepted,
  OrderStatusEnum.Preparing,
  OrderStatusEnum.Delivering,
  OrderStatusEnum.Done,
];

/** Short display id of an order (`#A1B2C3`). */
export function shortOrderId(id: string): string {
  return id.replace(/-/g, '').slice(0, 6).toUpperCase();
}

export interface OrderStatusProps {
  order: Order;
  /** Shown while the order is still `pending`. */
  onCancel?: (order: Order) => void;
  cancelling?: boolean;
  className?: string;
}

const CheckIcon = (): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="3"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M5 13l4 4L19 7" />
  </svg>
);

/** One order card: header, items summary and the Pending → Accepted → Preparing → Delivering → Done stepper. */
export function OrderStatus({ order, onCancel, cancelling = false, className }: OrderStatusProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const cancelled = order.status === OrderStatusEnum.Cancelled;
  const current = ORDER_STEPS.indexOf(order.status);
  const items = order.items.map((i) => (i.qty > 1 ? `${i.title} ×${i.qty}` : i.title)).join(', ');

  return (
    <article
      aria-label={t('shop.orderNumber', { id: shortOrderId(order.id) })}
      className={clsx(
        'glass flex min-w-[clamp(280px,22vw,380px)] flex-col gap-3 rounded-lg p-4',
        cancelled && 'opacity-70',
        className,
      )}
    >
      <header className="flex items-center justify-between gap-3">
        <div className="min-w-0">
          <p className="text-base font-bold text-text">{t('shop.orderNumber', { id: shortOrderId(order.id) })}</p>
          <p className="truncate text-sm text-muted" title={items}>
            {items}
          </p>
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1">
          <Badge
            tone={cancelled ? 'danger' : order.status === OrderStatusEnum.Done ? 'success' : 'primary'}
            live={!cancelled && order.status !== OrderStatusEnum.Done}
          >
            {t(`shop.status.${order.status}`)}
          </Badge>
          <span className="tnum text-sm text-muted">
            {formatTime(order.createdAt, locale)} · {formatMoney(order.total, locale)}
          </span>
        </div>
      </header>

      {!cancelled && (
        <ol className="flex items-center gap-1" aria-label={t('common.status')}>
          {ORDER_STEPS.map((step, i) => {
            const done = i < current;
            const active = i === current;
            return (
              <li key={step} className="flex flex-1 items-center gap-1 last:flex-none">
                <span
                  className={clsx(
                    'inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-xs font-bold transition-colors duration-[var(--dur-base)]',
                    done && 'bg-success text-bg',
                    active && 'bg-accent text-on-accent',
                    !done && !active && 'bg-text/10 text-muted',
                  )}
                  aria-current={active ? 'step' : undefined}
                  title={t(`shop.status.${step}`)}
                >
                  {done ? (
                    <span className="h-4 w-4">
                      <CheckIcon />
                    </span>
                  ) : (
                    i + 1
                  )}
                </span>
                {i < ORDER_STEPS.length - 1 && (
                  <span
                    className={clsx(
                      'h-1 flex-1 rounded-full transition-colors duration-[var(--dur-base)]',
                      done ? 'bg-success' : 'bg-text/10',
                    )}
                    aria-hidden="true"
                  />
                )}
              </li>
            );
          })}
        </ol>
      )}

      <div className="flex items-center justify-between gap-3">
        <p className="text-sm text-muted">{t(`shop.statusHint.${order.status}`)}</p>
        {onCancel && order.status === OrderStatusEnum.Pending && (
          <Button
            size="md"
            variant="ghost"
            loading={cancelling}
            onClick={() => onCancel(order)}
            className="shrink-0 text-danger"
          >
            {t('shop.cancel')}
          </Button>
        )}
      </div>
    </article>
  );
}

export interface OrderStatusStripProps {
  className?: string;
}

/** Horizontal strip of the user's in-progress orders, updated live by `agent://shop.orderUpdated`. */
export function OrderStatusStrip({ className }: OrderStatusStripProps): JSX.Element | null {
  const { t } = useTranslation();
  const orders = useShopStore(selectActiveOrders);
  const animations = useThemeStore(selectAnimationsEnabled);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [cancelling, setCancelling] = useState<string | null>(null);

  if (orders.length === 0) {
    return null;
  }

  // ponytail: no `shop.cancel` command exists in the protocol; a cancellation is a staff ticket. Swap for a real
  // command when the server grows one.
  const cancel = async (order: Order): Promise<void> => {
    const id = shortOrderId(order.id);
    setCancelling(order.id);
    try {
      await api.system.callAdmin('order', t('shop.cancelMessage', { id }));
      push({ title: t('shop.cancelRequested'), body: t('shop.cancelRequestedHint', { id }), level: 'info' });
    } catch (e) {
      pushError(e, t('shop.cancel'));
    } finally {
      setCancelling(null);
    }
  };

  return (
    <section aria-label={t('shop.activeOrders')} className={clsx('flex flex-col gap-2', className)}>
      <h2 className="text-base font-semibold text-muted">{t('shop.activeOrders')}</h2>
      <ul role="list" className="no-scrollbar flex gap-3 overflow-x-auto pb-1">
        <AnimatePresence initial={false}>
          {orders.map((order) => (
            <motion.li
              key={order.id}
              layout={animations}
              initial={animations ? { opacity: 0, y: 8 } : false}
              animate={{ opacity: 1, y: 0 }}
              exit={animations ? { opacity: 0, scale: 0.96, transition: { duration: 0.15 } } : undefined}
              className="shrink-0"
            >
              <OrderStatus order={order} onCancel={(o) => void cancel(o)} cancelling={cancelling === order.id} />
            </motion.li>
          ))}
        </AnimatePresence>
      </ul>
    </section>
  );
}
