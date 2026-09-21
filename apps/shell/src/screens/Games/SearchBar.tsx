/**
 * Debounced library search. Escape clears the field (or leaves it when already empty) without bubbling to the
 * screen's back handler; Enter flushes the pending value and hands focus to the results via `onSubmit`.
 */
import { forwardRef, useCallback, useEffect, useRef, useState, type KeyboardEvent } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { Input } from '@/components/ui/Input';

export interface SearchBarProps {
  value: string;
  onChange: (value: string) => void;
  /** Debounce (default 200 ms). */
  delayMs?: number;
  placeholder?: string;
  /** Enter (after flushing the debounce). */
  onSubmit?: (value: string) => void;
  autoFocus?: boolean;
  className?: string;
}

function SearchIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <circle cx="11" cy="11" r="7" />
      <path d="M20 20l-3.5-3.5" />
    </svg>
  );
}

function ClearIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      className="h-5 w-5"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      aria-hidden="true"
    >
      <path d="M6 6l12 12M18 6L6 18" />
    </svg>
  );
}

export const SearchBar = forwardRef<HTMLInputElement, SearchBarProps>(function SearchBar(
  { value, onChange, delayMs = 200, placeholder, onSubmit, autoFocus = false, className },
  ref,
) {
  const { t } = useTranslation();
  const [text, setText] = useState(value);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const latest = useRef({ text, onChange });
  latest.current = { text, onChange };
  /** Last value handed to `onChange`; any other prop value is an external change (e.g. "clear filters"). */
  const emitted = useRef(value);

  useEffect(() => {
    if (value !== emitted.current) {
      emitted.current = value;
      setText(value);
    }
  }, [value]);

  const cancelPending = useCallback((): void => {
    if (timer.current !== null) {
      clearTimeout(timer.current);
      timer.current = null;
    }
  }, []);

  useEffect(() => cancelPending, [cancelPending]);

  const emit = useCallback(
    (next: string): void => {
      cancelPending();
      emitted.current = next;
      latest.current.onChange(next);
    },
    [cancelPending],
  );

  const flush = useCallback((): void => emit(latest.current.text), [emit]);

  const update = (next: string): void => {
    setText(next);
    cancelPending();
    timer.current = setTimeout(() => {
      timer.current = null;
      emit(next);
    }, delayMs);
  };

  const clear = (): void => {
    setText('');
    emit('');
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>): void => {
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      if (text.length > 0) {
        clear();
      } else {
        e.currentTarget.blur();
      }
    } else if (e.key === 'Enter') {
      e.preventDefault();
      flush();
      onSubmit?.(text);
    }
  };

  return (
    <div role="search" className={clsx('w-full shrink-0', className)}>
      <Input
        ref={ref}
        type="search"
        inputMode="search"
        autoComplete="off"
        spellCheck={false}
        aria-label={t('games.search')}
        placeholder={placeholder ?? t('games.searchPlaceholder')}
        value={text}
        autoFocus={autoFocus}
        onChange={(e) => update(e.target.value)}
        onKeyDown={onKeyDown}
        leading={<SearchIcon />}
        trailing={
          text.length > 0 ? (
            <button
              type="button"
              data-nav="true"
              aria-label={t('common.clear')}
              // Keep focus in the field: a blur here closes the on-screen keyboard, which reflows the
              // layout between mousedown and mouseup and can swallow the click.
              onMouseDown={(e) => e.preventDefault()}
              onClick={clear}
              className="focus-ring inline-flex h-9 w-9 items-center justify-center rounded-full text-muted hover:bg-text/10 hover:text-text"
            >
              <ClearIcon />
            </button>
          ) : undefined
        }
        className="[&::-webkit-search-cancel-button]:hidden"
      />
    </div>
  );
});

export default SearchBar;
