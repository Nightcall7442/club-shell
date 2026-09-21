//! Binary entry point; everything lives in the library so tests and the mobile entry point share it.

// Release builds run as the kiosk user's shell: no console window.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    clubshell_shell_lib::run();
}
