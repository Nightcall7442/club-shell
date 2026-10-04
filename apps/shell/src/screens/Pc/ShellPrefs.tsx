/**
 * The Shell's own preferences in the PC block on Home: when an idle screen locks itself, and the theme (live swatches).
 * Both are saved on this PC (`settings_set`) and stay for the next player, as they did in Profile; errors surface as
 * toasts.
 */
import { useEffect, useState } from 'react';
import type { Theme } from '@clubshell/contracts';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { OptionGroup, SettingsSection } from '@/components/settings/SettingsSection';
import { useNotificationsStore, useSettingsStore, useThemeStore } from '@/store';
import { builtinThemes, loadTheme } from '@/theme/themes';

// ---------------------------------------------------------------------------------------------------------------------
// Idle lock
// ---------------------------------------------------------------------------------------------------------------------

const IDLE_OPTIONS_SEC: readonly number[] = [0, 60, 180, 300, 600, 900, 1800];

export function IdleLockCard(): JSX.Element {
  const { t } = useTranslation();
  const idleTimeoutSec = useSettingsStore((s) => s.settings.idleTimeoutSec);
  const setIdleTimeout = useSettingsStore((s) => s.setIdleTimeout);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [busy, setBusy] = useState(false);

  const change = async (sec: number): Promise<void> => {
    setBusy(true);
    try {
      await setIdleTimeout(sec);
    } catch (e) {
      pushError(e, t('settings.idleTimeout'));
    } finally {
      setBusy(false);
    }
  };

  // The club may have set a value that is not on the list: it is offered as it is.
  const options = Array.from(new Set([...IDLE_OPTIONS_SEC, idleTimeoutSec]))
    .sort((a, b) => a - b)
    .map((sec) => ({
      key: String(sec),
      label: sec === 0 ? t('settings.idleNever') : t('settings.idleTimeoutValue', { minutes: Math.round(sec / 60) }),
    }));

  return (
    <SettingsSection title={t('settings.idleTimeout')}>
      <OptionGroup
        label={t('settings.idleTimeout')}
        options={options}
        value={String(idleTimeoutSec)}
        onChange={(v) => void change(Number(v))}
        disabled={busy}
      />
    </SettingsSection>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Theme
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
    // Fills whatever width the card gets: one column of the home block's three.
    <div className="grid grid-cols-[repeat(auto-fill,minmax(9rem,1fr))] gap-3">
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

export function ThemeCard(): JSX.Element {
  const { t } = useTranslation();
  return (
    <SettingsSection title={t('settings.theme')}>
      <ThemePicker />
    </SettingsSection>
  );
}
