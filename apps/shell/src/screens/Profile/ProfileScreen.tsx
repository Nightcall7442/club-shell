/**
 * Profile screen: header (avatar, inline-editable display name, role, member since, loyalty level/points, balance)
 * and four tabs — Stats, Achievements, Loyalty, Settings. Stats/achievements/loyalty are fetched here once and
 * handed to the panels as props; the active tab is mirrored in `?tab=` so `/profile?tab=settings` deep-links.
 */
import { useCallback, useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import type { Achievement, Loyalty as LoyaltyInfo, User, UserRole, UserStats } from '@clubshell/contracts';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { Avatar } from '@/components/ui/Avatar';
import { Badge, type BadgeTone } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Skeleton } from '@/components/ui/Skeleton';
import { Tabs, type TabItem } from '@/components/ui/Tabs';
import { focusElement, useGamepad } from '@/hooks/useGamepad';
import { useLocale } from '@/hooks/useLocale';
import { formatDate, formatMoney, formatNumber } from '@/lib/format';
import { api } from '@/lib/tauri';
import { useAuthStore, useNotificationsStore, useSettingsStore, useThemeStore } from '@/store';
import Achievements from './Achievements';
import Loyalty, { displayLevel } from './Loyalty';
import Settings, { nudgeRange } from './Settings';
import Stats from './Stats';

export type ProfileTab = 'stats' | 'achievements' | 'loyalty' | 'settings';

export const PROFILE_TABS: readonly ProfileTab[] = ['stats', 'achievements', 'loyalty', 'settings'];

const isProfileTab = (v: string | null): v is ProfileTab => PROFILE_TABS.includes(v as ProfileTab);

const ROLE_TONE: Record<UserRole, BadgeTone> = { guest: 'muted', member: 'primary', vip: 'accent', admin: 'danger' };

// ---------------------------------------------------------------------------------------------------------------------
// Data
// ---------------------------------------------------------------------------------------------------------------------

export interface ProfileData {
  stats: UserStats | null;
  achievements: Achievement[] | null;
  loyalty: LoyaltyInfo | null;
  loading: boolean;
  reload: () => void;
}

/** Loads stats, achievements and loyalty in parallel for `userId`; each part fails alone (one toast). */
export function useProfileData(userId: string | null): ProfileData {
  const { t } = useTranslation();
  const pushError = useNotificationsStore((s) => s.pushError);
  const [data, setData] = useState<Pick<ProfileData, 'stats' | 'achievements' | 'loyalty'>>({
    stats: null,
    achievements: null,
    loyalty: null,
  });
  const [loading, setLoading] = useState(userId !== null);
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    if (!userId) {
      setData({ stats: null, achievements: null, loyalty: null });
      setLoading(false);
      return undefined;
    }
    let active = true;
    setLoading(true);
    void Promise.allSettled([api.profile.stats(), api.profile.achievements(), api.profile.loyalty()]).then(
      ([stats, achievements, loyalty]) => {
        if (!active) {
          return;
        }
        setData({
          stats: stats.status === 'fulfilled' ? stats.value : null,
          achievements: achievements.status === 'fulfilled' ? achievements.value : null,
          loyalty: loyalty.status === 'fulfilled' ? loyalty.value : null,
        });
        setLoading(false);
        const failed = [stats, achievements, loyalty].find((r) => r.status === 'rejected');
        if (failed?.status === 'rejected') {
          pushError(failed.reason, t('profile.title'));
        }
      },
    );
    return () => {
      active = false;
    };
  }, [userId, generation, pushError, t]);

  const reload = useCallback(() => setGeneration((g) => g + 1), []);
  return { ...data, loading, reload };
}

// ---------------------------------------------------------------------------------------------------------------------
// Header
// ---------------------------------------------------------------------------------------------------------------------

function PencilIcon(): JSX.Element {
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
      <path d="M4 20h4l10.5-10.5a2.1 2.1 0 0 0-3-3L5 17z" />
      <path d="M13.5 6.5l3 3" />
    </svg>
  );
}

interface HeaderStatProps {
  label: string;
  value: string;
  accent?: boolean;
}

