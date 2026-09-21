use std::{env, fs, path::PathBuf};

/// Application manifest for the *test* executables (unit tests of the lib target).
///
/// `tauri_build` embeds a manifest into the real binary, but Cargo test harnesses get none, so
/// they load the Windows-5 `comctl32.dll` from System32, which lacks `TaskDialogIndirect`
/// (imported by Tauri's dialog code) and the process dies at load time with
/// `STATUS_ENTRYPOINT_NOT_FOUND` before a single test runs. Declaring the Common Controls v6
/// dependency (plus the same DPI awareness as the app) makes the test binaries start.
const TEST_MANIFEST: &str = r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <dependency>
    <dependentAssembly>
      <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*"/>
    </dependentAssembly>
  </dependency>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
"#;

fn main() {
    if env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows")
        && env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc")
    {
        let out = PathBuf::from(env::var("OUT_DIR").expect("OUT_DIR"));
        let manifest = out.join("clubshell-shell-tests.manifest");
        fs::write(&manifest, TEST_MANIFEST).expect("write test manifest");
        println!("cargo:rustc-link-arg-tests=/MANIFEST:EMBED");
        println!(
            "cargo:rustc-link-arg-tests=/MANIFESTINPUT:{}",
            manifest.display()
        );
    }

    tauri_build::build()
}
