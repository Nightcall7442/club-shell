//! Audio outputs of the player's session: Core Audio enumeration of the active render endpoints (friendly name + form
//! factor), switching the default endpoint, and the master volume of the default endpoint.
//!
//! Switching goes through `IPolicyConfig` (CLSID `PolicyConfigClient`): undocumented, but it is what the Sound control
//! panel itself calls, it has kept its Windows 7 IID through Windows 11, and SoundSwitch, EarTrumpet and NirSoft's tools
//! rely on it. Only its `SetDefaultEndpoint` slot is used, through a hand-written vtable. When the object cannot be
//! created the list is still shown, read-only (`canSwitch: false`). The default endpoint is machine-wide and survives a
//! reboot, which is why it is part of the restored baseline.
//!
//! The volume: the Agent runs in session 0 and often has no endpoint to talk to (`SystemHandlers.SetVolume`), so the
//! Shell applies the level it confirmed in the player's session as well ([`set_volume`]).

use serde::{Deserialize, Serialize};

use crate::state::{CmdResult, ShellError};

/// Longest endpoint id accepted from the webview (`{0.0.0.00000000}.{guid}` is 55 characters).
pub const DEVICE_ID_MAX: usize = 512;

/// Rough kind of an output, from the endpoint's form factor (`PKEY_AudioEndpoint_FormFactor`), for the icon.
#[derive(Serialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum AudioKind {
    Speakers,
    Headphones,
    Headset,
    /// HDMI / DisplayPort (a monitor or TV) and S/PDIF.
    Digital,
    Other,
}

/// `EndpointFormFactor` → [`AudioKind`].
pub fn kind_of(form_factor: u32) -> AudioKind {
    match form_factor {
        1 => AudioKind::Speakers,
        3 => AudioKind::Headphones,
        5 | 6 => AudioKind::Headset,
        8 | 9 => AudioKind::Digital,
        _ => AudioKind::Other,
    }
}

/// One active render endpoint.
#[derive(Serialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AudioOutput {
    pub id: String,
    pub name: String,
    pub kind: AudioKind,
    /// Default endpoint for the console role (what games and the desktop play to).
    pub is_default: bool,
}

/// `pc_audio_outputs` result.
#[derive(Serialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AudioOutputs {
    pub devices: Vec<AudioOutput>,
    /// `false` when the default device cannot be changed from here (list shown read-only).
    pub can_switch: bool,
}

/// Default render endpoint per role, restored as a whole.
#[derive(Serialize, Deserialize, Clone, Debug, Default, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AudioDefaults {
    pub console: Option<String>,
    pub multimedia: Option<String>,
    pub communications: Option<String>,
}

impl AudioDefaults {
    /// Every role on `id`, like the Sound panel's "Set Default" + "Default Communication Device".
    pub fn all(id: &str) -> Self {
        Self {
            console: Some(id.to_owned()),
            multimedia: Some(id.to_owned()),
            communications: Some(id.to_owned()),
        }
    }
}

/// Trims `id` and checks it names one of `devices` (`validation` when blank or too long, `notFound` otherwise).
pub fn validate_device_id(id: &str, devices: &[AudioOutput]) -> CmdResult<String> {
    let id = super::super::validate::required_text("deviceId", id, DEVICE_ID_MAX)?;
    if devices.iter().any(|d| d.id == id) {
        Ok(id)
    } else {
        Err(ShellError::not_found("audio device"))
    }
}

/// Orders the list for display: by name, the id breaking ties so the order never flickers.
pub fn sort_outputs(devices: &mut [AudioOutput]) {
    devices.sort_by(|a, b| {
        a.name
            .to_lowercase()
            .cmp(&b.name.to_lowercase())
            .then_with(|| a.id.cmp(&b.id))
    });
}

#[cfg(windows)]
pub(super) mod native {
    use std::ffi::c_void;

    use windows::core::{IUnknown, Interface, GUID, HRESULT, PCWSTR, PWSTR};
    use windows::Win32::Foundation::BOOL;
    use windows::Win32::Media::Audio::Endpoints::IAudioEndpointVolume;
    use windows::Win32::Media::Audio::{
        eCommunications, eConsole, eMultimedia, eRender, ERole, IMMDevice, IMMDeviceEnumerator,
        MMDeviceEnumerator, DEVICE_STATE_ACTIVE,
    };
    use windows::Win32::System::Com::{CoCreateInstance, CoTaskMemFree, CLSCTX_ALL, STGM_READ};
    use windows::Win32::UI::Shell::PropertiesSystem::PROPERTYKEY;

    use super::{kind_of, sort_outputs, AudioDefaults, AudioOutput, AudioOutputs};
    use crate::state::{CmdResult, ShellError};

