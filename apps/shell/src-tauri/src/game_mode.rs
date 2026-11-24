//! Which games keep the kiosk in game mode (D-99).
//!
//! Game mode used to be one flag, switched by every `game.stateChanged` and by `games_launch`: game B
//! exiting (or a second launch failing) turned it off while game A still ran, and a Shell that
//! started (or reconnected) while a game ran never turned it on — its foreground guard then minimized
//! the game every few seconds. [`RunningGames`] is the set instead: game mode is on while it is not
//! empty. The Agent's `games.running` answer, asked after every hello, replaces what the Agent knows
//! ([`RunningGames::reconcile`]); a launch this Shell has in flight is kept until its result.
//!
//! Pure logic (no Tauri, no Win32) so it is unit-tested on any host.

use std::collections::HashMap;

use clubshell_protocol::games::GameState;
use uuid::Uuid;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
struct Entry {
    /// Process id the Agent reported, when known.
    pid: Option<u32>,
    /// `games_launch` of this Shell is waiting for the Agent's answer.
    launching_locally: bool,
    /// The Agent itself reported the game (an event or `games.running`).
    confirmed: bool,
}

/// What a change did to game mode: `Some(on)` when it switched, `None` when it stayed as it was.
pub type Transition = Option<bool>;

/// The running (or launching) games, by game id.
#[derive(Debug, Default)]
pub struct RunningGames {
    games: HashMap<Uuid, Entry>,
}

impl RunningGames {
    pub fn new() -> Self {
        Self::default()
    }

    /// Game mode wanted: any game launching or running.
    pub fn is_active(&self) -> bool {
        !self.games.is_empty()
    }

    pub fn len(&self) -> usize {
        self.games.len()
    }

    pub fn is_empty(&self) -> bool {
        self.games.is_empty()
    }

    pub fn contains(&self, game_id: Uuid) -> bool {
        self.games.contains_key(&game_id)
    }

    /// Known pids of the running games.
    pub fn pids(&self) -> Vec<u32> {
        let mut pids: Vec<u32> = self.games.values().filter_map(|e| e.pid).collect();
        pids.sort_unstable();
        pids
    }

    fn change(&mut self, f: impl FnOnce(&mut HashMap<Uuid, Entry>)) -> Transition {
        let before = self.is_active();
        f(&mut self.games);
        let after = self.is_active();
        (before != after).then_some(after)
    }

    /// `games_launch` starts. Returns the transition and whether the game was not in the set yet
    /// (pass it back to [`launch_finished`](Self::launch_finished)).
    pub fn launch_started(&mut self, game_id: Uuid) -> (Transition, bool) {
        let mut fresh = false;
        let t = self.change(|games| {
            let entry = games.entry(game_id).or_insert_with(|| {
                fresh = true;
                Entry::default()
            });
            entry.launching_locally = true;
        });
        (t, fresh)
    }

    /// `games_launch` answered. A launch that failed drops its game unless it was there before or
    /// the Agent reported it in the meantime; a launch that worked stays until the game's exit event.
    pub fn launch_finished(&mut self, game_id: Uuid, ok: bool, fresh: bool) -> Transition {
        self.change(|games| {
            let Some(entry) = games.get_mut(&game_id) else {
                return; // its terminal event came first
            };
            entry.launching_locally = false;
            if !ok && fresh && !entry.confirmed {
                games.remove(&game_id);
            }
        })
    }

    /// `game.stateChanged` from the Agent.
    pub fn apply_event(&mut self, game_id: Uuid, state: GameState, pid: Option<i32>) -> Transition {
        let pid = pid.and_then(|p| u32::try_from(p).ok()).filter(|p| *p != 0);
        self.change(|games| {
            if state.is_terminal() {
                games.remove(&game_id);
            } else {
                let entry = games.entry(game_id).or_default();
                entry.confirmed = true;
                if pid.is_some() {
                    entry.pid = pid;
                }
            }
        })
    }

    /// The Agent's `games.running` (after a hello: the Shell or the Agent restarted). The set
    /// becomes what the Agent reports, plus launches of this Shell still waiting for their answer.
    pub fn reconcile<I>(&mut self, running: I) -> Transition
    where
        I: IntoIterator<Item = (Uuid, GameState, i32)>,
    {
        let reported: HashMap<Uuid, Entry> = running
            .into_iter()
            .filter(|(_, state, _)| !state.is_terminal())
            .map(|(id, _, pid)| {
                let pid = u32::try_from(pid).ok().filter(|p| *p != 0);
                (
                    id,
                    Entry {
                        pid,
                        launching_locally: false,
                        confirmed: true,
                    },
                )
            })
            .collect();
        self.change(|games| {
            let mut next = reported;
            for (id, entry) in games.iter() {
                if entry.launching_locally {
                    next.entry(*id)
                        .and_modify(|e| e.launching_locally = true)
                        .or_insert(*entry);
                }
            }
            *games = next;
        })
    }

