/**
 * The club's mark on the lock and idle screens: the logo, the club name and the PC label (D-95), laid out the way the
 * owner set them in the designer (D-104, `shell.club.look`): shown or not, the logo's size, and aligned for where the
 * block stands — left, centred or right, or stacked (logo over the name) in the middle of the screen. Without a look
 * it is exactly today's block.
 */
import clsx from 'clsx';
import { ClubMark } from '@/components/brand/ClubMark';
import { PcLabel, usePcLabel } from '@/components/brand/PcLabel';
import { useClub } from '@/hooks/useClub';
import { logoBox, nameClass, type LogoPosition, type ScreenLayout } from '@/lib/look';

export interface ClubBrandProps {
  screen: 'lock' | 'idle';
  logo: ScreenLayout['logo'];
  className?: string;
}

/** How the block lines up for a position: the left column, the middle or the right one. */
function alignOf(position: LogoPosition): 'start' | 'center' | 'end' {
  if (position === 'topCenter' || position === 'center') return 'center';
  if (position === 'topRight' || position === 'bottomRight') return 'end';
  return 'start';
}

export function ClubBrand({ screen, logo, className }: ClubBrandProps): JSX.Element | null {
  const club = useClub();
  const label = usePcLabel(screen);
  if (!logo.show && !logo.name && !label.text) {
    return null;
  }
  const align = alignOf(logo.position);
  const stacked = logo.position === 'center';
  // Today's sizes stay class-based (exactly as before); the owner's other sizes come as CSS.
  const mark =
    logo.size === 'm' ? (
      <ClubMark className={screen === 'lock' ? 'h-7 w-7' : 'h-8 w-8'} />
    ) : (
      // In the middle the logo also yields to the screen height: the sign-in card or the clock keeps its room.
      <ClubMark
        size={
          stacked ? `min(${logoBox(screen, logo.size)}, ${screen === 'lock' ? 7 : 8}vh)` : logoBox(screen, logo.size)
        }
      />
    );
  const name = logo.name && (
    <p
      className={clsx(
        'truncate font-display font-light tracking-tight text-text',
        nameClass(screen, logo.size),
        stacked && 'max-w-full text-center',
      )}
    >
      {club.name}
    </p>
  );
  const labelAlign = clsx(
    align === 'center' && 'justify-center text-center',
    align === 'end' && 'justify-end text-right',
  );
  const labelClass = clsx('mt-2', labelAlign);

  const row = stacked ? (
    <div
      className={clsx(
        'flex min-w-0 max-w-full flex-col items-center gap-3',
        // A short screen (1366×768) sets the logo beside the name, so the card or the clock below keeps its height.
        '[@media(max-height:900px)]:flex-row [@media(max-height:900px)]:gap-4',
      )}
      data-testid="club-brand-row"
    >
      {logo.show && mark}
      {name}
    </div>
  ) : (
    (logo.show || name) && (
      <div
        className={clsx(
          'flex min-w-0 items-center',
          screen === 'lock' ? 'gap-3' : 'gap-4',
          align === 'center' && 'justify-center',
          align === 'end' && 'justify-end',
        )}
        data-testid="club-brand-row"
      >
        {logo.show && mark}
        {screen === 'idle' ? (
          <div className="min-w-0">
            {name}
            <PcLabel place="idle" className={labelClass} />
          </div>
        ) : (
          name
        )}
      </div>
    )
  );

  if (screen === 'idle') {
    return (
      <div
        data-testid="club-brand"
        data-position={logo.position}
        data-size={logo.size}
        className={clsx(
          'flex min-w-0 max-w-full items-center gap-4 rounded-2xl bg-bg/55 backdrop-blur-md',
          stacked ? 'flex-col px-6 py-4' : clsx('py-2 pr-5', logo.show ? 'pl-3' : 'pl-5'),
          className,
        )}
      >
        {row}
        {/* Without the row the label still needs its place; stacked, it goes under the name. */}
        {(stacked || (!logo.show && !name)) && (
          <PcLabel place="idle" className={clsx(labelAlign, !stacked ? 'mt-0' : 'mt-0.5')} />
        )}
      </div>
    );
  }

  return (
    <div
      data-testid="club-brand"
      data-position={logo.position}
      data-size={logo.size}
      className={clsx('min-w-0', align === 'center' && 'text-center', align === 'end' && 'text-right', className)}
    >
      {row}
      <PcLabel place="lock" className={clsx(labelAlign, row ? 'mt-2' : 'mt-0')} />
    </div>
  );
}