    /// `PKEY_Device_FriendlyName` ("Speakers (Realtek(R) Audio)").
    const PKEY_FRIENDLY_NAME: PROPERTYKEY = PROPERTYKEY {
        fmtid: GUID::from_u128(0xa45c254e_df1c_4efd_8020_67d146a850e0),
        pid: 14,
    };
    /// `PKEY_AudioEndpoint_FormFactor` (`EndpointFormFactor`, VT_UI4).
    const PKEY_FORM_FACTOR: PROPERTYKEY = PROPERTYKEY {
        fmtid: GUID::from_u128(0x1da5d803_d492_4edd_8c23_e0c0ffee7f0e),
        pid: 0,
    };
    /// `CLSID_PolicyConfigClient`.
    const POLICY_CONFIG_CLIENT: GUID = GUID::from_u128(0x870af99c_171d_4f9e_af0d_e63df40c2bc9);
    /// `IID_IPolicyConfig` (Windows 7 – 11).
    const IID_POLICY_CONFIG: GUID = GUID::from_u128(0xf8679f50_850a_41cf_9c72_430f290290c8);

    /// `IPolicyConfig` vtable up to `SetDefaultEndpoint`: IUnknown (3), then GetMixFormat, GetDeviceFormat,
    /// ResetDeviceFormat, SetDeviceFormat, GetProcessingPeriod, SetProcessingPeriod, GetShareMode, SetShareMode,
    /// GetPropertyValue, SetPropertyValue (10 slots never called here).
    #[repr(C)]
    struct PolicyConfigVtbl {
        _unknown: [usize; 3],
        _unused: [usize; 10],
        set_default_endpoint:
            unsafe extern "system" fn(this: *mut c_void, device_id: PCWSTR, role: ERole) -> HRESULT,
    }

    /// An `IPolicyConfig` pointer; released through the `IUnknown` it was queried from.
    struct PolicyConfig {
        raw: *mut c_void,
        _owner: IUnknown,
    }

    impl PolicyConfig {
        fn create() -> CmdResult<Self> {
            // SAFETY: plain COM activation on a thread the caller initialised for COM.
            let unknown: IUnknown =
                unsafe { CoCreateInstance(&POLICY_CONFIG_CLIENT, None, CLSCTX_ALL) }
                    .map_err(|e| com_error("PolicyConfigClient", e))?;
            let mut raw: *mut c_void = std::ptr::null_mut();
            // SAFETY: `raw` receives an AddRef'ed interface pointer on success.
            unsafe { unknown.query(&IID_POLICY_CONFIG, &mut raw) }
                .ok()
                .map_err(|e| com_error("IPolicyConfig", e))?;
            // SAFETY: the queried pointer is an IUnknown-derived interface; owning it as IUnknown releases it on drop.
            let owner = unsafe { IUnknown::from_raw(raw) };
            Ok(Self { raw, _owner: owner })
        }

        fn set_default(&self, device_id: &str, role: ERole) -> CmdResult<()> {
            let wide: Vec<u16> = device_id.encode_utf16().chain(std::iter::once(0)).collect();
            // SAFETY: `raw` is a live IPolicyConfig (owned by `_owner`); slot 13 is SetDefaultEndpoint on every Windows
            // version since 7; `wide` is NUL-terminated and outlives the call.
            let hr = unsafe {
                let vtbl = *(self.raw as *const *const PolicyConfigVtbl);
                ((*vtbl).set_default_endpoint)(self.raw, PCWSTR(wide.as_ptr()), role)
            };
            hr.ok().map_err(|e| com_error("SetDefaultEndpoint", e))
        }
    }

    fn com_error(what: &str, e: windows::core::Error) -> ShellError {
        ShellError::internal(format!("{what}: {e}"))
    }

