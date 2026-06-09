import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "node:path";

// The node serves this SPA from Kestrel under /. In dev, proxy the API + SSE to
// a running node (override host with VITE_API_PROXY, default the node's :8080).
const apiTarget = process.env.VITE_API_PROXY || "http://127.0.0.1:8080";

export default defineConfig({
  plugins: [react()],
  resolve: { alias: { "@": path.resolve(__dirname, "./src") } },
  server: {
    port: 5173,
    proxy: {
      "/api": { target: apiTarget, changeOrigin: true },
    },
  },
  build: {
    outDir: "dist",
    // Served by the .NET host; emit a clean asset layout.
    sourcemap: false,
  },
});
