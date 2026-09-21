//! `session_*` commands (`TAURI_COMMANDS.md` §2.2) plus the user-scoped groups the doc lists
//! without a module of their own: `booking_*` (§2.8), `tournaments_*` (§2.9) and `profile_*`
//! (§2.10). Every command that returns a `Session` refreshes `AppState::session_cache`; every
//! command that returns a `User` refreshes `AppState::user_cache`.

use chrono::{DateTime, Duration, NaiveDate, Utc};
use clubshell_protocol::commands::{
    names, BookingCancelRequest, BookingReserveRequest, BookingSeatsRequest, BookingSeatsResponse, ProfileAchievementsResponse,
    SessionEndRequest, SessionExtendRequest, SessionLockRequest, SessionPauseRequest, SessionStartRequest,
    SessionTimeLeftResponse, SessionUnlockRequest, TournamentsJoinRequest, TournamentsLeaderboardRequest,
    TournamentsLeaderboardResponse, TournamentsListRequest, TournamentsListResponse,
};
use clubshell_protocol::session::{Session, SessionEndReason, SessionEndResult};
use clubshell_protocol::user::{Achievement, Booking, Loyalty, ProfileUpdateRequest, Tournament, User, UserStats};
use serde::Serialize;
use tauri::State;
use uuid::Uuid;

use super::validate;
use crate::state::{AppState, CmdResult, ShellError};

/// Upper bound for `minutes` in `session_start` / `session_extend` (7 days); the tariff's own
/// `minMinutes`/`maxMinutes` are enforced by the Agent.
const MAX_MINUTES: i32 = 7 * 24 * 60;
/// Free-text `reason` fields.
const REASON_MAX: usize = 200;
const PASSWORD_MAX: usize = 128;
const DISPLAY_NAME_MAX: usize = 64;
const AVATAR_URL_MAX: usize = 2048;
/// Longest booking accepted before asking the Agent (24 h).
const BOOKING_MAX_HOURS: i64 = 24;
/// `tournaments.leaderboard` limit ≤ 100.
const LEADERBOARD_MAX: i32 = 100;

/// Sends a session-scoped request and caches the returned `Session`.
async fn call_session<Req: Serialize + ?Sized>(state: &AppState, name: &str, req: &Req) -> CmdResult<Session> {
    let session: Session = state.agent.request(name, req).await?;
    state.set_session(Some(session.clone()));
    Ok(session)
}

/// Sends a request and caches the returned `User`.
async fn call_user<Req: Serialize + ?Sized>(state: &AppState, name: &str, req: &Req) -> CmdResult<User> {
    let user: User = state.agent.request(name, req).await?;
    state.set_user(Some(user.clone()));
    Ok(user)
}

// ───────────────────────────── session ─────────────────────────────

/// `session_get` → `session.get`; `null` when there is no session.
#[tauri::command]
pub async fn session_get(state: State<'_, AppState>) -> CmdResult<Option<Session>> {
    let session: Option<Session> = state.agent.request_optional(names::session::GET, &()).await?;
    state.set_session(session.clone());
    Ok(session)
}

/// `session_start` → `session.start`.
#[tauri::command]
pub async fn session_start(state: State<'_, AppState>, req: SessionStartRequest) -> CmdResult<Session> {
    validate::optional_range("minutes", req.minutes, 1, MAX_MINUTES)?;
    if req.tariff_id.is_nil() {
        return Err(ShellError::validation("tariffId", "required"));
    }
    let session = call_session(&state, names::session::START, &req).await?;
    tracing::info!(session_id = %session.id, tariff_id = %req.tariff_id, prepaid = req.prepaid, minutes = ?req.minutes, "session started");
    Ok(session)
}

/// `session_pause` → `session.pause`.
#[tauri::command]
pub async fn session_pause(state: State<'_, AppState>, reason: Option<String>) -> CmdResult<Session> {
    let reason = validate::optional_text("reason", reason, REASON_MAX)?;
    call_session(&state, names::session::PAUSE, &SessionPauseRequest { reason }).await
}

/// `session_resume` → `session.resume`.
#[tauri::command]
pub async fn session_resume(state: State<'_, AppState>) -> CmdResult<Session> {
    call_session(&state, names::session::RESUME, &()).await
}

/// `session_end` → `session.end`; `reason` defaults to `user` on the Agent. Clears the session
/// cache (the `session.ended` event does the same and re-arms the kiosk guard).
#[tauri::command]
pub async fn session_end(state: State<'_, AppState>, reason: Option<SessionEndReason>) -> CmdResult<SessionEndResult> {
    let result: SessionEndResult = state.agent.request(names::session::END, &SessionEndRequest { reason }).await?;
    state.set_session(None);
    tracing::info!(session_id = %result.session.id, charged = %result.charged, refunded = %result.refunded, "session ended");
    Ok(result)
}

