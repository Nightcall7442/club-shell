//! Mirror of `ClubShell.Contracts.Wallet` (Balance.cs, Tariff.cs, Transaction.cs).

use chrono::{DateTime, NaiveTime, Utc};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

// ---- BEGIN MANUAL ----
use std::cmp::Ordering;
use std::fmt;
use std::iter::Sum;
use std::ops::{Add, AddAssign, Mul, Neg, Sub, SubAssign};

use serde::Deserializer;

/// Currency assumed when none is given.
pub const DEFAULT_CURRENCY: &str = "UZS";

/// Monetary amount in integer minor units (tiyin for UZS), serialized as
/// `{ "amount": 123, "currency": "UZS" }`. `currency` is normalized to upper case and defaults to
/// [`DEFAULT_CURRENCY`] when absent, `null` or blank on the wire (like the C# `MoneyJsonConverter`).
///
/// Arithmetic operators panic on a currency mismatch (the C# operators throw); use
/// [`Money::checked_add`] / [`Money::checked_sub`] for fallible paths. Ordering is only defined
/// within one currency ([`PartialOrd`] returns `None` otherwise).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Hash)]
#[serde(rename_all = "camelCase")]
pub struct Money {
    /// Signed minor units (negative for charges/purchases where noted).
    pub amount: i64,
    /// ISO-4217 code, upper case, never empty.
    #[serde(default = "default_currency", deserialize_with = "deserialize_currency")]
    pub currency: String,
}

fn default_currency() -> String {
    DEFAULT_CURRENCY.to_owned()
}

fn deserialize_currency<'de, D: Deserializer<'de>>(d: D) -> Result<String, D::Error> {
    Ok(normalize_currency(Option::<String>::deserialize(d)?.as_deref()))
}

/// Trims and upper-cases a currency code; blank/absent → [`DEFAULT_CURRENCY`].
pub fn normalize_currency(currency: Option<&str>) -> String {
    match currency.map(str::trim).filter(|s| !s.is_empty()) {
        Some(s) => s.to_ascii_uppercase(),
        None => default_currency(),
    }
}

impl Money {
    /// Amount in `currency` (normalized).
    pub fn new(amount: i64, currency: &str) -> Self {
        Self { amount, currency: normalize_currency(Some(currency)) }
    }

    /// Amount in [`DEFAULT_CURRENCY`].
    pub fn uzs(amount: i64) -> Self {
        Self { amount, currency: default_currency() }
    }

    /// Zero in [`DEFAULT_CURRENCY`].
    pub fn zero() -> Self {
        Self::uzs(0)
    }

    pub fn is_zero(&self) -> bool {
        self.amount == 0
    }

    pub fn is_negative(&self) -> bool {
        self.amount < 0
    }

    pub fn is_positive(&self) -> bool {
        self.amount > 0
    }

    /// `true` when both amounts are denominated in the same currency.
    pub fn is_same_currency(&self, other: &Money) -> bool {
        self.currency == other.currency
    }

    /// Absolute value.
    pub fn abs(&self) -> Money {
        Money { amount: self.amount.abs(), currency: self.currency.clone() }
    }

    /// Sum; `None` on currency mismatch or overflow.
    pub fn checked_add(&self, other: &Money) -> Option<Money> {
        if !self.is_same_currency(other) {
            return None;
        }
        Some(Money { amount: self.amount.checked_add(other.amount)?, currency: self.currency.clone() })
    }

    /// Difference; `None` on currency mismatch or overflow.
    pub fn checked_sub(&self, other: &Money) -> Option<Money> {
        if !self.is_same_currency(other) {
            return None;
        }
        Some(Money { amount: self.amount.checked_sub(other.amount)?, currency: self.currency.clone() })
    }

    /// Scales by an integer factor (unit price × quantity); `None` on overflow.
    pub fn checked_mul(&self, factor: i64) -> Option<Money> {
        Some(Money { amount: self.amount.checked_mul(factor)?, currency: self.currency.clone() })
    }

    fn expect_same_currency(&self, other: &Money) {
        assert!(self.is_same_currency(other), "currency mismatch: {} vs {}", self.currency, other.currency);
    }
}

impl Default for Money {
    fn default() -> Self {
        Self::zero()
    }
}

impl fmt::Display for Money {
    /// `<amount> <currency>` in minor units, e.g. `1500000 UZS`.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{} {}", self.amount, self.currency)
    }
}

impl Add for Money {
    type Output = Money;

    fn add(self, rhs: Money) -> Money {
        self.expect_same_currency(&rhs);
        Money { amount: self.amount + rhs.amount, currency: self.currency }
    }
}

