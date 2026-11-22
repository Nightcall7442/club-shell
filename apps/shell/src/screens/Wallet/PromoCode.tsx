/**
 * «Промокод» on the wallet (D-88): the member types the club's code and the bonus lands on the balance at once — the
 * same codes the desk redeems, the same route the club's mobile app uses. Each refusal says what to do next: an
 * unknown or mistyped code, one that expired or was used up (or already used by this member), too many tries.
 */
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { useLocale } from '@/hooks/useLocale';
import { formatMoney } from '@/lib/format';
import { api, isShellApiError } from '@/lib/tauri';
import { useAuthStore } from '@/store/auth';
import { describeError, useNotificationsStore } from '@/store/notifications';

/** The message under the field for a refusal of `wallet.redeemPromo`. */
function refusal(e: unknown, t: (key: string) => string): string {
  if (!isShellApiError(e)) return describeError(e);
  const details = (e.details ?? {}) as { reason?: unknown };
  const reason = typeof details.reason === 'string' ? details.reason : null;
  switch (e.code) {
    case 'notFound':
      return t('wallet.promo.unknown');
    case 'rateLimited':
      return t('wallet.promo.tooMany');
    case 'forbidden':
      return t('wallet.promo.guest');
    case 'validation':
      if (reason === 'expired') return t('wallet.promo.expired');
      if (reason === 'exhausted') return t('wallet.promo.used');
      if (reason === 'discountAtCheckout') return t('wallet.promo.atDesk');
      return t('wallet.promo.unknown');
    default:
      return describeError(e);
  }
}

export function PromoCode(): JSX.Element | null {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const user = useAuthStore((s) => s.user);
  const push = useNotificationsStore((s) => s.push);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  // A guest seated by the desk has no account to credit.
  if (!user || user.role === 'guest') return null;

  const submit = async (e: FormEvent): Promise<void> => {
    e.preventDefault();
    const typed = code.trim();
    if (!typed || busy) return;
    setBusy(true);
    setError(null);
    setDone(null);
    try {
      const r = await api.wallet.redeemPromo(typed);
      const bonus = formatMoney(r.bonus, locale);
      const text = t('wallet.promo.done', { bonus, code: r.code });
      setDone(text);
      setCode('');
      push({ title: t('wallet.promo.doneTitle'), body: text, level: 'success', ttlSec: 5 });
    } catch (err) {
      setError(refusal(err, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-labelledby="promo-title" className="glass shrink-0 rounded-xl p-6">
      <h2 id="promo-title" className="text-[length:var(--fs-lg)] font-semibold text-text">
        {t('wallet.promo.title')}
      </h2>
      <p className="mt-1 text-sm text-muted">{t('wallet.promo.hint')}</p>
      <form onSubmit={(e) => void submit(e)} className="mt-4 flex items-start gap-3">
        <Input
          aria-label={t('wallet.promo.field')}
          value={code}
          onChange={(e) => {
            setCode(e.target.value.toUpperCase());
            setError(null);
          }}
          maxLength={32}
          autoComplete="off"
          spellCheck={false}
          placeholder={t('wallet.promo.placeholder')}
          error={error}
          hint={done}
          wrapperClassName="min-w-0 flex-1"
          className="font-mono uppercase tracking-[0.08em]"
          data-nav
        />
        <Button type="submit" variant="primary" disabled={busy || code.trim() === ''} loading={busy} data-nav>
          {t('wallet.promo.apply')}
        </Button>
      </form>
    </section>
  );
}
