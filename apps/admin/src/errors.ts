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
  // Cash desk part 3
  outOfStock: 'Столько нет на складе',
  notSellable: 'Этот товар не продаётся на кассе',
  saleExists: 'Эта продажа уже проведена',
  alreadyVoided: 'Продажа уже аннулирована',
  saleShiftClosed: 'Продажа из закрытой смены — аннулировать её нельзя',
  voidWindow: 'Кассир аннулирует продажу только в первые 15 минут — обратитесь к владельцу',
  idempotencyKeyReused: 'Повтор с другими данными — начните действие заново',
  stockChanged: 'Остаток изменился, пока вы редактировали',
  sessionMoved: 'Сеанс уже не на этом ПК — карта обновлена',
  sessionEnding: 'Сеанс завершается',
  targetOffline: 'ПК не на связи',
  targetHasLocalSession: 'На ПК ещё не отправлен свой сеанс — подождите минуту',
};

/** An amount the server sends as minor units or as a `Money`; null when neither. */
export function amountOf(v: unknown): number | null {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  return isMoney(v) ? v.amount : null;
}

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
      if (reason === 'priceChanged' && amountOf(d['total']) !== null)
        return t('Цена изменилась: {total}', { total: moneyExact(amountOf(d['total']) ?? 0) });
      if (reason === 'outOfStock' && typeof d['available'] === 'number')
        return d['available'] > 0 ? t('На складе осталось {n} шт', { n: d['available'] }) : t('Товар закончился');
      if (reason === 'tariffZone') return t('Тариф не для зоны нового ПК — выберите другой');
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
  return amountOf(e.details[field]);
}

/** The `reason` of a refusal, when the server named one. */
export function reasonOf(e: unknown): string | null {
  if (!(e instanceof AdminError)) return null;
  const d = e.details ?? {};
  const r = d['reason'] ?? d['rule'];
  return typeof r === 'string' ? r : null;
}

/** True for an answer that never came (no response or 5xx): the money may have been booked; retry under the same key. */
export function isLostAnswer(e: unknown): boolean {
  return e instanceof AdminError && (e.status === 0 || e.status >= 500);
}
