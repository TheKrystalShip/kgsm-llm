# Krystal Control Panel (`kgsm-web`)

The web Control Panel SPA for the KGSM ecosystem — a **standard Vite + React 18**
single-page app (JSX). It was ported from the no-build `krystal-design`
prototype (React + Babel from a CDN, every symbol on `window`) into a real,
tree-shaken ES-module build.

> Runs fully standalone against bundled fixtures — **no backend required** to
> develop the UI. Point it at the real Control Panel aggregator API (`kgsm-api`)
> by setting `VITE_API_BASE`.

## Quick start

```bash
npm install
npm run dev        # http://localhost:5173  (bundled fixtures)
npm run build      # → dist/  (minified, hashed, production bundle)
npm run preview    # serve the built dist/ locally
```

## Verifying

No-browser smoke harnesses (jsdom + the real Vite module graph):

```bash
npm run smoke                   # routes (SSR) + mount (effects) against fixtures
node scripts/smoke-routes.mjs   # SSR-render every route, catch render throws
node scripts/smoke-mount.mjs    # client-mount effect-heavy routes, run effects

# Live wiring (needs a running kgsm-api):
KGSM_API=http://127.0.0.1:8097 npm run smoke:live
```

## Layout

```
kgsm-web/
  index.html              Vite entry → /src/main.jsx
  vite.config.js  package.json  .env.example
  public/
    fonts/                self-hosted Inter + JetBrains Mono (variable)
    assets/  icons/       brand mark + PWA icons
  src/
    main.jsx              mounts <App/> in the root ErrorBoundary; imports global CSS
    App.jsx               the shell: auth gate, hash routing, sidebar, assistant dock
    components/           25 shared components (Sidebar, ServerCard, ConsolePanel, …)
    pages/                18 routed pages (Dashboard, Servers, Library, Alerts, …)
    lib/                  client data layer (see below)
    styles/
      tokens.css          design tokens + @font-face (the brand DS)
      kit.css             component class library
      consumer.css        app-level overrides
  scripts/                codemod (migration record) + smoke harnesses
  reference/              the scaffold's live-backend API seam (not wired; see below)
  MIGRATION.md            prototype → production playbook (auth, realtime, TS, PWA)
```

### The data layer (`src/lib/`)

The prototype's reactive store layer, ported verbatim to ESM:

- `store.js` — `createStore` / `useStore` (React 18 `useSyncExternalStore`).
- `apiClient.js` — the backend seam (`api`). A mock that resolves from
  `data.js` with simulated latency + a streaming channel. **This is the one file
  to replace to go live** (swap `get`/`post`/`stream` for `fetch`/WebSocket
  against `kgsm-api`); call sites never change.
- `data.js` — bundled demo fixtures (`KRYSTAL_DATA`).
- `stores.js` — domain stores (`serversStore`, `hostsStore`, `auditStore`, …).
- `sessionStore.js`, `capabilities.js`, `alertsApi.js`, `chatTools.js`,
  `persona.js`, `router.js` — sessions/auth, per-host capabilities, alerts,
  the assistant tool layer, RBAC persona, and hash↔route translation.

> **Init order note.** Because the prototype relied on a fixed global script
> load order, a few base-layer modules import upper ones only for deferred,
> call-time use; those edges are `import(...)`-lazy to keep the ESM graph
> acyclic. See the comments in `apiClient.js` / `data.js`.

## Connecting the backend

```bash
cp .env.example .env.local
# VITE_API_BASE=http://your-host:8097   (kgsm-api ORIGIN — no /api/v1, the client appends it)
# VITE_WS_BASE is optional (derived from VITE_API_BASE if unset)
```

With `VITE_API_BASE` set, the SPA talks to a real `kgsm-api` over `fetch`; leave it
blank to run against the bundled fixtures (offline demo). The one seam is
`src/lib/apiClient.js` (live `get/post/patch` + adapters in `src/lib/adapters.js`);
call sites never change. **`WIRING.md` is the authoritative front↔back contract**
(endpoint/realtime/schema diff + the sequenced wiring plan). The read path
(servers/hosts) is wired and live-verified; auth, realtime, and the rest are the
remaining slices in `WIRING.md §8`.

## What's done vs. what's left

**Done:** the full UI ported to a real toolchain — all 25 components + 18 pages,
the store/router/data layer, self-hosted fonts, lucide-react icons, the complete
CSS, a green production build, and the read-path backend wiring (servers/hosts via
the live transport + adapters, honest-unknown rendering) verified by the smoke
harnesses.

**Left** (see `WIRING.md §8`): the rest of the read path (audit/library/alerts),
real auth
(Discord OAuth + per-host re-auth), a realtime WebSocket client, optional
TypeScript, tests (Vitest + RTL), and a PWA precache (`vite-plugin-pwa`).
