import type { Config } from 'tailwindcss';

/** Onyx palette of the shell, inlined: the console is a separate app and does not load `themes/*.json`. */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        bg: '#09090B',
        surface: '#151518',
        line: 'rgba(250,250,250,0.08)',
        primary: '#F4F4F5',
        'on-primary': '#09090B',
        accent: '#F2B84B',
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
