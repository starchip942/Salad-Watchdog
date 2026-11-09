import { defineConfig } from "vite";
// GitHub Pages serves the site from /<repo>/, set by the deploy workflow
export default defineConfig({ base: process.env.SITE_BASE ?? "/", build: { target: "es2020", assetsInlineLimit: 0 } });
