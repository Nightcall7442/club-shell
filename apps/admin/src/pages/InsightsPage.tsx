/**
 * Owner insights ("Подсказки"): conclusions from the club's own data, each with its facts, an estimated monthly effect
 * and one action — an empty zone gets a happy hour in one click, a full one points at the prices, a product about to run
 * out says how much to order, a PC waiting for repair and players who stopped coming say what they cost.
 */
import { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';
import { clubApi, type Insight, type InsightAction } from '@/api';
import { describe } from '@/errors';
import { dateLocale, t } from '@/i18n';
import { Button, Note, PageHeader } from '@/ui';

const nf = new Intl.NumberFormat('ru-RU');
const sum = (minor: number): string => nf.format(Math.round(minor / 100));

const DAYS: Record<string, string> = {
  weekdays: 'по будням',
  weekend: 'в выходные',
  daily: 'каждый день',
};

const SECTION: Record<Extract<InsightAction, { kind: 'open' }>['section'], string> = {
  pricing: 'Открыть тарифы',
  shop: 'Открыть склад',
  health: 'Открыть «Состояние ПК»',
  clients: 'Открыть клиентов',
};

/** 0 = Sunday … 6 = Saturday, in the console language. */
const weekday = (i: number): string => new Date(2024, 0, 7 + i).toLocaleDateString(dateLocale(), { weekday: 'long' });

function phrase(i: Insight): { title: string; body: string; impact: string } {
  const p = i.params;
  const days = t(DAYS[String(p['days'])] ?? '');
  switch (i.kind) {
    case 'idleWindow':
      return {
        title: t('{zone} пустует {days} {range}', { zone: p['zone']!, days, range: p['range']! }),
        body: t(
          'Занято в среднем {occupancy}% мест. Счастливый час −{discount}% именно для этой зоны и этих часов приведёт игроков, которые сейчас не приходят.',
          { occupancy: p['occupancy']!, discount: p['discount']! },
        ),
        impact: t('≈ +{sum} сум в месяц', { sum: sum(i.impact) }),
      };
    case 'peakWindow':
      return {
        title: t('{zone} заполнен {days} {range}', { zone: p['zone']!, days, range: p['range']! }),
        body: t(
          'Занято {occupancy}% мест — свободных почти не бывает. Поднимите цену в эти часы на 10% или продавайте их бронью с предоплатой.',
          { occupancy: p['occupancy']! },
        ),
        impact: t('≈ +{sum} сум в месяц', { sum: sum(i.impact) }),
      };
    case 'stockOut': {
      const best = Number(p['bestDay']);
      const peak = best >= 0 ? ` ${t('Пик продаж — {day}.', { day: weekday(best) })}` : '';
      return {
        title:
          Number(p['qty']) === 0
            ? t('{product} закончился', { product: p['product']! })
            : t('{product} закончится через {days} дн.', { product: p['product']!, days: p['daysLeft']! }),
        body:
          t('Продаётся ≈{perDay} шт. в день, осталось {qty}. Закажите {order} шт. — хватит на неделю.', {
            perDay: p['perDay']!,
            qty: p['qty']!,
            order: p['order']!,
          }) + peak,
        impact: t('−{sum} сум за неделю без товара', { sum: sum(i.impact) }),
      };
    }
    case 'staleStock':
      return {
        title: t('{product} не продаётся', { product: p['product']! }),
        body: t(
          'За {days} дней ни одной продажи, на складе {qty} шт. Сделайте скидку или добавьте в пакет с часами — деньги лежат на полке.',
          { days: p['days']!, qty: p['qty']! },
        ),
        impact: t('{sum} сум в товаре', { sum: sum(i.impact) }),
      };
    case 'repairWaiting':
      return {
        title: t('{pc} ждёт ремонта {days} дн.', { pc: p['pc']!, days: p['days']! }),
        body: t('Место простаивает или его обходят стороной — каждый день без ремонта это потерянные часы.'),
        impact: t('−{sum} сум в месяц', { sum: sum(i.impact) }),
      };
    case 'winBack':
      return {
        title: t('Перестали приходить: {players}', { players: p['players']! }),
        body: t(
          'Не были в клубе больше двух недель, а деньги на балансе остались. Напомните о себе — бонус к пополнению вернёт часть из них.',
        ),
        impact: t('{sum} сум на их балансах', { sum: sum(i.impact) }),
      };
  }
}

function Card({ insight, onChanged }: { insight: Insight; onChanged: (note: string) => void }): JSX.Element {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { title, body, impact } = phrase(insight);
  const act = async (run: () => Promise<unknown>, done: string): Promise<void> => {
    setBusy(true);
    try {
      await run();
      onChanged(done);
    } catch (e) {
      setError(describe(e));
      setBusy(false);
    }
  };
  const a = insight.action;
  return (
    <article className="panel flex flex-col gap-3 p-5" aria-label={title}>
      <header className="flex flex-wrap items-start justify-between gap-3">
        <h2 className="min-w-0 font-display text-lg tracking-tight">{title}</h2>
        <span
          className={clsx(
            'tnum shrink-0 rounded px-2 py-0.5 text-sm',
            insight.tone === 'risk' ? 'bg-danger/15 text-danger' : 'bg-accent/15 text-accent',
          )}
        >
          {impact}
        </span>
      </header>
      <p className="text-sm text-muted">{body}</p>
      <footer className="mt-1 flex flex-wrap items-center gap-3">
        {a?.kind === 'createHappyHour' && (
          <Button
            variant="primary"
            disabled={busy}
            onClick={() =>
              void act(
                () => clubApi.applyInsight(insight.id),
                t('Счастливый час «{name}» создан', { name: a.happyHour.name }),
              )
            }
          >
            {t('Создать счастливый час −{discount}%', { discount: a.happyHour.discountPct })}
          </Button>
        )}
        {a?.kind === 'open' && (
          <Button onClick={() => (window.location.hash = `/${a.section}`)}>{t(SECTION[a.section])}</Button>
        )}
        <Button
          variant="ghost"
          disabled={busy}
          onClick={() => void act(() => clubApi.dismissInsight(insight.id), t('Скрыто на неделю'))}
        >
          {t('Скрыть')}
        </Button>
        {error && <span className="text-sm text-danger">{error}</span>}
      </footer>
    </article>
  );
}

export default function InsightsPage(): JSX.Element {
  const [items, setItems] = useState<Insight[] | null>(null);
  const [note, setNote] = useState<{ text: string; tone: 'ok' | 'err' } | null>(null);

  const load = useCallback(() => {
    clubApi
      .insights()
      .then((r) => setItems(r.items))
      .catch((e: unknown) => setNote({ text: describe(e), tone: 'err' }));
  }, []);

  useEffect(() => {
    load();
    const id = setInterval(load, 5 * 60_000);
    return () => clearInterval(id);
  }, [load]);

  const risks = items?.filter((i) => i.tone === 'risk').length ?? 0;

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title={t('Подсказки')} />
      <p className="max-w-3xl text-sm text-muted">
        {t(
          'Выводы из данных клуба: загрузка зон по часам за неделю, продажи за две недели, ремонт и игроки. Суммы — оценка на месяц.',
        )}
      </p>
      <Note note={note} />
      {items && items.length === 0 && <p className="text-muted">{t('Сейчас подсказок нет — всё идёт хорошо.')}</p>}
      {items && items.length > 0 && (
        <p className="text-sm">
          {t('Подсказок: {n}', { n: items.length })}
          {risks > 0 && <span className="ml-3 text-danger">{t('Срочно: {n}', { n: risks })}</span>}
        </p>
      )}
      <div className="grid grid-cols-1 gap-5 xl:grid-cols-2">
        {items?.map((i) => (
          <Card
            key={i.id}
            insight={i}
            onChanged={(text) => {
              setNote({ text, tone: 'ok' });
              load();
            }}
          />
        ))}
      </div>
    </div>
  );
}
