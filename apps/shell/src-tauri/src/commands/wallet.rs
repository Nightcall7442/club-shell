//! `wallet_*` commands (`TAURI_COMMANDS.md` §2.5) → `wallet.*` IPC messages.

use clubshell_protocol::commands::{
    names, WalletHistoryRequest, WalletHistoryResponse, WalletTariffsRequest,
    WalletTariffsResponse, WalletTopupIntentRequest,
};
use clubshell_protocol::wallet::{
    Balance, Money, TopupIntent, TopupIntentCreateRequest, TopupProvider,
};
use tauri::State;

use super::validate;
use crate::state::{AppState, CmdResult, ShellError};

const ZONE_MAX: usize = 64;
const HISTORY_PAGE_MAX: i32 = 200;

/// `wallet_balance` → `wallet.balance`.
#[tauri::command]
pub async fn wallet_balance(state: State<'_, AppState>) -> CmdResult<Balance> {
    state.agent.request(names::wallet::BALANCE, &()).await
}

/// `wallet_tariffs` → `wallet.tariffs`; `zone` defaults to this PC's zone on the Agent.
#[tauri::command]
pub async fn wallet_tariffs(
    state: State<'_, AppState>,
    zone: Option<String>,
) -> CmdResult<WalletTariffsResponse> {
    let zone = validate::optional_text("zone", zone, ZONE_MAX)?;
    state
        .agent
        .request(names::wallet::TARIFFS, &WalletTariffsRequest { zone })
        .await
}

/// `wallet_history` → `wallet.history`.
#[tauri::command]
pub async fn wallet_history(
    state: State<'_, AppState>,
    q: Option<WalletHistoryRequest>,
) -> CmdResult<WalletHistoryResponse> {
    let q = q.unwrap_or_default();
    validate::paging(q.page, q.page_size, HISTORY_PAGE_MAX)?;
    if let (Some(from), Some(to)) = (q.from, q.to) {
        if to <= from {
            return Err(ShellError::validation("to", "must be after from"));
        }
    }
    state.agent.request(names::wallet::HISTORY, &q).await
}

/// `wallet_topup_intent` → `wallet.topupIntent`; `amount` ≥ 1 000 UZS (100 000 minor units).
#[tauri::command]
pub async fn wallet_topup_intent(
    state: State<'_, AppState>,
    amount: Money,
    provider: TopupProvider,
) -> CmdResult<TopupIntent> {
    if amount.amount < TopupIntentCreateRequest::MIN_AMOUNT_MINOR {
        return Err(ShellError::validation(
            "amount",
            &format!(
                "must be at least {} minor units",
                TopupIntentCreateRequest::MIN_AMOUNT_MINOR
            ),
        ));
    }
    let intent: TopupIntent = state
        .agent
        .request(
            names::wallet::TOPUP_INTENT,
            &WalletTopupIntentRequest { amount, provider },
        )
        .await?;
    tracing::info!(intent_id = %intent.id, provider = %intent.provider, amount = %intent.amount, "top-up intent created");
    Ok(intent)
}
