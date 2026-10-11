import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import clsx from 'clsx';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import type { AuthLoginResponse } from '@clubshell/contracts';
import { ClubMark } from '@/components/brand/ClubMark';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { CARD_KEY, CARD_MAX, readerKey } from '@/lib/cardReader';
import { insertText } from '@/lib/insertText';
import { isTauri, toShellApiError } from '@/lib/tauri';
import { useAuthStore } from '@/store/auth';
import { loginErrorMessage } from './LoginForm';

export interface CardLoginProps {
  onSuccess?: (res: AuthLoginResponse) => void;
  className?: string;
  /** A card the lock screen read while another method was showing (`onCardRead`): signed in with as it arrives. */
  tapped?: { cardId: string } | null;
}

/** `•••• 4567`: only the last four characters ever show, and none of an entry that short. */
function maskCard(id: string): string {
  return id.length > 4 ? `•••• ${id.slice(-4)}` : '••••';
}

/**
 * As for a password, except that a card no account holds reads "not recognised" rather than a wrong login, and the
 * server's lockout of this PC's card sign-in (`attemptsLeft: 0` after 5 wrong cards in 15 minutes) reads "wait".
 */
function cardErrorMessage(e: unknown, t: TFunction): string {
  const err = toShellApiError(e);
  if (err.code !== 'unauthorized') {
    return loginErrorMessage(err, t);
  }
  const details =
    typeof err.details === 'object' && err.details !== null ? (err.details as Record<string, unknown>) : {};
  return details['attemptsLeft'] === 0 ? t('lock.tooManyAttempts') : t('lock.cardUnknown');
}

const ContactlessIcon = (
  <svg
    viewBox="0 0 24 24"
    className="h-7 w-7"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M7 9a5 5 0 0 1 0 6M11 6.5a9 9 0 0 1 0 11M15 4a13 13 0 0 1 0 16" />
  </svg>
);

/**
 * Club card sign-in. A USB reader types the number like a keyboard and presses Enter, so the field submits on Enter;
 * a key pressed while no text field has the focus (the method tab, a button) lands in it too, so a card tapped right
 * after choosing the tab still counts. Typing by hand works the same. The number is a password field, shows only its
 * last four characters on the card face, is cleared after every attempt and never goes to the log or analytics.
 */
export function CardLogin({ onSuccess, className, tapped = null }: CardLoginProps): JSX.Element {
  const { t } = useTranslation();
  const login = useAuthStore((s) => s.login);
  const field = useRef<HTMLInputElement>(null);
  const [cardId, setCardId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent): void => {
      const el = field.current;
      if (!el || el.readOnly || e.defaultPrevented || e.ctrlKey || e.altKey || e.metaKey || !CARD_KEY.test(e.key)) {
        return;
      }
      const target = e.target instanceof HTMLElement ? e.target : null;
      // Someone signed in (the tariff picker is over this form), another field or a dialog has the keys: not ours.
      if (
        useAuthStore.getState().user !== null ||
        target?.closest('input, textarea, select, [contenteditable], [role="dialog"]')
      ) {
        return;
      }
      e.preventDefault();
      el.focus();
      insertText(el, readerKey(e));
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>): void => {
    const key = readerKey(e);
    if (key !== e.key && !e.ctrlKey && !e.altKey && !e.metaKey) {
      e.preventDefault();
      insertText(e.currentTarget, key);
    }
  };

  const signIn = async (id: string): Promise<void> => {
    setError(null);
    setBusy(true);
    try {
      const res = await login('card', { cardId: id });
      onSuccess?.(res);
    } catch (err) {
      setError(cardErrorMessage(err, t));
    } finally {
      // Right or wrong, the number does not stay on screen; the field keeps the focus for the next tap.
      setCardId('');
      setBusy(false);
    }
  };

  // Each tap is a new object, so the same card tapped twice is tried twice; one tap is tried once, even when Strict
  // Mode runs the effect again. The card face shows it masked while it is checked, as a card read here would.
  const signInRef = useRef(signIn);
  signInRef.current = signIn;
  const tried = useRef<object | null>(null);
  useEffect(() => {
    if (tapped && tried.current !== tapped) {
      tried.current = tapped;
      setCardId(tapped.cardId);
      void signInRef.current(tapped.cardId);
    }
  }, [tapped]);

  const submit = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    if (busy) {
      return;
    }
    const id = cardId.trim();
    if (id.length === 0) {
      setError(t('lock.cardRequired'));
      setCardId('');
      return;
    }
    await signIn(id);
  };

  const masked = cardId.trim().length > 0 ? maskCard(cardId.trim()) : null;

  return (
    <form
      onSubmit={(e) => void submit(e)}
      noValidate
      autoComplete="off"
      className={clsx('flex flex-col items-center gap-[clamp(0.75rem,1.9vh,1.25rem)]', className)}
    >
      <p className="text-center text-base text-muted">{t('lock.cardHint')}</p>

      {/* The card face, in the QR code's viewfinder brackets: what the reader took, masked. */}
      <div className="hud-brackets relative [--brk-inset:-10px] [--brk-size:20px]">
        <div
          aria-hidden="true"
          className="glass flex aspect-[1.586] w-[clamp(14rem,30vh,18rem)] flex-col justify-between rounded-xl p-[clamp(0.875rem,2vh,1.25rem)] shadow-[0_0_56px_-16px_rgb(var(--c-accent)/0.55)]"
        >
          <div className="flex items-start justify-between gap-3">
            <div className="flex min-w-0 items-center gap-3">
              <ClubMark className="h-4 w-4" />
              <span className="hud-label truncate">{t('lock.card')}</span>
            </div>
            <span className={clsx('transition-colors', busy ? 'text-accent' : 'text-muted')}>{ContactlessIcon}</span>
          </div>
          <p
            className={clsx(
              'tnum font-mono text-[clamp(1.25rem,2.6vh,1.625rem)] tracking-[0.18em]',
              masked ? 'text-text' : 'text-muted/50',
            )}
          >
            {masked ?? '•••• ••••'}
          </p>
        </div>
      </div>

      <Input
        ref={field}
        name="cardId"
        size="lg"
        type="password"
        autoComplete="off"
        autoCapitalize="none"
        spellCheck={false}
        maxLength={CARD_MAX}
        label={t('lock.cardNumber')}
        placeholder={t('lock.cardPlaceholder')}
        value={cardId}
        onChange={(e) => setCardId(e.target.value.slice(0, CARD_MAX))}
        onKeyDown={onKeyDown}
        // Read-only rather than disabled while checking: the field keeps the focus, so the next card goes straight in.
        readOnly={busy}
        // Under the field, as when unlocking: a separate box would push the button off a 768-px-high card.
        error={error}
        hint={isTauri() ? undefined : t('lock.cardDemoHint')}
        wrapperClassName="text-left"
      />
      <Button type="submit" variant="cta" size="xl" block loading={busy}>
        {busy ? t('lock.loggingIn') : t('lock.login')}
      </Button>
    </form>
  );
}

export default CardLogin;
