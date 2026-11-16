import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { VitePWA } from "vite-plugin-pwa";
export default defineConfig({
  plugins: [react(), tailwindcss(), VitePWA({
    registerType: "autoUpdate", includeAssets: ["icon.svg"],
    // Cache only the public app shell and fingerprinted static assets.
    // Navigation fallback is root-only: /api, /mcp, /.well-known, auth
    // endpoints and all other server routes MUST remain network-only.
    // Never put tenant rows, files, JWTs, or REST/MCP responses in SW caches.
    workbox: {
      navigateFallbackAllowlist: [/^\/$/],
      runtimeCaching: [],
      cleanupOutdatedCaches: true
    },
    manifest: { name: "Peritial", short_name: "Peritial", description: "Gestione perizie e sinistri",
      theme_color: "#0f172a", background_color: "#ffffff", display: "standalone", start_url: "/",
      icons: [{ src: "/icon.svg", sizes: "any", type: "image/svg+xml", purpose: "any" }] }
  })],
  server: { proxy: { "/api": "http://localhost:8080" } }
});