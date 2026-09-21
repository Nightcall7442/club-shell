//! Kiosk-hardening logic of `clubshell-winutil` that runs on every platform: blocked-combo parsing
//! (`policy.explorer.blockedKeyCombos`, `shell.json → kiosk.exitHotkey`), the keyboard/mouse filter
//! semantics used by the low-level hooks, `Rect` geometry and monitor selection. The Windows-only tests
//! at the bottom touch the real desktop (monitor enumeration, the `Progman` desktop window) and skip
//! themselves when no display is attached (session 0 / headless CI).

use std::collections::HashSet;

use clubshell_winutil::hooks::{block_combos, edge_blocker, key_name, llkhf, vk, KIOSK_DEFAULTS};
use clubshell_winutil::monitor::{monitor_containing, pick_primary};
use clubshell_winutil::window::alt_tab_combos;
use clubshell_winutil::{
    BlockedCombo, HookAction, KeyEvent, MonitorInfo, MouseEvent, Rect, WinUtilError,
};

// ───────────────────────────── Helpers ─────────────────────────────

fn combo(ctrl: bool, alt: bool, shift: bool, win: bool, key: Option<u32>) -> BlockedCombo {
    BlockedCombo {
        ctrl,
        alt,
        shift,
        win,
        key,
    }
}

fn key(vk: u32) -> KeyEvent {
    KeyEvent {
        vk,
        scan: 0,
        flags: 0,
        alt: false,
        ctrl: false,
        shift: false,
        win: false,
        key_up: false,
    }
}

fn chord(vk: u32, ctrl: bool, alt: bool, shift: bool, win: bool) -> KeyEvent {
    KeyEvent {
        ctrl,
        alt,
        shift,
        win,
        ..key(vk)
    }
}

fn letter(c: u8) -> u32 {
    u32::from(c.to_ascii_uppercase())
}

fn mouse(x: i32, y: i32) -> MouseEvent {
    MouseEvent {
        x,
        y,
        message: 0x0200,
        data: 0,
        flags: 0,
    }
}

fn monitor(index: usize, rect: Rect, primary: bool) -> MonitorInfo {
    MonitorInfo {
        index,
        handle: index as isize + 0x1000,
        name: format!(r"\\.\DISPLAY{}", index + 1),
        rect,
        work_rect: Rect::new(rect.left, rect.top, rect.right, rect.bottom - 48),
        width: rect.width(),
        height: rect.height(),
        hz: 60,
        primary,
        scale: 1.0,
    }
}

// ───────────────────────────── BlockedCombo parsing ─────────────────────────────

