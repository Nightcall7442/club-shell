import {
  forwardRef,
  useCallback,
  useImperativeHandle,
  useRef,
  useState,
  type FocusEvent,
  type FormEvent,
  type KeyboardEvent,
} from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { ChatRooms } from '@clubshell/contracts';
import { Button } from '@/components/ui/Button';
import { insertText, useVirtualKeyboard } from '@/components/ui/VirtualKeyboard';
import { useSettingsStore } from '@/store/settings';

export interface ComposerProps {
  /** Resolves when the message is accepted; rejections keep the draft so the user can retry. */
  onSend: (text: string) => Promise<void>;
  disabled?: boolean;
  /** Defaults to `ChatRooms.MaxTextLength` (2000). */
  maxLength?: number;
  className?: string;
}

export interface ComposerHandle {
  focus: () => void;
}

const QUICK_KEYS = ['help', 'order', 'tech', 'thanks'] as const;
const EMOJI = ['👍', '🙏', '😀', '🔥', '🎮', '❓', '✅', '⏳'] as const;
const MAX_ROWS = 5;

function SendIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M22 2 11 13" />
      <path d="m22 2-7 20-4-9-9-4 20-7z" />
    </svg>
  );
}

/** Multiline composer: Enter sends, Shift+Enter inserts a newline, quick replies and emoji insert at the caret. */
export const Composer = forwardRef<ComposerHandle, ComposerProps>(function Composer(
  { onSend, disabled = false, maxLength = ChatRooms.MaxTextLength, className },
  ref,
) {
  const { t } = useTranslation();
  const [text, setText] = useState('');
  const [sending, setSending] = useState(false);
  const [failed, setFailed] = useState(false);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  const vkAllowed = useSettingsStore((s) => s.settings.allowVirtualKeyboard);

  useImperativeHandle(ref, () => ({ focus: () => textareaRef.current?.focus() }), []);

  const trimmed = text.trim();
  const canSend = !disabled && !sending && trimmed.length > 0 && trimmed.length <= maxLength;
  const left = maxLength - text.length;

  const autoGrow = useCallback((el: HTMLTextAreaElement) => {
    el.style.height = 'auto';
    const line = parseFloat(getComputedStyle(el).lineHeight) || 28;
    el.style.height = `${Math.min(el.scrollHeight, line * MAX_ROWS + 24)}px`;
  }, []);

  const submit = async (): Promise<void> => {
    if (!canSend) {
      return;
    }
    setSending(true);
    setFailed(false);
    try {
      await onSend(trimmed);
      setText('');
      const el = textareaRef.current;
      if (el) {
        el.style.height = 'auto';
        el.focus();
      }
    } catch {
      setFailed(true);
    } finally {
      setSending(false);
    }
  };

  const onSubmit = (e: FormEvent): void => {
    e.preventDefault();
    void submit();
  };

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>): void => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      void submit();
    }
  };

  const onFocus = (e: FocusEvent<HTMLTextAreaElement>): void => {
    if (vkAllowed) {
      useVirtualKeyboard.getState().open(e.currentTarget);
    }
  };

  const onBlur = (e: FocusEvent<HTMLTextAreaElement>): void => {
    const vk = useVirtualKeyboard.getState();
    if (vk.target === e.currentTarget) {
      vk.close();
    }
  };

  const insert = (value: string): void => {
    const el = textareaRef.current;
    if (!el) {
      setText((v) => (v + value).slice(0, maxLength));
      return;
    }
    el.focus();
    insertText(el, value);
  };

  return (
    <form onSubmit={onSubmit} className={clsx('flex flex-col gap-2', className)}>
      <div
        className="no-scrollbar flex items-center gap-2 overflow-x-auto"
        role="group"
        aria-label={t('chat.quickReplies')}
      >
        {QUICK_KEYS.map((k) => (
          <button
            key={k}
            type="button"
            data-nav="true"
            disabled={disabled}
            onClick={() => insert(t(`chat.quick.${k}`))}
            className="focus-ring glass h-10 shrink-0 rounded-full px-4 text-sm font-medium text-text hover:bg-surface/80 disabled:opacity-50"
          >
            {t(`chat.quick.${k}`)}
          </button>
        ))}
        <span className="mx-1 h-6 w-px shrink-0 bg-text/15" aria-hidden="true" />
        <span className="sr-only">{t('chat.emoji')}</span>
        {EMOJI.map((e) => (
          <button
            key={e}
            type="button"
            data-nav="true"
            disabled={disabled}
            aria-label={`${t('chat.emoji')} ${e}`}
            onClick={() => insert(e)}
            className="focus-ring h-10 w-10 shrink-0 rounded-full text-xl leading-none hover:bg-text/10 disabled:opacity-50"
          >
            {e}
          </button>
        ))}
      </div>
      <div
        className={clsx(
          'glass flex items-end gap-2 rounded-xl p-2 pl-4 transition-[box-shadow,border-color] duration-[var(--dur-fast)]',
          'focus-within:border-primary/60 focus-within:shadow-[var(--shadow-glow)]',
          failed && 'border-danger/70',
        )}
      >
        <textarea
          ref={textareaRef}
          data-nav="true"
          rows={1}
          value={text}
          maxLength={maxLength}
          disabled={disabled}
          placeholder={t('chat.placeholder')}
          aria-label={t('chat.placeholder')}
          aria-invalid={failed || undefined}
          onChange={(e) => {
            setText(e.target.value);
            setFailed(false);
            autoGrow(e.target);
          }}
          onKeyDown={onKeyDown}
          onFocus={onFocus}
          onBlur={onBlur}
          className="max-h-[11rem] min-h-[2.75rem] flex-1 resize-none select-text bg-transparent py-2 text-base leading-7 text-text outline-none placeholder:text-muted/70 disabled:cursor-not-allowed"
        />
        <Button
          type="submit"
          size="lg"
          loading={sending}
          disabled={!canSend}
          icon={<SendIcon />}
          aria-label={t('chat.send')}
        >
          {t('chat.send')}
        </Button>
      </div>
      <div className="flex min-h-[1.25rem] items-center justify-between px-1 text-sm">
        <span role="alert" className={clsx('text-danger', !failed && 'invisible')}>
          {t('chat.failed')}
        </span>
        <span
          className={clsx('tnum text-muted', left < 0 && 'text-danger', left > maxLength / 10 && 'invisible')}
          aria-live="polite"
        >
          {left < 0 ? t('chat.maxLength', { max: maxLength }) : t('chat.charsLeft', { count: left })}
        </span>
      </div>
    </form>
  );
});
