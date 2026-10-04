import type { Money } from '@clubshell/contracts';
import { AdminError } from '@/api';
import { money, moneyExact } from '@/format';
import { t } from '@/i18n';

const ERROR_COPY: Record<string, string> = {
  insufficientFunds: 'Недостаточно средств на балансе',
  sessionAlreadyActive: 'На этом ПК или у клиента уже открыт сеанс',
  policyDenied: 'Действие запрещено правилами клуба',
  notFound: 'Не найдено',
  unauthorized: 'Неверный PIN или сессия истекла',
  forbidden: 'Доступно только владельцу',
  conflict: 'Действие сейчас невозможно',
  validation: 'Проверьте введённые данные',
  network: 'Нет связи с сервером. Запущен ли mock-сервер (pnpm mock)?',
};

const DETAIL_COPY: Record<string, string> = {
  blacklisted: 'Клиент в чёрном списке',
  minorCurfew: 'Несовершеннолетним нельзя играть в это время',
  shiftOpen: 'Смена уже открыта',
  noShift: 'Смена не открыта',
  shiftClosed: 'Смена не открыта — откройте смену, чтобы принимать деньги',
  pcBusy: 'ПК занят',
  taken: 'Уже занято',
  digits4to8: 'PIN — от 4 до 8 цифр',
  expired: 'Срок действия истёк',
  exhausted: 'Лимит использований исчерпан',
  postpaidNotAllowed: 'Постоплата для гостей выключена в настройках клуба',
  tariffTime: 'Тариф сейчас не действует',
  tariffZone: 'Тариф не для этой зоны',
  pcMaintenance: 'ПК на обслуживании',
  postpaidSession: 'Сеанс на постоплате: продлевать не нужно',
  noDebt: 'Долга нет — он уже оплачен',
  notGuest: 'Выдать наличными можно только гостю',
  guestPlaying: 'Гость ещё играет — сначала завершите сеанс',
  staffOnly: 'Нужен вход кассира, ключ API не подходит',
  ownerOnly: 'Только владелец может это сделать',
  pcOccupied: 'На этом ПК идёт чужой сеанс',
};

/** Validation refusals with a meaning of their own, keyed by `field:reason` (a bare reason would catch unrelated ones). */
const VALIDATION_COPY: Record<string, string> = {
  'prepaid:package': 'Пакет продаётся только с предоплатой',
  'payment:postpaid': 'Постоплата берётся после сеанса, не сейчас',
  'payment:required': 'Гость платит сразу: нужна оплата',
  'note:required': 'Для «Другое» напишите комментарий',
  'note:min': 'Комментарий — не короче 3 символов',
  'note:max': 'Комментарий — не длиннее 200 символов',
};

function isMoney(v: unknown): v is Money {
  return typeof v === 'object' && v !== null && typeof (v as Money).amount === 'number';
}

/** Server error → one line for the cashier, in the console language. */
export function describe(e: unknown): string {
  if (e instanceof AdminError) {
    const d = e.details ?? {};
    // The server says how much was needed and how much the client has: the cashier sees the gap, not just "no money".
    if (e.code === 'insufficientFunds' && isMoney(d['required']) && isMoney(d['available'])) {
      return t('Не хватает {gap}: нужно {required}, на балансе {available}', {
        gap: money({ ...d['required'], amount: Math.max(0, d['required'].amount - d['available'].amount) }),
        required: money(d['required']),
        available: money(d['available']),
      });
    }
    // Amounts that changed under the cashier: the new figure, to the tiyin, so the next try is the right one.
    if (e.code === 'conflict') {
      const reason = d['reason'];
      if (reason === 'cashShort' && isMoney(d['available']))
        return t('В кассе только {available}', { available: moneyExact(d['available'].amount) });
      if (reason === 'debtChanged' && isMoney(d['debt']))
        return t('Долг изменился: {debt}', { debt: moneyExact(d['debt'].amount) });
      if (reason === 'payableChanged' && isMoney(d['payable']))
        return t('К выдаче теперь {payable}', { payable: moneyExact(d['payable'].amount) });
      if (reason === 'priceChanged' && isMoney(d['total']))
        return t('Цена изменилась: {total}', { total: moneyExact(d['total'].amount) });
    }
    if (e.code === 'validation') {
      const copy = VALIDATION_COPY[`${String(d['field'])}:${String(d['reason'])}`];
      if (copy) return t(copy);
    }
    const detail = [d['rule'], d['reason'], d['state'], d['code']].find(
      (v): v is string => typeof v === 'string' && v in DETAIL_COPY,
    );
    if (detail) return t(DETAIL_COPY[detail] as string);
    const copy = ERROR_COPY[e.code];
    return copy ? t(copy) : e.message;
  }
  return e instanceof Error ? e.message : t('Ошибка');
}

/** A refusal's amount to take next time (`debtChanged {debt}`, `payableChanged {payable}`, `priceChanged {total}`). */
export function changedAmount(e: unknown, reason: string, field: string): number | null {
  if (!(e instanceof AdminError) || e.code !== 'conflict' || e.details?.['reason'] !== reason) return null;
  const m = e.details[field];
  return isMoney(m) ? m.amount : null;
}

/** True for an answer that never came (no response or 5xx): the money may have been booked; retry under the same key. */
export function isLostAnswer(e: unknown): boolean {
  return e instanceof AdminError && (e.status === 0 || e.status >= 500);
}
