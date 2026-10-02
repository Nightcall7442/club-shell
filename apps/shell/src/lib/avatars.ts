/**
 * Preset avatars a player picks in the Profile (public/avatars/<id>.svg, drawn by tools/scripts/make-avatars.mjs).
 * The profile stores the picture's path, `/avatars/<id>.svg`: every shell serves the same files from its own origin,
 * so the avatar shows on any PC, offline too. Agent and server accept such a path besides an http(s) URL.
 */
export const AVATAR_IDS = [
  'wolf',
  'fox',
  'dragon',
  'alien',
  'robot',
  'skull',
  'ninja',
  'ufo',
  'lion',
  'tiger',
  'panda',
  'eagle',
  'crown',
  'bolt',
  'fire',
  'target',
  'gamepad',
  'joystick',
  'shield',
  'swords',
  'snake',
  'shark',
  'bear',
  'owl',
] as const;

export type AvatarId = (typeof AVATAR_IDS)[number];

/** The stored value of a preset avatar. */
export function presetAvatarUrl(id: AvatarId): string {
  return `/avatars/${id}.svg`;
}
