//! Display modes of one GDI display device (`\\.\DISPLAY1`, the `MonitorInfo::name`): the current
//! mode, the modes the driver offers for the attached monitor (`EnumDisplaySettingsExW`) and a
//! refresh-rate switch (`ChangeDisplaySettingsExW`, always checked with `CDS_TEST` first).
//!
//! A switch is applied for the running session only; [`save_current_mode`] writes what is on screen
//! to the per-user settings once the player has confirmed it, [`restore_saved_mode`] drops an
//! unsaved one. `CDS_GLOBAL` is never passed, so other accounts on the PC keep their own settings.

use crate::Result;

/// The `DEVMODEW` fields a refresh-rate switch cares about.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct DisplayMode {
    pub width: u32,
    pub height: u32,
    pub bits_per_pixel: u32,
    /// Refresh rate in Hz; 0 and 1 mean "hardware default" (`DEVMODEW::dmDisplayFrequency`).
    pub hz: u32,
    pub interlaced: bool,
}

/// Refresh rates the player may pick at `current`'s resolution and colour depth: ascending, without
/// duplicates, always including the current rate. Interlaced modes and the 0/1 Hz "hardware default"
/// entries are left out, and so is the NTSC twin one below a listed rate (59 next to 60, 143 next to
/// 144): Windows lists both, a player only needs the round one. A portrait display may report its
/// modes in either orientation, so swapped width and height match as well.
pub fn refresh_rates(modes: &[DisplayMode], current: &DisplayMode) -> Vec<u32> {
    let size = (current.width, current.height);
    let mut rates: Vec<u32> = modes
        .iter()
        .filter(|m| {
            ((m.width, m.height) == size || (m.height, m.width) == size)
                && m.bits_per_pixel == current.bits_per_pixel
                && !m.interlaced
                && m.hz > 1
        })
        .map(|m| m.hz)
        .collect();
    rates.sort_unstable();
    rates.dedup();
    let listed = rates.clone();
    rates.retain(|&hz| {
        hz == current.hz
            || hz
                .checked_add(1)
                .map_or(true, |next| listed.binary_search(&next).is_err())
    });
    if current.hz > 1 {
        if let Err(at) = rates.binary_search(&current.hz) {
            rates.insert(at, current.hz);
        }
    }
    rates
}

#[cfg(windows)]
mod imp {
    use super::*;
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::HWND;
    use windows::Win32::Graphics::Gdi::{
        ChangeDisplaySettingsExW, EnumDisplaySettingsExW, CDS_NORESET, CDS_TEST, CDS_TYPE,
        CDS_UPDATEREGISTRY, DEVMODEW, DISP_CHANGE, DISP_CHANGE_BADMODE, DISP_CHANGE_RESTART,
        DISP_CHANGE_SUCCESSFUL, DM_BITSPERPEL, DM_DISPLAYFREQUENCY, DM_PELSHEIGHT, DM_PELSWIDTH,
        ENUM_CURRENT_SETTINGS, ENUM_DISPLAY_SETTINGS_FLAGS, ENUM_DISPLAY_SETTINGS_MODE,
    };

    use crate::{last_error, WinUtilError};

    /// `DM_INTERLACED` of `dmDisplayFlags` (windows-rs types it as a field flag).
    const DM_INTERLACED: u32 = 0x2;
    /// Upper bound on `EnumDisplaySettingsExW` indices, in case a driver never reports the end.
    const MAX_MODES: u32 = 4096;

