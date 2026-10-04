/**
 * What the counter needs from the club's settings and the signed-in staff member: the club's name and the cashier's for
 * the slips, and the limits that decide what the seat panel offers (`guestPostpaid`, `memberDebtLimit`). `App` fills it
 * from the `clubApi.settings()` call it already makes and refreshes it every minute and on request, so a setting the
 * owner changes reaches the counter without a sign-out.
 */
import { createContext, useContext } from 'react';
import type { ClubSettings, StaffMember } from '@/api';

export interface ClubState {
  clubName: string | null;
  limits: ClubSettings['limits'] | null;
  staff: StaffMember | null;
  /** Reads the settings again (after the owner saved them, before a decision that depends on them). */
  reload: () => void;
}

export const ClubContext = createContext<ClubState>({
  clubName: null,
  limits: null,
  staff: null,
  reload: () => undefined,
});

export function useClub(): ClubState {
  return useContext(ClubContext);
}
