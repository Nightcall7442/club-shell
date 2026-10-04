/**
 * Printing through the browser (D-51): no driver of our own, the cashier's default printer. A slip is a print-only layout
 * rendered into `#print-root` (next to `#root`, outside every sheet: sheets portal into `body`) synchronously with
 * `flushSync` in the receipt language ({@link inLang}); once the fonts are in and a frame has passed, its height is
 * measured and `@page` is sized to it (80 / 58 mm rolls; A4 with margins), so a roll does not feed a long blank tail.
 * The print CSS in `index.css` hides everything but `#print-root` and prints black on white whatever the console's theme.
 *
 * Paper and language are settings of this console (localStorage): receipts and reports may go to different printers
 * ('80' | '58' | 'a4', default '80'), the receipt language defaults to the console's. In silent mode (Chrome started with
 * `--kiosk-printing` on its own `--user-data-dir`) everything goes to the default printer, so X/Z then use the receipt
 * paper. Every slip says it is not a fiscal receipt; a reprint is marked «Копия» and has no received / change (never
 * stored). Labels come from the console's tables, never from server descriptions.
 */
import { flushSync } from 'react-dom';
import { createRoot } from 'react-dom/client';
import type { ReactNode } from 'react';
import type { PayMethod, Shift, ShiftTotals, VoidReason } from '@/api';
import { expectedOf } from '@/api';
import { pcLabel } from '@/clientSearch';
import { exactDigits, minutesLabel } from '@/format';
import { currentLang, dateLocale, inLang, t, type Lang } from '@/i18n';
import { PAY_METHOD_LABEL, REASON_LABEL, VOID_REASON_LABEL, guestDisplayName } from '@/labels';

export type Paper = '80' | '58' | 'a4';
export type PrintKind = 'receipt' | 'report';

export const PAPERS: { id: Paper; label: string }[] = [
  { id: '80', label: '80 мм' },
  { id: '58', label: '58 мм' },
  { id: 'a4', label: 'A4' },
];

const PAPER_KEY: Record<PrintKind, string> = {
  receipt: 'clubshell.admin.paper.receipt',
  report: 'clubshell.admin.paper.report',
};
const LANG_KEY = 'clubshell.admin.receiptLang';

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string | null): void {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    // private mode: the setting lasts until the reload
  }
}

/** The paper of `kind` on this console. */
export function paperOf(kind: PrintKind): Paper {
  const v = read(PAPER_KEY[kind]);
  return v === '58' || v === 'a4' ? v : '80';
}

export function setPaper(kind: PrintKind, paper: Paper): void {
  write(PAPER_KEY[kind], paper);
}

/** The language slips are printed in: the setting, else the console's. */
export function receiptLang(): Lang {
  const v = read(LANG_KEY);
  return v === 'ru' || v === 'uz' || v === 'en' ? v : currentLang();
}

/** null — follow the console's language. */
export function setReceiptLang(lang: Lang | null): void {
  write(LANG_KEY, lang);
}

/** The setting as stored: null when the slips follow the console's language. */
export function storedReceiptLang(): Lang | null {
  const v = read(LANG_KEY);
  return v === 'ru' || v === 'uz' || v === 'en' ? v : null;
}

let printing = false;

