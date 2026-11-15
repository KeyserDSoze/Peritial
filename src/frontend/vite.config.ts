import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { VitePWA } from "vite-plugin-pwa";
export default defineConfig({
  plugins: [react(), tailwindcss(), VitePWA({
    registerType: "autoUpdate", includeAssets: ["icon.svg"],
    manifest: { name: "Peritial", short_name: "Peritial", description: "Gestione perizie e sinistri",
      theme_color: "#0f172a", background_color: "#ffffff", display: "standalone", start_url: "/",
      icons: [{ src: "/icon.svg", sizes: "any", type: "image/svg+xml", purpose: "any" }] }
  })],
  server: { proxy: { "/api": "http://localhost:8080" } }
});