impl<'a> Add<&'a Money> for &'a Money {
    type Output = Money;

    fn add(self, rhs: &Money) -> Money {
        self.expect_same_currency(rhs);
        Money { amount: self.amount + rhs.amount, currency: self.currency.clone() }
    }
}

impl AddAssign for Money {
    fn add_assign(&mut self, rhs: Money) {
        self.expect_same_currency(&rhs);
        self.amount += rhs.amount;
    }
}

impl Sub for Money {
    type Output = Money;

    fn sub(self, rhs: Money) -> Money {
        self.expect_same_currency(&rhs);
        Money { amount: self.amount - rhs.amount, currency: self.currency }
    }
}

impl<'a> Sub<&'a Money> for &'a Money {
    type Output = Money;

    fn sub(self, rhs: &Money) -> Money {
        self.expect_same_currency(rhs);
        Money { amount: self.amount - rhs.amount, currency: self.currency.clone() }
    }
}

impl SubAssign for Money {
    fn sub_assign(&mut self, rhs: Money) {
        self.expect_same_currency(&rhs);
        self.amount -= rhs.amount;
    }
}

impl Neg for Money {
    type Output = Money;

    fn neg(self) -> Money {
        Money { amount: -self.amount, currency: self.currency }
    }
}

impl Mul<i64> for Money {
    type Output = Money;

    fn mul(self, factor: i64) -> Money {
        Money { amount: self.amount * factor, currency: self.currency }
    }
}

impl Mul<i64> for &Money {
    type Output = Money;

    fn mul(self, factor: i64) -> Money {
        Money { amount: self.amount * factor, currency: self.currency.clone() }
    }
}

impl PartialOrd for Money {
    fn partial_cmp(&self, other: &Money) -> Option<Ordering> {
        self.is_same_currency(other).then(|| self.amount.cmp(&other.amount))
    }
}

/// Sums a sequence; an empty sequence yields [`Money::zero`].
impl Sum for Money {
    fn sum<I: Iterator<Item = Money>>(iter: I) -> Money {
        iter.fold(None, |acc: Option<Money>, m| Some(match acc {
            None => m,
            Some(t) => t + m,
        }))
        .unwrap_or_default()
    }
}

impl<'a> Sum<&'a Money> for Money {
    fn sum<I: Iterator<Item = &'a Money>>(iter: I) -> Money {
        iter.cloned().sum()
    }
}
// ---- END MANUAL ----

/// Wallet balance of a user (IPC_PROTOCOL.md §6.12). Payload of `wallet.balance` and the
/// `wallet.updated` event.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Balance {
    pub user_id: Uuid,
    /// Main (real money) balance.
    pub amount: Money,
    /// Bonus balance (promotional, non-withdrawable).
    pub bonus: Money,
    /// ISO-4217 code of both balances.
    pub currency: String,
    /// Last change time; stale when served from cache while offline.
    #[serde(with = "crate::wire::ts")]
    pub updated_at: DateTime<Utc>,
}

// ---- BEGIN MANUAL ----
impl Balance {
    /// Main + bonus.
    pub fn total(&self) -> Money {
        &self.amount + &self.bonus
    }
}
// ---- END MANUAL ----

wire_enum! {
    /// Day of week as used by tariff time windows.
    Weekday {
        Mon = "mon",
        Tue = "tue",
        Wed = "wed",
        Thu = "thu",
        Fri = "fri",
        Sat = "sat",
        Sun = "sun",
    }
}

// ---- BEGIN MANUAL ----
impl Weekday {
    /// Maps to [`chrono::Weekday`].
    pub const fn to_chrono(self) -> chrono::Weekday {
        match self {
            Weekday::Mon => chrono::Weekday::Mon,
            Weekday::Tue => chrono::Weekday::Tue,
            Weekday::Wed => chrono::Weekday::Wed,
            Weekday::Thu => chrono::Weekday::Thu,
            Weekday::Fri => chrono::Weekday::Fri,
            Weekday::Sat => chrono::Weekday::Sat,
            Weekday::Sun => chrono::Weekday::Sun,
        }
    }

    /// Maps from [`chrono::Weekday`].
    pub const fn from_chrono(day: chrono::Weekday) -> Weekday {
        match day {
            chrono::Weekday::Mon => Weekday::Mon,
            chrono::Weekday::Tue => Weekday::Tue,
            chrono::Weekday::Wed => Weekday::Wed,
            chrono::Weekday::Thu => Weekday::Thu,
            chrono::Weekday::Fri => Weekday::Fri,
            chrono::Weekday::Sat => Weekday::Sat,
            chrono::Weekday::Sun => Weekday::Sun,
        }
    }

