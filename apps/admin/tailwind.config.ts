import type { Config } from 'tailwindcss';

/** Graphite palette of the shell, inlined: the console is a separate app and does not load `themes/*.json`. */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        bg: '#0C0C0E',
        surface: '#161619',
        line: 'rgba(250,250,250,0.08)',
        primary: '#F4F4F5',
        'on-primary': '#0C0C0E',
        accent: '#7AA2F7',
        text: '#FAFAFA',
        muted: '#8E8E96',
        danger: '#EF4444',
        success: '#22C55E',
      },
      fontFamily: { sans: ['Inter', 'Segoe UI', 'system-ui', 'sans-serif'] },
    },
  },
  plugins: [],
} satisfies Config;
