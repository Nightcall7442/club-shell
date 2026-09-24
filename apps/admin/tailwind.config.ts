import type { Config } from 'tailwindcss';

/** Obsidian palette of the shell, inlined: the console is a separate app and does not load `themes/*.json`. */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        bg: '#07090C',
        surface: '#0D1117',
        line: 'rgba(154,223,255,0.1)',
        'on-accent': '#031018',
        primary: '#F4F4F5',
        'on-primary': '#07090C',
        accent: '#9ADFFF',
        text: '#E8F1F6',
        muted: '#7D8A96',
        danger: '#EF4444',
        success: '#22C55E',
      },
      fontFamily: {
        sans: ['"Inter Variable"', 'Inter', 'Segoe UI', 'system-ui', 'sans-serif'],
        display: ['"Unbounded Variable"', '"Inter Variable"', 'sans-serif'],
        mono: ['"JetBrains Mono Variable"', 'ui-monospace', 'monospace'],
        dot: ['"Doto Variable"', '"JetBrains Mono Variable"', 'monospace'],
      },
    },
  },
  plugins: [],
} satisfies Config;