    /// The day before.
    pub const fn previous(self) -> Weekday {
        match self {
            Weekday::Mon => Weekday::Sun,
            Weekday::Tue => Weekday::Mon,
            Weekday::Wed => Weekday::Tue,
            Weekday::Thu => Weekday::Wed,
            Weekday::Fri => Weekday::Thu,
            Weekday::Sat => Weekday::Fri,
            Weekday::Sun => Weekday::Sat,
        }
    }
}

impl From<chrono::Weekday> for Weekday {
    fn from(day: chrono::Weekday) -> Self {
        Weekday::from_chrono(day)
    }
}

impl From<Weekday> for chrono::Weekday {
    fn from(day: Weekday) -> Self {
        day.to_chrono()
    }
}
// ---- END MANUAL ----

/// Weekly time window during which a tariff is valid (club local time). `to < from` wraps midnight.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct TariffTimeWindow {
    pub days: Vec<Weekday>,
    /// Start time, inclusive (`HH:mm`).
    #[serde(with = "crate::wire::hm")]
    pub from: NaiveTime,
    /// End time, exclusive (`HH:mm`).
    #[serde(with = "crate::wire::hm")]
    pub to: NaiveTime,
}

// ---- BEGIN MANUAL ----
impl TariffTimeWindow {
    /// `true` when `local_time` on `day` falls inside the window (handles midnight wrap).
    pub fn contains(&self, day: Weekday, local_time: NaiveTime) -> bool {
        if self.from == self.to {
            return self.days.contains(&day);
        }
        if self.from < self.to {
            return self.days.contains(&day) && local_time >= self.from && local_time < self.to;
        }
        // Wraps midnight: [from, 24:00) on `day` or [00:00, to) on the following day.
        if local_time >= self.from {
            return self.days.contains(&day);
        }
        local_time < self.to && self.days.contains(&day.previous())
    }
}
// ---- END MANUAL ----

/// Billing tariff (IPC_PROTOCOL.md §6.11).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Tariff {
    pub id: Uuid,
    pub name: String,
    pub price_per_hour: Money,
    /// Minimum purchasable minutes.
    pub min_minutes: i32,
    /// Maximum purchasable minutes; `None` = unlimited.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_minutes: Option<i32>,
    /// Zones the tariff applies to; empty = all.
    pub zones: Vec<String>,
    /// Validity windows; empty = always.
    pub time_windows: Vec<TariffTimeWindow>,
    /// Fixed package (`package_minutes` for `package_price`) instead of hourly billing.
    pub is_package: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub package_minutes: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub package_price: Option<Money>,
}

// ---- BEGIN MANUAL ----
impl Tariff {
    /// Price for `minutes` under this tariff: the package price when `is_package`, otherwise
    /// pro-rata per minute rounded up to the minor unit.
    pub fn price_for(&self, minutes: u32) -> Money {
        if self.is_package {
            if let Some(package) = &self.package_price {
                return package.clone();
            }
        }
        let total = self.price_per_hour.amount * i64::from(minutes);
        let rounded = total / 60 + i64::from(total % 60 != 0);
        Money { amount: rounded, currency: self.price_per_hour.currency.clone() }
    }

    /// `true` when the tariff is valid for `zone` at the given local day/time.
    pub fn is_valid_for(&self, zone: &str, day: Weekday, local_time: NaiveTime) -> bool {
        if !self.zones.is_empty() && !self.zones.iter().any(|z| z.eq_ignore_ascii_case(zone)) {
            return false;
        }
        self.time_windows.is_empty() || self.time_windows.iter().any(|w| w.contains(day, local_time))
    }
}
// ---- END MANUAL ----

wire_enum! {
    /// Kind of wallet transaction.
    TransactionType {
        TopUp = "topUp",
        Charge = "charge",
        Refund = "refund",
        Bonus = "bonus",
        Purchase = "purchase",
        Adjustment = "adjustment",
    }
}

wire_enum! {
    /// Top-up payment provider.
    TopupProvider {
        Payme = "payme",
        Click = "click",
        Uzum = "uzum",
        /// Cash at the desk; creates an admin ticket.
        Cash = "cash",
    }
}

wire_enum! {
    /// Lifecycle of a [`TopupIntent`].
    TopupStatus {
        Pending = "pending",
        Paid = "paid",
        Expired = "expired",
        Cancelled = "cancelled",
    }
}

