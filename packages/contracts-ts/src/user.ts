/**
 * User contracts — mirror of `ClubShell.Contracts.Users` (UserProfile.cs, AuthRequest.cs).
 * Notification types live in `events.ts`.
 */
import type { PcStatus } from './pc.js';
import type { Session, SessionEndReason } from './session.js';
import type { Money } from './wallet.js';

/** User role; ordered by privilege (see {@link USER_ROLE_RANK}). */
export const UserRole = {
  Guest: 'guest',
  Member: 'member',
  Vip: 'vip',
  Admin: 'admin',
} as const;
/** User role; ordered by privilege. */
export type UserRole = (typeof UserRole)[keyof typeof UserRole];

// ---- BEGIN MANUAL ----
/** Privilege rank of each role (higher = more privileged). */
export const USER_ROLE_RANK: Readonly<Record<UserRole, number>> = { guest: 0, member: 1, vip: 2, admin: 3 };
// ---- END MANUAL ----

/** UI locale. */
export const Locale = {
  En: 'en',
  Ru: 'ru',
  Uz: 'uz',
} as const;
/** UI locale. */
export type Locale = (typeof Locale)[keyof typeof Locale];

/** Well-known values of `User.flags`. */
export const UserFlags = {
  Banned: 'banned',
  NoShop: 'noShop',
  Staff: 'staff',
} as const;
/** Well-known user flag. */
export type UserFlag = (typeof UserFlags)[keyof typeof UserFlags];

/** Player account (IPC_PROTOCOL.md §6.3). */
export interface User {
  /** User id. */
  id: string;
  /** Login name, 3–32 chars. */
  username: string;
  /** Display name. */
  displayName: string;
  /** Absolute avatar URL. */
  avatarUrl?: string | null;
  /** Role. */
  role: UserRole;
  /** Main balance (excludes bonus). */
  balance: Money;
  /** 0-based loyalty level. */
  loyaltyLevel: number;
  /** Loyalty points. */
  loyaltyPoints: number;
  /** Registration time. */
  createdAt: string;
  /** Last activity. */
  lastSeenAt?: string | null;
  /** Preferred locale. */
  locale: Locale;
  /** Free-form server flags ({@link UserFlags}); empty allowed. */
  flags: string[];
}

// ---- BEGIN MANUAL ----
/** `true` when `flag` is present on the user (ordinal comparison). */
export function userHasFlag(user: User, flag: string): boolean {
  return user.flags.includes(flag);
}

/** `true` when the user is a registered member or higher. */
export function isUserMember(user: User): boolean {
  return USER_ROLE_RANK[user.role] >= USER_ROLE_RANK.member;
}
// ---- END MANUAL ----

/** Body of `profile.update` and `PATCH /users/{userId}`; all fields optional. */
export interface ProfileUpdateRequest {
  /** New display name. */
  displayName?: string | null;
  /** New avatar URL. */
  avatarUrl?: string | null;
  /** New locale. */
  locale?: Locale | null;
  /** New 4–6 digit PIN used by `session.unlock`. */
  pin?: string | null;
}

/** Play time of one game for the stats view. */
export interface FavoriteGame {
  /** Game. */
  gameId: string;
  /** Hours played. */
  hours: number;
}

/** Aggregate statistics of a user (IPC_PROTOCOL.md §6.17). */
export interface UserStats {
  /** Total hours played. */
  totalHours: number;
  /** Number of sessions. */
  sessionsCount: number;
  /** Most played games. */
  favoriteGames: FavoriteGame[];
  /** Total spent. */
  spent: Money;
  /** Club-wide rank. */
  rank: number;
}

/** Progress towards an achievement. */
export interface AchievementProgress {
  /** Current value. */
  current: number;
  /** Value required to unlock. */
  target: number;
}

/** Achievement (IPC_PROTOCOL.md §6.17). */
export interface Achievement {
  /** Achievement id. */
  id: string;
  /** Title. */
  title: string;
  /** Description. */
  description: string;
  /** Icon URL. */
  iconUrl: string;
  /** Unlock time; null while locked. */
  unlockedAt?: string | null;
  /** Progress. */
  progress: AchievementProgress;
}

/** Loyalty programme status (IPC_PROTOCOL.md §6.17). */
export interface Loyalty {
  /** Current level (0-based). */
  level: number;
  /** Current points. */
  points: number;
  /** Points required for the next level. */
  nextLevelAt: number;
  /** Localized perk descriptions. */
  perks: string[];
}

/** Kind of chat message. */
export const ChatMessageKind = {
  Text: 'text',
  System: 'system',
  Admin: 'admin',
} as const;
/** Kind of chat message. */
export type ChatMessageKind = (typeof ChatMessageKind)[keyof typeof ChatMessageKind];

