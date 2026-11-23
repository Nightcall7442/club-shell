/**
 * Player logins as the server makes them (D-92, `server/src/ClubShell.Server/Users/Logins.cs`): a phone-shaped login is
 * kept as its digits; without one, the phone's digits when free, else the name in Latin (Russian and Uzbek Cyrillic
 * transliterated, diacritics dropped, two words joined by a dot, at most 24 characters, `player` for another script)
 * with the smallest number that makes it unique.
 */
const CYRILLIC: Record<string, string> = {
  а: 'a',
  б: 'b',
  в: 'v',
  г: 'g',
  д: 'd',
  е: 'e',
  ё: 'e',
  ж: 'zh',
  з: 'z',
  и: 'i',
  й: 'y',
  к: 'k',
  л: 'l',
  м: 'm',
  н: 'n',
  о: 'o',
  п: 'p',
  р: 'r',
  с: 's',
  т: 't',
  у: 'u',
  ф: 'f',
  х: 'kh',
  ц: 'ts',
  ч: 'ch',
  ш: 'sh',
  щ: 'shch',
  ъ: '',
  ы: 'y',
  ь: '',
  э: 'e',
  ю: 'yu',
  я: 'ya',
  і: 'i',
  ї: 'i',
  є: 'e',
  ў: 'o',
  қ: 'q',
  ғ: 'g',
  ҳ: 'h',
};

/** Digits of a text that reads as a phone (digits with spaces and `+ - ( )`, 7–15 digits), else null. */
export function phoneDigits(text: string | null | undefined): string | null {
  const t = (text ?? '').trim();
  if (!t || !/^[\d\s+\-()]+$/.test(t)) return null;
  const digits = t.replace(/\D/g, '');
  return digits.length >= 7 && digits.length <= 15 ? digits : null;
}

/** The name in lower-case Latin letters and digits, two words joined by a dot. */
export function loginSlug(displayName: string): string {
  const words: string[] = [];
  let word = '';
  for (const ch of displayName.toLowerCase()) {
    const latin = CYRILLIC[ch];
    if (latin !== undefined) {
      word += latin;
      continue;
    }
    if (/['ʻʼ’‘`]/.test(ch) || /\p{Mn}/u.test(ch)) continue;
    const plain = ch.normalize('NFD').replace(/[^a-z0-9]/g, '');
    if (plain) word += plain;
    else if (word) {
      words.push(word);
      word = '';
    }
  }
  if (word) words.push(word);
  let slug = words.slice(0, 2).join('.');
  if (slug.length > 24) slug = slug.slice(0, 24).replace(/\.+$/, '');
  return slug || 'player';
}

/** A free login among `taken` (lower case): the phone's digits, else the name numbered on a collision. */
export function generateLogin(
  displayName: string,
  phone: string | null | undefined,
  taken: (login: string) => boolean,
): string {
  const digits = phoneDigits(phone);
  if (digits && !taken(digits)) return digits;
  const slug = loginSlug(displayName);
  if (slug.length >= 3 && !taken(slug)) return slug;
  for (let n = slug.length >= 3 ? 2 : 1; ; n++) {
    if (!taken(`${slug}${n}`)) return `${slug}${n}`;
  }
}
