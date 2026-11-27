/**
 * A wallet row as the player reads it (the wallet's history): a title in the UI language from the row's type and sign,
 * with how a top-up was paid when the description says it, and a detail only where the description says more than the
 * type — the minutes and tariff of play time, the goods of a purchase, the promo code or rule of a bonus, the reason of
 * a write-off. The server writes descriptions in Russian whatever the language, so known ones are read, not shown.
 * Returns i18n keys with their values (`wallet.tx.*`); `text` is a value shown as it is (a tariff, goods, a rule).
 */
export type TxPart = { key: string; vars?: Record<string, string | number> } | { text: string };

export interface TxView {
  title: TxPart;
  /** How it was paid: `wallet.tx.method.cash`, … or a brand name. */
  method: TxPart | null;
  detail: TxPart[];
}

interface TxLike {
  type: string;
  amount: { amount: number };
  description: string;
  bonus?: boolean | null;
}

const BRANDS = ['payme', 'click', 'uzum'] as const;

function methodIn(d: string): TxPart | null {
  if (/налич|cash/i.test(d)) return { key: 'wallet.tx.method.cash' };
  if (/карт|card/i.test(d)) return { key: 'wallet.tx.method.card' };
  const brand = BRANDS.find((m) => new RegExp(m, 'i').test(d));
  return brand ? { text: brand.charAt(0).toUpperCase() + brand.slice(1) } : null;
}

/** What follows the first «: » or « · » (the goods of a sale, the reason of a write-off). */
function after(d: string): string | null {
  const m = /^[^:·]*?(?::|\s·)\s*(.+)$/.exec(d);
  return m?.[1]?.trim() || null;
}

export function txView(x: TxLike): TxView {
  const d = x.description ?? '';
  const plus = x.amount.amount >= 0;
  const key = (k: string, vars?: Record<string, string | number>): TxPart => ({ key: `wallet.tx.${k}`, vars });
  switch (x.type) {
    case 'topUp':
      return {
        title: key(!plus ? 'topUpCancel' : /долг|debt/i.test(d) ? 'debt' : 'topUp'),
        method: methodIn(d),
        detail: [],
      };
    case 'charge': {
      const minutes = /(\d+)\s*мин/.exec(d)?.[1];
      const tariff = /·\s*([^·]+?)(?:\s·\sбонусы)?$/.exec(d)?.[1]?.trim();
      const title = /^Продление/.test(d) ? 'extend' : /^Постоплата/.test(d) ? 'postpaid' : 'charge';
      const detail: TxPart[] = [];
      if (minutes) detail.push(key('minutes', { n: Number(minutes) }));
      if (tariff && tariff !== 'бонусы') detail.push({ text: tariff });
      if (x.bonus) detail.push(key('fromBonus'));
      return { title: key(title), method: null, detail };
    }
    case 'refund':
      return { title: key('refund'), method: null, detail: x.bonus ? [key('toBonus')] : [] };
    case 'bonus': {
      if (!plus) return { title: key('bonusBack'), method: null, detail: [] };
      const code = /^Промокод\s+(\S+)/.exec(d)?.[1];
      const rule = /^Бонус:\s*(.+)$/.exec(d)?.[1];
      const pct = /\+(\d+)%/.exec(d)?.[1];
      const detail: TxPart | null = code
        ? key('promo', { code })
        : rule
          ? { text: rule }
          : /пополнен/i.test(d)
            ? pct
              ? key('forTopUpPromo', { pct: Number(pct) })
              : key('forTopUp')
            : /лояльн|loyalty/i.test(d)
              ? key('loyalty')
              : null;
      return { title: key('bonus'), method: null, detail: detail ? [detail] : [] };
    }
    case 'purchase': {
      const goods = plus ? null : after(d);
      return { title: key(plus ? 'purchaseBack' : 'purchase'), method: null, detail: goods ? [{ text: goods }] : [] };
    }
    case 'adjustment': {
      if (/^Выдано наличными/.test(d)) return { title: key('payout'), method: null, detail: [] };
      if (/^Списание долга:/.test(d)) {
        const reason = after(d);
        return { title: key('writeOff'), method: null, detail: reason ? [{ text: reason }] : [] };
      }
      return { title: key('adjustment'), method: null, detail: d ? [{ text: d }] : [] };
    }
    default:
      return { title: d ? { text: d } : key('other'), method: null, detail: [] };
  }
}
