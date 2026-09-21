/**
 * Realistic in-memory fixtures for the browser mock (`VITE_MOCK=1`). Everything is typed with the contracts
 * package; money is UZS in integer minor units (tiyin), timestamps ISO-8601 UTC relative to load time.
 */
import type {
  Achievement,
  App,
  Booking,
  ChatMessage,
  Game,
  HardwareInfo,
  LeaderboardEntry,
  Loyalty,
  Money,
  Notification,
  Order,
  Pc,
  PcInfo,
  PcMetrics,
  Policy,
  Product,
  QrLoginStart,
  Seat,
  Session,
  ShellSettings,
  Tariff,
  Tournament,
  Transaction,
  UpdateManifest,
  User,
  UserStats,
} from '@clubshell/contracts';
import type { KioskMonitor, KioskState, ShellConfig } from '@/lib/tauri';

// ---------------------------------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------------------------------

/** Deterministic UUID for fixture rows: `<group>0000000-0000-4000-8000-<n>`. */
export function uid(group: number, n: number): string {
  return `${group}0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
}

/** UZS in whole soms → Money in minor units. */
export function uzs(soms: number): Money {
  return { amount: Math.round(soms * 100), currency: 'UZS' };
}

const NOW = Date.now();

/** ISO timestamp `minutes` in the past. */
export function ago(minutes: number): string {
  return new Date(NOW - minutes * 60_000).toISOString();
}

/** ISO timestamp `minutes` in the future. */
export function inMin(minutes: number): string {
  return new Date(NOW + minutes * 60_000).toISOString();
}

/** ISO timestamp `days` in the past. */
export function daysAgo(days: number): string {
  return ago(days * 24 * 60);
}

/** Original key art per game in `public/mock-art/<seed>-{cover,hero}.jpg`. */
const cover = (seed: string): string => `/mock-art/${seed}-cover.jpg`;
const hero = (seed: string): string => `/mock-art/${seed}-hero.jpg`;
const square = (seed: string, size = 256): string => `https://picsum.photos/seed/${seed}/${size}/${size}`;

// ---------------------------------------------------------------------------------------------------------------------
// PC / kiosk
// ---------------------------------------------------------------------------------------------------------------------

export const PC_ID = uid(1, 12);
export const CLUB_NAME = 'CyberArena Tashkent';

export const PC: Pc = {
  id: PC_ID,
  name: 'PC-12',
  zone: 'Standard',
  number: 12,
  hwid: 'a3f1c2d4e5b6978899aabbccddeeff00112233445566778899aabbccddeeff00',
  ipAddress: '10.0.1.112',
  status: 'busy',
  currentSessionId: null,
  agentVersion: '1.4.2',
  shellVersion: '1.0.0',
  lastHeartbeatAt: ago(0),
};

export const PC_INFO: PcInfo = {
  pc: PC,
  agentVersion: '1.4.2',
  shellVersion: '1.0.0',
  protocolVersion: 1,
  uptimeSec: 5 * 3600 + 12 * 60,
  kioskUser: 'club',
  connectivity: 'online',
  serverTime: ago(0),
  policyVersion: 17,
};

export const MONITORS: KioskMonitor[] = [
  { index: 0, name: 'ASUS VG279QM', x: 0, y: 0, width: 1920, height: 1080, scale: 1, hz: 240, primary: true },
  { index: 1, name: 'LG 24GN600', x: 1920, y: 0, width: 1920, height: 1080, scale: 1, hz: 144, primary: false },
];

export const KIOSK_STATE: KioskState = {
  agentConnected: true,
  fullscreen: true,
  guardActive: true,
  hooksActive: true,
  monitors: MONITORS,
  idle: false,
  idleSec: 0,
  gamepadConnected: false,
  locked: false,
  gameMode: false,
  overlay: 'none',
  dev: true,
  version: '1.0.0',
  devtools: true,
};

export const HARDWARE: HardwareInfo = {
  cpu: { model: 'Intel Core i7-13700F', cores: 16, threads: 24 },
  gpu: [{ model: 'NVIDIA GeForce RTX 4070', vramMb: 12_288, driver: '552.44' }],
  ramMb: 32_768,
  disks: [
    { mount: 'C:', totalGb: 953.9, freeGb: 412.3, type: 'nvme' },
    { mount: 'D:', totalGb: 1863.0, freeGb: 620.8, type: 'ssd' },
  ],
  monitors: MONITORS.map(({ index, width, height, hz, primary }) => ({ index, width, height, hz, primary })),
  network: { mac: '3C:7C:3F:1A:2B:4C', ip: '10.0.1.112', adapter: 'Realtek 2.5GbE' },
  os: { version: 'Windows 11 Pro', build: '26200.1234' },
  peripherals: [
    { kind: 'keyboard', name: 'HyperX Alloy Origins', vendorId: '03f0', productId: '0c8e' },
    { kind: 'mouse', name: 'Logitech G Pro X Superlight', vendorId: '046d', productId: 'c094' },
    { kind: 'headset', name: 'HyperX Cloud II', vendorId: '0951', productId: '16a4' },
    { kind: 'gamepad', name: 'Xbox Wireless Controller', vendorId: '045e', productId: '0b12' },
  ],
};

export const METRICS: PcMetrics = {
  cpuPct: 23,
  gpuPct: 11,
  ramUsedMb: 9_830,
  temps: { cpu: 47, gpu: 41 },
  fps: null,
  netMbps: { up: 2.4, down: 18.7 },
  uptimeSec: PC_INFO.uptimeSec,
  at: ago(0),
};

// ---------------------------------------------------------------------------------------------------------------------
// Users / auth
// ---------------------------------------------------------------------------------------------------------------------

export const USER_ID = uid(2, 1);

export const USER: User = {
  id: USER_ID,
  username: 'demo',
  displayName: 'Bobur Y.',
  avatarUrl: square('avatar-bobur'),
  role: 'member',
  balance: uzs(45_000),
  loyaltyLevel: 2,
  loyaltyPoints: 1_340,
  createdAt: daysAgo(214),
  lastSeenAt: ago(35),
  locale: 'ru',
  flags: [],
};

export const VIP_USER: User = {
  id: uid(2, 2),
  username: 'vip',
  displayName: 'Sardor K.',
  avatarUrl: square('avatar-sardor'),
  role: 'vip',
  balance: uzs(320_000),
  loyaltyLevel: 4,
  loyaltyPoints: 8_920,
  createdAt: daysAgo(480),
  lastSeenAt: daysAgo(1),
  locale: 'uz',
  flags: [],
};

/** Template for a guest login; the handler assigns a fresh id and name. */
export const GUEST_TEMPLATE: Omit<User, 'id' | 'displayName' | 'username' | 'createdAt'> = {
  avatarUrl: null,
  role: 'guest',
  balance: uzs(0),
  loyaltyLevel: 0,
  loyaltyPoints: 0,
  lastSeenAt: null,
  locale: 'ru',
  flags: [],
};

/** Accepted password logins in the mock (`username → password`). */
export const MOCK_CREDENTIALS: Readonly<Record<string, string>> = { demo: '1234', vip: '1234', player: 'player' };

/** Accepted PINs for `session_unlock` / admin unlock. */
export const MOCK_USER_PIN = '1234';
export const MOCK_ADMIN_PIN = '0000';

export const QR_START: QrLoginStart = {
  qrToken: 'qr-7f3a9c1e',
  qrUrl: 'https://club.example.uz/q/qr-7f3a9c1e',
  expiresAt: inMin(2),
  pollIntervalSec: 2,
};

// ---------------------------------------------------------------------------------------------------------------------
// Session / tariffs
// ---------------------------------------------------------------------------------------------------------------------

export const TARIFFS: Tariff[] = [
  {
    id: uid(3, 1),
    name: 'Standard',
    pricePerHour: uzs(12_000),
    minMinutes: 30,
    maxMinutes: 720,
    zones: ['Standard', 'Bootcamp'],
    timeWindows: [],
    isPackage: false,
    packageMinutes: null,
    packagePrice: null,
  },
  {
    id: uid(3, 2),
    name: 'VIP',
    pricePerHour: uzs(20_000),
    minMinutes: 30,
    maxMinutes: 720,
    zones: ['VIP'],
    timeWindows: [],
    isPackage: false,
    packageMinutes: null,
    packagePrice: null,
  },
  {
    id: uid(3, 3),
    name: 'Night 5h',
    pricePerHour: uzs(8_000),
    minMinutes: 300,
    maxMinutes: 300,
    zones: [],
    timeWindows: [{ days: ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'], from: '22:00', to: '06:00' }],
    isPackage: true,
    packageMinutes: 300,
    packagePrice: uzs(40_000),
  },
];

export const SESSION_ID = uid(4, 1);

/** Active prepaid session with 87 minutes left (2 h bought 33 min ago). */
export const SESSION: Session = {
  id: SESSION_ID,
  userId: USER_ID,
  pcId: PC_ID,
  state: 'active',
  startedAt: ago(33),
  endsAt: inMin(87),
  pausedAt: null,
  tariffId: TARIFFS[0]?.id ?? uid(3, 1),
  secondsLeft: 87 * 60,
  secondsUsed: 33 * 60,
  cost: uzs(24_000),
  isPrepaid: true,
  warningsSent: [],
};

// ---------------------------------------------------------------------------------------------------------------------
// Games / apps
// ---------------------------------------------------------------------------------------------------------------------

interface GameSeed {
  n: number;
  seed: string;
  title: string;
  launcher: Game['launcher'];
  appId?: string;
  category: string[];
  tags: string[];
  age: number;
  pop: number;
  sizeGb: number;
  ac: Game['antiCheat'];
  account: boolean;
  installed: boolean;
  lastPlayedMin?: number;
  description: string;
  video?: string;
}

const GAME_SEEDS: GameSeed[] = [
  {
    n: 1,
    seed: 'cs2',
    title: 'Counter-Strike 2',
    launcher: 'steam',
    appId: '730',
    category: ['shooter', 'multiplayer'],
    tags: ['fps', 'esports', '5v5'],
    age: 16,
    pop: 98,
    sizeGb: 34.2,
    ac: 'none',
    account: true,
    installed: true,
    lastPlayedMin: 40,
    description:
      'The definitive competitive 5v5 shooter. Plant or defuse, clutch rounds and climb the Premier ladder with your squad.',
  },
  {
    n: 2,
    seed: 'dota2',
    title: 'Dota 2',
    launcher: 'steam',
    appId: '570',
    category: ['moba', 'multiplayer'],
    tags: ['moba', 'esports', 'heroes'],
    age: 12,
    pop: 95,
    sizeGb: 41.7,
    ac: 'none',
    account: false,
    installed: true,
    lastPlayedMin: 2 * 24 * 60,
    description: 'Two teams of five heroes battle to destroy the enemy Ancient. Every match is a new strategy.',
  },
  {
    n: 3,
    seed: 'valorant',
    title: 'VALORANT',
    launcher: 'riot',
    appId: 'valorant',
    category: ['shooter', 'multiplayer'],
    tags: ['fps', 'tactical', 'agents'],
    age: 16,
    pop: 94,
    sizeGb: 38.5,
    ac: 'vanguard',
    account: true,
    installed: true,
    lastPlayedMin: 5 * 24 * 60,
    description: 'A 5v5 character-based tactical shooter where precise gunplay meets unique agent abilities.',
  },
  {
    n: 4,
    seed: 'fortnite',
    title: 'Fortnite',
    launcher: 'epic',
    appId: 'fn',
    category: ['battleRoyale', 'multiplayer'],
    tags: ['battle royale', 'building', 'crossplay'],
    age: 12,
    pop: 90,
    sizeGb: 48.0,
    ac: 'eac',
    account: true,
    installed: true,
    description: 'Drop in, build up and be the last one standing. New season, new map, new weapons.',
  },
  {
    n: 5,
    seed: 'pubg',
    title: 'PUBG: BATTLEGROUNDS',
    launcher: 'steam',
    appId: '578080',
    category: ['battleRoyale', 'shooter'],
    tags: ['battle royale', 'realistic', 'squads'],
    age: 16,
    pop: 88,
    sizeGb: 45.3,
    ac: 'battlEye',
    account: false,
    installed: true,
    lastPlayedMin: 9 * 24 * 60,
    description: 'Land, loot and survive on Erangel and Miramar. 100 players, one winner-winner chicken dinner.',
  },
  {
    n: 6,
    seed: 'gtav',
    title: 'Grand Theft Auto V',
    launcher: 'steam',
    appId: '271590',
    category: ['action', 'sandbox'],
    tags: ['open world', 'story', 'online'],
    age: 18,
    pop: 86,
    sizeGb: 105.4,
    ac: 'battlEye',
    account: true,
    installed: true,
    description: 'Los Santos awaits: a sprawling sun-soaked metropolis with a story campaign and GTA Online.',
  },
  {
    n: 7,
    seed: 'apex',
    title: 'Apex Legends',
    launcher: 'ea',
    appId: 'apex',
    category: ['battleRoyale', 'shooter'],
    tags: ['battle royale', 'legends', 'squads'],
    age: 16,
    pop: 84,
    sizeGb: 75.0,
    ac: 'eac',
    account: true,
    installed: true,
    description: 'A free-to-play hero shooter where legendary characters fight for glory on the Frontier.',
  },
  {
    n: 8,
    seed: 'lol',
    title: 'League of Legends',
    launcher: 'riot',
    appId: 'lol',
    category: ['moba', 'multiplayer'],
    tags: ['moba', 'esports', 'champions'],
    age: 12,
    pop: 92,
    sizeGb: 22.1,
    ac: 'vanguard',
    account: true,
    installed: true,
    lastPlayedMin: 60 * 24 * 60,
    description: "The world's most-played MOBA. Pick a champion, take the towers, destroy the Nexus.",
  },
  {
    n: 9,
    seed: 'rocket',
    title: 'Rocket League',
    launcher: 'epic',
    appId: 'rl',
    category: ['sports', 'racing'],
    tags: ['cars', 'football', 'crossplay'],
    age: 3,
    pop: 78,
    sizeGb: 20.4,
    ac: 'none',
    account: false,
    installed: true,
    description: 'Soccer meets driving. Boost, fly and score aerial goals in fast 3v3 matches.',
  },
  {
    n: 10,
    seed: 'minecraft',
    title: 'Minecraft',
    launcher: 'exe',
    category: ['sandbox', 'casual'],
    tags: ['building', 'survival', 'creative'],
    age: 7,
    pop: 80,
    sizeGb: 1.2,
    ac: 'none',
    account: true,
    installed: true,
    description: 'Build anything you can imagine, explore infinite worlds and survive the night.',
  },
  {
    n: 11,
    seed: 'fc25',
    title: 'EA SPORTS FC 25',
    launcher: 'ea',
    appId: 'fc25',
    category: ['sports'],
    tags: ['football', 'ultimate team', 'local co-op'],
    age: 3,
    pop: 82,
    sizeGb: 100.3,
    ac: 'eac',
    account: true,
    installed: true,
    description: 'The club is yours. Play Ultimate Team, Career and Clubs with the most authentic football gameplay.',
  },
  {
    n: 12,
    seed: 'forza5',
    title: 'Forza Horizon 5',
    launcher: 'steam',
    appId: '1551360',
    category: ['racing', 'sandbox'],
    tags: ['open world', 'cars', 'mexico'],
    age: 3,
    pop: 74,
    sizeGb: 116.0,
    ac: 'none',
    account: true,
    installed: false,
    description: 'Lead breathtaking expeditions across the vibrant open world of Mexico with hundreds of cars.',
  },
  {
    n: 13,
    seed: 'ow2',
    title: 'Overwatch 2',
    launcher: 'battleNet',
    appId: 'pro',
    category: ['shooter', 'multiplayer'],
    tags: ['hero shooter', '5v5', 'esports'],
    age: 12,
    pop: 76,
    sizeGb: 50.1,
    ac: 'none',
    account: true,
    installed: true,
    description: 'Team up with heroes across the globe in this fast-paced 5v5 hero shooter.',
  },
  {
    n: 14,
    seed: 'warzone',
    title: 'Call of Duty: Warzone',
    launcher: 'battleNet',
    appId: 'auks',
    category: ['battleRoyale', 'shooter'],
    tags: ['battle royale', 'fps', 'squads'],
    age: 18,
    pop: 79,
    sizeGb: 124.6,
    ac: 'ricochet',
    account: true,
    installed: false,
    description: 'Drop into massive combat with up to 150 players. Loot, fight and extract.',
  },
  {
    n: 15,
    seed: 'tekken8',
    title: 'TEKKEN 8',
    launcher: 'steam',
    appId: '1778820',
    category: ['fighting', 'multiplayer'],
    tags: ['fighting', 'local co-op', 'arcade'],
    age: 16,
    pop: 70,
    sizeGb: 100.5,
    ac: 'none',
    account: false,
    installed: true,
    description: 'The king of iron fist tournament returns with the new Heat system and stunning visuals.',
  },
];

export const GAMES: Game[] = GAME_SEEDS.map((g) => ({
  id: uid(5, g.n),
  title: g.title,
  launcher: g.launcher,
  launcherAppId: g.appId ?? null,
  exePath: g.launcher === 'exe' ? 'D:\\Games\\Minecraft\\MinecraftLauncher.exe' : null,
  args: null,
  installPath: g.installed ? `D:\\Games\\${g.seed}` : null,
  installed: g.installed,
  category: g.category,
  tags: g.tags,
  coverUrl: cover(g.seed),
  heroUrl: hero(g.seed),
  videoUrl: g.video ?? null,
  description: g.description,
  ageRating: g.age,
  popularity: g.pop,
  lastPlayedAt: g.lastPlayedMin !== undefined ? ago(g.lastPlayedMin) : null,
  requiresAccount: g.account,
  antiCheat: g.ac,
  minSpec: { cpu: 'Intel Core i5-9400F', gpu: 'GeForce GTX 1660', ramMb: 8_192 },
  sizeGb: g.sizeGb,
  version: '1.0',
}));

/** Category ids present in the catalogue (order of first appearance). */
export const GAME_CATEGORIES: string[] = Array.from(new Set(GAMES.flatMap((g) => g.category)));

export const APPS: App[] = [
  {
    id: uid(6, 1),
    title: 'Google Chrome',
    exePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    iconUrl: square('app-chrome', 128),
    category: 'browser',
    allowed: true,
  },
  {
    id: uid(6, 2),
    title: 'Discord',
    exePath: 'C:\\Users\\kiosk\\AppData\\Local\\Discord\\Update.exe',
    iconUrl: square('app-discord', 128),
    category: 'voice',
    allowed: true,
  },
  {
    id: uid(6, 3),
    title: 'Telegram',
    exePath: 'C:\\Program Files\\Telegram Desktop\\Telegram.exe',
    iconUrl: square('app-telegram', 128),
    category: 'voice',
    allowed: true,
  },
  {
    id: uid(6, 4),
    title: 'Spotify',
    exePath: 'C:\\Users\\kiosk\\AppData\\Roaming\\Spotify\\Spotify.exe',
    iconUrl: square('app-spotify', 128),
    category: 'media',
    allowed: true,
  },
  {
    id: uid(6, 5),
    title: 'VLC media player',
    exePath: 'C:\\Program Files\\VideoLAN\\VLC\\vlc.exe',
    iconUrl: square('app-vlc', 128),
    category: 'media',
    allowed: true,
  },
  {
    id: uid(6, 6),
    title: 'OBS Studio',
    exePath: 'C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe',
    iconUrl: square('app-obs', 128),
    category: 'tool',
    allowed: false,
  },
];

// ---------------------------------------------------------------------------------------------------------------------
// Shop
// ---------------------------------------------------------------------------------------------------------------------

export const PRODUCTS: Product[] = [
  {
    id: uid(7, 1),
    title: 'Coca-Cola 0.5 l',
    category: 'drink',
    price: uzs(9_000),
    imageUrl: square('p-cola'),
    inStock: true,
    stockQty: 40,
    tags: ['cold', 'popular'],
  },
  {
    id: uid(7, 2),
    title: 'Red Bull 0.25 l',
    category: 'drink',
    price: uzs(18_000),
    imageUrl: square('p-redbull'),
    inStock: true,
    stockQty: 22,
    tags: ['energy'],
  },
  {
    id: uid(7, 3),
    title: 'Americano',
    category: 'drink',
    price: uzs(15_000),
    imageUrl: square('p-coffee'),
    inStock: true,
    stockQty: null,
    tags: ['hot', 'coffee'],
  },
  {
    id: uid(7, 4),
    title: 'Pepperoni pizza 30 cm',
    category: 'food',
    price: uzs(65_000),
    imageUrl: square('p-pizza'),
    inStock: true,
    stockQty: null,
    tags: ['hot', 'popular'],
  },
  {
    id: uid(7, 5),
    title: 'Chicken burger',
    category: 'food',
    price: uzs(38_000),
    imageUrl: square('p-burger'),
    inStock: true,
    stockQty: 8,
    tags: ['hot'],
  },
  {
    id: uid(7, 6),
    title: 'Lavash with beef',
    category: 'food',
    price: uzs(42_000),
    imageUrl: square('p-lavash'),
    inStock: false,
    stockQty: 0,
    tags: ['hot'],
  },
  {
    id: uid(7, 7),
    title: "Lay's 150 g",
    category: 'snack',
    price: uzs(16_000),
    imageUrl: square('p-chips'),
    inStock: true,
    stockQty: 3,
    tags: ['salty'],
  },
  {
    id: uid(7, 8),
    title: 'Snickers',
    category: 'snack',
    price: uzs(8_000),
    imageUrl: square('p-snickers'),
    inStock: true,
    stockQty: 50,
    tags: ['sweet'],
  },
  {
    id: uid(7, 9),
    title: 'Headset rental (session)',
    category: 'service',
    price: uzs(10_000),
    imageUrl: square('p-headset'),
    inStock: true,
    stockQty: null,
    tags: ['rental'],
  },
  {
    id: uid(7, 10),
    title: 'Gamepad rental (session)',
    category: 'service',
    price: uzs(15_000),
    imageUrl: square('p-gamepad'),
    inStock: true,
    stockQty: 4,
    tags: ['rental'],
  },
  {
    id: uid(7, 11),
    title: 'CyberArena T-shirt',
    category: 'merch',
    price: uzs(120_000),
    imageUrl: square('p-tshirt'),
    inStock: true,
    stockQty: 12,
    tags: ['merch', 'black'],
  },
  {
    id: uid(7, 12),
    title: '+60 min Standard',
    category: 'time',
    price: uzs(12_000),
    imageUrl: square('p-time60'),
    inStock: true,
    stockQty: null,
    tags: ['time'],
  },
  {
    id: uid(7, 13),
    title: '+180 min Standard',
    category: 'time',
    price: uzs(33_000),
    imageUrl: square('p-time180'),
    inStock: true,
    stockQty: null,
    tags: ['time', 'deal'],
  },
];

export const ORDERS: Order[] = [
  {
    id: uid(8, 1),
    userId: USER_ID,
    pcId: PC_ID,
    items: [
      { productId: uid(7, 1), title: 'Coca-Cola 0.5 l', qty: 2, price: uzs(9_000) },
      { productId: uid(7, 8), title: 'Snickers', qty: 1, price: uzs(8_000) },
    ],
    total: uzs(26_000),
    status: 'done',
    createdAt: ago(28),
    updatedAt: ago(19),
    note: null,
  },
  {
    id: uid(8, 2),
    userId: USER_ID,
    pcId: PC_ID,
    items: [{ productId: uid(7, 4), title: 'Pepperoni pizza 30 cm', qty: 1, price: uzs(65_000) }],
    total: uzs(65_000),
    status: 'preparing',
    createdAt: ago(6),
    updatedAt: ago(3),
    note: 'Extra sauce please',
  },
];

// ---------------------------------------------------------------------------------------------------------------------
// Wallet
// ---------------------------------------------------------------------------------------------------------------------

export const TRANSACTIONS: Transaction[] = [
  {
    id: uid(9, 1),
    userId: USER_ID,
    type: 'topUp',
    amount: uzs(100_000),
    balanceAfter: uzs(112_000),
    description: 'Top-up via Payme',
    createdAt: daysAgo(6),
    ref: 'payme-8813',
  },
  {
    id: uid(9, 2),
    userId: USER_ID,
    type: 'charge',
    amount: uzs(-24_000),
    balanceAfter: uzs(88_000),
    description: 'Session PC-07, 2 h Standard',
    createdAt: daysAgo(6),
    ref: uid(4, 11),
  },
  {
    id: uid(9, 3),
    userId: USER_ID,
    type: 'purchase',
    amount: uzs(-17_000),
    balanceAfter: uzs(71_000),
    description: 'Shop order: Coca-Cola, Snickers',
    createdAt: daysAgo(6),
    ref: uid(8, 11),
  },
  {
    id: uid(9, 4),
    userId: USER_ID,
    type: 'bonus',
    amount: uzs(5_000),
    balanceAfter: uzs(76_000),
    description: 'Loyalty bonus: level 2',
    createdAt: daysAgo(4),
    ref: null,
  },
  {
    id: uid(9, 5),
    userId: USER_ID,
    type: 'charge',
    amount: uzs(-36_000),
    balanceAfter: uzs(40_000),
    description: 'Session PC-12, 3 h Standard',
    createdAt: daysAgo(3),
    ref: uid(4, 12),
  },
  {
    id: uid(9, 6),
    userId: USER_ID,
    type: 'refund',
    amount: uzs(6_000),
    balanceAfter: uzs(46_000),
    description: 'Refund: unused 30 min',
    createdAt: daysAgo(3),
    ref: uid(4, 12),
  },
  {
    id: uid(9, 7),
    userId: USER_ID,
    type: 'topUp',
    amount: uzs(50_000),
    balanceAfter: uzs(96_000),
    description: 'Top-up: cash at the desk',
    createdAt: daysAgo(1),
    ref: 'cash-0412',
  },
  {
    id: uid(9, 8),
    userId: USER_ID,
    type: 'charge',
    amount: uzs(-24_000),
    balanceAfter: uzs(72_000),
    description: 'Session PC-12, 2 h Standard',
    createdAt: ago(33),
    ref: SESSION_ID,
  },
  {
    id: uid(9, 9),
    userId: USER_ID,
    type: 'purchase',
    amount: uzs(-26_000),
    balanceAfter: uzs(46_000),
    description: 'Shop order: Coca-Cola ×2, Snickers',
    createdAt: ago(28),
    ref: uid(8, 1),
  },
  {
    id: uid(9, 10),
    userId: USER_ID,
    type: 'adjustment',
    amount: uzs(-1_000),
    balanceAfter: uzs(45_000),
    description: 'Rounding adjustment',
    createdAt: ago(20),
    ref: null,
  },
];

// ---------------------------------------------------------------------------------------------------------------------
// Chat
// ---------------------------------------------------------------------------------------------------------------------

export const ROOM_ID = `pc:${PC_ID}`;
export const ADMIN_ID = uid(2, 99);
export const ADMIN_NAME = 'Admin Aziz';

export const CHAT_MESSAGES: ChatMessage[] = [
  {
    id: uid(10, 1),
    roomId: ROOM_ID,
    senderId: ADMIN_ID,
    senderName: 'ClubShell',
    senderRole: 'admin',
    text: 'Welcome to CyberArena! Staff are here if you need anything.',
    createdAt: ago(34),
    readAt: ago(33),
    kind: 'system',
  },
  {
    id: uid(10, 2),
    roomId: ROOM_ID,
    senderId: USER_ID,
    senderName: 'Bobur Y.',
    senderRole: 'member',
    text: 'Hi! Is the pizza still 65k?',
    createdAt: ago(30),
    readAt: ago(30),
    kind: 'text',
  },
  {
    id: uid(10, 3),
    roomId: ROOM_ID,
    senderId: ADMIN_ID,
    senderName: ADMIN_NAME,
    senderRole: 'admin',
    text: 'Yes, 65 000 for the 30 cm. Order it from the Shop tab and we bring it to PC-12.',
    createdAt: ago(29),
    readAt: ago(28),
    kind: 'admin',
  },
  {
    id: uid(10, 4),
    roomId: ROOM_ID,
    senderId: USER_ID,
    senderName: 'Bobur Y.',
    senderRole: 'member',
    text: 'Ordered, thanks',
    createdAt: ago(27),
    readAt: ago(27),
    kind: 'text',
  },
  {
    id: uid(10, 5),
    roomId: ROOM_ID,
    senderId: ADMIN_ID,
    senderName: 'ClubShell',
    senderRole: 'admin',
    text: 'Order #2 accepted by staff.',
    createdAt: ago(25),
    readAt: ago(24),
    kind: 'system',
  },
  {
    id: uid(10, 6),
    roomId: ROOM_ID,
    senderId: USER_ID,
    senderName: 'Bobur Y.',
    senderRole: 'member',
    text: 'Can you check the headset on this seat? Left side is quiet',
    createdAt: ago(12),
    readAt: ago(12),
    kind: 'text',
  },
  {
    id: uid(10, 7),
    roomId: ROOM_ID,
    senderId: ADMIN_ID,
    senderName: ADMIN_NAME,
    senderRole: 'admin',
    text: 'Sure, coming over in 2 minutes with a spare one.',
    createdAt: ago(10),
    readAt: null,
    kind: 'admin',
  },
  {
    id: uid(10, 8),
    roomId: ROOM_ID,
    senderId: ADMIN_ID,
    senderName: ADMIN_NAME,
    senderRole: 'admin',
    text: 'Also: tonight 22:00 CS2 2v2 cup, 200k prize pool. Join from Tournaments!',
    createdAt: ago(9),
    readAt: null,
    kind: 'admin',
  },
];

/** Canned staff replies used by the mock `chat_send` echo. */
export const ADMIN_REPLIES: string[] = [
  'Got it, on my way!',
  'Sure, give me a minute.',
  'Done. Anything else?',
  'You can also call an admin from the Support tab.',
  'Thanks! Enjoy your game.',
];

// ---------------------------------------------------------------------------------------------------------------------
// Booking
// ---------------------------------------------------------------------------------------------------------------------

function seat(n: number, zone: string, x: number, y: number, status: Seat['status']): Seat {
  return { pcId: n === 12 ? PC_ID : uid(1, n), name: `PC-${String(n).padStart(2, '0')}`, zone, x, y, status };
}

/** 24 seats: VIP (6) top row, Standard (12) middle block, Bootcamp (6) bottom row. */
export const SEATS: Seat[] = [
  seat(1, 'VIP', 0, 0, 'busy'),
  seat(2, 'VIP', 1, 0, 'free'),
  seat(3, 'VIP', 2, 0, 'booked'),
  seat(4, 'VIP', 3, 0, 'free'),
  seat(5, 'VIP', 4, 0, 'maintenance'),
  seat(6, 'VIP', 5, 0, 'busy'),
  seat(7, 'Standard', 0, 2, 'busy'),
  seat(8, 'Standard', 1, 2, 'busy'),
  seat(9, 'Standard', 2, 2, 'free'),
  seat(10, 'Standard', 3, 2, 'free'),
  seat(11, 'Standard', 4, 2, 'locked'),
  seat(12, 'Standard', 5, 2, 'busy'),
  seat(13, 'Standard', 0, 3, 'free'),
  seat(14, 'Standard', 1, 3, 'offline'),
  seat(15, 'Standard', 2, 3, 'busy'),
  seat(16, 'Standard', 3, 3, 'free'),
  seat(17, 'Standard', 4, 3, 'booked'),
  seat(18, 'Standard', 5, 3, 'free'),
  seat(19, 'Bootcamp', 0, 5, 'busy'),
  seat(20, 'Bootcamp', 1, 5, 'busy'),
  seat(21, 'Bootcamp', 2, 5, 'busy'),
  seat(22, 'Bootcamp', 3, 5, 'free'),
  seat(23, 'Bootcamp', 4, 5, 'free'),
  seat(24, 'Bootcamp', 5, 5, 'busy'),
];

export const SLOT_MINUTES = 30;

export const BOOKINGS: Booking[] = [
  { id: uid(11, 1), userId: USER_ID, pcId: uid(1, 3), from: inMin(180), to: inMin(300), status: 'confirmed' },
  {
    id: uid(11, 2),
    userId: '00000000-0000-0000-0000-000000000000',
    pcId: uid(1, 17),
    from: inMin(60),
    to: inMin(180),
    status: 'reserved',
  },
  {
    id: uid(11, 3),
    userId: '00000000-0000-0000-0000-000000000000',
    pcId: uid(1, 2),
    from: inMin(240),
    to: inMin(360),
    status: 'reserved',
  },
];

// ---------------------------------------------------------------------------------------------------------------------
// Tournaments
// ---------------------------------------------------------------------------------------------------------------------

const P = (n: number): string => uid(12, n);
export const PLAYER_NAMES: Readonly<Record<string, string>> = {
  [P(1)]: 'Sardor K.',
  [P(2)]: 'Jasur T.',
  [P(3)]: 'Bobur Y.',
  [P(4)]: 'Umid R.',
  [P(5)]: 'Diyor M.',
  [P(6)]: 'Azamat S.',
  [P(7)]: 'Timur B.',
  [P(8)]: 'Nodir A.',
  [P(9)]: 'Farrux Q.',
  [P(10)]: 'Otabek N.',
};

export const TOURNAMENTS: Tournament[] = [
  {
    id: uid(13, 1),
    title: 'CS2 2v2 Night Cup',
    gameId: uid(5, 1),
    startsAt: ago(45),
    state: 'live',
    prizePool: uzs(200_000),
    maxPlayers: 8,
    players: 8,
    joined: true,
    bracket: {
      rounds: [
        {
          matches: [
            { id: uid(14, 1), a: P(1), b: P(8), winner: P(1), score: '16-9' },
            { id: uid(14, 2), a: P(4), b: P(5), winner: P(5), score: '13-16' },
            { id: uid(14, 3), a: USER_ID, b: P(6), winner: USER_ID, score: '16-12' },
            { id: uid(14, 4), a: P(2), b: P(7), winner: P(2), score: '16-4' },
          ],
        },
        {
          matches: [
            { id: uid(14, 5), a: P(1), b: P(5), winner: P(1), score: '16-14' },
            { id: uid(14, 6), a: USER_ID, b: P(2), winner: null, score: '11-9' },
          ],
        },
        { matches: [{ id: uid(14, 7), a: P(1), b: null, winner: null, score: null }] },
      ],
    },
  },
  {
    id: uid(13, 2),
    title: 'Dota 2 Weekend Clash',
    gameId: uid(5, 2),
    startsAt: inMin(2 * 24 * 60),
    state: 'registration',
    prizePool: uzs(500_000),
    maxPlayers: 20,
    players: 14,
    joined: false,
    bracket: null,
  },
  {
    id: uid(13, 3),
    title: 'EA FC 25 1v1 Championship',
    gameId: uid(5, 11),
    startsAt: daysAgo(3),
    state: 'finished',
    prizePool: uzs(150_000),
    maxPlayers: 16,
    players: 16,
    joined: true,
    bracket: {
      rounds: [{ matches: [{ id: uid(14, 8), a: P(9), b: P(10), winner: P(9), score: '3-1' }] }],
    },
  },
];

export const LEADERBOARD: LeaderboardEntry[] = [
  { rank: 1, userId: P(1), name: 'Sardor K.', score: 48, avatarUrl: square('avatar-sardor') },
  { rank: 2, userId: P(2), name: 'Jasur T.', score: 41, avatarUrl: square('avatar-jasur') },
  { rank: 3, userId: USER_ID, name: 'Bobur Y.', score: 39, avatarUrl: square('avatar-bobur') },
  { rank: 4, userId: P(5), name: 'Diyor M.', score: 33, avatarUrl: null },
  { rank: 5, userId: P(4), name: 'Umid R.', score: 27, avatarUrl: null },
  { rank: 6, userId: P(6), name: 'Azamat S.', score: 21, avatarUrl: square('avatar-azamat') },
  { rank: 7, userId: P(7), name: 'Timur B.', score: 15, avatarUrl: null },
  { rank: 8, userId: P(8), name: 'Nodir A.', score: 9, avatarUrl: null },
];

// ---------------------------------------------------------------------------------------------------------------------
// Profile
// ---------------------------------------------------------------------------------------------------------------------

export const STATS: UserStats = {
  totalHours: 186.5,
  sessionsCount: 73,
  favoriteGames: [
    { gameId: uid(5, 1), hours: 92.3 },
    { gameId: uid(5, 2), hours: 41.0 },
    { gameId: uid(5, 3), hours: 22.7 },
    { gameId: uid(5, 8), hours: 12.5 },
  ],
  spent: uzs(2_140_000),
  rank: 17,
};

export const ACHIEVEMENTS: Achievement[] = [
  {
    id: uid(15, 1),
    title: 'First Blood',
    description: 'Play your first session',
    iconUrl: square('ach-first', 128),
    unlockedAt: daysAgo(214),
    progress: { current: 1, target: 1 },
  },
  {
    id: uid(15, 2),
    title: 'Night Owl',
    description: 'Play 10 sessions after midnight',
    iconUrl: square('ach-owl', 128),
    unlockedAt: daysAgo(90),
    progress: { current: 10, target: 10 },
  },
  {
    id: uid(15, 3),
    title: 'Marathon',
    description: 'Play 100 hours in total',
    iconUrl: square('ach-marathon', 128),
    unlockedAt: daysAgo(31),
    progress: { current: 100, target: 100 },
  },
  {
    id: uid(15, 4),
    title: 'Regular',
    description: 'Visit the club 30 days in a row',
    iconUrl: square('ach-regular', 128),
    unlockedAt: null,
    progress: { current: 12, target: 30 },
  },
  {
    id: uid(15, 5),
    title: 'Champion',
    description: 'Win a tournament',
    iconUrl: square('ach-champion', 128),
    unlockedAt: null,
    progress: { current: 0, target: 1 },
  },
  {
    id: uid(15, 6),
    title: 'Big Spender',
    description: 'Spend 5 000 000 UZS in the club',
    iconUrl: square('ach-spender', 128),
    unlockedAt: null,
    progress: { current: 2_140_000, target: 5_000_000 },
  },
];

export const LOYALTY: Loyalty = {
  level: 2,
  points: 1_340,
  nextLevelAt: 2_000,
  perks: ['5% bonus on every top-up', 'Free headset rental', 'Priority booking on weekends'],
};

// ---------------------------------------------------------------------------------------------------------------------
// Settings / config / policy
// ---------------------------------------------------------------------------------------------------------------------

export const SETTINGS: ShellSettings = {
  locale: 'ru',
  theme: 'default',
  availableThemes: ['default', 'neon'],
  volume: 60,
  muted: false,
  idleTimeoutSec: 300,
  showMetricsOverlay: false,
  allowVirtualKeyboard: true,
  uiSounds: true,
  features: {
    shop: true,
    chat: true,
    booking: true,
    tournaments: true,
    profile: true,
    topup: true,
    apps: true,
    callAdmin: true,
  },
};

export const SHELL_CONFIG: ShellConfig = {
  version: 1,
  locale: 'ru',
  theme: 'default',
  ipc: {
    pipeName: 'clubshell-agent',
    connectTimeoutMs: 3000,
    requestTimeoutMs: 15000,
    reconnectMinMs: 500,
    reconnectMaxMs: 5000,
  },
  kiosk: {
    fullscreen: true,
    topmostGuard: true,
    hideTaskbar: true,
    blockAltTab: true,
    blockWinKey: true,
    blockCtrlAltDel: false,
    hideCursorAfterSec: 0,
    overlayOnLock: true,
    allowVirtualKeyboard: true,
    exitHotkey: 'Ctrl+Alt+Shift+F12',
    adminPinHash: null,
  },
  idle: { timeoutSec: 300, dimAfterSec: 120, screensaverAfterSec: 600 },
  ads: {
    enabled: true,
    intervalSec: 900,
    durationSec: 15,
    playlist: [
      { url: '/mock-art/valorant-hero.jpg', type: 'image', durationSec: 8 },
      { url: '/mock-art/forza5-hero.jpg', type: 'image', durationSec: 8 },
      { url: '/mock-art/lol-hero.jpg', type: 'image', durationSec: 8 },
    ],
  },
  gamepad: { enabled: true, pollMs: 16, deadzone: 0.25, navigation: true },
  monitors: { primaryIndex: 0, secondaryMode: 'black' },
  ui: {
    defaultRoute: '/home',
    gridColumns: 5,
    showClock: true,
    clockFormat: 'HH:mm',
    showMetricsOverlay: false,
    showSessionBar: true,
    coverAspect: '2:3',
    currencyFormat: { locale: 'uz-UZ', minorDigits: 0 },
  },
  features: SETTINGS.features,
  sound: { uiSounds: true, defaultVolume: 60 },
  logging: { level: 'info', directory: 'logs' },
  devtools: true,
};

export const POLICY: Policy = {
  version: 17,
  updatedAt: daysAgo(2),
  shellReplacement: { enabled: true, shellExe: 'C:\\Program Files\\ClubShell\\ClubShell.exe' },
  processAllowlist: { mode: 'deny', patterns: ['*cheat*', '*inject*', 'cmd.exe', 'powershell.exe'] },
  usb: { allowStorage: false, allowHid: true },
  webFilter: {
    enabled: true,
    blockedDomains: ['*.torrent*', '*casino*'],
    allowedDomains: [],
    dnsServers: ['10.0.0.53'],
  },
  explorer: {
    disableTaskManager: true,
    disableRun: true,
    disableSettings: true,
    hideTaskbar: true,
    disableAltTab: true,
    disableWinKey: true,
    blockedKeyCombos: ['Ctrl+Shift+Esc', 'Ctrl+Alt+Del'],
  },
  power: { idleShutdownMin: 90, scheduledShutdown: '05:00' },
  updates: { channel: 'stable', autoInstall: true },
  anticheat: { required: ['eac', 'vanguard'], blockOnViolation: true },
  kiosk: { idleTimeoutSec: 300, adsIntervalSec: 900, allowVirtualKeyboard: true },
};

export const UPDATE_MANIFEST: UpdateManifest = {
  channel: 'stable',
  component: 'shell',
  version: '1.1.0',
  url: 'https://updates.example.uz/shell/1.1.0/ClubShell-1.1.0.zip',
  sha256: '9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08',
  size: 48_231_552,
  signature: 'MEUCIQDx…',
  releaseNotes: '- New tournaments bracket view\n- Faster game launch\n- Bug fixes',
  mandatory: false,
  publishedAt: daysAgo(1),
  minAgentVersion: '1.4.0',
};

export const NOTIFICATIONS: Notification[] = [
  {
    id: uid(16, 1),
    title: 'Happy hour',
    body: 'Standard tariff −20% until 18:00',
    level: 'info',
    ttlSec: 12,
    action: { label: 'Open wallet', command: '/wallet', args: null },
  },
  {
    id: uid(16, 2),
    title: 'Tournament tonight',
    body: 'CS2 2v2 Night Cup starts at 22:00',
    level: 'success',
    ttlSec: 10,
    action: { label: 'View', command: '/tournaments', args: null },
  },
];
