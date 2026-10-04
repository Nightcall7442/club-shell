/**
 * Names the counter gives to codes the server sends (payment methods, cash reasons, the kinds of the operations feed),
 * as Russian source keys of `t()`: the screens, the feed and the printed slips share them.
 */
import type { CashReason, OperationKind, PayMethod } from '@/api';

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
};