/// `session_extend` → `session.extend`.
#[tauri::command]
pub async fn session_extend(state: State<'_, AppState>, minutes: i32, tariff_id: Option<Uuid>) -> CmdResult<Session> {
    validate::range("minutes", minutes, 1, MAX_MINUTES)?;
    call_session(&state, names::session::EXTEND, &SessionExtendRequest { minutes, tariff_id }).await
}

/// `session_lock` → `session.lock`.
#[tauri::command]
pub async fn session_lock(state: State<'_, AppState>, reason: Option<String>) -> CmdResult<Session> {
    let reason = validate::optional_text("reason", reason, REASON_MAX)?;
    call_session(&state, names::session::LOCK, &SessionLockRequest { reason }).await
}

/// `session_unlock` → `session.unlock`; exactly one of `password` / `pin` (4–6 digits).
#[tauri::command]
pub async fn session_unlock(state: State<'_, AppState>, password: Option<String>, pin: Option<String>) -> CmdResult<Session> {
    let req = validate_unlock(password, pin)?;
    call_session(&state, names::session::UNLOCK, &req).await
}

/// `session_time_left` → `session.timeLeft` (drift correction; never errors when idle).
#[tauri::command]
pub async fn session_time_left(state: State<'_, AppState>) -> CmdResult<SessionTimeLeftResponse> {
    state.agent.request(names::session::TIME_LEFT, &()).await
}

fn validate_unlock(password: Option<String>, pin: Option<String>) -> CmdResult<SessionUnlockRequest> {
    let password = password.filter(|p| !p.is_empty());
    let pin = pin.map(|p| p.trim().to_owned()).filter(|p| !p.is_empty());
    match (password, pin) {
        (None, None) => Err(ShellError::validation("pin", "password or pin required")),
        (Some(_), Some(_)) => Err(ShellError::validation("pin", "give either password or pin, not both")),
        (Some(password), None) => {
            if password.chars().count() > PASSWORD_MAX {
                return Err(ShellError::validation("password", &format!("must be at most {PASSWORD_MAX} characters")));
            }
            Ok(SessionUnlockRequest { password: Some(password), pin: None })
        }
        (None, Some(pin)) => {
            validate::pin("pin", &pin)?;
            Ok(SessionUnlockRequest { password: None, pin: Some(pin) })
        }
    }
}

// ───────────────────────────── booking (§2.8) ─────────────────────────────

/// `booking_seats` → `booking.seats`; `date` is the club-local `YYYY-MM-DD`.
#[tauri::command]
pub async fn booking_seats(state: State<'_, AppState>, date: NaiveDate) -> CmdResult<BookingSeatsResponse> {
    state.agent.request(names::booking::SEATS, &BookingSeatsRequest { date }).await
}

/// `booking_reserve` → `booking.reserve`; `from < to`, at most 24 h, not in the past.
#[tauri::command]
pub async fn booking_reserve(state: State<'_, AppState>, pc_id: Uuid, from: DateTime<Utc>, to: DateTime<Utc>) -> CmdResult<Booking> {
    validate_booking(pc_id, from, to, Utc::now())?;
    state.agent.request(names::booking::RESERVE, &BookingReserveRequest { pc_id, from, to }).await
}

/// `booking_cancel` → `booking.cancel`.
#[tauri::command]
pub async fn booking_cancel(state: State<'_, AppState>, booking_id: Uuid) -> CmdResult<Booking> {
    state.agent.request(names::booking::CANCEL, &BookingCancelRequest { booking_id }).await
}

fn validate_booking(pc_id: Uuid, from: DateTime<Utc>, to: DateTime<Utc>, now: DateTime<Utc>) -> CmdResult<()> {
    if pc_id.is_nil() {
        return Err(ShellError::validation("pcId", "required"));
    }
    if to <= from {
        return Err(ShellError::validation("to", "must be after from"));
    }
    if to - from > Duration::hours(BOOKING_MAX_HOURS) {
        return Err(ShellError::validation("to", &format!("booking longer than {BOOKING_MAX_HOURS} hours")));
    }
    if to <= now {
        return Err(ShellError::validation("to", "must be in the future"));
    }
    Ok(())
}

// ───────────────────────────── tournaments (§2.9) ─────────────────────────────

/// `tournaments_list` → `tournaments.list` (unwrapped `items`).
#[tauri::command]
pub async fn tournaments_list(state: State<'_, AppState>, q: Option<TournamentsListRequest>) -> CmdResult<Vec<Tournament>> {
    let resp: TournamentsListResponse = state.agent.request(names::tournaments::LIST, &q.unwrap_or_default()).await?;
    Ok(resp.items)
}

/// `tournaments_join` → `tournaments.join`.
#[tauri::command]
pub async fn tournaments_join(state: State<'_, AppState>, tournament_id: Uuid) -> CmdResult<Tournament> {
    state.agent.request(names::tournaments::JOIN, &TournamentsJoinRequest { tournament_id }).await
}

