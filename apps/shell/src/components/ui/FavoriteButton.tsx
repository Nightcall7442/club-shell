/**
 * Star toggle of a game or an app (D-86): `aria-pressed` while it is starred, saved to the member's account through
 * `settings_set { favorites }`. Rendered only while a member's account preferences apply: a guest and an empty PC
 * have no account to keep favourites on, so they get no star at all rather than one that fails.
 */
import { useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { Button, type ButtonSize } from '@/components/ui/Button';
import { favoriteKey, type FavoriteKind } from '@/lib/favorites';
import { useNotificationsStore } from '@/store/notifications';
import { selectAccountPrefs, selectFavorites, useSettingsStore } from '@/store/settings';

export interface FavoriteButtonProps {
  kind: FavoriteKind;
  id: string;
  /** Title of the game / app, for the accessible name. */
  title: string;
  /** `labeled`: star and text (the details page); `icon`: a round star over a tile. */
  variant?: 'labeled' | 'icon';
  size?: ButtonSize;
  /**
   * Out of the gamepad's and Tab's way (`tabIndex -1`, no `data-nav`): a pointer shortcut over a card whose own page
   * carries the labeled toggle.
   */
  pointerOnly?: boolean;
  className?: string;
}

export function StarIcon({ filled }: { filled: boolean }): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill={filled ? 'currentColor' : 'none'}
      stroke="currentColor"
      strokeWidth="2"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M12 3.6l2.6 5.3 5.8.8-4.2 4.1 1 5.8-5.2-2.7-5.2 2.7 1-5.8-4.2-4.1 5.8-.8z" />
    </svg>
  );
}

export function FavoriteButton({
  kind,
  id,
  title,
  variant = 'icon',
  size = 'md',
  pointerOnly = false,
  className,
}: FavoriteButtonProps): JSX.Element | null {
  const { t } = useTranslation();
  const account = useSettingsStore(selectAccountPrefs);
  const key = favoriteKey(kind, id);
  const starred = useSettingsStore((s) => selectFavorites(s).includes(key));
  const toggle = useSettingsStore((s) => s.toggleFavorite);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [busy, setBusy] = useState(false);

  if (!account) {
    return null;
  }

  const onClick = async (): Promise<void> => {
    setBusy(true);
    try {
      await toggle(key);
    } catch (e) {
      pushError(e, t('favorites.failed'));
    } finally {
      setBusy(false);
    }
  };

  const label = starred ? t('favorites.removeNamed', { title }) : t('favorites.addNamed', { title });

  if (variant === 'labeled') {
    return (
      <Button
        variant="secondary"
        size={size}
        icon={<StarIcon filled={starred} />}
        aria-pressed={starred}
        aria-label={label}
        disabled={busy}
        onClick={(e) => {
          e.stopPropagation();
          void onClick();
        }}
        className={clsx(starred && 'text-accent', className)}
      >
        {starred ? t('favorites.remove') : t('favorites.add')}
      </Button>
    );
  }

  return (
    <button
      type="button"
      aria-pressed={starred}
      aria-label={label}
      title={starred ? t('favorites.remove') : t('favorites.add')}
      disabled={busy}
      data-nav={pointerOnly ? undefined : 'true'}
      tabIndex={pointerOnly ? -1 : undefined}
      data-favorite={starred ? 'on' : 'off'}
      onClick={(e) => {
        e.stopPropagation();
        void onClick();
      }}
      className={clsx(
        'focus-ring inline-flex h-10 w-10 items-center justify-center rounded-full bg-bg/70 backdrop-blur transition-[opacity,color,transform] duration-[var(--dur-fast)] ease-[var(--ease-out)] hover:scale-110 [&>svg]:h-5 [&>svg]:w-5',
        starred ? 'text-accent' : 'text-text/80 hover:text-text',
        className,
      )}
    >
      <StarIcon filled={starred} />
    </button>
  );
}

export default FavoriteButton;
