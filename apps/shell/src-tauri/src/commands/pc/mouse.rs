//! Pointer settings of the player's logon session through `SystemParametersInfoW`: speed (`SPI_*MOUSESPEED`, 1–20),
//! "Enhance pointer precision" (`SPI_*MOUSE`: two acceleration thresholds + the acceleration flag) and the double-click
//! time (`GetDoubleClickTime` / `SPI_SETDOUBLECLICKTIME`).
//!
//! Values are applied with `SPIF_SENDCHANGE` only, never written to the registry (`SPIF_UPDATEINIFILE`): the cursor
//! code reads the in-memory values, so the change is immediate, and a Windows logoff or reboot drops it even when the
//! Shell never got to restore the club's values.

use serde::{Deserialize, Serialize};

use super::super::validate;
use crate::state::CmdResult;

/// Windows pointer-speed scale (10 is the Windows default).
pub const SPEED_MIN: i32 = 1;
pub const SPEED_MAX: i32 = 20;
/// Range of the Mouse control panel's double-click slider, in ms (500 is the Windows default).
pub const DOUBLE_CLICK_MIN_MS: i32 = 200;
pub const DOUBLE_CLICK_MAX_MS: i32 = 900;
/// Thresholds + acceleration the Mouse control panel writes when precision is switched on / off.
const PRECISION_ON: [i32; 3] = [6, 10, 1];
const PRECISION_OFF: [i32; 3] = [0, 0, 0];

/// What the Settings screen shows and edits (`pc_mouse_get` / `pc_mouse_set`).
#[derive(Serialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MouseSettings {
    pub speed: u32,
    pub enhance_precision: bool,
    pub double_click_ms: u32,
}

/// `pc_mouse_set` argument: only the given fields change.
#[derive(Deserialize, Clone, Copy, Debug, Default, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MousePatch {
    pub speed: Option<i32>,
    pub enhance_precision: Option<bool>,
    pub double_click_ms: Option<i32>,
}

impl MousePatch {
    /// `validation` for a speed outside 1–20 or a double-click time outside 200–900 ms.
    pub fn validate(&self) -> CmdResult<()> {
        validate::optional_range("speed", self.speed, SPEED_MIN, SPEED_MAX)?;
        validate::optional_range(
            "doubleClickMs",
            self.double_click_ms,
            DOUBLE_CLICK_MIN_MS,
            DOUBLE_CLICK_MAX_MS,
        )
    }
}

/// Raw session values. The baseline keeps these rather than [`MouseSettings`] so a restore puts back exactly what the
/// club had, custom acceleration thresholds included.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MouseState {
    pub speed: u32,
    /// `SPI_GETMOUSE`: threshold 1, threshold 2, acceleration (0 = precision off).
    pub params: [i32; 3],
    pub double_click_ms: u32,
}

impl MouseState {
    pub fn settings(&self) -> MouseSettings {
        MouseSettings {
            speed: self.speed,
            enhance_precision: self.params[2] != 0,
            double_click_ms: self.double_click_ms,
        }
    }

    /// `self` with a validated `patch` applied. Switching precision on keeps thresholds already in effect, else the
    /// club's (`club`, from the baseline), else the Windows defaults.
    pub fn patched(&self, patch: &MousePatch, club: Option<&MouseState>) -> MouseState {
        let mut next = *self;
        if let Some(speed) = patch.speed {
            next.speed = speed.clamp(SPEED_MIN, SPEED_MAX).unsigned_abs();
        }
        if let Some(ms) = patch.double_click_ms {
            next.double_click_ms = ms
                .clamp(DOUBLE_CLICK_MIN_MS, DOUBLE_CLICK_MAX_MS)
                .unsigned_abs();
        }
        match patch.enhance_precision {
            Some(true) if self.params[2] == 0 => {
                next.params = club
                    .map(|c| c.params)
                    .filter(|p| p[2] != 0)
                    .unwrap_or(PRECISION_ON);
            }
            Some(false) => next.params = PRECISION_OFF,
            _ => {}
        }
        next
    }
}

#[cfg(windows)]
pub(super) mod native {
    use std::ffi::c_void;

    use windows::Win32::UI::Input::KeyboardAndMouse::GetDoubleClickTime;
    use windows::Win32::UI::WindowsAndMessaging::{
        SystemParametersInfoW, SPIF_SENDCHANGE, SPI_GETMOUSE, SPI_GETMOUSESPEED,
        SPI_SETDOUBLECLICKTIME, SPI_SETMOUSE, SPI_SETMOUSESPEED, SYSTEM_PARAMETERS_INFO_ACTION,
        SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS,
    };

    use super::MouseState;
    use crate::state::{CmdResult, ShellError};

    fn spi(
        action: SYSTEM_PARAMETERS_INFO_ACTION,
        ui_param: u32,
        pv_param: Option<*mut c_void>,
        flags: SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS,
        what: &str,
    ) -> CmdResult<()> {
        // SAFETY: every caller passes the buffer (or by-value pointer) its SPI action documents.
        unsafe { SystemParametersInfoW(action, ui_param, pv_param, flags) }
            .map_err(|e| ShellError::internal(format!("SystemParametersInfo({what}): {e}")))
    }

