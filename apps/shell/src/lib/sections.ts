/** Where a screen sits in the menu, for the direction the screens slide in (no React here: unit-tested). */

/** Order of the top-level sections, left to right as in the menu; screens outside the menu sit after it. */
export function sectionIndex(pathname: string, order: readonly string[]): number {
  const i = order.findIndex((p) => pathname === p || pathname.startsWith(`${p}/`));
  return i === -1 ? order.length : i;
}

/** Which way the next screen comes from: +1 from the right (a section further right in the menu), -1 from the left. */
export function slideDirection(from: number, to: number): -1 | 0 | 1 {
  return to > from ? 1 : to < from ? -1 : 0;
}