/// Wallet ledger entry (IPC_PROTOCOL.md §6.12).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Transaction {
    pub id: Uuid,
    pub user_id: Uuid,
    pub r#type: TransactionType,
    /// Signed: negative for `charge` and `purchase`.
    pub amount: Money,
    /// Main balance after this entry.
    pub balance_after: Money,
    pub description: String,
    #[serde(with = "crate::wire::ts")]
    pub created_at: DateTime<Utc>,
    /// External reference: order id, session id or payment id.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub r#ref: Option<String>,
}

/// Pending or settled top-up (IPC_PROTOCOL.md §6.12). Payload of `wallet.topupIntent`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TopupIntent {
    pub id: Uuid,
    pub provider: TopupProvider,
    pub amount: Money,
    pub status: TopupStatus,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub qr_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub deep_link: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub payment_url: Option<String>,
    #[serde(with = "crate::wire::ts")]
    pub expires_at: DateTime<Utc>,
    #[serde(with = "crate::wire::ts")]
    pub created_at: DateTime<Utc>,
}

/// Body of `POST /wallet/{userId}/topup-intent` (SERVER_API.md §4.8).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TopupIntentCreateRequest {
    pub amount: Money,
    pub provider: TopupProvider,
    pub pc_id: Uuid,
}

impl TopupIntentCreateRequest {
    /// Minimum top-up amount in minor units (1 000 UZS).
    pub const MIN_AMOUNT_MINOR: i64 = 100_000;
}