// ---- BEGIN MANUAL ----
/** Chat room id helpers (SERVER_API.md §4.10). */
export const ChatRooms = {
  /** Club-wide room. */
  Club: 'club',
  /** Maximum message length. */
  MaxTextLength: 2000,
  /** Support room of a PC: `pc:<pcId>`. */
  forPc: (pcId: string): string => `pc:${pcId}`,
  /** Zone room: `zone:<zone>`. */
  forZone: (zone: string): string => {
    if (zone.length === 0) {
      throw new RangeError('zone must be non-empty');
    }
    return `zone:${zone}`;
  },
  /** Direct-message room: `dm:<a>:<b>` with ids sorted. */
  forDirect: (a: string, b: string): string => (a <= b ? `dm:${a}:${b}` : `dm:${b}:${a}`),
} as const;
// ---- END MANUAL ----

/** Chat message (IPC_PROTOCOL.md §6.14). */
export interface ChatMessage {
  /** Message id. */
  id: string;
  /** Room ({@link ChatRooms}). */
  roomId: string;
  /** Sender user id. */
  senderId: string;
  /** Sender display name. */
  senderName: string;
  /** Sender role. */
  senderRole: UserRole;
  /** Text, ≤ 2000 chars. */
  text: string;
  /** Send time. */
  createdAt: string;
  /** When the current user read it. */
  readAt?: string | null;
  /** Kind. */
  kind: ChatMessageKind;
}

/** Body of `POST /chat/{roomId}/messages`. */
export interface ChatPostRequest {
  /** Message text. */
  text: string;
}

/** Body of `POST /chat/{roomId}/read`. */
export interface ChatReadRequest {
  /** Mark everything up to and including this message as read. */
  upToMessageId: string;
}

/** Booking lifecycle. */
export const BookingStatus = {
  Reserved: 'reserved',
  Confirmed: 'confirmed',
  Cancelled: 'cancelled',
  Expired: 'expired',
} as const;
/** Booking lifecycle. */
export type BookingStatus = (typeof BookingStatus)[keyof typeof BookingStatus];

/** Seat on the club map (IPC_PROTOCOL.md §6.15). */
export interface Seat {
  /** PC. */
  pcId: string;
  /** PC name. */
  name: string;
  /** Zone. */
  zone: string;
  /** Grid column. */
  x: number;
  /** Grid row. */
  y: number;
  /** Current status. */
  status: PcStatus;
}

/** Seat reservation; in `booking.seats` lists `userId` is the caller's id or the empty UUID for others. */
export interface Booking {
  /** Booking id. */
  id: string;
  /** Owner (anonymized for others). */
  userId: string;
  /** PC. */
  pcId: string;
  /** Start. */
  from: string;
  /** End. */
  to: string;
  /** Status. */
  status: BookingStatus;
}

// ---- BEGIN MANUAL ----
/** `userId` of other users' bookings in `booking.seats` lists. */
export const ANONYMOUS_USER_ID = '00000000-0000-0000-0000-000000000000';
// ---- END MANUAL ----

/** Body of `POST /booking/reserve` (sent with an `Idempotency-Key`). */
export interface BookingCreateRequest {
  /** Owner. */
  userId: string;
  /** PC. */
  pcId: string;
  /** Start (slot-aligned). */
  from: string;
  /** End (slot-aligned). */
  to: string;
}

/** Tournament lifecycle. */
export const TournamentState = {
  Upcoming: 'upcoming',
  Registration: 'registration',
  Live: 'live',
  Finished: 'finished',
} as const;
/** Tournament lifecycle. */
export type TournamentState = (typeof TournamentState)[keyof typeof TournamentState];

/** Bracket match; `a`, `b`, `winner` are user ids. */
export interface BracketMatch {
  /** Match id. */
  id: string;
  /** First player. */
  a?: string | null;
  /** Second player. */
  b?: string | null;
  /** Winner. */
  winner?: string | null;
  /** Score, e.g. `2-1`. */
  score?: string | null;
}

/** Bracket round. */
export interface BracketRound {
  /** Matches in this round. */
  matches: BracketMatch[];
}

/** Tournament bracket. */
export interface Bracket {
  /** Rounds in order. */
  rounds: BracketRound[];
}

