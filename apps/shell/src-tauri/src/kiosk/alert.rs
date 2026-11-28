//! The low-time chime (D-109): a short two-note sound played natively when the Agent sends
//! `session.warning`, in a game or not. Native (`PlaySoundW` from memory, asynchronous) rather than
//! WebAudio: the overlay webview never gets a user gesture, so its audio context stays suspended,
//! and the main webview is hidden while a game runs. Sound also reaches a player whose game holds
//! the screen in exclusive fullscreen, where no window can be drawn over it.
//!
//! The WAV is synthesised once (no asset in the bundle) and kept for the process' life, as
//! `SND_MEMORY | SND_ASYNC` needs the buffer to outlive the call. Other kinds of message (an admin
//! text) do not chime: only the warning that the time runs out.

use std::sync::OnceLock;

/// Sample rate of the chime.
pub const SAMPLE_RATE: u32 = 22_050;
/// Two notes (E6 then B5), each this long, with a short gap.
const NOTE_MS: u32 = 140;
const GAP_MS: u32 = 40;
const NOTES: [f32; 2] = [1318.5, 987.8];
/// Peak level, a fraction of full scale: noticeable over a game, not startling.
const PEAK: f32 = 0.35;

/// The chime as a mono 16-bit PCM WAV file.
pub fn chime_wav() -> &'static [u8] {
    static WAV: OnceLock<Vec<u8>> = OnceLock::new();
    WAV.get_or_init(build_chime)
}

fn note_samples(freq: f32, ms: u32, out: &mut Vec<i16>) {
    let n = SAMPLE_RATE * ms / 1000;
    let attack = (SAMPLE_RATE / 200).max(1); // 5 ms
    for i in 0..n {
        let t = i as f32 / SAMPLE_RATE as f32;
        // Quick attack, exponential decay: a bell, not a beep.
        let env = (i.min(attack) as f32 / attack as f32) * (-(i as f32) / (n as f32 / 4.0)).exp();
        let v = (std::f32::consts::TAU * freq * t).sin() * env * PEAK;
        out.push((v * f32::from(i16::MAX)) as i16);
    }
}

fn build_chime() -> Vec<u8> {
    let mut samples = Vec::new();
    for (i, freq) in NOTES.iter().enumerate() {
        if i > 0 {
            let gap = (SAMPLE_RATE * GAP_MS / 1000) as usize;
            samples.resize(samples.len() + gap, 0);
        }
        note_samples(*freq, NOTE_MS, &mut samples);
    }
    wav(&samples)
}

/// A RIFF/WAVE file of mono 16-bit samples at [`SAMPLE_RATE`].
fn wav(samples: &[i16]) -> Vec<u8> {
    let data_len = u32::try_from(samples.len() * 2).unwrap_or(u32::MAX);
    let mut out = Vec::with_capacity(44 + samples.len() * 2);
    out.extend_from_slice(b"RIFF");
    out.extend_from_slice(&(36 + data_len).to_le_bytes());
    out.extend_from_slice(b"WAVEfmt ");
    out.extend_from_slice(&16u32.to_le_bytes()); // fmt chunk size
    out.extend_from_slice(&1u16.to_le_bytes()); // PCM
    out.extend_from_slice(&1u16.to_le_bytes()); // mono
    out.extend_from_slice(&SAMPLE_RATE.to_le_bytes());
    out.extend_from_slice(&(SAMPLE_RATE * 2).to_le_bytes()); // byte rate
    out.extend_from_slice(&2u16.to_le_bytes()); // block align
    out.extend_from_slice(&16u16.to_le_bytes()); // bits per sample
    out.extend_from_slice(b"data");
    out.extend_from_slice(&data_len.to_le_bytes());
    for s in samples {
        out.extend_from_slice(&s.to_le_bytes());
    }
    out
}

/// Plays the chime without waiting; a PC without sound output just stays silent.
pub fn play_chime() {
    #[cfg(windows)]
    {
        use windows::core::PCWSTR;
        use windows::Win32::Foundation::HMODULE;
        use windows::Win32::Media::Audio::{PlaySoundW, SND_ASYNC, SND_MEMORY, SND_NODEFAULT};
        let wav = chime_wav();
        // SAFETY: with SND_MEMORY the first argument points at a complete WAV image, which is
        // 'static, so it outlives the asynchronous playback.
        let ok = unsafe {
            PlaySoundW(
                PCWSTR(wav.as_ptr().cast()),
                HMODULE::default(),
                SND_MEMORY | SND_ASYNC | SND_NODEFAULT,
            )
        };
        if !ok.as_bool() {
            tracing::debug!("low-time chime not played (no sound device?)");
        }
    }
    #[cfg(not(windows))]
    tracing::debug!(bytes = chime_wav().len(), "low-time chime (Windows only)");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn chime_is_a_short_valid_pcm_wav() {
        let wav = chime_wav();
        assert_eq!(&wav[0..4], b"RIFF");
        assert_eq!(&wav[8..16], b"WAVEfmt ");
        assert_eq!(&wav[36..40], b"data");
        let riff = u32::from_le_bytes(wav[4..8].try_into().unwrap()) as usize;
        let data = u32::from_le_bytes(wav[40..44].try_into().unwrap()) as usize;
        assert_eq!((riff + 8, data + 44), (wav.len(), wav.len()));
        let ms = data as u32 / 2 * 1000 / SAMPLE_RATE;
        assert!(
            (300..=400).contains(&ms),
            "two notes and a gap, about a third of a second: {ms} ms"
        );
    }

    #[test]
    fn chime_stays_below_full_scale_and_is_not_silent() {
        let wav = chime_wav();
        let peak = wav[44..]
            .chunks_exact(2)
            .map(|b| i16::from_le_bytes([b[0], b[1]]).unsigned_abs())
            .max()
            .unwrap();
        assert!(peak > 4_000, "audible: {peak}");
        assert!(
            f32::from(peak) <= PEAK * f32::from(i16::MAX) + 1.0,
            "never clips: {peak}"
        );
    }
}
