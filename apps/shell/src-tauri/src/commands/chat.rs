//! `chat_*` commands (`TAURI_COMMANDS.md` §2.7) → `chat.*` IPC messages. `roomId` defaults to the
//! PC support room (`pc:<pcId>`) on the Agent.

use clubshell_protocol::commands::{names, ChatHistoryRequest, ChatHistoryResponse, ChatMarkReadRequest, ChatMarkReadResponse, ChatSendRequest};
use clubshell_protocol::user::{chat_rooms, ChatMessage};
use tauri::State;
use uuid::Uuid;

use super::validate;
use crate::state::{AppState, CmdResult, ShellError};

const ROOM_ID_MAX: usize = 128;

/// `chat_history` → `chat.history`; `limit` ≤ 200.
#[tauri::command]
pub async fn chat_history(state: State<'_, AppState>, q: Option<ChatHistoryRequest>) -> CmdResult<ChatHistoryResponse> {
    let mut q = q.unwrap_or_default();
    validate::optional_range("limit", q.limit, 1, ChatHistoryRequest::MAX_LIMIT)?;
    q.room_id = validate::optional_text("roomId", q.room_id, ROOM_ID_MAX)?;
    state.agent.request(names::chat::HISTORY, &q).await
}

/// `chat_send` → `chat.send`; `text` 1–2000 chars. `idempotencyKey` is generated here when the
/// frontend omits it (then a retry is a new message).
#[tauri::command]
pub async fn chat_send(state: State<'_, AppState>, text: String, room_id: Option<String>, idempotency_key: Option<Uuid>) -> CmdResult<ChatMessage> {
    let text = validate::required_text("text", &text, chat_rooms::MAX_TEXT_LENGTH)?;
    let room_id = validate::optional_text("roomId", room_id, ROOM_ID_MAX)?;
    let idempotency_key = idempotency_key.filter(|k| !k.is_nil()).unwrap_or_else(Uuid::new_v4);
    state.agent.request(names::chat::SEND, &ChatSendRequest { text, idempotency_key, room_id }).await
}

/// `chat_mark_read` → `chat.markRead`.
#[tauri::command]
pub async fn chat_mark_read(state: State<'_, AppState>, up_to_message_id: Uuid, room_id: Option<String>) -> CmdResult<ChatMarkReadResponse> {
    if up_to_message_id.is_nil() {
        return Err(ShellError::validation("upToMessageId", "required"));
    }
    let room_id = validate::optional_text("roomId", room_id, ROOM_ID_MAX)?;
    state.agent.request(names::chat::MARK_READ, &ChatMarkReadRequest { up_to_message_id, room_id }).await
}
