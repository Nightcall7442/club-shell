use std::{env, fs, path::PathBuf};

use tauri_build::{Attributes, WindowsAttributes};

/// Windows application manifest, embedded by the linker into every executable this crate
/// produces: the app binary *and* the Cargo test harnesses.
///
/// `tauri_build` normally embeds its default manifest as a resource of the bin target only.
/// Test executables then carry no manifest, load the Windows-5 `comctl32.dll` from System32,
/// which lacks `TaskDialogIndirect` (imported by Tauri's dialog code), and die at load time
/// with `STATUS_ENTRYPOINT_NOT_FOUND` before a single test runs. Linking the manifest via
/// `/MANIFEST:EMBED` applies to all link targets, so the Tauri resource manifest is disabled
/// to avoid a duplicate `RT_MANIFEST` in the app binary.
const APP_MANIFEST: &str = r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <dependency>
    <dependentAssembly>
      <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*"/>
    </dependentAssembly>
  </dependency>
</assembly>
"#;

fn main() {
    let msvc = env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows")
        && env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc");

    let mut attributes = Attributes::new();
    if msvc {
        let out = PathBuf::from(env::var("OUT_DIR").expect("OUT_DIR"));
        let manifest = out.join("clubshell-shell.manifest");
        fs::write(&manifest, APP_MANIFEST).expect("write app manifest");
        println!("cargo:rustc-link-arg=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg=/MANIFESTINPUT:{}", manifest.display());
        attributes = attributes.windows_attributes(WindowsAttributes::new_without_app_manifest());
    }

    tauri_build::try_build(attributes).expect("tauri build");
}