    fn enumerator() -> CmdResult<IMMDeviceEnumerator> {
        // SAFETY: plain COM activation on a thread the caller initialised for COM.
        unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }
            .map_err(|e| com_error("MMDeviceEnumerator", e))
    }

    /// Takes ownership of a CoTaskMem string.
    fn take_string(p: PWSTR) -> String {
        // SAFETY: `p` is a NUL-terminated string the callee allocated with CoTaskMemAlloc; freed exactly once here.
        unsafe {
            let s = p.to_string().unwrap_or_default();
            CoTaskMemFree(Some(p.0 as *const c_void));
            s
        }
    }

    fn device_id(device: &IMMDevice) -> CmdResult<String> {
        // SAFETY: COM call on a live device.
        let id = unsafe { device.GetId() }.map_err(|e| com_error("IMMDevice::GetId", e))?;
        Ok(take_string(id))
    }

    fn default_id(e: &IMMDeviceEnumerator, role: ERole) -> Option<String> {
        // SAFETY: COM call on a live enumerator; no default endpoint (nothing plugged in) is an error, mapped to None.
        unsafe { e.GetDefaultAudioEndpoint(eRender, role) }
            .ok()
            .and_then(|d| device_id(&d).ok())
    }

    fn describe(device: &IMMDevice, default: Option<&str>) -> CmdResult<AudioOutput> {
        let id = device_id(device)?;
        // SAFETY: COM calls on a live device / property store; PROPVARIANTs clear themselves on drop.
        let (name, form_factor) = unsafe {
            let store = device
                .OpenPropertyStore(STGM_READ)
                .map_err(|e| com_error("OpenPropertyStore", e))?;
            let name = store
                .GetValue(&PKEY_FRIENDLY_NAME)
                .map(|v| v.to_string())
                .unwrap_or_default();
            let form_factor = store
                .GetValue(&PKEY_FORM_FACTOR)
                .ok()
                .and_then(|v| u32::try_from(&v).ok())
                .unwrap_or(10);
            (name, form_factor)
        };
        Ok(AudioOutput {
            is_default: default == Some(id.as_str()),
            name: if name.trim().is_empty() {
                id.clone()
            } else {
                name
            },
            id,
            kind: kind_of(form_factor),
        })
    }

    /// Active render endpoints, by name. Needs a COM-initialised thread.
    pub fn list() -> CmdResult<AudioOutputs> {
        let e = enumerator()?;
        let default = default_id(&e, eConsole);
        // SAFETY: COM calls on a live enumerator / collection.
        let collection = unsafe { e.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE) }
            .map_err(|e| com_error("EnumAudioEndpoints", e))?;
        let count = unsafe { collection.GetCount() }.map_err(|e| com_error("GetCount", e))?;
        let mut devices = Vec::new();
        for i in 0..count {
            // SAFETY: `i` < count.
            let item = unsafe { collection.Item(i) }.map_err(|e| com_error("Item", e))?;
            match describe(&item, default.as_deref()) {
                Ok(output) => devices.push(output),
                Err(e) => tracing::debug!(error = %e, "audio endpoint skipped"),
            }
        }
        sort_outputs(&mut devices);
        let can_switch = match PolicyConfig::create() {
            Ok(_) => true,
            Err(e) => {
                tracing::warn!(error = %e, "default audio device cannot be switched from the shell");
                false
            }
        };
        Ok(AudioOutputs {
            devices,
            can_switch,
        })
    }

    /// Current default render endpoint per role.
    pub fn defaults() -> CmdResult<AudioDefaults> {
        let e = enumerator()?;
        Ok(AudioDefaults {
            console: default_id(&e, eConsole),
            multimedia: default_id(&e, eMultimedia),
            communications: default_id(&e, eCommunications),
        })
    }

    /// Makes `wanted` the default for every role where it differs from `current`; every role is tried.
    pub fn set_defaults(wanted: &AudioDefaults, current: &AudioDefaults) -> CmdResult<()> {
        let policy = PolicyConfig::create()?;
        let mut first_error = None;
        for (role, want, have) in [
            (eConsole, &wanted.console, &current.console),
            (eMultimedia, &wanted.multimedia, &current.multimedia),
            (
                eCommunications,
                &wanted.communications,
                &current.communications,
            ),
        ] {
            let Some(id) = want.as_deref() else { continue };
            if have.as_deref() == Some(id) {
                continue;
            }
            if let Err(e) = policy.set_default(id, role) {
                tracing::warn!(role = role.0, error = %e, "cannot set the default audio device");
                first_error.get_or_insert(e);
            }
        }
        first_error.map_or(Ok(()), Err)
    }

    /// Master volume (0–100) + mute of the default multimedia endpoint, like the Agent's `CoreAudioVolume`.
    pub fn set_volume(level: i32, muted: bool) -> CmdResult<()> {
        let e = enumerator()?;
        // SAFETY: COM calls on live objects; the event-context GUID may be null.
        unsafe {
            let device = e
                .GetDefaultAudioEndpoint(eRender, eMultimedia)
                .map_err(|e| com_error("GetDefaultAudioEndpoint", e))?;
            let volume: IAudioEndpointVolume = device
                .Activate(CLSCTX_ALL, None)
                .map_err(|e| com_error("IAudioEndpointVolume", e))?;
            let scalar = (level.clamp(0, 100) as f32) / 100.0;
            volume
                .SetMasterVolumeLevelScalar(scalar, std::ptr::null())
                .map_err(|e| com_error("SetMasterVolumeLevelScalar", e))?;
            volume
                .SetMute(BOOL::from(muted), std::ptr::null())
                .map_err(|e| com_error("SetMute", e))?;
        }
        Ok(())
    }
}

