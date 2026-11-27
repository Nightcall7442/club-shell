/**
 * Names the counter gives to codes the server sends (payment methods, cash reasons, the kinds of the operations feed),
 * as Russian source keys of `t()`: the screens, the feed and the printed slips share them.
 */
import type { CallCategory, CashReason, OperationKind, PayMethod, TopUpCancelReason, VoidReason } from '@/api';
import { t } from '@/i18n';

export const PAY_METHOD_LABEL: Record<PayMethod, string> = {
  cash: 'Наличные',
  card: 'оплата|Карта',
  payme: 'Payme',
  click: 'Click',
  uzum: 'Uzum',
};

/** Why cash went into or out of the drawer (D-40): codes on the wire, words on the screen and the slip. */
export const REASON_LABEL: Record<CashReason | string, string> = {
  change: 'Размен',
  collection: 'Инкассация',
  expenses: 'Хозрасходы',
  other: 'Другое',
};

export const REASONS: CashReason[] = ['change', 'collection', 'expenses', 'other'];

export const OPERATION_LABEL: Record<OperationKind, string> = {
  topUp: 'Пополнение',
  debtPaid: 'Оплата долга',
  sessionOpen: 'Посадка',
  sessionExtend: 'Продление',
  sessionEnd: 'Завершение',
  payout: 'Выдача гостю',
  cashIn: 'Внесение',
  cashOut: 'Изъятие',
  shiftOpen: 'Открытие смены',
  shiftClose: 'Закрытие смены',
  promoRedeem: 'Промокод',
  shopSale: 'Продажа бара',
  shopVoid: 'Аннулирование',
  sessionMove: 'Пересадка',
  topUpCancel: 'Отмена пополнения',
};

/** What a player called the desk about (`problem` — a «report a problem» text from the kiosk). */
export const CALL_CATEGORY_LABEL: Record<CallCategory, string> = {
  help: 'Помощь',
  technical: 'Технический',
  order: 'Заказ',
  other: 'Другое',
  problem: 'Проблема',
};

/** Why a bar sale was taken back (D-56). */
export const VOID_REASON_LABEL: Record<VoidReason, string> = {
  mistake: 'Ошибка кассира',
  returned: 'Возврат товара',
  defect: 'Брак',
  other: 'Другое',
};

export const VOID_REASONS: VoidReason[] = ['mistake', 'returned', 'defect', 'other'];

/** Why a desk top-up is taken back (D-91). */
export const TOPUP_CANCEL_REASON_LABEL: Record<TopUpCancelReason, string> = {
  wrongAmount: 'Не та сумма',
  wrongClient: 'Не тот клиент',
  other: 'Другое',
};

export const TOPUP_CANCEL_REASONS: TopUpCancelReason[] = ['wrongAmount', 'wrongClient', 'other'];

/** Product categories of the shop, in the order the bar shows them. */
export const PRODUCT_CATEGORY_LABEL: Record<string, string> = {
  drink: 'Напитки',
  food: 'Еда',
  snack: 'Снеки',
  service: 'Услуги',
  merch: 'Мерч',
  time: 'Пакеты времени',
};

/**
 * A guest's name as the console shows and prints it: the server names a guest the cashier did not name «Гость <PC
 * number>» in Russian whatever the console's language, so that default is said in the language of the screen or slip
 * (`t` at the time of the call); a name the cashier typed stays as typed.
 */
export function guestDisplayName(name: string): string {
  const n = /^Гость (\d+)$/.exec(name)?.[1];
  return n ? t('Гость {n}', { n }) : name;
}

/**
 * How a top-up was paid, read from its ledger description (a transaction carries no method field): the server writes
 * «наличными», «картой», «через Payme». Null when it says none.
 */
function methodIn(description: string): PayMethod | null {
  if (/налич|cash/i.test(description)) return 'cash';
  if (/карт|card/i.test(description)) return 'card';
  for (const m of ['payme', 'click', 'uzum'] as const) if (new RegExp(m, 'i').test(description)) return m;
  return null;
}

/** What follows the first «: » or « · » of a description (the goods of a bar sale, the reason of a write-off). */
function after(description: string): string | null {
  const m = /^[^:·]*?(?::|\s·)\s*(.+)$/.exec(description);
  return m?.[1]?.trim() || null;
}

/**
 * A wallet row as the console names it (the client card's «Последние операции»): a label in the console's language from
 * the row's type and sign, with the payment method where the description names one, and a detail only where the
 * description says more than the type — the minutes and tariff of play time, the goods of a bar sale, the promo code or
 * rule of a bonus, the reason of a write-off. The server writes descriptions in Russian whatever the console's language,
 * so known ones are read, not shown as they are.
 */
export function txView(x: { type: string; amount: { amount: number }; description: string; bonus?: boolean | null }): {
  label: string;
  detail: string | null;
} {
  const d = x.description ?? '';
  const plus = x.amount.amount >= 0;
  const method = methodIn(d);
  const withVia = (label: string): string => (method ? `${t(label)} · ${t(PAY_METHOD_LABEL[method])}` : t(label));
  switch (x.type) {
    case 'topUp':
      if (!plus) return { label: withVia('Отмена пополнения'), detail: null };
      return { label: withVia(/долг|debt/i.test(d) ? 'Оплата долга' : 'Пополнение'), detail: null };
    case 'charge': {
      const minutes = /(\d+)\s*мин/.exec(d)?.[1];
      const tariff = /·\s*([^·]+?)(?:\s·\sбонусы)?$/.exec(d)?.[1]?.trim();
      const label = /^Продление/.test(d) ? 'Продление' : /^Постоплата/.test(d) ? 'Постоплата' : 'Игровое время';
      const detail = [minutes ? t('{n} мин', { n: minutes }) : null, tariff && tariff !== 'бонусы' ? tariff : null]
        .filter(Boolean)
        .join(' · ');
      return { label: x.bonus ? `${t(label)} · ${t('бонусами')}` : t(label), detail: detail || null };
    }
    case 'refund':
      return { label: x.bonus ? `${t('Возврат времени')} · ${t('на бонусы')}` : t('Возврат времени'), detail: null };
    case 'bonus': {
      if (!plus) return { label: t('Бонус списан'), detail: /пополнен/i.test(d) ? t('отмена пополнения') : null };
      const code = /^Промокод\s+(\S+)/.exec(d)?.[1];
      const rule = /^Бонус:\s*(.+)$/.exec(d)?.[1];
      const pct = /\+(\d+)%/.exec(d)?.[1];
      const detail = code
        ? t('промокод {code}', { code })
        : rule
          ? rule
          : /пополнен/i.test(d)
            ? pct
              ? t('за пополнение, акция +{pct}%', { pct })
              : t('за пополнение')
            : /лояльн|loyalty/i.test(d)
              ? t('лояльность')
              : null;
      return { label: t('Бонус'), detail };
    }
    case 'purchase':
      return { label: plus ? t('Отмена покупки в баре') : t('Бар'), detail: plus ? null : after(d) };
    case 'adjustment':
      if (/^Выдано наличными/.test(d)) return { label: t('Выдано наличными'), detail: null };
      if (/^Списание долга:/.test(d)) return { label: t('Списание долга'), detail: after(d) };
      if (/удалении клиента/.test(d)) return { label: t('Списание при удалении клиента'), detail: null };
      return { label: t('Корректировка'), detail: d || null };
    default:
      return { label: d || t('Операция'), detail: null };
  }
}
