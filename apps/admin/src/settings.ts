/**
 * Shared state of the club settings document for the owner's pages: load once, edit a draft, save the changed keys.
 * `useClubSettings()` returns the draft, a setter per key, `dirty`, `save()` and `reset()`.
 */
import { useCallback, useEffect, useState } from 'react';
import { clubApi, type ClubSettings } from '@/api';
import { describe } from '@/errors';

export interface ClubSettingsState {
  data: ClubSettings | null;
  draft: ClubSettings | null;
  error: string | null;
  dirty: boolean;
  saving: boolean;
  set: <K extends keyof ClubSettings>(key: K, value: ClubSettings[K]) => void;
  save: () => Promise<boolean>;
  reset: () => void;
  reload: () => Promise<void>;
}

export function useClubSettings(): ClubSettingsState {
  const [data, setData] = useState<ClubSettings | null>(null);
  const [draft, setDraft] = useState<ClubSettings | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const reload = useCallback(async () => {
    try {
      const s = await clubApi.settings();
      setData(s);
      setDraft(s);
      setError(null);
    } catch (e) {
      setError(describe(e));
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const set = useCallback(<K extends keyof ClubSettings>(key: K, value: ClubSettings[K]) => {
    setDraft((d) => (d ? { ...d, [key]: value } : d));
  }, []);

  const changed = (): Partial<ClubSettings> => {
    if (!data || !draft) return {};
    const out: Partial<ClubSettings> = {};
    for (const k of Object.keys(draft) as (keyof ClubSettings)[]) {
      if (JSON.stringify(draft[k]) !== JSON.stringify(data[k])) (out as Record<string, unknown>)[k] = draft[k];
    }
    return out;
  };

  const save = async (): Promise<boolean> => {
    const diff = changed();
    if (Object.keys(diff).length === 0) return true;
    setSaving(true);
    try {
      await clubApi.saveSettings(diff);
      await reload();
      return true;
    } catch (e) {
      setError(describe(e));
      return false;
    } finally {
      setSaving(false);
    }
  };

  return {
    data,
    draft,
    error,
    dirty: Object.keys(changed()).length > 0,
    saving,
    set,
    save,
    reset: () => setDraft(data),
    reload,
  };
}
