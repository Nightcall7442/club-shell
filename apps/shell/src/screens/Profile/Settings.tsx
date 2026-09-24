/**
 * Settings tab: locale, theme (live swatches), sound, idle lock, UI toggles, user PIN, logout, plus the hidden
 * administrator panel (exit hotkey from shell.json or `kiosk://hotkey exit` → PIN → reboot/shutdown/exit/devtools/reload).
 * Everything user-visible goes through the settings/theme/auth stores; errors surface as toasts.
 */
import { useCallback, useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from 'react';
import type { Locale, SettingsSetRequest, Theme } from '@clubshell/contracts';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { useLocale } from '@/hooks/useLocale';
import { formatTime } from '@/lib/format';
import { api, isTauri, toShellApiError } from '@/lib/tauri';
import { secondsUntil } from '@/lib/time';
import { describeError, useAuthStore, useNotificationsStore, useSettingsStore, useThemeStore } from '@/store';
import { builtinThemes, loadTheme } from '@/theme/themes';

// ---------------------------------------------------------------------------------------------------------------------
// Primitives local to the settings tab
// ---------------------------------------------------------------------------------------------------------------------

export interface SettingsSectionProps {
  title: string;
  description?: string;
  children: ReactNode;
  className?: string;
}

/** Glass card with a heading. */
export function SettingsSection({ title, description, children, className }: SettingsSectionProps): JSX.Element {
  const id = useId();
  return (
    <section aria-labelledby={id} className={clsx('glass flex flex-col gap-4 rounded-xl p-[var(--gap)]', className)}>
      <header>
        <h3 id={id} className="text-xl font-bold">
          {title}
        </h3>
        {description && <p className="text-sm text-muted">{description}</p>}
      </header>
      {children}
    </section>
  );
}

export interface ToggleProps {
  label: string;
  hint?: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}

/** Row-sized switch (`role="switch"`), focusable and gamepad-navigable. */
export function Toggle({ label, hint, checked, onChange, disabled = false }: ToggleProps): JSX.Element {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      data-nav="true"
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className="focus-ring flex w-full items-center justify-between gap-4 rounded-lg px-3 py-3 text-left transition-colors duration-[var(--dur-fast)] hover:bg-text/5 disabled:cursor-not-allowed disabled:opacity-50"
    >
      <span className="min-w-0">
        <span className="block text-base font-medium">{label}</span>
        {hint && <span className="block text-sm text-muted">{hint}</span>}
      </span>
      <span
        aria-hidden="true"
        className={clsx(
          'relative h-8 w-14 shrink-0 rounded-full transition-colors duration-[var(--dur-base)]',
          checked ? 'bg-primary' : 'bg-text/20',
        )}
      >
        <span
          className={clsx(
            'absolute top-1 h-6 w-6 rounded-full bg-white shadow transition-transform duration-[var(--dur-base)]',
            checked ? 'translate-x-7' : 'translate-x-1',
          )}
        />
      </span>
    </button>
  );
}

export interface OptionGroupOption<K extends string> {
  key: K;
  label: ReactNode;
}

export interface OptionGroupProps<K extends string> {
  label: string;
  options: OptionGroupOption<K>[];
  value: K;
  onChange: (key: K) => void;
  disabled?: boolean;
}

