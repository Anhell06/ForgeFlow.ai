import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const backend = "http://localhost:5180";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": backend,
      "/files": backend,
      "/mcp": backend,
      "/openapi": backend,
    },
  },
  build: {
    // The ASP.NET backend serves the built UI from wwwroot.
    outDir: "../backend/ForgeFlow.Api/wwwroot",
    emptyOutDir: true,
    chunkSizeWarningLimit: 2000,
  },
});