    fn wide(device: &str) -> Vec<u16> {
        device.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn empty_devmode() -> DEVMODEW {
        DEVMODEW {
            dmSize: std::mem::size_of::<DEVMODEW>() as u16,
            ..Default::default()
        }
    }

    fn to_mode(dm: &DEVMODEW) -> DisplayMode {
        // SAFETY: both members of the union are a plain u32; Windows fills `dmDisplayFlags` for displays.
        let flags = unsafe { dm.Anonymous2.dmDisplayFlags };
        DisplayMode {
            width: dm.dmPelsWidth,
            height: dm.dmPelsHeight,
            bits_per_pixel: dm.dmBitsPerPel,
            hz: dm.dmDisplayFrequency,
            interlaced: flags & DM_INTERLACED != 0,
        }
    }

    /// `EnumDisplaySettingsExW(device, index)`; `None` once `index` is past the last mode.
    fn devmode(device: &[u16], index: ENUM_DISPLAY_SETTINGS_MODE) -> Option<DEVMODEW> {
        let mut dm = empty_devmode();
        // SAFETY: `device` is NUL-terminated and outlives the call; `dm` is sized.
        unsafe {
            EnumDisplaySettingsExW(
                PCWSTR(device.as_ptr()),
                index,
                &mut dm,
                ENUM_DISPLAY_SETTINGS_FLAGS(0),
            )
        }
        .as_bool()
        .then_some(dm)
    }

    fn current_devmode(device: &[u16]) -> Result<DEVMODEW> {
        devmode(device, ENUM_CURRENT_SETTINGS).ok_or_else(|| last_error("EnumDisplaySettingsExW"))
    }

    /// The mode `device` shows right now.
    pub fn current_mode(device: &str) -> Result<DisplayMode> {
        current_devmode(&wide(device)).map(|dm| to_mode(&dm))
    }

    /// Every mode the driver offers for the monitor on `device` (raw modes excluded); empty for an
    /// unknown device.
    pub fn modes(device: &str) -> Result<Vec<DisplayMode>> {
        let name = wide(device);
        Ok((0..MAX_MODES)
            .map_while(|i| devmode(&name, ENUM_DISPLAY_SETTINGS_MODE(i)))
            .map(|dm| to_mode(&dm))
            .collect())
    }

    fn change(device: &[u16], dm: &DEVMODEW, flags: CDS_TYPE) -> Result<()> {
        // SAFETY: `device` is NUL-terminated, `dm` is a sized DEVMODEW; both outlive the call.
        let code = unsafe {
            ChangeDisplaySettingsExW(
                PCWSTR(device.as_ptr()),
                Some(dm),
                HWND::default(),
                flags,
                None,
            )
        };
        disp_change("ChangeDisplaySettingsExW", code)
    }

    fn disp_change(api: &'static str, code: DISP_CHANGE) -> Result<()> {
        if code == DISP_CHANGE_SUCCESSFUL {
            return Ok(());
        }
        let msg = match code {
            DISP_CHANGE_BADMODE => "the display does not support this mode",
            DISP_CHANGE_RESTART => "the mode needs a restart",
            _ => "the display driver refused the mode",
        };
        Err(WinUtilError::Win32 {
            api,
            code: code.0 as u32,
            msg: msg.to_owned(),
        })
    }

    /// Switches `device` to `hz` at its current resolution and colour depth, for this session only
    /// (nothing is written to the registry). Fails without touching the display when `CDS_TEST`
    /// rejects the mode.
    pub fn set_refresh_rate(device: &str, hz: u32) -> Result<()> {
        let name = wide(device);
        let mut dm = current_devmode(&name)?;
        dm.dmDisplayFrequency = hz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
        change(&name, &dm, CDS_TEST)?;
        change(&name, &dm, CDS_TYPE(0))
    }

    /// Writes the mode `device` shows now to the current user's display settings without another
    /// mode set (`CDS_UPDATEREGISTRY | CDS_NORESET`), so a later reset (a game restoring the desktop
    /// mode on exit, the next sign-in) comes back to it.
    pub fn save_current_mode(device: &str) -> Result<()> {
        let name = wide(device);
        let mut dm = current_devmode(&name)?;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
        change(&name, &dm, CDS_UPDATEREGISTRY | CDS_NORESET)
    }

    /// Returns `device` to its saved mode, dropping any session-only change
    /// (`ChangeDisplaySettingsExW` with no `DEVMODEW`).
    pub fn restore_saved_mode(device: &str) -> Result<()> {
        let name = wide(device);
        // SAFETY: `name` is NUL-terminated and outlives the call; no DEVMODEW, no parameters.
        let code = unsafe {
            ChangeDisplaySettingsExW(
                PCWSTR(name.as_ptr()),
                None,
                HWND::default(),
                CDS_TYPE(0),
                None,
            )
        };
        disp_change("ChangeDisplaySettingsExW", code)
    }
}

#[cfg(not(windows))]
mod imp {
    use super::*;
    use crate::WinUtilError;

