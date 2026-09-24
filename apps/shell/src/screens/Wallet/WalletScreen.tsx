import { useCallback, useEffect, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { focusElement, useGamepad } from '@/hooks/useGamepad';
import { useNotificationsStore } from '@/store/notifications';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';
import { useWalletStore } from '@/store/wallet';
import { Balance } from './Balance';
import { History } from './History';
import { Tariffs } from './Tariffs';
import { TopUpModal } from './TopUpModal';

/** Wallet: balance card + tariffs on the left, the transaction ledger on the right, top-up flow in a modal. */
export function WalletScreen(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const animations = useThemeStore(selectAnimationsEnabled);
  const balance = useWalletStore((s) => s.balance);
  const status = useWalletStore((s) => s.status);
  const error = useWalletStore((s) => s.error);
  const load = useWalletStore((s) => s.load);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [topUpOpen, setTopUpOpen] = useState(false);
  const topUpButton = useRef<HTMLButtonElement>(null);
  const root = useRef<HTMLDivElement>(null);
  const focused = useRef(false);

  useEffect(() => {
    if (balance === null && status !== 'loading') {
      void load();
    }
    // Only on mount: later balance changes arrive through `wallet.updated`.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (status === 'error' && error && balance === null) {
      pushError(error, t('wallet.title'));
    }
  }, [status, error, balance, pushError, t]);

  // Initial focus: the Top up button (or the first navigable when top-ups are disabled).
  useEffect(() => {
    if (focused.current || status === 'loading' || status === 'idle') {
      return;
    }
    focused.current = true;
    const target = topUpButton.current ?? root.current?.querySelector<HTMLElement>('[data-nav]');
    if (target) {
      focusElement(target);
    }
  }, [status]);

  useGamepad({
    onBack: () => {
      if (!document.documentElement.dataset['modalOpen']) {
        navigate('/home');
      }
    },
  });

  const openTopUp = useCallback(() => setTopUpOpen(true), []);
  const closeTopUp = useCallback(() => setTopUpOpen(false), []);

  return (
    <motion.div
      ref={root}
      className="grid h-full min-h-0 grid-cols-[minmax(0,3fr)_minmax(clamp(360px,28vw,560px),2fr)] gap-[var(--gap)]"
      initial={animations ? { opacity: 0, y: 12 } : false}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.2, ease: 'easeOut' }}
    >
      <div className="themed-scrollbar flex min-h-0 min-w-0 flex-col gap-[var(--gap)] overflow-y-auto overflow-x-hidden pr-1">
        <header>
          <h1 className="text-[length:var(--fs-2xl)] font-semibold tracking-tight text-text">{t('wallet.title')}</h1>
          <p className="text-base text-muted">{t('wallet.subtitle')}</p>
        </header>
        <Balance ref={topUpButton} onTopUp={openTopUp} />
        <Tariffs onInsufficientFunds={openTopUp} />
      </div>

      <History className="min-h-0" />

      <TopUpModal open={topUpOpen} onClose={closeTopUp} />
    </motion.div>
  );
}

export default WalletScreen;