#[test]
fn parses_the_documented_combo_forms() {
    let f = |n: u32| vk::F1 + n - 1;
    let cases: &[(&str, BlockedCombo)] = &[
        // ARCHITECTURE.md §5 kiosk set
        ("Alt+Tab", combo(false, true, false, false, Some(vk::TAB))),
        (
            "Alt+Esc",
            combo(false, true, false, false, Some(vk::ESCAPE)),
        ),
        (
            "Ctrl+Esc",
            combo(true, false, false, false, Some(vk::ESCAPE)),
        ),
        ("Alt+F4", combo(false, true, false, false, Some(f(4)))),
        (
            "Ctrl+Shift+Esc",
            combo(true, false, true, false, Some(vk::ESCAPE)),
        ),
        ("Win", combo(false, false, false, true, None)),
        (
            "Win+D",
            combo(false, false, false, true, Some(letter(b'd'))),
        ),
        (
            "Win+R",
            combo(false, false, false, true, Some(letter(b'r'))),
        ),
        (
            "Win+E",
            combo(false, false, false, true, Some(letter(b'e'))),
        ),
        (
            "Win+L",
            combo(false, false, false, true, Some(letter(b'l'))),
        ),
        (
            "Win+I",
            combo(false, false, false, true, Some(letter(b'i'))),
        ),
        ("Win+Tab", combo(false, false, false, true, Some(vk::TAB))),
        (
            "PrintScreen",
            combo(false, false, false, false, Some(vk::SNAPSHOT)),
        ),
        // shell.json → kiosk.exitHotkey default
        (
            "Ctrl+Alt+Shift+F12",
            combo(true, true, true, false, Some(f(12))),
        ),
        // Secure attention sequence (parses, never intercepted)
        (
            "Ctrl+Alt+Del",
            combo(true, true, false, false, Some(vk::DELETE)),
        ),
        (
            "Ctrl+Alt+Delete",
            combo(true, true, false, false, Some(vk::DELETE)),
        ),
        // Modifier aliases, case and whitespace tolerance
        (
            "control+delete",
            combo(true, false, false, false, Some(vk::DELETE)),
        ),
        (
            "CTL + ESCAPE",
            combo(true, false, false, false, Some(vk::ESCAPE)),
        ),
        (
            "Meta+E",
            combo(false, false, false, true, Some(letter(b'e'))),
        ),
        (
            "Super+R",
            combo(false, false, false, true, Some(letter(b'r'))),
        ),
        (
            "Windows+L",
            combo(false, false, false, true, Some(letter(b'l'))),
        ),
        (
            "cmd+space",
            combo(false, false, false, true, Some(vk::SPACE)),
        ),
        ("LWin", combo(false, false, false, true, None)),
        ("RWin+Tab", combo(false, false, false, true, Some(vk::TAB))),
        (
            "Shift+Ctrl+Alt+Win+X",
            combo(true, true, true, true, Some(letter(b'x'))),
        ),
        // Key aliases
        ("PgUp", combo(false, false, false, false, Some(vk::PRIOR))),
        (
            "PageDown",
            combo(false, false, false, false, Some(vk::NEXT)),
        ),
        (
            "PrtSc",
            combo(false, false, false, false, Some(vk::SNAPSHOT)),
        ),
        (
            "Snapshot",
            combo(false, false, false, false, Some(vk::SNAPSHOT)),
        ),
        ("Break", combo(false, false, false, false, Some(vk::PAUSE))),
        (
            "ContextMenu",
            combo(false, false, false, false, Some(vk::APPS)),
        ),
        ("Apps", combo(false, false, false, false, Some(vk::APPS))),
        (
            "Return",
            combo(false, false, false, false, Some(vk::RETURN)),
        ),
        ("Enter", combo(false, false, false, false, Some(vk::RETURN))),
        (
            "Backspace",
            combo(false, false, false, false, Some(vk::BACK)),
        ),
        ("Ins", combo(false, false, false, false, Some(vk::INSERT))),
        (
            "CapsLock",
            combo(false, false, false, false, Some(vk::CAPITAL)),
        ),
        (
            "NumLock",
            combo(false, false, false, false, Some(vk::NUMLOCK)),
        ),
        (
            "ScrollLock",
            combo(false, false, false, false, Some(vk::SCROLL)),
        ),
        ("Left", combo(false, false, false, false, Some(vk::LEFT))),
        (
            "Ctrl+Home",
            combo(true, false, false, false, Some(vk::HOME)),
        ),
        // Function keys, digits, letters, raw virtual-key codes
        ("F1", combo(false, false, false, false, Some(vk::F1))),
        ("f12", combo(false, false, false, false, Some(f(12)))),
        ("F24", combo(false, false, false, false, Some(vk::F24))),
        (
            "Ctrl+1",
            combo(true, false, false, false, Some(u32::from(b'1'))),
        ),
        (
            "ctrl+a",
            combo(true, false, false, false, Some(letter(b'a'))),
        ),
        ("Vk0x5D", combo(false, false, false, false, Some(vk::APPS))),
        (
            "0x2C",
            combo(false, false, false, false, Some(vk::SNAPSHOT)),
        ),
        ("Alt+vk0xBA", combo(false, true, false, false, Some(0xBA))),
    ];
    for (text, expected) in cases {
        let parsed = BlockedCombo::parse(text).unwrap_or_else(|e| panic!("{text}: {e}"));
        assert_eq!(&parsed, expected, "{text}");
        assert_eq!(
            text.parse::<BlockedCombo>().unwrap(),
            parsed,
            "FromStr and parse agree for {text}"
        );
        // Display is canonical and round-trips through the parser.
        let shown = parsed.to_string();
        assert_eq!(
            BlockedCombo::parse(&shown).unwrap(),
            parsed,
            "{text} → {shown}"
        );
    }
}

