import type { Money } from '@clubshell/contracts';
import { AdminError } from '@/api';
import { money } from '@/format';
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
    const detail = [d['rule'], d['reason'], d['state'], d['code']].find(
      (v): v is string => typeof v === 'string' && v in DETAIL_COPY,
    );
    if (detail) return t(DETAIL_COPY[detail] as string);
    const copy = ERROR_COPY[e.code];
    return copy ? t(copy) : e.message;
  }
  return e instanceof Error ? e.message : t('Ошибка');
}
