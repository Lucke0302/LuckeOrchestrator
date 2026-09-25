/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  theme: {
    extend: {
      // Paleta corporativa do Orquestrador Lucke: tons de azul sobre fundo escuro
      // (dark mode nativo). Gera utilitários como `bg-lucke-blue-dark`, `text-lucke-blue-light`.
      colors: {
        'lucke-blue': {
          DEFAULT: '#2f6fed', // ações primárias (botões, foco)
          darkest: '#050b18', // fundo da aplicação
          darker: '#081228', // fundo de painéis e modais
          dark: '#0d1b33', // superfícies (cards, sidebar, tabelas)
          soft: '#1b3a6b', // bordas e divisores
          light: '#5b9dff', // destaques, links e ícones ativos
          lighter: '#a9c9ff', // texto secundário sobre fundo escuro
        },
      },
      fontFamily: {
        sans: ['Inter', 'Segoe UI', 'system-ui', 'sans-serif'],
        mono: ['JetBrains Mono', 'Cascadia Code', 'Consolas', 'monospace'],
      },
      boxShadow: {
        'lucke-glow': '0 0 24px -6px rgba(47, 111, 237, 0.55)',
      },
    },
  },
  plugins: [],
}