#[test]
fn display_is_canonical() {
    for (text, shown) in [
        ("alt + tab", "Alt+Tab"),
        ("SHIFT+CONTROL+ESCAPE", "Ctrl+Shift+Esc"),
        ("win+d", "Win+D"),
        ("Super", "Win"),
        ("f4", "F4"),
        ("Ctrl+Alt+Shift+F12", "Ctrl+Alt+Shift+F12"),
        ("PrtSc", "PrintScreen"),
        ("Vk0x5D", "Apps"),
        ("Alt+vk0xBA", "Alt+Vk0xBA"),
        ("control+delete", "Ctrl+Del"),
        ("Ctrl+1", "Ctrl+1"),
    ] {
        assert_eq!(
            BlockedCombo::parse(text).unwrap().to_string(),
            shown,
            "{text}"
        );
    }
    assert_eq!(key_name(vk::DELETE), "Del");
    assert_eq!(key_name(vk::F1 + 11), "F12");
    assert_eq!(key_name(letter(b'q')), "Q");
    assert_eq!(key_name(u32::from(b'7')), "7");
    assert_eq!(key_name(0xBA), "Vk0xBA");
    assert_eq!(key_name(0x05), "Vk0x05");
}

#[test]
fn rejects_malformed_combos() {
    for bad in [
        "",
        "   ",
        "+",
        "Ctrl+",
        "+Tab",
        "Ctrl++Del",
        "Ctrl+Foo",
        "Hyper+X",
        "Alt+Tab+Esc",
        "Ctrl+A+B",
        "F0",
        "F25",
        "F1a",
        "Vk0x00",
        "Vk0xFF",
        "0xZZ",
        "ab",
        "Alt+Tab Esc",
        "Ctrl-Alt-Del",
    ] {
        match BlockedCombo::parse(bad) {
            Err(WinUtilError::Invalid(msg)) => assert!(!msg.is_empty(), "{bad}"),
            other => panic!("{bad:?} must be rejected as Invalid, got {other:?}"),
        }
    }
    // A modifier-only combo is fine; a single modifier repeated is idempotent.
    assert_eq!(
        BlockedCombo::parse("Ctrl+Ctrl").unwrap(),
        combo(true, false, false, false, None)
    );
    assert_eq!(
        BlockedCombo::parse("Shift").unwrap(),
        combo(false, false, true, false, None)
    );
}

#[test]
fn parse_all_and_kiosk_defaults() {
    let defaults = BlockedCombo::kiosk_defaults();
    assert_eq!(defaults.len(), KIOSK_DEFAULTS.len());
    assert_eq!(defaults, BlockedCombo::parse_all(KIOSK_DEFAULTS).unwrap());
    assert!(
        defaults.contains(&combo(false, false, false, true, None)),
        "bare Win is part of the kiosk set"
    );
    assert!(
        !defaults.iter().any(BlockedCombo::is_secure_attention),
        "Ctrl+Alt+Del is policy, not a hook"
    );
    let unique: HashSet<BlockedCombo> = defaults.iter().copied().collect();
    assert_eq!(unique.len(), defaults.len(), "no duplicate defaults");

    let policy = vec![
        "Alt+Tab".to_owned(),
        "Win".to_owned(),
        "Ctrl+Alt+Del".to_owned(),
    ];
    let parsed = BlockedCombo::parse_all(&policy).unwrap();
    assert_eq!(parsed.len(), 3);
    assert!(parsed[2].is_secure_attention());

    let err = BlockedCombo::parse_all(&["Alt+Tab", "Nope", "Win"]).unwrap_err();
    assert!(
        matches!(err, WinUtilError::Invalid(ref m) if m.contains("Nope")),
        "{err}"
    );
    assert!(BlockedCombo::parse_all::<&str>(&[]).unwrap().is_empty());
}

#[test]
fn secure_attention_detection() {
    assert!(BlockedCombo::parse("Ctrl+Alt+Del")
        .unwrap()
        .is_secure_attention());
    assert!(
        BlockedCombo::parse("Ctrl+Alt+Shift+Del")
            .unwrap()
            .is_secure_attention(),
        "extra modifiers still hit winlogon"
    );
    assert!(!BlockedCombo::parse("Ctrl+Del")
        .unwrap()
        .is_secure_attention());
    assert!(!BlockedCombo::parse("Alt+Del")
        .unwrap()
        .is_secure_attention());
    assert!(!BlockedCombo::parse("Ctrl+Alt+End")
        .unwrap()
        .is_secure_attention());
    assert!(!BlockedCombo::default().is_secure_attention());
}

// ───────────────────────────── Matching & filters ─────────────────────────────