function HeaderStat({ label, value, accent = false }: HeaderStatProps): JSX.Element {
  return (
    <div className="min-w-[7rem] rounded-lg bg-surface/40 px-4 py-3">
      <p className="text-xs uppercase tracking-wide text-muted">{label}</p>
      <p className={accent ? 'tnum text-2xl font-bold text-accent' : 'tnum text-2xl font-bold'}>{value}</p>
    </div>
  );
}

const DISPLAY_NAME_MAX = 32;

export interface ProfileHeaderProps {
  user: User;
  loyalty: LoyaltyInfo | null;
}

/** Avatar, editable display name, role badge, member-since line and the loyalty/balance tiles. */
export function ProfileHeader({ user, loyalty }: ProfileHeaderProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const updateUser = useAuthStore((s) => s.updateUser);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(user.displayName);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const editRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (editing) {
      inputRef.current?.focus();
      inputRef.current?.select();
    }
  }, [editing]);

  const startEdit = (): void => {
    setName(user.displayName);
    setError(null);
    setEditing(true);
  };

  const cancel = (): void => {
    setEditing(false);
    requestAnimationFrame(() => editRef.current?.focus());
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>): void => {
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      cancel();
    }
  };

  const submit = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    const trimmed = name.trim();
    if (trimmed.length === 0) {
      setError(t('profile.displayNameRequired'));
      return;
    }
    if (trimmed.length > DISPLAY_NAME_MAX) {
      setError(t('profile.displayNameTooLong'));
      return;
    }
    if (trimmed === user.displayName) {
      cancel();
      return;
    }
    setSaving(true);
    try {
      const updated = await api.profile.update({ displayName: trimmed });
      updateUser({ displayName: updated.displayName });
      push({ title: t('profile.saved'), level: 'success', ttlSec: 4 });
      cancel();
    } catch (err) {
      pushError(err, t('profile.edit'));
    } finally {
      setSaving(false);
    }
  };

  const level = loyalty?.level ?? user.loyaltyLevel;
  const points = loyalty?.points ?? user.loyaltyPoints;
  const isGuest = user.role === 'guest';

  return (
    <section
      className="glass flex flex-wrap items-center gap-[var(--gap)] rounded-xl p-[var(--gap)]"
      aria-label={t('profile.title')}
    >
      <Avatar
        name={user.displayName}
        src={user.avatarUrl}
        size="xl"
        ring={user.role === 'vip' || user.role === 'admin'}
      />
      <div className="min-w-0 flex-1">
        {editing ? (
          <form onSubmit={(e) => void submit(e)} className="flex flex-wrap items-end gap-3" noValidate>
            <Input
              ref={inputRef}
              label={t('profile.displayName')}
              value={name}
              size="lg"
              maxLength={DISPLAY_NAME_MAX}
              autoComplete="off"
              error={error}
              onChange={(e) => {
                setName(e.target.value);
                setError(null);
              }}
              onKeyDown={onKeyDown}
              wrapperClassName="max-w-md"
            />
            <Button type="submit" size="lg" loading={saving}>
              {t('profile.save')}
            </Button>
            <Button variant="ghost" size="lg" onClick={cancel} disabled={saving}>
              {t('common.cancel')}
            </Button>
          </form>
        ) : (
          <div className="flex flex-wrap items-center gap-3">
            <h1 className="truncate text-3xl font-bold leading-tight">{user.displayName}</h1>
            <Button
              ref={editRef}
              variant="ghost"
              iconOnly
              aria-label={t('profile.edit')}
              onClick={startEdit}
              icon={<PencilIcon />}
            />
            <Badge tone={ROLE_TONE[user.role]} size="lg">
              {t(`profile.role.${user.role}`)}
            </Badge>
          </div>
        )}
        <p className="mt-1 truncate text-base text-muted">
          @{user.username} · {t('profile.memberSince', { date: formatDate(user.createdAt, locale) })}
        </p>
        {isGuest && <p className="mt-1 text-base text-accent">{t('profile.guestHint')}</p>}
      </div>
      <div className="flex flex-wrap gap-3">
        <HeaderStat label={t('profile.loyaltyLevel')} value={t('profile.level', { level: displayLevel(level) })} />
        <HeaderStat label={t('profile.points')} value={formatNumber(points, locale)} accent />
        <HeaderStat label={t('profile.balance')} value={formatMoney(user.balance, locale)} />
      </div>
    </section>
  );
}

