import type { Config } from 'tailwindcss';

/**
 * Theme colours come from CSS variables written by the theme loader (`Theme.colors` → `--c-<name>: R G B`).
 * Space-separated RGB channels let Tailwind opacity modifiers (`bg-primary/50`) work.
 */
const themeColor = (name: string): string => `rgb(var(--c-${name}) / <alpha-value>)`;

export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        bg: themeColor('bg'),
        surface: themeColor('surface'),
        primary: themeColor('primary'),
        accent: themeColor('accent'),
        text: themeColor('text'),
        muted: themeColor('muted'),
        danger: themeColor('danger'),
        success: themeColor('success'),
      },
      borderRadius: {
        DEFAULT: 'var(--radius)',
        sm: 'calc(var(--radius) * 0.5)',
        md: 'calc(var(--radius) * 0.75)',
        lg: 'var(--radius)',
        xl: 'calc(var(--radius) * 1.5)',
        '2xl': 'calc(var(--radius) * 2)',
      },
      fontFamily: {
        sans: ['var(--font)', 'system-ui', 'Segoe UI', 'sans-serif'],
      },
      keyframes: {
        shimmer: {
          '0%': { backgroundPosition: '-200% 0' },
          '100%': { backgroundPosition: '200% 0' },
        },
        'pulse-glow': {
          '0%, 100%': { boxShadow: '0 0 0 0 rgb(var(--c-primary) / 0.45)' },
          '50%': { boxShadow: '0 0 24px 6px rgb(var(--c-primary) / 0.25)' },
        },
        'slide-up': {
          '0%': { opacity: '0', transform: 'translateY(16px)' },
          '100%': { opacity: '1', transform: 'translateY(0)' },
        },
        'fade-in': {
          '0%': { opacity: '0' },
          '100%': { opacity: '1' },
        },
      },
      animation: {
        shimmer: 'shimmer 1.6s linear infinite',
        'pulse-glow': 'pulse-glow 2s ease-in-out infinite',
        'slide-up': 'slide-up 0.25s ease-out both',
        'fade-in': 'fade-in 0.2s ease-out both',
      },
    },
  },
  plugins: [],
} satisfies Config;