#[test]
fn matching_requires_listed_modifiers_and_ignores_extra_ones() {
    let alt_tab = BlockedCombo::parse("Alt+Tab").unwrap();
    assert!(alt_tab.matches(&chord(vk::TAB, false, true, false, false)));
    assert!(
        alt_tab.matches(&chord(vk::TAB, false, true, true, false)),
        "Alt+Shift+Tab"
    );
    assert!(
        alt_tab.matches(&chord(vk::TAB, true, true, false, true)),
        "every modifier held"
    );
    assert!(!alt_tab.matches(&key(vk::TAB)), "Tab alone");
    assert!(
        !alt_tab.matches(&chord(vk::TAB, true, false, false, false)),
        "Ctrl+Tab is not Alt+Tab"
    );
    assert!(
        !alt_tab.matches(&chord(vk::ESCAPE, false, true, false, false)),
        "Alt+Esc is another combo"
    );
    assert!(
        alt_tab.matches(&KeyEvent {
            key_up: true,
            ..chord(vk::TAB, false, true, false, false)
        }),
        "up events match too"
    );

    let exit = BlockedCombo::parse("Ctrl+Alt+Shift+F12").unwrap();
    assert!(exit.matches(&chord(vk::F1 + 11, true, true, true, false)));
    assert!(
        !exit.matches(&chord(vk::F1 + 11, true, true, false, false)),
        "Shift missing"
    );
    assert!(
        !exit.matches(&chord(vk::F1 + 10, true, true, true, false)),
        "F11"
    );

    // Modifier-only combos match the modifier key itself, left or right, and nothing else.
    let win = BlockedCombo::parse("Win").unwrap();
    assert!(win.matches(&chord(vk::LWIN, false, false, false, true)));
    assert!(win.matches(&chord(vk::RWIN, false, false, false, true)));
    assert!(
        !win.matches(&chord(vk::LWIN, false, false, false, false)),
        "hook state says Win is not held"
    );
    assert!(
        !win.matches(&chord(letter(b'd'), false, false, false, true)),
        "Win+D needs its own entry"
    );
    assert!(
        !win.matches(&chord(vk::LCONTROL, true, false, false, true)),
        "another modifier while Win is held"
    );

    let ctrl = BlockedCombo::parse("Ctrl").unwrap();
    for k in [vk::CONTROL, vk::LCONTROL, vk::RCONTROL] {
        assert!(ctrl.matches(&chord(k, true, false, false, false)), "{k:#x}");
    }
    assert!(!ctrl.matches(&chord(vk::LSHIFT, true, true, false, false)));
    let ctrl_alt = BlockedCombo::parse("Ctrl+Alt").unwrap();
    assert!(
        ctrl_alt.matches(&chord(vk::LMENU, true, true, false, false)),
        "second modifier of the chord"
    );
    assert!(!ctrl_alt.matches(&chord(vk::LMENU, false, true, false, false)));

    // The secure attention sequence matches structurally; the OS just never delivers it.
    let cad = BlockedCombo::parse("Ctrl+Alt+Del").unwrap();
    assert!(cad.matches(&chord(vk::DELETE, true, true, false, false)));
}

