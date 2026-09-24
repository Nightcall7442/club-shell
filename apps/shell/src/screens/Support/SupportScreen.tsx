/**
 * Support (`/support`): call-admin card, FAQ accordion, club rules, "this PC" facts (from `PcInfo`), contact
 * shortcuts and a "report a problem" form forwarded through `sys_log_client_error`.
 */
import { useEffect, useId, useRef, useState, type FocusEvent } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router-dom';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { useVirtualKeyboard } from '@/components/ui/VirtualKeyboard';
import { collectNavigables, focusElement } from '@/hooks/useGamepad';
import { track } from '@/lib/analytics';
import { formatDurationSec } from '@/lib/format';
import { api } from '@/lib/tauri';
import { CallAdminButton } from '@/screens/Support/CallAdminButton';
import { useNotificationsStore } from '@/store/notifications';
import { selectFeatures, useSettingsStore } from '@/store/settings';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';

const FAQ_COUNT = 4;
const RULES_COUNT = 5;
const REPORT_MAX = 500;

// ---------------------------------------------------------------------------------------------------------------------
// FAQ accordion
// ---------------------------------------------------------------------------------------------------------------------

export interface FaqItem {
  question: string;
  answer: string;
}

export function FaqAccordion({ items }: { items: FaqItem[] }): JSX.Element {
  const animations = useThemeStore(selectAnimationsEnabled);
  const id = useId();
  const [openIndex, setOpenIndex] = useState<number | null>(0);
  return (
    <ul role="list" className="flex flex-col gap-2">
      {items.map((item, i) => {
        const open = openIndex === i;
        const panelId = `${id}-panel-${i}`;
        const buttonId = `${id}-button-${i}`;
        return (
          <li key={item.question} className="glass overflow-hidden rounded-lg">
            <button
              id={buttonId}
              type="button"
              data-nav="true"
              aria-expanded={open}
              aria-controls={panelId}
              onClick={() => setOpenIndex(open ? null : i)}
              className={clsx(
                'focus-ring flex w-full items-center justify-between gap-4 px-5 py-4 text-left transition-colors duration-[var(--dur-fast)] hover:bg-surface/80',
                open && 'text-primary',
              )}
            >
              <span className="text-base font-semibold">{item.question}</span>
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                strokeLinecap="round"
                strokeLinejoin="round"
                aria-hidden="true"
                className={clsx(
                  'h-5 w-5 shrink-0 transition-transform duration-[var(--dur-base)]',
                  open && 'rotate-180',
                )}
              >
                <path d="M6 9l6 6 6-6" />
              </svg>
            </button>
            <AnimatePresence initial={false}>
              {open && (
                <motion.div
                  id={panelId}
                  role="region"
                  aria-labelledby={buttonId}
                  initial={animations ? { height: 0, opacity: 0 } : false}
                  animate={{ height: 'auto', opacity: 1 }}
                  exit={{ height: 0, opacity: 0 }}
                  transition={{ duration: animations ? 0.2 : 0, ease: 'easeOut' }}
                  className="overflow-hidden"
                >
                  <p className="px-5 pb-4 text-base text-muted">{item.answer}</p>
                </motion.div>
              )}
            </AnimatePresence>
          </li>
        );
      })}
    </ul>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Report a problem
// ---------------------------------------------------------------------------------------------------------------------

export function ReportProblemForm(): JSX.Element {
  const { t } = useTranslation();
  const location = useLocation();
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const vkAllowed = useSettingsStore((s) => s.settings.allowVirtualKeyboard);
  const [text, setText] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [sending, setSending] = useState(false);
  const id = useId();

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

  const submit = async (): Promise<void> => {
    const message = text.trim();
    if (message.length === 0) {
      setError(t('support.reportRequired'));
      return;
    }
    setError(null);
    setSending(true);
    track('support.report', { length: message.length });
    try {
      await api.system.logClientError({
        level: 'warn',
        message: `[user report] ${message}`,
        stack: null,
        route: location.pathname,
      });
      push({ title: t('support.reportSent'), body: t('support.reportSentHint'), level: 'success', source: 'local' });
      setText('');
    } catch (e) {
      pushError(e, t('support.reportProblem'));
    } finally {
      setSending(false);
    }
  };

  return (
    <form
      className="glass flex flex-col gap-3 rounded-xl p-5"
      onSubmit={(e) => {
        e.preventDefault();
        void submit();
      }}
    >
      <h2 className="font-display text-xl font-normal text-text tracking-tight">{t('support.reportProblem')}</h2>
      <p className="text-sm text-muted">{t('support.reportProblemHint')}</p>
      <label htmlFor={id} className="text-sm font-medium text-muted">
        {t('support.message')}
      </label>
      <textarea
        id={id}
        data-nav="true"
        value={text}
        rows={4}
        maxLength={REPORT_MAX}
        placeholder={t('support.reportPlaceholder')}
        aria-invalid={error ? true : undefined}
        aria-describedby={`${id}-hint`}
        onFocus={onFocus}
        onBlur={onBlur}
        onChange={(e) => {
          setText(e.currentTarget.value);
          if (error) {
            setError(null);
          }
        }}
        className={clsx(
          'glass themed-scrollbar w-full resize-none rounded-md px-3 py-2 text-base text-text outline-none placeholder:text-muted/70',
          'focus:border-primary/60 focus:shadow-[var(--shadow-glow)]',
          error && 'border-danger/70',
        )}
      />
      <p
        id={`${id}-hint`}
        role={error ? 'alert' : undefined}
        className={clsx('text-sm', error ? 'text-danger' : 'text-muted')}
      >
        {error ?? `${text.length} / ${REPORT_MAX}`}
      </p>
      <Button type="submit" variant="secondary" loading={sending} className="self-end">
        {t('support.reportSend')}
      </Button>
    </form>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// This PC
// ---------------------------------------------------------------------------------------------------------------------

export function PcFacts(): JSX.Element {
  const { t } = useTranslation();
  const pcInfo = useSettingsStore((s) => s.pcInfo);
  const agentConnected = useNotificationsStore((s) => s.agentConnected);
  const server = useNotificationsStore((s) => s.serverConnectivity);

  const rows: { label: string; value: string }[] = pcInfo
    ? [
        { label: t('lock.pc'), value: `${pcInfo.pc.name} (#${pcInfo.pc.number})` },
        { label: t('lock.zone'), value: pcInfo.pc.zone },
        { label: t('support.ipAddress'), value: pcInfo.pc.ipAddress },
        { label: t('support.agentVersion'), value: pcInfo.agentVersion },
        { label: t('support.shellVersion'), value: pcInfo.shellVersion },
        { label: t('support.protocol'), value: String(pcInfo.protocolVersion) },
        { label: t('support.policyVersion'), value: String(pcInfo.policyVersion) },
        { label: t('support.uptime'), value: formatDurationSec(pcInfo.uptimeSec, { compact: true }) },
      ]
    : [];

  return (
    <section aria-label={t('support.pcInfo')} className="glass flex flex-col gap-3 rounded-xl p-5">
      <div className="flex items-center justify-between gap-3">
        <h2 className="font-display text-xl font-normal text-text tracking-tight">{t('support.pcInfo')}</h2>
        <Badge
          tone={!agentConnected ? 'danger' : server === 'online' ? 'success' : 'accent'}
          size="sm"
          dot
          live={!agentConnected}
        >
          {!agentConnected ? t('lock.agentOffline') : server === 'online' ? t('common.online') : t('common.offline')}
        </Badge>
      </div>
      {rows.length === 0 ? (
        <p className="text-base text-muted">{t('common.loading')}</p>
      ) : (
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-base">
          {rows.map((r) => (
            <div key={r.label} className="contents">
              <dt className="text-muted">{r.label}</dt>
              <dd className="tnum truncate text-right font-semibold text-text">{r.value}</dd>
            </div>
          ))}
        </dl>
      )}
    </section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Screen
// ---------------------------------------------------------------------------------------------------------------------

export default function SupportScreen(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const features = useSettingsStore(selectFeatures);
  const animations = useThemeStore(selectAnimationsEnabled);
  const root = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const frame = requestAnimationFrame(() => {
      const active = document.activeElement;
      if (root.current && (active === null || active === document.body || active.id === 'main')) {
        const first = collectNavigables(root.current)[0];
        if (first) {
          focusElement(first);
        }
      }
    });
    return () => cancelAnimationFrame(frame);
  }, []);

  const faq: FaqItem[] = Array.from({ length: FAQ_COUNT }, (_, i) => ({
    question: t(`support.faqItems.q${i + 1}`),
    answer: t(`support.faqItems.a${i + 1}`),
  }));
  const rules = Array.from({ length: RULES_COUNT }, (_, i) => t(`support.rules.r${i + 1}`));

  return (
    <motion.div
      ref={root}
      className="mx-auto flex w-full max-w-[1800px] flex-col gap-[calc(var(--gap)*1.5)]"
      initial={animations ? { opacity: 0, y: 12 } : false}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: animations ? 0.25 : 0, ease: 'easeOut' }}
    >
      <header>
        <h1 className="font-display text-[length:var(--fs-2xl)] font-light tracking-tight text-text">
          {t('support.title')}
        </h1>
        <p className="text-base text-muted">{t('support.subtitle')}</p>
      </header>

      <div className="grid grid-cols-1 gap-[calc(var(--gap)*1.5)] xl:grid-cols-[minmax(0,2fr)_minmax(20rem,1fr)]">
        <div className="flex min-w-0 flex-col gap-[calc(var(--gap)*1.5)]">
          {features.callAdmin && (
            <section
              aria-label={t('support.callAdmin')}
              className="glass flex flex-wrap items-center justify-between gap-4 rounded-xl p-6"
            >
              <div className="min-w-0 flex-1">
                <h2 className="font-display text-2xl font-normal text-text tracking-tight">{t('support.callAdmin')}</h2>
                <p className="text-base text-muted">{t('support.callHint')}</p>
              </div>
              <CallAdminButton size="xl" />
            </section>
          )}

          <section aria-label={t('support.faqTitle')} className="flex flex-col gap-3">
            <h2 className="font-display text-xl font-normal text-text tracking-tight">{t('support.faqTitle')}</h2>
            <FaqAccordion items={faq} />
          </section>

          <section aria-label={t('support.rulesTitle')} className="glass flex flex-col gap-3 rounded-xl p-5">
            <h2 className="font-display text-xl font-normal text-text tracking-tight">{t('support.rulesTitle')}</h2>
            <ol className="flex list-decimal flex-col gap-2 pl-6 text-base text-text marker:font-bold marker:text-primary">
              {rules.map((r) => (
                <li key={r}>{r}</li>
              ))}
            </ol>
          </section>
        </div>

        <div className="flex min-w-0 flex-col gap-[calc(var(--gap)*1.5)]">
          <PcFacts />

          <section aria-label={t('support.contact')} className="glass flex flex-col gap-3 rounded-xl p-5">
            <h2 className="font-display text-xl font-normal text-text tracking-tight">{t('support.contact')}</h2>
            {features.chat && (
              <Button variant="secondary" block onClick={() => navigate('/chat')}>
                {t('support.chatWithStaff')}
              </Button>
            )}
            {features.callAdmin && (
              <CallAdminButton
                variant="secondary"
                size="md"
                block
                category="technical"
                label={t('support.category.technical')}
              />
            )}
            {!features.chat && !features.callAdmin && <p className="text-base text-muted">{t('lock.askStaff')}</p>}
          </section>

          <ReportProblemForm />
        </div>
      </div>
    </motion.div>
  );
}
