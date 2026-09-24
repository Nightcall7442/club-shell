import { memo, useState } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { ORDER_MAX_QTY, type Product } from '@clubshell/contracts';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { useResolvedAsset } from '@/components/media/GameArtwork';
import { useLocale } from '@/hooks/useLocale';
import { formatMoney } from '@/lib/format';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';

export interface ProductCardProps {
  product: Product;
  /** Quantity currently in the cart (0 = not added). */
  qty: number;
  /** Called with the new quantity; 0 removes the line. */
  onChange: (qty: number) => void;
  className?: string;
}

/** Stock threshold under which the remaining quantity is shown. */
export const LOW_STOCK_QTY = 5;

const PlusIcon = (): JSX.Element => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true">
    <path d="M12 5v14M5 12h14" />
  </svg>
);

const MinusIcon = (): JSX.Element => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true">
    <path d="M5 12h14" />
  </svg>
);

function hueOf(text: string): number {
  let h = 0;
  for (let i = 0; i < text.length; i += 1) {
    h = (h * 31 + text.charCodeAt(i)) % 360;
  }
  return h;
}

/** Product tile: image (gradient fallback), title, price, stock badge and add / ± quantity controls. */
export const ProductCard = memo(function ProductCard({
  product,
  qty,
  onChange,
  className,
}: ProductCardProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const animations = useThemeStore(selectAnimationsEnabled);
  const { url, error } = useResolvedAsset(product.imageUrl);
  const [imgFailed, setImgFailed] = useState(false);

  const available = product.inStock && (product.stockQty == null || product.stockQty > 0);
  const maxQty = Math.min(ORDER_MAX_QTY, product.stockQty ?? ORDER_MAX_QTY);
  const lowStock = available && product.stockQty != null && product.stockQty <= LOW_STOCK_QTY;
  const showImage = url !== null && !error && !imgFailed;
  const hue = hueOf(product.id);

  return (
    <motion.article
      layout={animations}
      className={clsx(
        'glass group relative flex flex-col overflow-hidden rounded-lg transition-[box-shadow] duration-[var(--dur-fast)]',
        'focus-within:border-glow',
        !available && 'opacity-60',
        className,
      )}
      aria-label={product.title}
    >
      <div
        className="relative aspect-[4/3] w-full overflow-hidden"
        style={{ background: `linear-gradient(135deg, hsl(${hue} 55% 28%), hsl(${(hue + 40) % 360} 60% 18%))` }}
      >
        {showImage ? (
          <img
            src={url}
            alt={t('shop.imageAlt', { title: product.title })}
            loading="lazy"
            decoding="async"
            draggable={false}
            onError={() => setImgFailed(true)}
            className="h-full w-full object-cover transition-transform duration-[var(--dur-slow)] ease-[var(--ease-out)] group-hover:scale-105"
          />
        ) : (
          <span
            className="absolute inset-0 flex items-center justify-center text-4xl font-semibold text-white/70"
            aria-hidden="true"
          >
            {product.title.slice(0, 1).toUpperCase()}
          </span>
        )}
        <div className="absolute left-2 top-2 flex flex-wrap gap-1.5">
          <Badge size="sm" tone="neutral" className="bg-bg/70">
            {t(`shop.category.${product.category}`)}
          </Badge>
          {!available && (
            <Badge size="sm" tone="danger" solid>
              {t('shop.outOfStock')}
            </Badge>
          )}
          {lowStock && (
            <Badge size="sm" tone="accent">
              {t('shop.lowStock', { count: product.stockQty ?? 0 })}
            </Badge>
          )}
        </div>
        {qty > 0 && (
          <Badge size="md" tone="primary" solid className="absolute right-2 top-2 tnum">
            ×{qty}
          </Badge>
        )}
      </div>

      <div className="flex flex-1 flex-col gap-2 p-3">
        <h3 className="line-clamp-2 min-h-[2.6em] text-base font-semibold leading-snug text-text">{product.title}</h3>
        <div className="mt-auto flex items-center justify-between gap-2">
          <span className="tnum whitespace-nowrap text-lg font-bold text-text">
            {formatMoney(product.price, locale)}
          </span>
          {qty === 0 ? (
            // Icon-only rather than labelled: next to the price the label wrapped the amount onto two lines. Size lg
            // (3rem) is exactly the height of the − n + stepper it turns into, so the card does not jump.
            <Button
              size="lg"
              variant="secondary"
              iconOnly
              icon={<PlusIcon />}
              disabled={!available}
              aria-label={`${t('shop.addToCart')}: ${product.title}`}
              title={t('shop.addToCart')}
              className="shrink-0"
              onClick={() => onChange(1)}
            />
          ) : (
            <div
              className="flex items-center gap-1 rounded-lg bg-text/[0.06] p-1"
              role="group"
              aria-label={`${t('shop.qty')}: ${product.title}`}
            >
              <Button
                size="md"
                variant="ghost"
                iconOnly
                icon={<MinusIcon />}
                aria-label={qty === 1 ? t('shop.removeFromCart') : t('shop.decrease')}
                className="h-10 w-10"
                onClick={() => onChange(qty - 1)}
              />
              <span className="tnum min-w-[2ch] text-center text-lg font-bold" aria-live="polite">
                {qty}
              </span>
              <Button
                size="md"
                variant="ghost"
                iconOnly
                icon={<PlusIcon />}
                aria-label={t('shop.increase')}
                disabled={qty >= maxQty}
                className="h-10 w-10"
                onClick={() => onChange(Math.min(maxQty, qty + 1))}
              />
            </div>
          )}
        </div>
      </div>
    </motion.article>
  );
});