#[test]
fn kiosk_filter_blocks_the_documented_chords_and_passes_everything_else() {
    let filter = block_combos(BlockedCombo::kiosk_defaults());
    let blocked = [
        ("Alt+Tab", chord(vk::TAB, false, true, false, false)),
        ("Alt+Shift+Tab", chord(vk::TAB, false, true, true, false)),
        ("Alt+Esc", chord(vk::ESCAPE, false, true, false, false)),
        ("Ctrl+Esc", chord(vk::ESCAPE, true, false, false, false)),
        ("Alt+F4", chord(vk::F1 + 3, false, true, false, false)),
        (
            "Ctrl+Shift+Esc",
            chord(vk::ESCAPE, true, false, true, false),
        ),
        ("LWin", chord(vk::LWIN, false, false, false, true)),
        ("RWin", chord(vk::RWIN, false, false, false, true)),
        ("Win+D", chord(letter(b'd'), false, false, false, true)),
        ("Win+R", chord(letter(b'r'), false, false, false, true)),
        ("Win+E", chord(letter(b'e'), false, false, false, true)),
        ("Win+L", chord(letter(b'l'), false, false, false, true)),
        ("Win+I", chord(letter(b'i'), false, false, false, true)),
        ("Win+Tab", chord(vk::TAB, false, false, false, true)),
        ("PrintScreen", key(vk::SNAPSHOT)),
        (
            "Alt+PrintScreen",
            chord(vk::SNAPSHOT, false, true, false, false),
        ),
        (
            "Win+L key up",
            KeyEvent {
                key_up: true,
                ..chord(letter(b'l'), false, false, false, true)
            },
        ),
        (
            "injected Alt+Tab",
            KeyEvent {
                flags: llkhf::INJECTED,
                ..chord(vk::TAB, false, true, false, false)
            },
        ),
    ];
    for (name, ev) in blocked {
        assert_eq!(filter(&ev), HookAction::Block, "{name}");
    }
    let passed = [
        ("Tab", key(vk::TAB)),
        ("Ctrl+Tab", chord(vk::TAB, true, false, false, false)),
        ("Shift+Tab", chord(vk::TAB, false, false, true, false)),
        ("Esc", key(vk::ESCAPE)),
        ("Shift+Esc", chord(vk::ESCAPE, false, false, true, false)),
        ("F4", key(vk::F1 + 3)),
        ("Ctrl+F4", chord(vk::F1 + 3, true, false, false, false)),
        ("Ctrl+C", chord(letter(b'c'), true, false, false, false)),
        (
            "Ctrl+Alt+Shift+F12 exit hotkey",
            chord(vk::F1 + 11, true, true, true, false),
        ),
        ("Space", key(vk::SPACE)),
        ("letter W without Win", key(letter(b'w'))),
        ("Alt alone", chord(vk::LMENU, false, true, false, false)),
        ("Ctrl alone", chord(vk::LCONTROL, true, false, false, false)),
        ("Shift alone", chord(vk::LSHIFT, false, false, true, false)),
        ("Ctrl+Alt+Del", chord(vk::DELETE, true, true, false, false)),
    ];
    for (name, ev) in passed {
        assert_eq!(filter(&ev), HookAction::Pass, "{name}");
    }

    // Policy combos extend the built-in set; an empty policy blocks nothing.
    let with_policy =
        block_combos(BlockedCombo::parse_all(&["Ctrl+Alt+Del", "F1", "Win+P"]).unwrap());
    assert_eq!(with_policy(&key(vk::F1)), HookAction::Block);
    assert_eq!(
        with_policy(&chord(letter(b'p'), false, false, false, true)),
        HookAction::Block
    );
    assert_eq!(
        with_policy(&chord(vk::TAB, false, true, false, false)),
        HookAction::Pass,
        "Alt+Tab is not in this policy"
    );
    let nothing = block_combos(Vec::new());
    assert_eq!(
        nothing(&chord(vk::TAB, false, true, false, false)),
        HookAction::Pass
    );
    assert!(!BlockedCombo::any_matches(
        &[],
        &chord(vk::LWIN, false, false, false, true)
    ));
}

#[test]
fn key_event_flags() {
    assert!(KeyEvent {
        flags: llkhf::INJECTED,
        ..key(vk::SPACE)
    }
    .injected());
    assert!(KeyEvent {
        flags: llkhf::INJECTED | llkhf::UP | llkhf::EXTENDED,
        ..key(vk::SPACE)
    }
    .injected());
    assert!(!KeyEvent {
        flags: llkhf::ALTDOWN | llkhf::UP,
        ..key(vk::SPACE)
    }
    .injected());
    assert!(!key(vk::SPACE).injected());
}

#[test]
fn alt_tab_combos_cover_every_switcher_chord() {
    let combos = alt_tab_combos();
    assert_eq!(combos.len(), 4);
    for (name, ev) in [
        ("Alt+Tab", chord(vk::TAB, false, true, false, false)),
        ("Alt+Esc", chord(vk::ESCAPE, false, true, false, false)),
        ("Win+Tab", chord(vk::TAB, false, false, false, true)),
        ("Ctrl+Alt+Tab", chord(vk::TAB, true, true, false, false)),
    ] {
        assert!(BlockedCombo::any_matches(&combos, &ev), "{name}");
    }
    assert!(
        !BlockedCombo::any_matches(&combos, &chord(vk::TAB, true, false, false, false)),
        "Ctrl+Tab switches tabs, not windows"
    );
    assert!(
        !BlockedCombo::any_matches(&combos, &chord(vk::LWIN, false, false, false, true)),
        "bare Win belongs to the kiosk set"
    );
}