    /// The session ended: nothing the player ran keeps game mode.
    pub fn clear(&mut self) -> Transition {
        self.change(HashMap::clear)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const A: Uuid = Uuid::from_u128(0xA);
    const B: Uuid = Uuid::from_u128(0xB);

    #[test]
    fn second_game_exiting_keeps_game_mode() {
        let mut g = RunningGames::new();
        assert_eq!(g.apply_event(A, GameState::Running, Some(100)), Some(true));
        assert_eq!(g.apply_event(B, GameState::Running, Some(200)), None);
        assert_eq!(g.pids(), vec![100, 200]);
        assert_eq!(g.apply_event(B, GameState::Exited, Some(200)), None);
        assert!(g.is_active(), "A still runs");
        assert_eq!(g.pids(), vec![100]);
        assert_eq!(g.apply_event(A, GameState::Killed, None), Some(false));
    }

    #[test]
    fn failed_launch_keeps_the_running_game() {
        let mut g = RunningGames::new();
        g.apply_event(A, GameState::Running, Some(100));
        let (t, fresh) = g.launch_started(B);
        assert_eq!((t, fresh), (None, true));
        assert_eq!(g.launch_finished(B, false, fresh), None);
        assert!(g.contains(A) && !g.contains(B));
        // Failed event too (after or before the answer) changes nothing for A.
        assert_eq!(g.apply_event(B, GameState::Failed, None), None);
        assert!(g.is_active());
    }

    #[test]
    fn failed_launch_alone_ends_game_mode() {
        let mut g = RunningGames::new();
        let (t, fresh) = g.launch_started(A);
        assert_eq!(t, Some(true));
        assert_eq!(g.launch_finished(A, false, fresh), Some(false));
    }

    #[test]
    fn relaunch_of_a_running_game_that_fails_keeps_it() {
        let mut g = RunningGames::new();
        g.apply_event(A, GameState::Running, Some(100));
        let (_, fresh) = g.launch_started(A);
        assert!(!fresh);
        assert_eq!(g.launch_finished(A, false, fresh), None);
        assert!(g.contains(A));
    }

    #[test]
    fn launch_answer_after_the_running_event_keeps_it() {
        let mut g = RunningGames::new();
        let (_, fresh) = g.launch_started(A);
        g.apply_event(A, GameState::Running, Some(100));
        // A transport error after the Agent did start it: the game runs, game mode stays.
        assert_eq!(g.launch_finished(A, false, fresh), None);
        assert!(g.contains(A));
        // A game that exited before the answer stays gone.
        let (_, fresh) = g.launch_started(B);
        g.apply_event(B, GameState::Exited, None);
        assert_eq!(g.launch_finished(B, true, fresh), None);
        assert!(!g.contains(B));
    }

    #[test]
    fn reconnect_restores_game_mode_from_the_agent() {
        // A fresh Shell (restart after a crash, an update or the heartbeat watchdog) knows nothing.
        let mut g = RunningGames::new();
        assert_eq!(
            g.reconcile([(A, GameState::Running, 100), (B, GameState::Exited, 200)]),
            Some(true)
        );
        assert_eq!(g.pids(), vec![100]);
        // Reconnect while nothing runs any more: game mode off.
        assert_eq!(g.reconcile(Vec::new()), Some(false));
    }

    #[test]
    fn reconcile_keeps_an_own_launch_in_flight() {
        let mut g = RunningGames::new();
        g.apply_event(A, GameState::Running, Some(100));
        let (_, fresh) = g.launch_started(B);
        assert_eq!(g.reconcile(Vec::new()), None, "B is still launching");
        assert!(!g.contains(A) && g.contains(B));
        assert_eq!(g.launch_finished(B, false, fresh), Some(false));
    }

    #[test]
    fn session_end_clears() {
        let mut g = RunningGames::new();
        g.apply_event(A, GameState::Launching, None);
        assert_eq!(g.pids(), Vec::<u32>::new());
        assert_eq!(g.clear(), Some(false));
        assert_eq!(g.clear(), None);
    }
}