#[cfg(not(windows))]
pub(super) mod native {
    use super::{AudioDefaults, AudioOutputs};
    use crate::state::{CmdResult, ShellError};

    pub fn list() -> CmdResult<AudioOutputs> {
        Err(ShellError::unsupported())
    }

    pub fn defaults() -> CmdResult<AudioDefaults> {
        Err(ShellError::unsupported())
    }

    pub fn set_defaults(_wanted: &AudioDefaults, _current: &AudioDefaults) -> CmdResult<()> {
        Err(ShellError::unsupported())
    }

    pub fn set_volume(_level: i32, _muted: bool) -> CmdResult<()> {
        Err(ShellError::unsupported())
    }
}

/// Applies a volume the Agent confirmed (`sys_set_volume`) to the player's default endpoint, off the caller's thread.
/// Best effort: failures are logged only.
pub fn set_volume(level: i32, muted: bool) {
    super::spawn_com("volume", move || {
        if let Err(e) = native::set_volume(level, muted) {
            tracing::debug!(level, muted, error = %e, "volume not applied in the user session");
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use clubshell_protocol::error::ErrorCode;

    fn output(id: &str, name: &str) -> AudioOutput {
        AudioOutput {
            id: id.to_owned(),
            name: name.to_owned(),
            kind: AudioKind::Other,
            is_default: false,
        }
    }

    #[test]
    fn form_factors_map_to_kinds() {
        assert_eq!(kind_of(1), AudioKind::Speakers);
        assert_eq!(kind_of(3), AudioKind::Headphones);
        assert_eq!(kind_of(5), AudioKind::Headset);
        assert_eq!(kind_of(6), AudioKind::Headset);
        assert_eq!(kind_of(9), AudioKind::Digital);
        assert_eq!(kind_of(8), AudioKind::Digital);
        assert_eq!(kind_of(0), AudioKind::Other);
        assert_eq!(kind_of(10), AudioKind::Other);
    }

    #[test]
    fn device_ids_must_name_an_active_output() {
        let devices = [
            output("{0.0.0.00000000}.{a}", "Speakers"),
            output("{0.0.0.00000000}.{b}", "Headphones"),
        ];
        assert_eq!(
            validate_device_id(" {0.0.0.00000000}.{b} ", &devices).unwrap(),
            "{0.0.0.00000000}.{b}"
        );
        assert_eq!(
            validate_device_id("{0.0.0.00000000}.{c}", &devices)
                .unwrap_err()
                .code,
            ErrorCode::NotFound
        );
        assert_eq!(
            validate_device_id("  ", &devices).unwrap_err().code,
            ErrorCode::Validation
        );
        assert_eq!(
            validate_device_id(&"x".repeat(DEVICE_ID_MAX + 1), &devices)
                .unwrap_err()
                .code,
            ErrorCode::Validation
        );
    }

    #[test]
    fn outputs_sort_by_name_and_serialize_camel_case() {
        let mut devices = vec![
            output("2", "speakers"),
            output("1", "Headphones"),
            output("0", "Speakers"),
        ];
        sort_outputs(&mut devices);
        let ids: Vec<&str> = devices.iter().map(|d| d.id.as_str()).collect();
        assert_eq!(ids, ["1", "0", "2"]);
        let json = serde_json::to_value(AudioOutputs {
            devices: vec![AudioOutput {
                kind: AudioKind::Headphones,
                is_default: true,
                ..output("1", "Headphones")
            }],
            can_switch: true,
        })
        .unwrap();
        assert_eq!(
            json,
            serde_json::json!({
                "devices": [{ "id": "1", "name": "Headphones", "kind": "headphones", "isDefault": true }],
                "canSwitch": true
            })
        );
        assert_eq!(
            AudioDefaults::all("x"),
            AudioDefaults {
                console: Some("x".into()),
                multimedia: Some("x".into()),
                communications: Some("x".into()),
            }
        );
    }

    /// Read-only: lists this PC's outputs and default devices, never changes them. A PC without audio may fail the
    /// enumeration; when it succeeds the list must be consistent.
    #[cfg(windows)]
    #[test]
    fn enumerates_outputs_read_only() {
        let _com = super::super::ComGuard::init();
        if let Ok(list) = native::list() {
            assert!(list.devices.iter().all(|d| !d.id.is_empty()));
            assert!(list.devices.iter().filter(|d| d.is_default).count() <= 1);
            let defaults = native::defaults().unwrap();
            if let Some(console) = defaults.console {
                assert!(list.devices.iter().any(|d| d.id == console && d.is_default));
            }
        }
    }
}