#[test]
fn mouse_edge_blocker_geometry() {
    let bounds = Rect::from_size(0, 0, 1920, 1080);

    let corners = edge_blocker(bounds, 4, true);
    for (name, ev) in [
        ("top-left", mouse(0, 0)),
        ("top-left inner", mouse(3, 3)),
        ("top-right", mouse(1919, 0)),
        ("bottom-left", mouse(0, 1079)),
        ("bottom-right", mouse(1916, 1076)),
    ] {
        assert_eq!(corners(&ev), HookAction::Block, "{name}");
    }
    for (name, ev) in [
        ("top edge middle", mouse(960, 0)),
        ("left edge middle", mouse(0, 540)),
        ("just inside the corner margin", mouse(4, 4)),
        ("centre", mouse(960, 540)),
    ] {
        assert_eq!(corners(&ev), HookAction::Pass, "{name}");
    }

    let edges = edge_blocker(bounds, 4, false);
    assert_eq!(
        edges(&mouse(960, 0)),
        HookAction::Block,
        "top strip (auto-hidden taskbar)"
    );
    assert_eq!(edges(&mouse(960, 1079)), HookAction::Block, "bottom strip");
    assert_eq!(edges(&mouse(0, 540)), HookAction::Block, "left strip");
    assert_eq!(edges(&mouse(1916, 540)), HookAction::Block, "right strip");
    assert_eq!(
        edges(&mouse(960, 4)),
        HookAction::Pass,
        "first row past the margin"
    );
    assert_eq!(edges(&mouse(960, 540)), HookAction::Pass);

    // A zero margin never blocks anything inside the bounds; the blocker follows the monitor origin.
    let none = edge_blocker(bounds, 0, false);
    assert_eq!(none(&mouse(0, 0)), HookAction::Pass);
    assert_eq!(none(&mouse(1919, 1079)), HookAction::Pass);
    let left_monitor = edge_blocker(Rect::from_size(-1920, 0, 1920, 1080), 2, true);
    assert_eq!(left_monitor(&mouse(-1920, 0)), HookAction::Block);
    assert_eq!(left_monitor(&mouse(-1, 1079)), HookAction::Block);
    assert_eq!(left_monitor(&mouse(-960, 540)), HookAction::Pass);

    // Message classification used by the mouse hook.
    assert!(mouse(0, 0).is_move());
    assert!(!mouse(0, 0).is_button());
    assert!(!mouse(0, 0).is_wheel());
    for msg in [
        0x0201u32, 0x0202, 0x0204, 0x0205, 0x0207, 0x0208, 0x020B, 0x020C,
    ] {
        assert!(
            MouseEvent {
                message: msg,
                ..mouse(0, 0)
            }
            .is_button(),
            "{msg:#x}"
        );
    }
    assert!(MouseEvent {
        message: 0x020A,
        ..mouse(0, 0)
    }
    .is_wheel());
    assert!(MouseEvent {
        message: 0x020E,
        ..mouse(0, 0)
    }
    .is_wheel());
    assert!(MouseEvent {
        flags: 1,
        ..mouse(0, 0)
    }
    .injected());
    assert!(!MouseEvent {
        flags: 2,
        ..mouse(0, 0)
    }
    .injected());
}

// ───────────────────────────── Rect ─────────────────────────────

#[test]
fn rect_geometry_and_edges() {
    let r = Rect::from_size(10, 20, 100, 50);
    assert_eq!(r, Rect::new(10, 20, 110, 70));
    assert_eq!((r.width(), r.height()), (100, 50));
    assert!(r.contains(10, 20), "top-left is inclusive");
    assert!(r.contains(109, 69), "last pixel");
    assert!(!r.contains(110, 20), "right edge is exclusive");
    assert!(!r.contains(10, 70), "bottom edge is exclusive");
    assert!(!r.contains(9, 20));
    assert!(!r.is_empty());

    assert!(Rect::default().is_empty());
    assert_eq!(Rect::default(), Rect::new(0, 0, 0, 0));
    assert!(Rect::new(5, 5, 5, 9).is_empty(), "zero width");
    assert!(Rect::new(5, 5, 9, 5).is_empty(), "zero height");
    assert!(Rect::new(10, 10, 5, 20).is_empty(), "inverted");
    assert_eq!(Rect::new(10, 10, 5, 20).width(), -5);
    assert!(!Rect::new(10, 10, 5, 20).contains(7, 15));

    // Virtual-screen coordinates left of / above the primary monitor are negative.
    let left = Rect::from_size(-1920, -200, 1920, 1080);
    assert_eq!(left.right, 0);
    assert_eq!(left.bottom, 880);
    assert!(left.contains(-1, 879));
    assert!(!left.contains(0, 0));

    // Copy + Hash + Eq: usable as a set key.
    let set: HashSet<Rect> = [r, r, left, Rect::default()].into_iter().collect();
    assert_eq!(set.len(), 3);
}

