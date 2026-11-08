/**
 * The name Tauri carries an event under: the dots of an IPC name become colons (`agent://sys.readinessChanged` →
 * `agent://sys:readinessChanged`). Tauri 2 takes only letters, digits, `-`, `/`, `:` and `_` in an event name, so the
 * shell's Rust side emits agent events that way (`agent_event_name`, SHELL_CHANGES 88); everything else in the
 * webview, the mock bus included, keeps the IPC names.
 */
export const wireEventName = (event: string): string => event.replace(/\./g, ':');