    pub fn read() -> CmdResult<MouseState> {
        let none = SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0);
        let mut speed: u32 = 0;
        spi(
            SPI_GETMOUSESPEED,
            0,
            Some(std::ptr::addr_of_mut!(speed).cast()),
            none,
            "GETMOUSESPEED",
        )?;
        let mut params = [0i32; 3];
        spi(
            SPI_GETMOUSE,
            0,
            Some(params.as_mut_ptr().cast()),
            none,
            "GETMOUSE",
        )?;
        // SAFETY: no preconditions.
        let double_click_ms = unsafe { GetDoubleClickTime() };
        Ok(MouseState {
            speed,
            params,
            double_click_ms,
        })
    }

    /// Applies the fields of `next` that differ from `current`.
    pub fn write(next: &MouseState, current: &MouseState) -> CmdResult<()> {
        if next.speed != current.speed {
            // SPI_SETMOUSESPEED takes the speed itself in pvParam, not a pointer to it.
            spi(
                SPI_SETMOUSESPEED,
                0,
                Some(next.speed as usize as *mut c_void),
                SPIF_SENDCHANGE,
                "SETMOUSESPEED",
            )?;
        }
        if next.params != current.params {
            let mut params = next.params;
            spi(
                SPI_SETMOUSE,
                0,
                Some(params.as_mut_ptr().cast()),
                SPIF_SENDCHANGE,
                "SETMOUSE",
            )?;
        }
        if next.double_click_ms != current.double_click_ms {
            spi(
                SPI_SETDOUBLECLICKTIME,
                next.double_click_ms,
                None,
                SPIF_SENDCHANGE,
                "SETDOUBLECLICKTIME",
            )?;
        }
        Ok(())
    }
}

#[cfg(not(windows))]
pub(super) mod native {
    use super::MouseState;
    use crate::state::{CmdResult, ShellError};

    pub fn read() -> CmdResult<MouseState> {
        Err(ShellError::unsupported())
    }

    pub fn write(_next: &MouseState, _current: &MouseState) -> CmdResult<()> {
        Err(ShellError::unsupported())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clubshell_protocol::error::ErrorCode;

    const CLUB: MouseState = MouseState {
        speed: 10,
        params: [6, 10, 1],
        double_click_ms: 500,
    };

    fn patch(speed: Option<i32>, precision: Option<bool>, dbl: Option<i32>) -> MousePatch {
        MousePatch {
            speed,
            enhance_precision: precision,
            double_click_ms: dbl,
        }
    }

    #[test]
    fn patch_ranges_are_validated() {
        assert!(patch(Some(1), None, Some(200)).validate().is_ok());
        assert!(patch(Some(20), Some(true), Some(900)).validate().is_ok());
        assert!(MousePatch::default().validate().is_ok());
        for (bad, field) in [
            (patch(Some(0), None, None), "speed"),
            (patch(Some(21), None, None), "speed"),
            (patch(None, None, Some(199)), "doubleClickMs"),
            (patch(None, None, Some(901)), "doubleClickMs"),
        ] {
            let e = bad.validate().unwrap_err();
            assert_eq!(e.code, ErrorCode::Validation);
            assert_eq!(e.details.unwrap()["field"], field);
        }
    }

    #[test]
    fn patch_wire_shape_is_camel_case() {
        let p: MousePatch = serde_json::from_value(
            serde_json::json!({ "enhancePrecision": false, "doubleClickMs": 400 }),
        )
        .unwrap();
        assert_eq!(p, patch(None, Some(false), Some(400)));
        let json = serde_json::to_value(CLUB.settings()).unwrap();
        assert_eq!(
            json,
            serde_json::json!({ "speed": 10, "enhancePrecision": true, "doubleClickMs": 500 })
        );
    }

    #[test]
    fn precision_toggles_thresholds_and_keeps_the_rest() {
        let off = CLUB.patched(&patch(None, Some(false), None), Some(&CLUB));
        assert_eq!(off.params, [0, 0, 0]);
        assert_eq!((off.speed, off.double_click_ms), (10, 500));
        assert!(!off.settings().enhance_precision);

        // Back on: the club's own thresholds come back.
        let custom = MouseState {
            params: [4, 12, 1],
            ..CLUB
        };
        assert_eq!(
            off.patched(&patch(None, Some(true), None), Some(&custom))
                .params,
            [4, 12, 1]
        );
        // The club had precision off: Windows' defaults.
        assert_eq!(
            off.patched(&patch(None, Some(true), None), Some(&off))
                .params,
            [6, 10, 1]
        );
        assert_eq!(
            off.patched(&patch(None, Some(true), None), None).params,
            [6, 10, 1]
        );
        // Already on: untouched.
        let on = MouseState {
            params: [2, 3, 1],
            ..CLUB
        };
        assert_eq!(
            on.patched(&patch(None, Some(true), None), Some(&CLUB))
                .params,
            [2, 3, 1]
        );
    }

    #[test]
    fn speed_and_double_click_are_clamped() {
        let next = CLUB.patched(&patch(Some(15), None, Some(350)), None);
        assert_eq!(next.speed, 15);
        assert_eq!(next.double_click_ms, 350);
        assert_eq!(next.params, CLUB.params);
        let clamped = CLUB.patched(&patch(Some(99), None, Some(5)), None);
        assert_eq!((clamped.speed, clamped.double_click_ms), (20, 200));
    }

    /// Read-only: this PC's settings are only read, never written.
    #[cfg(windows)]
    #[test]
    fn reads_the_session_values() {
        let state = native::read().expect("SPI_GETMOUSE* in an interactive session");
        assert!((1..=20).contains(&state.speed), "speed {}", state.speed);
        assert!(state.double_click_ms > 0);
    }
}