// ---- BEGIN MANUAL ----
#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::assert_wire;
    use chrono::TimeZone;

    #[test]
    fn enum_wire_values() {
        assert_wire(Weekday::ALL, &["mon", "tue", "wed", "thu", "fri", "sat", "sun"]);
        assert_wire(TransactionType::ALL, &["topUp", "charge", "refund", "bonus", "purchase", "adjustment"]);
        assert_wire(TopupProvider::ALL, &["payme", "click", "uzum", "cash"]);
        assert_wire(TopupStatus::ALL, &["pending", "paid", "expired", "cancelled"]);
    }

    #[test]
    fn money_wire_and_arithmetic() {
        let m = Money::uzs(1_500_000);
        assert_eq!(serde_json::to_string(&m).unwrap(), r#"{"amount":1500000,"currency":"UZS"}"#);
        assert_eq!(m.to_string(), "1500000 UZS");
        let back: Money = serde_json::from_str(r#"{"currency":"uzs","amount":5,"extra":1}"#).unwrap();
        assert_eq!(back, Money::uzs(5));
        let back: Money = serde_json::from_str(r#"{"amount":7,"currency":null}"#).unwrap();
        assert_eq!(back, Money::uzs(7));
        let back: Money = serde_json::from_str(r#"{"amount":7}"#).unwrap();
        assert_eq!(back, Money::uzs(7));
        assert!(serde_json::from_str::<Money>(r#"{"currency":"UZS"}"#).is_err());
        assert!(serde_json::from_str::<Money>(r#"{"amount":1.5}"#).is_err());

        assert_eq!(Money::uzs(3) + Money::uzs(4), Money::uzs(7));
        assert_eq!(Money::uzs(3) - Money::uzs(4), Money::uzs(-1));
        assert_eq!(-Money::uzs(3), Money::uzs(-3));
        assert_eq!(Money::uzs(-3).abs(), Money::uzs(3));
        assert_eq!(Money::uzs(3) * 4, Money::uzs(12));
        assert_eq!(vec![Money::uzs(1), Money::uzs(2)].into_iter().sum::<Money>(), Money::uzs(3));
        assert_eq!(Vec::<Money>::new().into_iter().sum::<Money>(), Money::zero());
        assert!(Money::uzs(1) < Money::uzs(2));
        assert_eq!(Money::uzs(1).partial_cmp(&Money::new(1, "usd")), None);
        assert_eq!(Money::uzs(1).checked_add(&Money::new(1, "USD")), None);
        assert_eq!(Money::new(1, " usd ").currency, "USD");
        assert_eq!(Money::default(), Money::zero());
    }

    #[test]
    #[should_panic(expected = "currency mismatch")]
    fn money_add_panics_on_currency_mismatch() {
        let _ = Money::uzs(1) + Money::new(1, "USD");
    }

    #[test]
    fn balance_json() {
        let b = Balance {
            user_id: Uuid::nil(),
            amount: Money::uzs(2_000_000),
            bonus: Money::uzs(50_000),
            currency: "UZS".into(),
            updated_at: Utc.with_ymd_and_hms(2026, 9, 21, 10, 22, 0).unwrap(),
        };
        let json = serde_json::to_string(&b).unwrap();
        assert_eq!(
            json,
            r#"{"userId":"00000000-0000-0000-0000-000000000000","amount":{"amount":2000000,"currency":"UZS"},"bonus":{"amount":50000,"currency":"UZS"},"currency":"UZS","updatedAt":"2026-09-21T10:22:00.000Z"}"#
        );
        assert_eq!(serde_json::from_str::<Balance>(&json).unwrap(), b);
        assert_eq!(b.total(), Money::uzs(2_050_000));
    }

    #[test]
    fn tariff_json_and_pricing() {
        let t = Tariff {
            id: Uuid::nil(),
            name: "Night".into(),
            price_per_hour: Money::uzs(100),
            min_minutes: 15,
            max_minutes: None,
            zones: vec!["VIP".into()],
            time_windows: vec![TariffTimeWindow {
                days: vec![Weekday::Fri, Weekday::Sat],
                from: NaiveTime::from_hms_opt(22, 0, 0).unwrap(),
                to: NaiveTime::from_hms_opt(6, 0, 0).unwrap(),
            }],
            is_package: false,
            package_minutes: None,
            package_price: None,
        };
        let json = serde_json::to_string(&t).unwrap();
        assert_eq!(
            json,
            r#"{"id":"00000000-0000-0000-0000-000000000000","name":"Night","pricePerHour":{"amount":100,"currency":"UZS"},"minMinutes":15,"zones":["VIP"],"timeWindows":[{"days":["fri","sat"],"from":"22:00","to":"06:00"}],"isPackage":false}"#
        );
        assert_eq!(serde_json::from_str::<Tariff>(&json).unwrap(), t);

        assert_eq!(t.price_for(60), Money::uzs(100));
        assert_eq!(t.price_for(7), Money::uzs(12)); // 700/60 = 11.67 → 12
        assert_eq!(t.price_for(0), Money::uzs(0));
        let sat_2300 = NaiveTime::from_hms_opt(23, 0, 0).unwrap();
        let sun_0300 = NaiveTime::from_hms_opt(3, 0, 0).unwrap();
        let sun_1200 = NaiveTime::from_hms_opt(12, 0, 0).unwrap();
        assert!(t.is_valid_for("vip", Weekday::Sat, sat_2300));
        assert!(t.is_valid_for("VIP", Weekday::Sun, sun_0300)); // wraps from Saturday
        assert!(!t.is_valid_for("VIP", Weekday::Sun, sun_1200));
        assert!(!t.is_valid_for("Standard", Weekday::Sat, sat_2300));
        assert!(!t.is_valid_for("VIP", Weekday::Mon, sun_0300)); // Sunday not in days

        let package = Tariff { is_package: true, package_minutes: Some(180), package_price: Some(Money::uzs(250)), ..t };
        assert_eq!(package.price_for(1), Money::uzs(250));
    }

    #[test]
    fn transaction_json_uses_type_and_ref_keys() {
        let tx = Transaction {
            id: Uuid::nil(),
            user_id: Uuid::nil(),
            r#type: TransactionType::TopUp,
            amount: Money::uzs(10),
            balance_after: Money::uzs(20),
            description: "d".into(),
            created_at: Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap(),
            r#ref: Some("order-1".into()),
        };
        let json = serde_json::to_string(&tx).unwrap();
        assert!(json.contains(r#""type":"topUp""#));
        assert!(json.ends_with(r#""createdAt":"2026-01-01T00:00:00.000Z","ref":"order-1"}"#));
        assert_eq!(serde_json::from_str::<Transaction>(&json).unwrap(), tx);
        assert_eq!(TopupIntentCreateRequest::MIN_AMOUNT_MINOR, 100_000);
    }

    #[test]
    fn topup_intent_omits_absent_links() {
        let i = TopupIntent {
            id: Uuid::nil(),
            provider: TopupProvider::Payme,
            amount: Money::uzs(100_000),
            status: TopupStatus::Pending,
            qr_url: Some("https://q".into()),
            deep_link: None,
            payment_url: None,
            expires_at: Utc.with_ymd_and_hms(2026, 1, 1, 0, 10, 0).unwrap(),
            created_at: Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap(),
        };
        let json = serde_json::to_string(&i).unwrap();
        assert_eq!(
            json,
            r#"{"id":"00000000-0000-0000-0000-000000000000","provider":"payme","amount":{"amount":100000,"currency":"UZS"},"status":"pending","qrUrl":"https://q","expiresAt":"2026-01-01T00:10:00.000Z","createdAt":"2026-01-01T00:00:00.000Z"}"#
        );
        assert_eq!(serde_json::from_str::<TopupIntent>(&json).unwrap(), i);
    }
}
// ---- END MANUAL ----
