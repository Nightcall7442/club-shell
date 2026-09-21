//! `clubshell-protocol` — Rust mirror of `src/ClubShell.Contracts` (the canonical contract source).
//!
//! Wire format (ARCHITECTURE.md §1.2, IPC_PROTOCOL.md §2/§6, SERVER_API.md §1): camelCase properties,
//! camelCase string enums, ISO-8601 UTC timestamps with milliseconds and `Z`, `HH:mm` club times,
//! `YYYY-MM-DD` dates, lowercase UUIDs, `Money = { amount, currency }`, nulls omitted except where a
//! key is documented as always present (`IpcEnvelope.payload/error`, `IpcError.details`,
//! `ServerError.details`, `WsFrame.payload`, `ShellCommand.args`).
//!
//! Module layout follows the C# folders: [`error`] (Errors + IpcErrors), [`ipc`] (IpcEnvelope +
//! framing), [`commands`] (IpcMessage payloads, AgentCommand, ServerCommand, WS/REST agent surface),
//! [`events`] (Agent → Shell event payloads), [`games`], [`pc`], [`session`], [`shop`], [`user`],
//! [`wallet`].
//!
//! Regeneration (`tools/scripts/gen-contracts-rs.ps1` → tools/ContractsGen): every module except this file and
//! [`ipc`] is generated. Hand-written code (impls, `wire_enum!` extras, tests, extra `use`s) lives between
//! "BEGIN MANUAL" / "END MANUAL" line comments (exact marker text in tools/ContractsGen/Program.cs); the generator
//! carries every such block over, in order, to the end of the regenerated module and replaces everything else.
//! This file is not generated: add a `pub mod` line here when a new module appears.

#![forbid(unsafe_code)]

/// IPC protocol major implemented by this crate (`IpcEnvelope.v`).
pub const PROTOCOL_VERSION: i32 = 1;

/// Pipe name as configured in `agent.json → ipc.pipeName`.
pub const PIPE_SHORT_NAME: &str = "clubshell-agent";

/// Full Win32 pipe path of the Agent IPC server.
pub const PIPE_NAME: &str = r"\\.\pipe\clubshell-agent";

/// Maximum JSON bytes of one IPC frame, excluding the 4-byte length prefix (IPC_PROTOCOL.md §1).
pub const MAX_FRAME_BYTES: usize = 4 * 1024 * 1024;

/// Maximum bytes of one WebSocket text frame (SERVER_API.md §6).
pub const WS_MAX_FRAME_BYTES: usize = 1024 * 1024;

/// WebSocket subprotocol negotiated on connect.
pub const WS_SUBPROTOCOL: &str = "clubshell.v1";

/// Builds the Win32 pipe path for a configured short pipe name.
pub fn pipe_path(short_name: &str) -> String {
    format!(r"\\.\pipe\{short_name}")
}

