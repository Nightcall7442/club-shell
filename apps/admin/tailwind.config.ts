import type { Config } from 'tailwindcss';

/**
 * Obsidian palette of the shell, variant F «Командный центр»: every colour is an `R G B` triplet in `src/index.css`
 * (`--c-*`), so `bg-accent/10` keeps working and a club accent can be wired later. Hairlines are the accent at a fixed
 * alpha; a modifier scales it (`border-line/60` is 60 % of the hairline).
 */
const rgb = (name: string): string => `rgb(var(--c-${name}) / <alpha-value>)`;
const hairline = (alpha: number): string => `rgb(var(--c-accent) / calc(${alpha} * <alpha-value>))`;

export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        bg: rgb('bg'),
        surface: rgb('surface'),
        art: rgb('art'),
        line: hairline(0.1),
        'line-strong': hairline(0.16),
        'line-hover': hairline(0.26),
        accent: rgb('accent'),
        'accent-hi': rgb('accent-hi'),
        'on-accent': rgb('on-accent'),
        primary: rgb('primary'),
        'on-primary': rgb('on-primary'),
        text: rgb('text'),
        hi: rgb('hi'),
        soft: rgb('soft'),
        dim: rgb('dim'),
        artlabel: rgb('artlabel'),
        muted: rgb('muted'),
        danger: rgb('danger'),
        'danger-ink': rgb('danger-ink'),
        warning: rgb('warning'),
        success: rgb('success'),
      },
      fontFamily: {
        sans: ['"Inter Variable"', 'Inter', 'Segoe UI', 'system-ui', 'sans-serif'],
        display: ['"Unbounded Variable"', '"Inter Variable"', 'sans-serif'],
        mono: ['"JetBrains Mono Variable"', 'ui-monospace', 'monospace'],
        dot: ['"Doto Variable"', '"JetBrains Mono Variable"', 'monospace'],
      },
      borderRadius: {
        sm: '5px',
        DEFAULT: '6px',
        md: '10px',
        lg: '10px',
        xl: '12px',
        chip: '8px',
        seg: '7px',
      },
      boxShadow: {
        glow: 'var(--shadow-glow)',
        'glow-inset': 'var(--shadow-glow-inset)',
        'glow-danger': 'var(--shadow-glow-danger)',
        float: 'var(--shadow-float)',
        panel: 'var(--shadow-panel)',
        sheet: 'var(--shadow-sheet)',
        sel: '0 0 0 1px rgb(var(--c-accent) / 0.16), 0 0 26px -6px rgb(var(--c-accent) / 0.55)',
        warn: '0 0 22px -8px rgb(var(--c-warning) / 0.5)',
        'inset-hi': 'inset 0 1px 0 rgb(var(--c-text) / 0.05)',
      },
      transitionTimingFunction: {
        out: 'cubic-bezier(0.16, 1, 0.3, 1)',
      },
    },
  },
  plugins: [],
} satisfies Config;