/** Tournament (IPC_PROTOCOL.md §6.16). */
export interface Tournament {
  /** Tournament id. */
  id: string;
  /** Title. */
  title: string;
  /** Game. */
  gameId: string;
  /** Start time. */
  startsAt: string;
  /** State. */
  state: TournamentState;
  /** Prize pool. */
  prizePool: Money;
  /** Capacity. */
  maxPlayers: number;
  /** Current player count. */
  players: number;
  /** Whether the current user has joined. */
  joined: boolean;
  /** Bracket, when published. */
  bracket?: Bracket | null;
}

/** Leaderboard row (IPC_PROTOCOL.md §6.16). */
export interface LeaderboardEntry {
  /** 1-based rank. */
  rank: number;
  /** User. */
  userId: string;
  /** Display name. */
  name: string;
  /** Score. */
  score: number;
  /** Avatar URL. */
  avatarUrl?: string | null;
}

/** Authentication method. */
export const AuthKind = {
  Password: 'password',
  Qr: 'qr',
  Guest: 'guest',
  Card: 'card',
  Token: 'token',
} as const;
/** Authentication method. */
export type AuthKind = (typeof AuthKind)[keyof typeof AuthKind];

/** State of a QR login handshake. */
export const QrStatus = {
  Pending: 'pending',
  Scanned: 'scanned',
  Confirmed: 'confirmed',
  Expired: 'expired',
} as const;
/** State of a QR login handshake. */
export type QrStatus = (typeof QrStatus)[keyof typeof QrStatus];

/** Why the user context was invalidated (`auth.expired` event). */
export const AuthExpiredReason = {
  TokenExpired: 'tokenExpired',
  Revoked: 'revoked',
  Admin: 'admin',
} as const;
/** Why the user context was invalidated (`auth.expired` event). */
export type AuthExpiredReason = (typeof AuthExpiredReason)[keyof typeof AuthExpiredReason];

/** Server-facing login request (`POST /auth/login`); the IPC `auth.login` payload is the subset without `pcId`/`hwid`. */
export interface AuthRequest {
  /** Method. */
  kind: AuthKind;
  /** Login name (`password`). */
  username?: string | null;
  /** Password (`password`); never logged. */
  password?: string | null;
  /** QR token (`qr`). */
  qrToken?: string | null;
  /** Card id (`card`). */
  cardId?: string | null;
  /** One-time token (`token`). */
  token?: string | null;
  /** PC the login originates from. */
  pcId: string;
  /** Hardware id of the PC (sha256 hex). */
  hwid: string;
}

// ---- BEGIN MANUAL ----
/** Copy of an auth request with secrets blanked (`***`), for logging/telemetry. */
export function redactAuthRequest<T extends Partial<AuthRequest>>(request: T): T {
  const out: T = { ...request };
  const view: Partial<AuthRequest> = out;
  for (const key of ['password', 'qrToken', 'cardId', 'token'] as const) {
    if (typeof view[key] === 'string') {
      view[key] = '***';
    }
  }
  return out;
}
// ---- END MANUAL ----

/** Result of a successful login (`POST /auth/login`, `POST /auth/guest`, QR confirm). */
export interface AuthResponse {
  /** Authenticated user. */
  user: User;
  /** Open session already bound to this user on this PC (e.g. after an Agent restart). */
  session?: Session | null;
  /** User access token (`X-User-Token`); Agent-held, never sent to the Shell. */
  accessToken: string;
  /** User refresh token. */
  refreshToken: string;
  /** Access token expiry. */
  expiresAt: string;
  /** Argon2id PHC string for offline password verification, when the club allows offline login. */
  offlineHash?: string | null;
}

/** Body of `POST /auth/qr/start`. */
export interface QrStartRequest {
  /** PC requesting the QR login. */
  pcId: string;
}

/** Response of `auth.qrStart` and `POST /auth/qr/start`. */
export interface QrLoginStart {
  /** Opaque token to poll with. */
  qrToken: string;
  /** Deep link to render as a QR code (`https://<server>/q/<qrToken>`). */
  qrUrl: string;
  /** Token expiry. */
  expiresAt: string;
  /** Suggested poll interval. */
  pollIntervalSec: number;
}

/** Response of `GET /auth/qr/{token}`. */
export interface QrLoginStatus {
  /** Handshake state. */
  status: QrStatus;
  /** Set only when `status` is `confirmed` (single read). */
  auth?: AuthResponse | null;
}

/** Body of `POST /auth/guest`. */
export interface GuestAuthRequest {
  /** PC. */
  pcId: string;
  /** Hardware id. */
  hwid: string;
  /** Optional display name. */
  displayName?: string | null;
  /** Preferred locale. */
  locale?: Locale | null;
}

/** Body of `POST /auth/logout`. */
export interface LogoutRequest {
  /** Why the user is logged out; recorded as the session end reason. */
  reason: SessionEndReason;
}