/** Pill radio group. */
export function OptionGroup<K extends string>({
  label,
  options,
  value,
  onChange,
  disabled = false,
}: OptionGroupProps<K>): JSX.Element {
  return (
    <div role="radiogroup" aria-label={label} className="flex flex-wrap gap-2">
      {options.map((o) => {
        const active = o.key === value;
        return (
          <button
            key={o.key}
            type="button"
            role="radio"
            aria-checked={active}
            data-nav="true"
            disabled={disabled}
            onClick={() => onChange(o.key)}
            className={clsx(
              'focus-ring inline-flex h-11 items-center rounded-full px-5 text-base font-semibold transition-colors duration-[var(--dur-fast)] disabled:cursor-not-allowed disabled:opacity-50',
              active ? 'bg-primary text-on-primary' : 'bg-text/10 text-text hover:bg-text/15',
            )}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}

/** Moves a range input by `delta` steps (gamepad left/right) and notifies React. */
export function nudgeRange(el: HTMLInputElement, delta: number): void {
  const step = Number(el.step) || 1;
  const min = Number(el.min) || 0;
  const max = Number(el.max) || 100;
  const next = Math.min(max, Math.max(min, Number(el.value) + delta * step));
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
  setter?.call(el, String(next));
  el.dispatchEvent(new Event('input', { bubbles: true }));
  el.dispatchEvent(new Event('change', { bubbles: true }));
}

// ---------------------------------------------------------------------------------------------------------------------
// Theme picker
// ---------------------------------------------------------------------------------------------------------------------

interface ThemeSwatchProps {
  theme: Theme;
  active: boolean;
  disabled: boolean;
  onSelect: () => void;
}

function ThemeSwatch({ theme, active, disabled, onSelect }: ThemeSwatchProps): JSX.Element {
  const c = theme.colors;
  const r = Math.max(2, theme.radius / 2);
  return (
    <button
      type="button"
      data-nav="true"
      aria-pressed={active}
      disabled={disabled}
      onClick={onSelect}
      className={clsx(
        'focus-ring flex flex-col gap-3 rounded-xl border p-3 text-left transition-colors duration-[var(--dur-fast)] disabled:cursor-not-allowed',
        active ? 'border-primary bg-primary/10' : 'border-text/10 bg-surface/40 hover:border-text/25',
      )}
    >
      <span
        aria-hidden="true"
        className="relative block h-28 w-full overflow-hidden"
        style={{ background: c.bg, borderRadius: theme.radius }}
      >
        <span className="absolute left-3 top-3 h-6 w-[45%]" style={{ background: c.surface, borderRadius: r }} />
        <span
          className="absolute left-3 top-12 h-3.5 w-[32%]"
          style={{ background: c.text, opacity: 0.85, borderRadius: 3 }}
        />
        <span
          className="absolute left-3 top-[4.25rem] h-2.5 w-[22%]"
          style={{ background: c.muted, borderRadius: 3 }}
        />
        <span
          className="absolute bottom-3 left-3 h-7 w-[36%]"
          style={{ background: c.primary, borderRadius: r, boxShadow: `0 0 16px ${c.primary}` }}
        />
        <span className="absolute bottom-3 right-3 h-7 w-7 rounded-full" style={{ background: c.accent }} />
        <span className="absolute right-3 top-3 h-3 w-3 rounded-full" style={{ background: c.success }} />
        <span className="absolute right-8 top-3 h-3 w-3 rounded-full" style={{ background: c.danger }} />
      </span>
      <span className="flex items-center justify-between gap-2">
        <span className="truncate font-semibold" style={{ fontFamily: `"${theme.font}", var(--font), system-ui` }}>
          {theme.displayName}
        </span>
        {active && (
          <svg
            viewBox="0 0 24 24"
            className="h-5 w-5 shrink-0 text-primary"
            fill="none"
            stroke="currentColor"
            strokeWidth="2.5"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
          >
            <path d="M5 12.5l4.5 4.5L19 7.5" />
          </svg>
        )}
      </span>
    </button>
  );
}

/** Installed themes as live colour swatches; selecting applies immediately and persists. */
export function ThemePicker(): JSX.Element {
  const { t } = useTranslation();
  const current = useThemeStore((s) => s.theme);
  const names = useThemeStore((s) => s.themes);
  const setTheme = useThemeStore((s) => s.setTheme);
  const reload = useThemeStore((s) => s.load);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [defs, setDefs] = useState<Record<string, Theme>>(() => ({ ...builtinThemes }));
  const [busy, setBusy] = useState<string | null>(null);

  useEffect(() => {
    setDefs((d) => (d[current.name] === current ? d : { ...d, [current.name]: current }));
  }, [current]);

  useEffect(() => {
    let active = true;
    for (const name of names) {
      if (defs[name]) {
        continue;
      }
      void loadTheme(name).then((theme) => {
        if (active) {
          setDefs((d) => (d[name] ? d : { ...d, [name]: theme }));
        }
      });
    }
    return () => {
      active = false;
    };
    // defs is intentionally read once per names change; each fetched theme is merged on arrival.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [names]);

  const select = async (name: string): Promise<void> => {
    if (name === current.name || busy) {
      return;
    }
    const previous = current.name;
    setBusy(name);
    try {
      await setTheme(name);
      push({ title: t('settings.themeApplied'), level: 'success', ttlSec: 3 });
    } catch (e) {
      pushError(e, t('settings.theme'));
      void reload(previous);
    } finally {
      setBusy(null);
    }
  };

  const list = names.length > 0 ? names : Object.keys(defs);

  return (
    <div className="grid grid-cols-2 gap-3 xl:grid-cols-3 2xl:grid-cols-4">
      {list.map((name) => {
        const theme = defs[name];
        if (!theme) {
          return <div key={name} className="anim-skeleton h-40 rounded-xl" aria-hidden="true" />;
        }
        return (
          <ThemeSwatch
            key={name}
            theme={theme}
            active={name === current.name}
            disabled={busy !== null}
            onSelect={() => void select(name)}
          />
        );
      })}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Sound
// ---------------------------------------------------------------------------------------------------------------------

function SpeakerIcon({ muted }: { muted: boolean }): JSX.Element {
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
      <path d="M4 9v6h4l5 4V5L8 9z" />
      {muted ? <path d="M17 9l4 6M21 9l-4 6" /> : <path d="M16 9a4 4 0 0 1 0 6M18.5 6.5a8 8 0 0 1 0 11" />}
    </svg>
  );
}

const VOLUME_COMMIT_MS = 150;

/** Volume slider (debounced `sys_set_volume`) + mute toggle. */
export function VolumeControl(): JSX.Element {
  const { t } = useTranslation();
  const volume = useSettingsStore((s) => s.settings.volume);
  const muted = useSettingsStore((s) => s.settings.muted);
  const setVolume = useSettingsStore((s) => s.setVolume);
  const toggleMute = useSettingsStore((s) => s.toggleMute);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [level, setLevel] = useState(volume);
  const timer = useRef<number | null>(null);

  useEffect(() => setLevel(volume), [volume]);
  useEffect(
    () => () => {
      if (timer.current !== null) {
        window.clearTimeout(timer.current);
      }
    },
    [],
  );

  const onInput = (next: number): void => {
    setLevel(next);
    if (timer.current !== null) {
      window.clearTimeout(timer.current);
    }
    timer.current = window.setTimeout(() => {
      timer.current = null;
      setVolume(next, false).catch((e: unknown) => pushError(e, t('settings.volume')));
    }, VOLUME_COMMIT_MS);
  };

  const onMute = (): void => {
    toggleMute().catch((e: unknown) => pushError(e, t('settings.volume')));
  };

  const label = muted ? t('settings.muted') : t('settings.volumeLevel', { level });

  return (
    <div className="flex items-center gap-4">
      <Button
        variant={muted ? 'danger' : 'secondary'}
        iconOnly
        size="lg"
        aria-label={muted ? t('kiosk.unmuted') : t('kiosk.muted')}
        aria-pressed={muted}
        onClick={onMute}
        icon={<SpeakerIcon muted={muted} />}
      />
      <input
        type="range"
        min={0}
        max={100}
        step={1}
        value={level}
        data-nav="true"
        aria-label={t('settings.volume')}
        aria-valuetext={label}
        onChange={(e) => onInput(Number(e.target.value))}
        className={clsx('focus-ring h-3 min-w-0 flex-1 cursor-pointer rounded-full', muted && 'opacity-50')}
        style={{ accentColor: 'rgb(var(--c-primary))' }}
      />
      <span className="tnum w-24 shrink-0 text-right text-base text-muted" aria-live="polite">
        {label}
      </span>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// PIN
// ---------------------------------------------------------------------------------------------------------------------

const PIN_RE = /^\d{4,6}$/;

export interface PinModalProps {
  open: boolean;
  onClose: () => void;
}

/** Sets / changes the session-unlock PIN (`profile_update { pin }`). */
export function PinModal({ open, onClose }: PinModalProps): JSX.Element {
  const { t } = useTranslation();
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const formId = useId();
  const firstRef = useRef<HTMLInputElement>(null);
  const [pin, setPin] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (open) {
      setPin('');
      setConfirm('');
      setError(null);
      setSaving(false);
    }
  }, [open]);

  const submit = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    if (!PIN_RE.test(pin)) {
      setError(t('lock.pinInvalid'));
      return;
    }
    if (pin !== confirm) {
      setError(t('profile.pinMismatch'));
      return;
    }
    setSaving(true);
    try {
      await api.profile.update({ pin });
      push({ title: t('profile.pinSaved'), level: 'success', ttlSec: 4 });
      onClose();
    } catch (err) {
      const shell = toShellApiError(err);
      if (shell.code === 'validation') {
        setError(t('lock.pinInvalid'));
      } else {
        pushError(err, t('profile.changePin'));
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('profile.changePin')}
      description={t('profile.pinHint')}
      size="sm"
      initialFocusRef={firstRef}
      footer={
        <>
          <Button variant="ghost" size="lg" onClick={onClose} disabled={saving}>
            {t('common.cancel')}
          </Button>
          <Button type="submit" form={formId} size="lg" loading={saving}>
            {t('profile.save')}
          </Button>
        </>
      }
    >
      <form id={formId} onSubmit={(e) => void submit(e)} className="flex flex-col gap-4" noValidate>
        <Input
          ref={firstRef}
          label={t('profile.newPin')}
          type="password"
          inputMode="numeric"
          autoComplete="off"
          maxLength={6}
          size="lg"
          value={pin}
          onChange={(e) => {
            setPin(e.target.value.replace(/\D/g, ''));
            setError(null);
          }}
          placeholder={t('lock.pinPlaceholder')}
        />
        <Input
          label={t('profile.confirmPin')}
          type="password"
          inputMode="numeric"
          autoComplete="off"
          maxLength={6}
          size="lg"
          value={confirm}
          onChange={(e) => {
            setConfirm(e.target.value.replace(/\D/g, ''));
            setError(null);
          }}
          placeholder={t('lock.pinPlaceholder')}
          error={error}
        />
      </form>
    </Modal>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Administrator panel (hidden: exit hotkey / kiosk://hotkey exit)
// ---------------------------------------------------------------------------------------------------------------------

export interface AdminPanelProps {
  open: boolean;
  onClose: () => void;
}

interface AdminToken {
  token: string;
  expiresAt: string;
}

type AdminAction = 'reboot' | 'shutdown' | 'explorer' | 'quit' | 'devtools' | 'reload';

const POWER_DELAY_SEC = 30;

/** PIN prompt → admin actions. The token is short-lived; an expired one drops back to the PIN prompt. */
export function AdminPanel({ open, onClose }: AdminPanelProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const setScheduledPower = useNotificationsStore((s) => s.setScheduledPower);
  const devtoolsAllowed = useSettingsStore((s) => s.shellConfig?.devtools ?? s.kiosk?.devtools ?? false);
  const formId = useId();
  const pinRef = useRef<HTMLInputElement>(null);
  const [pin, setPin] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [checking, setChecking] = useState(false);
  const [admin, setAdmin] = useState<AdminToken | null>(null);
  const [busy, setBusy] = useState<AdminAction | null>(null);

  useEffect(() => {
    if (open) {
      setPin('');
      setError(null);
      setChecking(false);
      setBusy(null);
      setAdmin((a) => (a && Date.parse(a.expiresAt) > Date.now() ? a : null));
    }
  }, [open]);

  const unlock = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    if (!PIN_RE.test(pin)) {
      setError(t('lock.pinInvalid'));
      return;
    }
    setChecking(true);
    try {
      const res = await api.system.unlockAdmin(pin);
      setAdmin({ token: res.adminToken, expiresAt: res.expiresAt });
      setPin('');
    } catch (err) {
      const shell = toShellApiError(err);
      setError(shell.code === 'unauthorized' ? t('settings.adminInvalidPin') : describeError(shell));
      setPin('');
    } finally {
      setChecking(false);
    }
  };

  const run = async (action: AdminAction): Promise<void> => {
    if (!admin || busy) {
      return;
    }
    if (Date.parse(admin.expiresAt) <= Date.now()) {
      setAdmin(null);
      return;
    }
    setBusy(action);
    try {
      switch (action) {
        case 'reboot':
        case 'shutdown': {
          const res =
            action === 'reboot'
              ? await api.system.reboot(POWER_DELAY_SEC, 'admin')
              : await api.system.shutdown(POWER_DELAY_SEC, 'admin');
          const seconds = Math.max(0, Math.round(secondsUntil(res.scheduledAt) || POWER_DELAY_SEC));
          setScheduledPower({
            kind: action,
            at: Date.parse(res.scheduledAt) || Date.now() + seconds * 1000,
            message: null,
          });
          push({
            id: action,
            title: action === 'reboot' ? t('admin.rebootScheduled') : t('admin.shutdownScheduled'),
            body: action === 'reboot' ? t('admin.rebootIn', { seconds }) : t('admin.shutdownIn', { seconds }),
            level: 'warning',
            ttlSec: null,
            source: 'system',
          });
          onClose();
          break;
        }
        case 'explorer':
        case 'quit':
          await api.kiosk.exit(admin.token, action);
          onClose();
          break;
        case 'devtools':
          await api.kiosk.openDevtools();
          break;
        case 'reload':
          await api.kiosk.reload();
          break;
      }
    } catch (err) {
      pushError(err, t('settings.adminPanel'));
    } finally {
      setBusy(null);
    }
  };

  const actions: { key: AdminAction; label: string; variant: 'primary' | 'secondary' | 'danger'; hidden?: boolean }[] =
    [
      { key: 'reload', label: t('settings.reload'), variant: 'secondary' },
      { key: 'devtools', label: t('settings.devtools'), variant: 'secondary', hidden: !devtoolsAllowed },
      { key: 'explorer', label: t('settings.exitExplorer'), variant: 'primary' },
      { key: 'quit', label: t('settings.exitQuit'), variant: 'primary' },
      { key: 'reboot', label: t('settings.reboot'), variant: 'danger' },
      { key: 'shutdown', label: t('settings.shutdown'), variant: 'danger' },
    ];

  if (!admin) {
    return (
      <Modal
        open={open}
        onClose={onClose}
        title={t('kiosk.exitTitle')}
        description={t('kiosk.exitHint')}
        size="sm"
        initialFocusRef={pinRef}
        footer={
          <>
            <Button variant="ghost" size="lg" onClick={onClose} disabled={checking}>
              {t('common.cancel')}
            </Button>
            <Button type="submit" form={formId} size="lg" loading={checking}>
              {t('settings.adminUnlock')}
            </Button>
          </>
        }
      >
        <form id={formId} onSubmit={(e) => void unlock(e)} noValidate>
          <Input
            ref={pinRef}
            label={t('settings.adminPin')}
            type="password"
            inputMode="numeric"
            autoComplete="off"
            maxLength={6}
            size="lg"
            value={pin}
            onChange={(e) => {
              setPin(e.target.value.replace(/\D/g, ''));
              setError(null);
            }}
            placeholder={t('lock.pinPlaceholder')}
            error={error}
          />
        </form>
      </Modal>
    );
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('settings.adminPanel')}
      description={t('settings.adminUnlocked', { time: formatTime(admin.expiresAt, locale) })}
      size="md"
      footer={
        <Button variant="ghost" size="lg" onClick={onClose} disabled={busy !== null}>
          {t('common.close')}
        </Button>
      }
    >
      <div className="grid grid-cols-2 gap-3">
        {actions
          .filter((a) => !a.hidden)
          .map((a) => (
            <Button
              key={a.key}
              variant={a.variant}
              size="lg"
              block
              loading={busy === a.key}
              disabled={busy !== null && busy !== a.key}
              onClick={() => void run(a.key)}
            >
              {a.label}
            </Button>
          ))}
      </div>
    </Modal>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Settings tab
// ---------------------------------------------------------------------------------------------------------------------

const IDLE_OPTIONS_SEC: readonly number[] = [0, 60, 180, 300, 600, 900, 1800];

export function Settings(): JSX.Element {
  const { t } = useTranslation();
  const { locale, setLocale, locales, names } = useLocale();
  const navigate = useNavigate();
  const settings = useSettingsStore((s) => s.settings);
  const set = useSettingsStore((s) => s.set);
  const shellConfig = useSettingsStore((s) => s.shellConfig);
  const kiosk = useSettingsStore((s) => s.kiosk);
  const agentConnected = useNotificationsStore((s) => s.agentConnected);
  const pushError = useNotificationsStore((s) => s.pushError);
  const user = useAuthStore((s) => s.user);
  const logout = useAuthStore((s) => s.logout);
  const [busy, setBusy] = useState<string | null>(null);
  const [pinOpen, setPinOpen] = useState(false);
  const [logoutOpen, setLogoutOpen] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);

  const patch = useCallback(
    async (key: string, p: SettingsSetRequest, context: string): Promise<void> => {
      setBusy(key);
      try {
        await set(p);
      } catch (e) {
        pushError(e, context);
      } finally {
        setBusy(null);
      }
    },
    [set, pushError],
  );

  const changeLocale = async (next: Locale): Promise<void> => {
    if (next === locale) {
      return;
    }
    setBusy('locale');
    try {
      await setLocale(next);
    } catch (e) {
      pushError(e, t('settings.language'));
    } finally {
      setBusy(null);
    }
  };

  const confirmLogout = async (): Promise<void> => {
    setLoggingOut(true);
    await logout('user');
    setLoggingOut(false);
    setLogoutOpen(false);
    navigate('/lock', { replace: true });
  };

  const idleOptions = Array.from(new Set([...IDLE_OPTIONS_SEC, settings.idleTimeoutSec]))
    .sort((a, b) => a - b)
    .map((sec) => ({
      key: String(sec),
      label: sec === 0 ? t('settings.idleNever') : t('settings.idleTimeoutValue', { minutes: Math.round(sec / 60) }),
    }));

  const isGuest = user?.role === 'guest';

  return (
    <div className="flex flex-col gap-[var(--gap)]">
      <div className="grid gap-[var(--gap)] xl:grid-cols-2">
        <SettingsSection title={t('settings.language')}>
          <OptionGroup
            label={t('settings.language')}
            options={locales.map((l) => ({ key: l, label: names[l] }))}
            value={locale}
            onChange={(l) => void changeLocale(l)}
            disabled={busy === 'locale'}
          />
        </SettingsSection>

        <SettingsSection title={t('settings.sound')}>
          <VolumeControl />
          <Toggle
            label={t('settings.uiSounds')}
            checked={settings.uiSounds}
            disabled={busy === 'uiSounds'}
            onChange={(v) => void patch('uiSounds', { uiSounds: v }, t('settings.uiSounds'))}
          />
        </SettingsSection>
      </div>

      <SettingsSection title={t('settings.theme')}>
        <ThemePicker />
      </SettingsSection>

      <div className="grid gap-[var(--gap)] xl:grid-cols-2">
        <SettingsSection title={t('settings.idleTimeout')}>
          <OptionGroup
            label={t('settings.idleTimeout')}
            options={idleOptions}
            value={String(settings.idleTimeoutSec)}
            onChange={(v) => void patch('idle', { idleTimeoutSec: Number(v) }, t('settings.idleTimeout'))}
            disabled={busy === 'idle'}
          />
        </SettingsSection>

        <SettingsSection title={t('settings.display')}>
          <Toggle
            label={t('settings.showMetrics')}
            checked={settings.showMetricsOverlay}
            disabled={busy === 'metrics'}
            onChange={(v) => void patch('metrics', { showMetricsOverlay: v }, t('settings.showMetrics'))}
          />
          <Toggle
            label={t('settings.virtualKeyboard')}
            checked={settings.allowVirtualKeyboard}
            disabled={busy === 'vk'}
            onChange={(v) => void patch('vk', { allowVirtualKeyboard: v }, t('settings.virtualKeyboard'))}
          />
        </SettingsSection>
      </div>

      <div className="grid gap-[var(--gap)] xl:grid-cols-2">
        <SettingsSection
          title={t('profile.settings')}
          description={isGuest ? t('profile.guestHint') : t('profile.pinHint')}
        >
          <div className="flex flex-wrap gap-3">
            {!isGuest && (
              <Button variant="secondary" size="lg" onClick={() => setPinOpen(true)}>
                {t('profile.changePin')}
              </Button>
            )}
            <Button variant="danger" size="lg" onClick={() => setLogoutOpen(true)}>
              {t('profile.logout')}
            </Button>
          </div>
        </SettingsSection>

        <SettingsSection title={t('settings.about')}>
          <div className="flex flex-wrap items-center gap-2">
            <Badge tone="neutral" size="lg">
              {t('settings.version', { version: kiosk?.version ?? '—' })}
            </Badge>
            <Badge tone={agentConnected ? 'success' : 'danger'} size="lg" dot>
              {agentConnected ? t('kiosk.agentConnected') : t('kiosk.agentDisconnected')}
            </Badge>
            <Badge tone={kiosk?.gamepadConnected ? 'primary' : 'muted'} size="lg">
              {kiosk?.gamepadConnected ? t('settings.gamepadConnected') : t('settings.gamepadDisconnected')}
            </Badge>
            {kiosk?.dev && (
              <Badge tone="accent" size="lg">
                {t('kiosk.devMode')}
              </Badge>
            )}
            {!isTauri() && (
              <Badge tone="accent" size="lg">
                {t('kiosk.mockMode')}
              </Badge>
            )}
          </div>
        </SettingsSection>
      </div>

      <PinModal open={pinOpen} onClose={() => setPinOpen(false)} />

      <Modal
        open={logoutOpen}
        onClose={() => !loggingOut && setLogoutOpen(false)}
        title={t('profile.logoutTitle')}
        description={t('profile.logoutConfirm')}
        size="sm"
        danger
        footer={
          <>
            <Button variant="ghost" size="lg" onClick={() => setLogoutOpen(false)} disabled={loggingOut}>
              {t('common.cancel')}
            </Button>
            <Button variant="danger" size="lg" loading={loggingOut} onClick={() => void confirmLogout()}>
              {t('profile.logout')}
            </Button>
          </>
        }
      />
    </div>
  );
}

export default Settings;
