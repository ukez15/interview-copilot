/// <reference types="vitest/config" />
import path from 'node:path';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  // Relative asset paths so the build loads from the desktop app's app:// protocol (and any web host in Mode C).
  base: './',
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(import.meta.dirname, 'src') },
  },
  server: { port: 5173, strictPort: true },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    // Opening a Radix dropdown per field makes form tests slower than the 5s default.
    testTimeout: 15000,
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}'],
      // main.tsx is bootstrap glue; components/ui is shadcn-generated vendor code.
      exclude: ['src/main.tsx', 'src/components/ui/**', 'src/test/**', 'src/**/*.test.{ts,tsx}', 'src/vite-env.d.ts'],
      thresholds: { lines: 75, functions: 75, branches: 75, statements: 75 },
    },
  },
});
