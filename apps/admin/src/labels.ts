/**
 * Names the counter gives to codes the server sends (payment methods, cash reasons, the kinds of the operations feed),
 * as Russian source keys of `t()`: the screens, the feed and the printed slips share them.
 */
import type { CallCategory, CashReason, OperationKind, PayMethod, VoidReason } from '@/api';
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
