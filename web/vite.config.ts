import { defineConfig } from 'vite';

export default defineConfig({
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    codeSplitting: false,
    rollupOptions: {
      output: {
        // Stable, non-hashed filenames so MSBuild's incremental copy cache
        // never references a stale hashed name (avoids MSB3030 on rebuild).
        entryFileNames: 'assets/editor.js',
        chunkFileNames: 'assets/[name].js',
        assetFileNames: 'assets/editor.[ext]',
      },
    },
  },
});