// ───────────────────────────── Monitors ─────────────────────────────

#[test]
fn primary_monitor_selection_rules() {
    let left = monitor(0, Rect::from_size(-1920, 0, 1920, 1080), false);
    let main = monitor(1, Rect::from_size(0, 0, 2560, 1440), true);
    let right = monitor(2, Rect::from_size(2560, 0, 1920, 1080), false);

    // 1. The flagged primary wins regardless of order.
    let all = [right.clone(), left.clone(), main.clone()];
    assert_eq!(pick_primary(&all), Some(&main));

    // 2. Without a flag, the monitor containing the virtual-screen origin.
    let unflagged: Vec<MonitorInfo> = all
        .iter()
        .cloned()
        .map(|m| MonitorInfo {
            primary: false,
            ..m
        })
        .collect();
    assert_eq!(pick_primary(&unflagged).map(|m| m.index), Some(1));

    // 3. Otherwise the first enumerated.
    let offset = [
        monitor(0, Rect::from_size(100, 100, 800, 600), false),
        monitor(1, Rect::from_size(900, 100, 800, 600), false),
    ];
    assert_eq!(pick_primary(&offset).map(|m| m.index), Some(0));
    assert_eq!(pick_primary(&[]), None);

    // Two flagged primaries (transient during a topology change): the first flagged one.
    let two = [
        monitor(0, Rect::from_size(0, 0, 1920, 1080), true),
        monitor(1, Rect::from_size(1920, 0, 1920, 1080), true),
    ];
    assert_eq!(pick_primary(&two).map(|m| m.index), Some(0));
}

#[test]
fn monitor_containing_prefers_containing_then_nearest() {
    let a = monitor(0, Rect::from_size(0, 0, 1920, 1080), true);
    let b = monitor(1, Rect::from_size(1920, 0, 1920, 1080), false);
    let c = monitor(2, Rect::from_size(0, 1080, 1280, 720), false);
    let all = [a, b, c];

    assert_eq!(monitor_containing(&all, 0, 0).map(|m| m.index), Some(0));
    assert_eq!(
        monitor_containing(&all, 1919, 1079).map(|m| m.index),
        Some(0)
    );
    assert_eq!(
        monitor_containing(&all, 1920, 0).map(|m| m.index),
        Some(1),
        "shared edge belongs to the right monitor"
    );
    assert_eq!(
        monitor_containing(&all, 100, 1080).map(|m| m.index),
        Some(2)
    );

    // Outside every monitor: nearest by edge distance.
    assert_eq!(monitor_containing(&all, -50, -50).map(|m| m.index), Some(0));
    assert_eq!(monitor_containing(&all, 5000, 10).map(|m| m.index), Some(1));
    assert_eq!(
        monitor_containing(&all, 100, 3000).map(|m| m.index),
        Some(2)
    );
    assert_eq!(
        monitor_containing(&all, 1500, 1500).map(|m| m.index),
        Some(2),
        "closer to the bottom monitor's right edge"
    );
    assert_eq!(
        monitor_containing(&all, 3000, 1500).map(|m| m.index),
        Some(1)
    );
    assert!(monitor_containing(&[], 0, 0).is_none());
}

#[test]
fn monitor_info_is_plain_data() {
    let m = monitor(0, Rect::from_size(0, 0, 1920, 1080), true);
    assert_eq!(m.width, 1920);
    assert_eq!(m.height, 1080);
    assert_eq!(
        m.work_rect.height(),
        1032,
        "taskbar excluded from the work area"
    );
    assert_eq!(m.name, r"\\.\DISPLAY1");
    assert_eq!(m.clone(), m);
    assert_ne!(
        MonitorInfo {
            scale: 1.5,
            ..m.clone()
        },
        m
    );
}

// ───────────────────────────── Windows desktop ─────────────────────────────