/// Declares a camelCase string enum mirroring a C# `[JsonConverter(typeof(CamelCaseEnumConverter<T>))]`
/// enum. Generates serde impls (exact wire literals), `ALL`, `wire_name()`, `parse()` (ASCII
/// case-insensitive, like the C# `TryParse` helpers), `Display` and `FromStr`.
macro_rules! wire_enum {
    (
        $(#[$meta:meta])*
        $name:ident { $( $(#[$vmeta:meta])* $variant:ident = $wire:literal ),+ $(,)? }
    ) => {
        $(#[$meta])*
        #[derive(::serde::Serialize, ::serde::Deserialize, Clone, Copy, Debug, PartialEq, Eq, Hash)]
        pub enum $name {
            $( $(#[$vmeta])* #[serde(rename = $wire)] $variant, )+
        }

        impl $name {
            /// Every value in declaration order.
            pub const ALL: &'static [$name] = &[ $( $name::$variant, )+ ];

            /// camelCase wire literal.
            pub const fn wire_name(self) -> &'static str {
                match self { $( $name::$variant => $wire, )+ }
            }

            /// Parses a wire literal, ASCII case-insensitively.
            pub fn parse(name: &str) -> Option<$name> {
                Self::ALL.iter().copied().find(|v| v.wire_name().eq_ignore_ascii_case(name))
            }
        }

        impl ::core::fmt::Display for $name {
            fn fmt(&self, f: &mut ::core::fmt::Formatter<'_>) -> ::core::fmt::Result {
                f.write_str(self.wire_name())
            }
        }

        impl ::core::str::FromStr for $name {
            type Err = $crate::error::UnknownWireName;

            fn from_str(s: &str) -> Result<Self, Self::Err> {
                Self::parse(s).ok_or_else(|| $crate::error::UnknownWireName {
                    kind: stringify!($name),
                    got: s.to_owned(),
                })
            }
        }
    };
}

pub mod wire {
    //! serde helpers that reproduce the exact C# wire form for scalars serde would otherwise format
    //! differently: timestamps (`2026-09-21T10:15:30.123Z`, always 3 fractional digits), club times
    //! (`HH:mm`) and doubles (integral values written without a fractional part, like
    //! System.Text.Json). Use with `#[serde(with = "crate::wire::ts")]` etc.

    use chrono::{DateTime, NaiveDateTime, NaiveTime, Utc};
    use serde::{de, Deserialize, Deserializer, Serializer};

    /// strftime pattern of the timestamp wire form.
    pub const TS_FORMAT: &str = "%Y-%m-%dT%H:%M:%S%.3fZ";

    /// strftime pattern of the club-time wire form.
    pub const TIME_FORMAT: &str = "%H:%M";

    /// Formats a timestamp as `yyyy-MM-ddTHH:mm:ss.fffZ`.
    pub fn format_ts(dt: &DateTime<Utc>) -> String {
        dt.format(TS_FORMAT).to_string()
    }

    /// Parses any ISO-8601 / RFC 3339 form (with or without offset) and normalizes to UTC.
    pub fn parse_ts(s: &str) -> Option<DateTime<Utc>> {
        if let Ok(dt) = DateTime::parse_from_rfc3339(s) {
            return Some(dt.with_timezone(&Utc));
        }
        NaiveDateTime::parse_from_str(s, "%Y-%m-%dT%H:%M:%S%.f")
            .or_else(|_| NaiveDateTime::parse_from_str(s, "%Y-%m-%d %H:%M:%S%.f"))
            .ok()
            .map(|n| n.and_utc())
    }

    /// Formats a club time as `HH:mm`.
    pub fn format_time(t: &NaiveTime) -> String {
        t.format(TIME_FORMAT).to_string()
    }

    /// Parses `HH:mm`, `H:mm`, `HH:mm:ss` or `HH:mm:ss.fffffff`.
    pub fn parse_time(s: &str) -> Option<NaiveTime> {
        NaiveTime::parse_from_str(s, "%H:%M")
            .or_else(|_| NaiveTime::parse_from_str(s, "%H:%M:%S"))
            .or_else(|_| NaiveTime::parse_from_str(s, "%H:%M:%S%.f"))
            .ok()
    }

    /// `DateTime<Utc>` ⇄ `yyyy-MM-ddTHH:mm:ss.fffZ`.
    pub mod ts {
        use super::*;

        pub fn serialize<S: Serializer>(dt: &DateTime<Utc>, s: S) -> Result<S::Ok, S::Error> {
            s.collect_str(&dt.format(TS_FORMAT))
        }

        pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<DateTime<Utc>, D::Error> {
            let text = String::deserialize(d)?;
            parse_ts(&text)
                .ok_or_else(|| de::Error::custom(format!("invalid ISO-8601 timestamp '{text}'")))
        }
    }

    /// `Option<DateTime<Utc>>`; pair with `#[serde(default, skip_serializing_if = "Option::is_none")]`.
    pub mod ts_opt {
        use super::*;

        pub fn serialize<S: Serializer>(
            v: &Option<DateTime<Utc>>,
            s: S,
        ) -> Result<S::Ok, S::Error> {
            match v {
                Some(dt) => super::ts::serialize(dt, s),
                None => s.serialize_none(),
            }
        }

        pub fn deserialize<'de, D: Deserializer<'de>>(
            d: D,
        ) -> Result<Option<DateTime<Utc>>, D::Error> {
            match Option::<String>::deserialize(d)? {
                None => Ok(None),
                Some(text) => parse_ts(&text).map(Some).ok_or_else(|| {
                    de::Error::custom(format!("invalid ISO-8601 timestamp '{text}'"))
                }),
            }
        }
    }

    /// `NaiveTime` ⇄ `HH:mm`.
    pub mod hm {
        use super::*;

        pub fn serialize<S: Serializer>(t: &NaiveTime, s: S) -> Result<S::Ok, S::Error> {
            s.collect_str(&t.format(TIME_FORMAT))
        }

        pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<NaiveTime, D::Error> {
            let text = String::deserialize(d)?;
            parse_time(&text)
                .ok_or_else(|| de::Error::custom(format!("invalid time '{text}', expected HH:mm")))
        }
    }

    /// `Option<NaiveTime>`; pair with `#[serde(default, skip_serializing_if = "Option::is_none")]`.
    pub mod hm_opt {
        use super::*;

        pub fn serialize<S: Serializer>(v: &Option<NaiveTime>, s: S) -> Result<S::Ok, S::Error> {
            match v {
                Some(t) => super::hm::serialize(t, s),
                None => s.serialize_none(),
            }
        }

        pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Option<NaiveTime>, D::Error> {
            match Option::<String>::deserialize(d)? {
                None => Ok(None),
                Some(text) => parse_time(&text).map(Some).ok_or_else(|| {
                    de::Error::custom(format!("invalid time '{text}', expected HH:mm"))
                }),
            }
        }
    }

    /// `f64` written like System.Text.Json: `50` for 50.0, `50.5` otherwise.
    pub mod num {
        use super::*;

        const MAX_EXACT: f64 = 9_007_199_254_740_992.0; // 2^53

        pub fn serialize<S: Serializer>(v: &f64, s: S) -> Result<S::Ok, S::Error> {
            if v.is_finite() && v.fract() == 0.0 && v.abs() < MAX_EXACT {
                s.serialize_i64(*v as i64)
            } else {
                s.serialize_f64(*v)
            }
        }

        pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<f64, D::Error> {
            f64::deserialize(d)
        }
    }

    /// `Option<f64>` in the compact form; pair with `#[serde(default, skip_serializing_if = "Option::is_none")]`.
    pub mod num_opt {
        use super::*;

        pub fn serialize<S: Serializer>(v: &Option<f64>, s: S) -> Result<S::Ok, S::Error> {
            match v {
                Some(f) => super::num::serialize(f, s),
                None => s.serialize_none(),
            }
        }

        pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Option<f64>, D::Error> {
            Option::<f64>::deserialize(d)
        }
    }

    /// Serializes a contract record to a JSON value. Contract records are plain data (no maps with
    /// non-string keys, no NaN), so this cannot fail; the `expect` documents that invariant.
    pub(crate) fn to_value_infallible<T: serde::Serialize>(value: &T) -> serde_json::Value {
        serde_json::to_value(value).expect("contract records always serialize")
    }

    #[cfg(test)]
    mod tests {
        use super::*;
        use chrono::TimeZone;

        #[test]
        fn timestamp_is_fixed_millis_utc() {
            let dt = Utc.with_ymd_and_hms(2026, 9, 21, 10, 15, 30).unwrap();
            assert_eq!(format_ts(&dt), "2026-09-21T10:15:30.000Z");
            let dt = dt + chrono::Duration::milliseconds(123);
            assert_eq!(format_ts(&dt), "2026-09-21T10:15:30.123Z");
            assert_eq!(parse_ts("2026-09-21T10:15:30.123Z"), Some(dt));
            assert_eq!(parse_ts("2026-09-21T15:15:30.123+05:00"), Some(dt));
            assert_eq!(parse_ts("2026-09-21T10:15:30.123"), Some(dt));
            assert_eq!(parse_ts("not a date"), None);
        }

        #[test]
        fn club_time_is_hh_mm() {
            let t = NaiveTime::from_hms_opt(4, 5, 0).unwrap();
            assert_eq!(format_time(&t), "04:05");
            assert_eq!(parse_time("04:05"), Some(t));
            assert_eq!(parse_time("4:05"), Some(t));
            assert_eq!(parse_time("04:05:00"), Some(t));
            assert_eq!(parse_time("04:05:00.0000000"), Some(t));
            assert_eq!(parse_time("25:00"), None);
        }

        #[test]
        fn doubles_match_system_text_json() {
            #[derive(serde::Serialize, serde::Deserialize, PartialEq, Debug)]
            struct D {
                #[serde(with = "num")]
                a: f64,
                #[serde(with = "num")]
                b: f64,
                #[serde(default, with = "num_opt", skip_serializing_if = "Option::is_none")]
                c: Option<f64>,
            }
            let json = serde_json::to_string(&D {
                a: 50.0,
                b: 50.5,
                c: None,
            })
            .unwrap();
            assert_eq!(json, r#"{"a":50,"b":50.5}"#);
            let json = serde_json::to_string(&D {
                a: -3.0,
                b: 0.1,
                c: Some(2.0),
            })
            .unwrap();
            assert_eq!(json, r#"{"a":-3,"b":0.1,"c":2}"#);
        }
    }
}

pub mod commands;
pub mod error;
pub mod events;
pub mod games;
pub mod ipc;
pub mod pc;
pub mod session;
pub mod shop;
pub mod user;
pub mod wallet;

/// Everything, for `use clubshell_protocol::prelude::*`.
pub mod prelude {
    pub use crate::commands::*;
    pub use crate::error::*;
    pub use crate::events::*;
    pub use crate::games::*;
    pub use crate::ipc::*;
    pub use crate::pc::*;
    pub use crate::session::*;
    pub use crate::shop::*;
    pub use crate::user::*;
    pub use crate::wallet::*;
    pub use crate::{
        pipe_path, MAX_FRAME_BYTES, PIPE_NAME, PIPE_SHORT_NAME, PROTOCOL_VERSION,
        WS_MAX_FRAME_BYTES, WS_SUBPROTOCOL,
    };
}

#[cfg(test)]
mod tests {
    #[test]
    fn constants_match_docs() {
        assert_eq!(super::PROTOCOL_VERSION, 1);
        assert_eq!(super::MAX_FRAME_BYTES, 4_194_304);
        assert_eq!(super::WS_MAX_FRAME_BYTES, 1_048_576);
        assert_eq!(super::PIPE_NAME, r"\\.\pipe\clubshell-agent");
        assert_eq!(super::pipe_path(super::PIPE_SHORT_NAME), super::PIPE_NAME);
        assert_eq!(super::WS_SUBPROTOCOL, "clubshell.v1");
    }
}