/// `tournaments_leaderboard` → `tournaments.leaderboard`; `limit` ≤ 100.
#[tauri::command]
pub async fn tournaments_leaderboard(
    state: State<'_, AppState>,
    tournament_id: Uuid,
    limit: Option<i32>,
) -> CmdResult<TournamentsLeaderboardResponse> {
    validate::optional_range("limit", limit, 1, LEADERBOARD_MAX)?;
    state.agent.request(names::tournaments::LEADERBOARD, &TournamentsLeaderboardRequest { tournament_id, limit }).await
}

// ───────────────────────────── profile (§2.10) ─────────────────────────────

/// `profile_get` → `profile.get`.
#[tauri::command]
pub async fn profile_get(state: State<'_, AppState>) -> CmdResult<User> {
    call_user(&state, names::profile::GET, &()).await
}

/// `profile_update` → `profile.update`; at least one field. `pin` (4–6 digits) is never logged.
#[tauri::command]
pub async fn profile_update(state: State<'_, AppState>, patch: ProfileUpdateRequest) -> CmdResult<User> {
    let patch = validate_profile_patch(patch)?;
    call_user(&state, names::profile::UPDATE, &patch).await
}

/// `profile_stats` → `profile.stats`.
#[tauri::command]
pub async fn profile_stats(state: State<'_, AppState>) -> CmdResult<UserStats> {
    state.agent.request(names::profile::STATS, &()).await
}

/// `profile_achievements` → `profile.achievements` (unwrapped `items`).
#[tauri::command]
pub async fn profile_achievements(state: State<'_, AppState>) -> CmdResult<Vec<Achievement>> {
    let resp: ProfileAchievementsResponse = state.agent.request(names::profile::ACHIEVEMENTS, &()).await?;
    Ok(resp.items)
}

/// `profile_loyalty` → `profile.loyalty`.
#[tauri::command]
pub async fn profile_loyalty(state: State<'_, AppState>) -> CmdResult<Loyalty> {
    state.agent.request(names::profile::LOYALTY, &()).await
}

fn validate_profile_patch(mut patch: ProfileUpdateRequest) -> CmdResult<ProfileUpdateRequest> {
    if patch == ProfileUpdateRequest::default() {
        return Err(ShellError::validation("patch", "at least one field required"));
    }
    if let Some(name) = patch.display_name.take() {
        patch.display_name = Some(validate::required_text("displayName", &name, DISPLAY_NAME_MAX)?);
    }
    if let Some(url) = patch.avatar_url.take() {
        let url = validate::required_text("avatarUrl", &url, AVATAR_URL_MAX)?;
        if !(url.starts_with("https://") || url.starts_with("http://")) {
            return Err(ShellError::validation("avatarUrl", "must be an http(s) URL"));
        }
        patch.avatar_url = Some(url);
    }
    if let Some(pin) = patch.pin.as_deref() {
        validate::pin("pin", pin)?;
    }
    Ok(patch)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unlock_needs_exactly_one_secret() {
        assert!(validate_unlock(None, None).is_err());
        assert!(validate_unlock(Some("p".into()), Some("1234".into())).is_err());
        assert!(validate_unlock(None, Some("12".into())).is_err());
        assert_eq!(validate_unlock(None, Some(" 1234 ".into())).unwrap().pin.as_deref(), Some("1234"));
        assert_eq!(validate_unlock(Some("pw".into()), None).unwrap().password.as_deref(), Some("pw"));
        assert!(validate_unlock(Some(String::new()), None).is_err(), "empty password counts as absent");
    }

    #[test]
    fn booking_window_is_checked() {
        let now = Utc::now();
        let pc = Uuid::from_u128(1);
        assert!(validate_booking(pc, now + Duration::hours(1), now + Duration::hours(2), now).is_ok());
        assert!(validate_booking(Uuid::nil(), now + Duration::hours(1), now + Duration::hours(2), now).is_err());
        assert!(validate_booking(pc, now + Duration::hours(2), now + Duration::hours(1), now).is_err());
        assert!(validate_booking(pc, now + Duration::hours(1), now + Duration::hours(26), now).is_err());
        assert!(validate_booking(pc, now - Duration::hours(3), now - Duration::hours(1), now).is_err());
    }

    #[test]
    fn profile_patch_is_checked() {
        assert!(validate_profile_patch(ProfileUpdateRequest::default()).is_err());
        let ok = validate_profile_patch(ProfileUpdateRequest { display_name: Some(" Bob ".into()), ..Default::default() }).unwrap();
        assert_eq!(ok.display_name.as_deref(), Some("Bob"));
        assert!(validate_profile_patch(ProfileUpdateRequest { avatar_url: Some("ftp://x".into()), ..Default::default() }).is_err());
        assert!(validate_profile_patch(ProfileUpdateRequest { pin: Some("12".into()), ..Default::default() }).is_err());
        assert!(validate_profile_patch(ProfileUpdateRequest { pin: Some("123456".into()), ..Default::default() }).is_ok());
    }
}
