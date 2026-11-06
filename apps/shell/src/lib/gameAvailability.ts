/**
 * How a game that cannot be started here is named (SHELL_CHANGES 74): a game whose catalogue path is on a drive this PC
 * does not have (the offline games disk H: on a PC without it) is "not available on this PC", not "not installed" —
 * the player cannot fix that and the operator should see why.
 */
import type { Game } from '@clubshell/contracts';
import type { TFunction } from 'i18next';

/** Badge text for a game that is not installed. */
export function notInstalledLabel(game: Pick<Game, 'missingDrive'>, t: TFunction): string {
  return game.missingDrive ? t('games.unavailableHere') : t('games.notInstalled');
}

/** Why Play is off / the launch was refused, for a game that is not installed. */
export function notInstalledReason(game: Pick<Game, 'missingDrive'> | null, t: TFunction): string {
  return game?.missingDrive
    ? t('errors.gameOnMissingDrive', { drive: game.missingDrive })
    : t('errors.gameNotInstalled');
}
