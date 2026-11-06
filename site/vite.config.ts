import { svelte } from "@sveltejs/vite-plugin-svelte";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [svelte()],
  build: { target: "es2022", sourcemap: false },
  server: { proxy: { "/api": "http://127.0.0.1:8787" } },
  test: { include: ["src/**/*.test.ts"], environment: "node" },
});