function HeaderSkeleton(): JSX.Element {
  return (
    <div className="glass flex items-center gap-[var(--gap)] rounded-xl p-[var(--gap)]" aria-hidden="true">
      <Skeleton variant="circle" width="6rem" height="6rem" />
      <div className="flex-1">
        <Skeleton variant="text" width="40%" className="h-8" />
        <Skeleton variant="text" width="25%" className="mt-3" />
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Screen
// ---------------------------------------------------------------------------------------------------------------------

export function ProfileScreen(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const user = useAuthStore((s) => s.user);
  const animations = useThemeStore((s) => s.theme.animations);
  const defaultRoute = useSettingsStore((s) => s.shellConfig?.ui.defaultRoute ?? '/home');
  const data = useProfileData(user?.id ?? null);

  const tabParam = params.get('tab');
  const tab: ProfileTab = isProfileTab(tabParam) ? tabParam : 'stats';

  const setTab = useCallback(
    (next: ProfileTab) => {
      setParams(next === 'stats' ? {} : { tab: next }, { replace: true });
    },
    [setParams],
  );

  const stepTab = useCallback(
    (delta: number) => {
      const idx = PROFILE_TABS.indexOf(tab);
      const next = PROFILE_TABS[(idx + delta + PROFILE_TABS.length) % PROFILE_TABS.length];
      if (next) {
        setTab(next);
      }
    },
    [tab, setTab],
  );

  useGamepad({
    onBack: () => {
      if (document.documentElement.dataset['modalOpen'] !== 'true') {
        navigate(defaultRoute);
      }
    },
    onTab: (dir) => stepTab(dir === 'next' ? 1 : -1),
    onNavigate: (dir) => {
      const el = document.activeElement;
      if (el instanceof HTMLInputElement && el.type === 'range' && (dir === 'left' || dir === 'right')) {
        nudgeRange(el, dir === 'right' ? 5 : -5);
        return true;
      }
      return false;
    },
  });

  // Initial focus lands on the active tab so keyboard/gamepad users have a starting point.
  useEffect(() => {
    const el = document.getElementById(`profile-tab-${tab}`);
    if (el) {
      focusElement(el);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const items: TabItem<ProfileTab>[] = [
    { key: 'stats', label: t('profile.stats') },
    { key: 'achievements', label: t('profile.achievements') },
    { key: 'loyalty', label: t('profile.loyalty') },
    { key: 'settings', label: t('profile.settings') },
  ];

  const duration = animations ? 0.2 : 0;

  let panel: JSX.Element;
  switch (tab) {
    case 'achievements':
      panel = <Achievements items={data.achievements} loading={data.loading} />;
      break;
    case 'loyalty':
      panel = <Loyalty loyalty={data.loyalty} loading={data.loading} />;
      break;
    case 'settings':
      panel = <Settings />;
      break;
    default:
      panel = <Stats stats={data.stats} loading={data.loading} />;
  }

  return (
    <motion.div
      className="mx-auto flex w-full max-w-[110rem] flex-col gap-[var(--gap)] pb-[var(--gap)]"
      initial={{ opacity: 0, y: 12 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration, ease: 'easeOut' }}
    >
      {user ? <ProfileHeader user={user} loyalty={data.loyalty} /> : <HeaderSkeleton />}

      <Tabs<ProfileTab>
        items={items}
        value={tab}
        onChange={setTab}
        label={t('profile.subtitle')}
        size="lg"
        gamepad={false}
        idPrefix="profile"
        className="self-start"
      />

      <div id={`profile-panel-${tab}`} role="tabpanel" aria-labelledby={`profile-tab-${tab}`} className="min-h-[20rem]">
        <AnimatePresence mode="wait" initial={false}>
          <motion.div
            key={tab}
            initial={{ opacity: 0, y: 10 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: -6 }}
            transition={{ duration, ease: 'easeOut' }}
          >
            {panel}
          </motion.div>
        </AnimatePresence>
      </div>
    </motion.div>
  );
}

export default ProfileScreen;
