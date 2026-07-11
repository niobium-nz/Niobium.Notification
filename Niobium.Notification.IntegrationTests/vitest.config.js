import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'node',
    setupFiles: ['./test/setup.js'],
    testTimeout: 20000,
    hookTimeout: 20000
  }
});
