import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'node',
    coverage: {
      provider: 'v8',
      include: ['src/**/*.ts'],
      // Electron glue (app lifecycle, windows, preload) needs a running Electron and is covered by the
      // manual release checklist; all decision logic lives in the tested modules.
      exclude: [
        'src/main.ts',
        'src/preload.ts',
        'src/capture-preload.ts',
        'src/windows.ts',
        // Browser-only glue (getDisplayMedia, AudioWorklet); its logic lives in the tested pcm/sender modules.
        'src/capture/capture-window.ts',
        'src/capture/pcm-worklet.ts',
        'src/**/*.test.ts',
      ],
      thresholds: { lines: 75, functions: 75, branches: 75, statements: 75 },
    },
  },
});