    pub fn current_mode(_device: &str) -> Result<DisplayMode> {
        Err(WinUtilError::Unsupported)
    }

    pub fn modes(_device: &str) -> Result<Vec<DisplayMode>> {
        Err(WinUtilError::Unsupported)
    }

    pub fn set_refresh_rate(_device: &str, _hz: u32) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    pub fn save_current_mode(_device: &str) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    pub fn restore_saved_mode(_device: &str) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }
}

pub use imp::{current_mode, modes, restore_saved_mode, save_current_mode, set_refresh_rate};

#[cfg(test)]
mod tests {
    use super::*;

    fn mode(width: u32, height: u32, bits_per_pixel: u32, hz: u32) -> DisplayMode {
        DisplayMode {
            width,
            height,
            bits_per_pixel,
            hz,
            interlaced: false,
        }
    }

    #[test]
    fn rates_follow_the_current_resolution_and_depth() {
        let current = mode(1920, 1080, 32, 144);
        let modes = [
            mode(1920, 1080, 32, 240),
            mode(1920, 1080, 32, 60),
            mode(1920, 1080, 32, 144),
            mode(1920, 1080, 32, 144), // stretched / centered duplicates
            mode(1920, 1080, 32, 120),
            mode(1920, 1080, 16, 165), // other depth
            mode(2560, 1440, 32, 165), // other resolution
            mode(1280, 720, 32, 75),
            DisplayMode {
                interlaced: true,
                ..mode(1920, 1080, 32, 50)
            },
            mode(1920, 1080, 32, 1), // hardware default
            mode(1920, 1080, 32, 0),
        ];
        assert_eq!(refresh_rates(&modes, &current), vec![60, 120, 144, 240]);
    }

    #[test]
    fn ntsc_twins_fold_into_the_round_rate() {
        let modes = [
            mode(1920, 1080, 32, 59),
            mode(1920, 1080, 32, 60),
            mode(1920, 1080, 32, 119),
            mode(1920, 1080, 32, 120),
            mode(1920, 1080, 32, 143),
            mode(1920, 1080, 32, 144),
            mode(1920, 1080, 32, 165),
        ];
        assert_eq!(
            refresh_rates(&modes, &mode(1920, 1080, 32, 60)),
            vec![60, 120, 144, 165]
        );
        // The rate on screen is always offered, even when it is a twin.
        assert_eq!(
            refresh_rates(&modes, &mode(1920, 1080, 32, 59)),
            vec![59, 60, 120, 144, 165]
        );
    }

    #[test]
    fn current_rate_is_kept_and_portrait_modes_match() {
        // The driver lists nothing at the current rate (a custom resolution): still offered.
        let modes = [mode(1080, 1920, 32, 60), mode(1080, 1920, 32, 75)];
        assert_eq!(
            refresh_rates(&modes, &mode(1920, 1080, 32, 100)),
            vec![60, 75, 100]
        );
        assert_eq!(
            refresh_rates(&[], &mode(1920, 1080, 32, 60)),
            vec![60],
            "no modes: just the current one"
        );
        assert!(refresh_rates(&[], &mode(1920, 1080, 32, 1)).is_empty());
    }

    /// Read-only check against the real driver: the current mode is among the offered ones.
    #[cfg(windows)]
    #[test]
    fn real_displays_offer_their_current_rate() {
        let Ok(monitors) = crate::monitor::enumerate() else {
            return;
        };
        for m in monitors {
            let (Ok(current), Ok(all)) = (current_mode(&m.name), modes(&m.name)) else {
                continue;
            };
            let rates = refresh_rates(&all, &current);
            if current.hz > 1 {
                assert!(rates.contains(&current.hz), "{}: {rates:?}", m.name);
            }
            assert!(rates.windows(2).all(|w| w[0] < w[1]), "sorted: {rates:?}");
        }
    }
}