/// Real display enumeration. `EnumDisplayMonitors` yields nothing exactly when
/// `GetSystemMetrics(SM_CMONITORS) == 0` (session 0, headless CI), in which case the test skips itself.
#[cfg(windows)]
#[test]
fn enumerates_attached_monitors() {
    use clubshell_winutil::monitor::{enumerate, monitor_at_point, primary, DisplayWatcher};

    let monitors = match enumerate() {
        Ok(m) if !m.is_empty() => m,
        Ok(_) => {
            eprintln!("skipped: no display attached (SM_CMONITORS == 0)");
            return;
        }
        Err(e) => {
            eprintln!("skipped: EnumDisplayMonitors unavailable in this session: {e}");
            return;
        }
    };

    for (i, m) in monitors.iter().enumerate() {
        assert_eq!(m.index, i, "enumeration order is the index");
        assert_ne!(m.handle, 0, "{}", m.name);
        assert!(m.name.starts_with(r"\\.\DISPLAY"), "{}", m.name);
        assert!(!m.rect.is_empty(), "{}: {:?}", m.name, m.rect);
        assert_eq!((m.width, m.height), (m.rect.width(), m.rect.height()));
        assert!(m.width > 0 && m.height > 0);
        assert!(!m.work_rect.is_empty(), "{}: {:?}", m.name, m.work_rect);
        assert!(
            m.work_rect.width() <= m.rect.width() && m.work_rect.height() <= m.rect.height(),
            "work area fits the monitor"
        );
        assert!(
            (1.0..=5.0).contains(&m.scale),
            "{}: scale {}",
            m.name,
            m.scale
        );
    }
    let handles: HashSet<isize> = monitors.iter().map(|m| m.handle).collect();
    assert_eq!(handles.len(), monitors.len(), "handles are unique");
    assert!(
        monitors.iter().filter(|m| m.primary).count() <= 1,
        "at most one primary"
    );

    let chosen = pick_primary(&monitors).expect("non-empty list has a primary");
    let p = primary().expect("primary()");
    assert_eq!(p.handle, chosen.handle);
    assert!(
        p.rect.contains(0, 0),
        "the primary monitor owns the virtual-screen origin"
    );

    let at_origin = monitor_at_point(0, 0).expect("monitor_at_point");
    assert_eq!(at_origin.handle, p.handle);
    let (cx, cy) = (
        p.rect.left + p.rect.width() / 2,
        p.rect.top + p.rect.height() / 2,
    );
    assert_eq!(monitor_at_point(cx, cy).expect("centre").handle, p.handle);
    let far = monitor_at_point(i32::MAX / 2, i32::MAX / 2).expect("nearest to a far point");
    assert!(handles.contains(&far.handle));

    // The watcher thread starts, publishes nothing without a topology change, and stops on drop.
    let (watcher, rx) = DisplayWatcher::start().expect("DisplayWatcher::start");
    std::thread::sleep(std::time::Duration::from_millis(50));
    assert!(
        rx.try_recv().is_err(),
        "no change event without a display change"
    );
    drop(watcher);
    assert!(
        rx.recv().is_err(),
        "sender is gone once the watcher thread exits"
    );
}

/// The desktop window (`Progman`, owned by explorer) exists in every interactive session; without one
/// (session 0) window enumeration comes back empty and the test skips itself.
#[cfg(windows)]
#[test]
fn finds_the_desktop_window() {
    use clubshell_winutil::window::{enumerate_windows, find_window, window_pid, window_rect};

    let windows = match enumerate_windows() {
        Ok(w) if !w.is_empty() => w,
        Ok(_) => {
            eprintln!("skipped: no top-level windows in this session");
            return;
        }
        Err(e) => {
            eprintln!("skipped: EnumWindows unavailable in this session: {e}");
            return;
        }
    };
    let Some(desktop) = find_window(Some("progman"), None).expect("find_window") else {
        eprintln!("skipped: no Progman window (explorer not running)");
        return;
    };
    assert_ne!(desktop, 0);
    assert!(
        windows.iter().any(|w| w.hwnd == desktop),
        "find_window picks from enumerate_windows"
    );
    let info = windows
        .iter()
        .find(|w| w.hwnd == desktop)
        .expect("desktop info");
    assert!(info.class.eq_ignore_ascii_case("Progman"));
    assert_eq!(
        find_window(Some("Progman"), Some(info.title.as_str())).expect("class+title"),
        Some(desktop)
    );
    assert_eq!(
        find_window(Some("ClubShellNoSuchClass"), None).expect("unknown class"),
        None
    );
    assert!(
        matches!(find_window(None, None), Err(WinUtilError::Invalid(_))),
        "needs a class or a title"
    );

    let pid = window_pid(desktop).expect("window_pid");
    assert!(pid > 0);
    assert_eq!(pid, info.pid);
    assert!(
        !window_rect(desktop).expect("window_rect").is_empty(),
        "the desktop covers the screen"
    );
    assert!(
        matches!(
            window_pid(0),
            Err(WinUtilError::Win32 {
                api: "GetWindowThreadProcessId",
                ..
            })
        ),
        "a null handle is rejected"
    );
}
