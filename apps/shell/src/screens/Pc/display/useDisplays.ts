/**
 * Displays and their refresh rates for the Monitor section: loads `display_list`, reloads on `kiosk://monitorChanged`,
 * and runs a switch → confirm / revert round trip. The Shell undoes an unconfirmed rate on its own after 15 s, so
 * nothing here has to survive an unmount: coming back shows the same pending change with the time it has left.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocale } from '@/hooks/useLocale';
import { api, events, toShellApiError, type DisplayInfo } from '@/lib/tauri';
import { useNotificationsStore } from '@/store';
import { formatUnit } from './specs';

export interface DisplaysController {
  /** `null` until the first answer. */
  displays: DisplayInfo[] | null;
  /** The last load failed (no list to show). */
  failed: boolean;
  /** Device whose rate is being changed, confirmed or reverted. */
  busy: string | null;
  reload: () => void;
  setRate: (device: string, hz: number) => Promise<void>;
  confirm: (device: string) => Promise<void>;
  revert: (device: string) => Promise<void>;
}

export function useDisplays(): DisplaysController {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [displays, setDisplays] = useState<DisplayInfo[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const alive = useRef(true);

  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);

  const reload = useCallback(() => {
    api.display.list().then(
      (list) => {
        if (alive.current) {
          setDisplays(list);
          setFailed(false);
        }
      },
      () => {
        if (alive.current) {
          setFailed(true);
        }
      },
    );
  }, []);

  useEffect(() => {
    reload();
    return events.onKiosk('monitorChanged', reload);
  }, [reload]);

  const replace = useCallback((next: DisplayInfo) => {
    if (alive.current) {
      setDisplays((list) => list?.map((d) => (d.device === next.device ? next : d)) ?? [next]);
    }
  }, []);

  const run = useCallback(async (device: string, work: () => Promise<void>): Promise<void> => {
    setBusy(device);
    try {
      await work();
    } finally {
      if (alive.current) {
        setBusy(null);
      }
    }
  }, []);

  const hz = useCallback((value: number) => formatUnit('hz', value, locale, t), [locale, t]);

  const setRate = useCallback(
    (device: string, rate: number) =>
      run(device, async () => {
        try {
          replace(await api.display.setRefreshRate(device, rate));
        } catch (e) {
          pushError(e, t('pcDisplay.monitor.switchFailed'));
          reload();
        }
      }),
    [run, replace, pushError, reload, t],
  );

  const confirm = useCallback(
    (device: string) =>
      run(device, async () => {
        try {
          const d = await api.display.confirm(device);
          replace(d);
          push({ title: t('pcDisplay.monitor.kept', { hz: hz(d.hz) }), level: 'success', ttlSec: 4 });
        } catch (e) {
          // `notFound`: the countdown ran out first and the previous rate is already back.
          if (toShellApiError(e).code !== 'notFound') {
            pushError(e, t('pcDisplay.monitor.title'));
          }
          reload();
        }
      }),
    [run, replace, push, pushError, reload, hz, t],
  );

  const revert = useCallback(
    (device: string) =>
      run(device, async () => {
        try {
          const d = await api.display.revert(device);
          replace(d);
          push({ title: t('pcDisplay.monitor.reverted', { hz: hz(d.hz) }), level: 'info', ttlSec: 4 });
        } catch (e) {
          pushError(e, t('pcDisplay.monitor.title'));
          reload();
        }
      }),
    [run, replace, push, pushError, reload, hz, t],
  );

  return { displays, failed, busy, reload, setRate, confirm, revert };
}