/** Prints `node` on the paper of `kind`; resolves when the print dialog has closed (Chrome's `print()` blocks). */
export async function printDocument(node: ReactNode, kind: PrintKind): Promise<void> {
  const host = document.getElementById('print-root');
  if (!host || printing) return;
  printing = true;
  const paper = paperOf(kind);
  host.dataset['paper'] = paper;
  const root = createRoot(host);
  const style = document.createElement('style');
  style.id = 'print-page';
  let done = false;
  const cleanup = (): void => {
    if (done) return;
    done = true;
    root.unmount();
    style.remove();
    delete host.dataset['paper'];
    printing = false;
  };
  try {
    // React 18 renders asynchronously: flushSync puts the slip into the DOM now, in the receipt language.
    inLang(receiptLang(), () => flushSync(() => root.render(<div className="slip">{node}</div>)));
    await document.fonts?.ready;
    // One frame for layout; a hidden or minimised console gets no frames, so a timer stands in for it.
    await new Promise<void>((resolve) => {
      requestAnimationFrame(() => resolve());
      window.setTimeout(resolve, 100);
    });
    if (paper === 'a4') {
      style.textContent = '@page { size: A4; margin: 12mm; }';
    } else {
      const mm = Math.ceil((host.getBoundingClientRect().height * 25.4) / 96) + 4;
      style.textContent = `@page { size: ${paper}mm ${mm}mm; margin: 0; }`;
    }
    document.getElementById('print-page')?.remove();
    document.head.appendChild(style);
    window.addEventListener('afterprint', cleanup, { once: true });
    window.print();
  } finally {
    cleanup();
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Pieces of a slip
// ---------------------------------------------------------------------------------------------------------------------

/** `45 000,50 сум` to the tiyin: a slip shows exactly what was taken. */
const sum = (minor: number): string => t('{n} сум', { n: exactDigits(minor) });

function stamp(iso: string): string {
  return new Date(iso).toLocaleString(dateLocale(), {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

/** № of a slip: the last 8 hex of the transaction / session / movement id (v7 ids: unique enough to find it). */
export function slipNumber(id: string | null | undefined): string {
  return id ? id.replace(/-/g, '').slice(-8) : '—';
}

function Row({ label, value, strong }: { label: string; value: ReactNode; strong?: boolean }): JSX.Element {
  return (
    <div className={strong ? 'slip-row slip-strong' : 'slip-row'}>
      <span>{label}</span>
      <span className="slip-value">{value}</span>
    </div>
  );
}

function Rule(): JSX.Element {
  return <div className="slip-rule" />;
}

function Head({
  club,
  title,
  number,
  at,
  copy,
}: {
  club: string | null;
  title: string;
  number: string;
  at: string;
  copy?: boolean;
}): JSX.Element {
  return (
    <>
      {club && <div className="slip-center slip-strong">{club}</div>}
      <div className="slip-center slip-title">{title}</div>
      {copy && <div className="slip-center slip-strong">{t('Копия')}</div>}
      <Row label={t('№ {n}', { n: number })} value={stamp(at)} />
    </>
  );
}

function Foot(): JSX.Element {
  return (
    <>
      <Rule />
      <div className="slip-center">{t('Не является фискальным чеком')}</div>
    </>
  );
}

const methodLabel = (method: string): string => t(PAY_METHOD_LABEL[method as PayMethod] ?? method);

/**
 * How a walk-in guest signs in to the seat the desk opened, in the kiosk's own words per language (its «Гость» tab,
 * the rules box, the button): `pc` is the PC's label.
 */
export function guestSignIn(pc: string): string {
  return t('{pc}: вкладка «Гость» → «Я принимаю правила клуба» → «Играть как гость»', { pc });
}

// ---------------------------------------------------------------------------------------------------------------------
// Receipt
// ---------------------------------------------------------------------------------------------------------------------

export type ReceiptKind = 'seat' | 'extend' | 'topup' | 'debt' | 'payout' | 'end' | 'sale' | 'saleVoid';

/** What a receipt says; amounts are minor units. */
export interface ReceiptData {
  kind: ReceiptKind;
  at: string;
  /** Transaction, session or entry id: its last 8 hex are the №. */
  ref: string | null;
  club: string | null;
  cashier: string | null;
  /** The client's name; null for a walk-in guest («Гость»). */
  client: string | null;
  guest: boolean;
  /** The PC's name as the server has it (`PC-03`): the slip says «ПК 03» in its own language. */
  pc: string | null;
  tariff?: string | null;
  /** Minutes bought; null with `pkg` (the package's own) or postpaid. */
  minutes?: number | null;
  /** A package was sold; undefined on a copy of an entry that does not say (no refund rule is printed then). */
  pkg?: boolean;
  prepaid?: boolean;
  quote?: { base: number; dayPct: number; discountPct: number } | null;
  /** Price of the time (or the debt, the payout). */
  total?: number | null;
  paid?: { amount: number; method: string } | null;
  /** Cash handed over and the change: live slips only. */
  received?: number | null;
  /** The member's balance after (never for a guest). */
  balance?: number | null;
  refunded?: number | null;
  charged?: number | null;
  /** A bar sale or its void: the goods as sold, and how it was paid (`balance` — from the client's balance). */
  lines?: { title: string; qty: number; price: number }[] | null;
  method?: string | null;
  /** A void's reason code and note. */
  reason?: string | null;
  note?: string | null;
  copy?: boolean;
}

const RECEIPT_TITLE: Record<ReceiptKind, string> = {
  seat: 'Чек · посадка',
  extend: 'Чек · продление',
  topup: 'Чек · пополнение',
  debt: 'Чек · оплата долга',
  payout: 'Выдача наличными',
  end: 'Завершение сеанса',
  sale: 'Чек · бар',
  saleVoid: 'Аннулирование · бар',
};

/** The goods of a bar slip: `Coca-Cola ×2   16 000 сум`, then the total. */
function SaleLines({ r }: { r: ReceiptData }): JSX.Element {
  return (
    <>
      <Rule />
      {(r.lines ?? []).map((l, i) => (
        <Row key={i} label={`${l.title} ×${l.qty}`} value={sum(l.price * l.qty)} />
      ))}
      {r.total != null && <Row label={t('Итого')} value={sum(r.total)} strong />}
    </>
  );
}

export function Receipt({ r }: { r: ReceiptData }): JSX.Element {
  const change = r.received != null && r.paid ? r.received - r.paid.amount : null;
  const timed = r.kind === 'seat' || r.kind === 'extend';
  return (
    <>
      <Head club={r.club} title={t(RECEIPT_TITLE[r.kind])} number={slipNumber(r.ref)} at={r.at} copy={r.copy} />
      {r.cashier && <Row label={t('Кассир')} value={r.cashier} />}
      <Row
        label={t('Клиент')}
        value={[r.guest ? t('Гость') : r.client, r.pc ? pcLabel(r.pc) : null].filter(Boolean).join(' · ') || '—'}
      />
      {timed && (
        <>
          <Rule />
          {r.tariff && <Row label={t('Тариф')} value={r.tariff} />}
          <Row
            label={t('Время')}
            value={
              r.prepaid === false ? t('постоплата') : r.pkg ? t('пакет') : r.minutes ? minutesLabel(r.minutes) : '—'
            }
          />
          {r.quote && r.quote.base > 0 && <Row label={t('Цена')} value={sum(r.quote.base)} />}
          {r.quote && r.quote.dayPct !== 100 && <Row label={t('Цена дня')} value={`${r.quote.dayPct}%`} />}
          {r.quote && r.quote.discountPct > 0 && <Row label={t('Скидка')} value={`−${r.quote.discountPct}%`} />}
          {r.total != null && <Row label={t('Итого')} value={sum(r.total)} strong />}
        </>
      )}
      {(r.kind === 'debt' || r.kind === 'payout') && r.total != null && (
        <>
          <Rule />
          <Row label={r.kind === 'debt' ? t('Долг') : t('Выдано')} value={sum(r.total)} strong />
        </>
      )}
      {r.kind === 'end' && (
        <>
          <Rule />
          {r.tariff && <Row label={t('Тариф')} value={r.tariff} />}
          {r.charged != null && r.charged > 0 && <Row label={t('Списано')} value={sum(r.charged)} strong />}
          {r.refunded != null && r.refunded > 0 && <Row label={t('Возвращено на баланс')} value={sum(r.refunded)} />}
        </>
      )}
      {r.kind === 'sale' && (
        <>
          <SaleLines r={r} />
          {r.method === 'balance' && r.total != null && (
            <Row label={t('Оплачено')} value={`${sum(r.total)} · ${t('с баланса')}`} strong />
          )}
        </>
      )}
      {r.kind === 'saleVoid' && (
        <>
          <SaleLines r={r} />
          {r.reason && <Row label={t('Причина')} value={t(VOID_REASON_LABEL[r.reason as VoidReason] ?? r.reason)} />}
          {r.note && <Row label={t('Комментарий')} value={r.note} />}
          {r.total != null && (
            <Row
              label={r.method === 'balance' ? t('Возвращено на баланс') : t('Возвращено')}
              value={r.method === 'balance' ? sum(r.total) : `${sum(r.total)} · ${methodLabel(r.method ?? 'cash')}`}
              strong
            />
          )}
          <div className="slip-sign">{t('Подпись')}</div>
        </>
      )}
      {r.paid && (
        <>
          <Rule />
          <Row label={t('Оплачено')} value={`${sum(r.paid.amount)} · ${methodLabel(r.paid.method)}`} strong />
          {!r.copy && r.received != null && r.received > 0 && (
            <>
              <Row label={t('Получено')} value={sum(r.received)} />
              {change !== null && change >= 0 && <Row label={t('Сдача')} value={sum(change)} />}
            </>
          )}
        </>
      )}
      {!r.guest && r.balance != null && <Row label={t('Баланс')} value={sum(r.balance)} />}
      {r.guest && r.kind === 'seat' && r.pc && <p className="slip-note">{guestSignIn(pcLabel(r.pc))}</p>}
      {timed && r.prepaid !== false && !(r.copy && r.pkg === undefined) && (
        <p className="slip-note">
          {r.pkg
            ? t('Время пакета не возвращается.')
            : r.guest
              ? t('Остаток времени выдаётся наличными только при завершении на кассе; при выходе с ПК он сгорает.')
              : t('Неиспользованное время возвращается на баланс при завершении на кассе.')}
        </p>
      )}
      <Foot />
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Cash slip (внесение / изъятие)
// ---------------------------------------------------------------------------------------------------------------------

export interface CashSlipData {
  kind: 'in' | 'out';
  amount: number;
  reasonCode: string;
  note: string | null;
  at: string;
  ref: string | null;
  club: string | null;
  staffName: string;
  copy?: boolean;
}

export function CashSlip({ s }: { s: CashSlipData }): JSX.Element {
  return (
    <>
      <Head
        club={s.club}
        title={s.kind === 'in' ? t('Внесение в кассу') : t('Изъятие из кассы')}
        number={slipNumber(s.ref)}
        at={s.at}
        copy={s.copy}
      />
      <Rule />
      <Row label={t('Сумма')} value={sum(s.amount)} strong />
      <Row label={t('Причина')} value={t(REASON_LABEL[s.reasonCode] ?? s.reasonCode)} />
      {s.note && <Row label={t('Комментарий')} value={s.note} />}
      <Row label={t('Кассир')} value={s.staffName} />
      <div className="slip-sign">{t('Подпись')}</div>
      <Foot />
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// X / Z report
// ---------------------------------------------------------------------------------------------------------------------

export interface ReportMove {
  at: string;
  kind: 'cashIn' | 'cashOut' | 'payout';
  amount: number;
  reasonCode: string | null;
  note: string | null;
  who: string | null;
}

export interface ShiftReportData {
  type: 'X' | 'Z';
  club: string | null;
  shift: Shift;
  x: ShiftTotals;
  /** The server's expected cash; the old formula when an older server sent none. */
  expected: number | null;
  moves: ReportMove[];
  at: string;
  copy?: boolean;
}

const MOVE_LABEL: Record<ReportMove['kind'], string> = {
  cashIn: 'Внесение',
  cashOut: 'Изъятие',
  payout: 'Выдача гостю',
};

export function ShiftReport({ r }: { r: ShiftReportData }): JSX.Element {
  const x = r.x;
  const expected = r.expected ?? expectedOf(r.shift.openingCash, x);
  const byMethod = x.topUpByMethod;
  // The bar by method (D-57); a Z saved before cash desk part 3 has none.
  const shop = x.shopByMethod;
  const counted = r.type === 'Z' ? r.shift.closingCash : null;
  return (
    <>
      <Head
        club={r.club}
        title={r.type === 'X' ? t('X-отчёт') : t('Z-отчёт')}
        number={slipNumber(r.shift.id)}
        at={r.at}
        copy={r.copy}
      />
      <Row label={t('Открыта')} value={`${stamp(r.shift.openedAt)} · ${r.shift.staffName}`} />
      {r.shift.closedAt && (
        <Row label={t('Закрыта')} value={[stamp(r.shift.closedAt), r.shift.closedBy].filter(Boolean).join(' · ')} />
      )}
      <Rule />
      <div className="slip-strong">{t('Пополнения')}</div>
      {byMethod ? (
        (['cash', 'card', 'payme', 'click', 'uzum'] as const).map((m) => (
          <Row key={m} label={methodLabel(m)} value={sum(byMethod[m])} />
        ))
      ) : (
        <>
          <Row label={methodLabel('cash')} value={sum(x.topUpCash)} />
          <Row label={t('Безнал')} value={sum(x.topUpOther)} />
        </>
      )}
      {byMethod && byMethod.other > 0 && <Row label={t('Другое')} value={sum(byMethod.other)} />}
      {(x.apiCash ?? 0) > 0 && <Row label={t('Через API (нал.)')} value={sum(x.apiCash ?? 0)} />}
      {shop && (
        <>
          <Rule />
          <div className="slip-strong">{t('Бар')}</div>
          {(['cash', 'card', 'payme', 'click', 'uzum'] as const).map((m) => (
            <Row key={m} label={methodLabel(m)} value={sum(shop[m])} />
          ))}
          <Row label={t('С баланса')} value={sum(shop.balance)} />
          {(x.shopVoidCount ?? 0) > 0 && (
            <Row label={t('Аннулировано: {n}', { n: x.shopVoidCount ?? 0 })} value={sum(x.shopVoids ?? 0)} />
          )}
        </>
      )}
      <Rule />
      <Row label={t('Сеансы')} value={sum(x.sessions)} />
      <Row label={t('Возвраты')} value={sum(x.refunds)} />
      <Row label={t('Магазин')} value={sum(x.shop)} />
      <Row label={t('Бонусы')} value={sum(x.bonuses)} />
      <Row label={t('Операций')} value={String(x.count)} />
      <Rule />
      <div className="slip-strong">{t('Касса')}</div>
      <Row label={t('На начало смены')} value={sum(r.shift.openingCash)} />
      <Row label={t('+ наличные пополнения')} value={sum(x.topUpCash - (x.apiCash ?? 0))} />
      {shop && <Row label={t('+ наличные продажи бара')} value={sum(shop.cash)} />}
      <Row label={t('+ внесения')} value={sum(x.cashIn ?? 0)} />
      <Row label={t('− изъятия')} value={sum(x.cashOut ?? 0)} />
      <Row label={t('− выдачи гостям')} value={sum(x.payouts ?? 0)} />
      <Row label={t('= ожидается')} value={sum(expected)} strong />
      {counted != null && (
        <>
          <Row label={t('Посчитано')} value={sum(counted)} />
          <Row label={t('Расхождение')} value={sum(counted - expected)} strong />
        </>
      )}
      {r.moves.length > 0 && (
        <>
          <Rule />
          <div className="slip-strong">{t('Движения по кассе')}</div>
          {r.moves.map((m, i) => (
            <div key={i} className="slip-move">
              <Row
                label={`${new Date(m.at).toLocaleTimeString(dateLocale(), { hour: '2-digit', minute: '2-digit' })} ${t(MOVE_LABEL[m.kind])}`}
                value={`${m.kind === 'cashIn' ? '+' : '−'}${sum(m.amount)}`}
              />
              {(m.reasonCode || m.note || m.who) && (
                <div className="slip-sub">
                  {[
                    m.reasonCode ? t(REASON_LABEL[m.reasonCode] ?? m.reasonCode) : null,
                    m.note,
                    // A payout's guest named «Гость N» by the server: said in the slip's language.
                    m.kind === 'payout' && m.who ? guestDisplayName(m.who) : m.who,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </div>
              )}
            </div>
          ))}
        </>
      )}
      <Foot />
    </>
  );
}
