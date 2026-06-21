# TheKrystalShip.Kgsm.Assistant.Service

The **HTTP/SSE turn API** — the assistant surface the web control panel (kgsm-web / kgsm-api)
talks to. An ASP.NET Core minimal-API app that drives the same assistant brain as the CLI, adds
Discord-OAuth auth, and streams turns over Server-Sent Events.

- Stand it up: [`../docs/DEPLOYMENT.md`](../docs/DEPLOYMENT.md) §6
- Every config key: [`../docs/CONFIGURATION.md`](../docs/CONFIGURATION.md)
- systemd unit + env template: [`../deploy/`](../deploy/)
- The SSE event contract in detail: [`../docs/m7-sse-5a-spec.md`](../docs/m7-sse-5a-spec.md)

## At a glance

- Binds **plain HTTP on `127.0.0.1:5180`** by default (loopback). TLS terminates at a reverse
  proxy in front — see [deployment §7](../docs/DEPLOYMENT.md#7--reverse-proxy--tls).
- **Stateful, single-instance:** sessions, conversations, and OAuth state are in-memory — a
  restart forces re-login. Confirmation tokens are stateless HMACs and survive a restart **iff**
  the signing key is stable.
- Logs via `AddSystemdConsole()` (journald `<N>` priority prefixes).
- Boots without any integration configured; only authenticated turns need the secrets.

## Endpoints

**Public:**

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | Liveness → `{"status":"ok"}` |
| GET | `/auth/login` | Returns the Discord authorize URL (PKCE) to send the browser to |
| POST | `/auth/callback` | Exchanges `{code,state}` for a session bearer + display name |
| POST | `/events` | kgsm webhook (HMAC-signed via `X-KGSM-Signature`); invalidates the inventory cache |

**Authenticated** (`Authorization: Bearer <token>`, or the trusted-relay headers):

| Method | Path | Purpose | SSE |
|--------|------|---------|-----|
| GET | `/auth/me` | Session identity + `canPerformActions` | — |
| GET | `/tools` | The tool picker the user is allowed to see (authority + availability filtered) | — |
| POST | `/turn` | **Run a turn** — `{prompt, think?, tools?}` | ✅ if `Accept: text/event-stream` |
| POST | `/confirm` | Execute a staged mutating action — `{token}` | — |
| POST | `/auth/logout` | Tear down the session | — |

### `/turn` and SSE

`POST /turn` with `Accept: text/event-stream` streams typed events; without it, you get a buffered
`{text, confirmations[], usage}` JSON response. The streamed event types (each an SSE `event:` line
with a matching in-band `type`): `text.delta`, `thinking.delta` (opt-in), `tool.start`,
`tool.result`, `command.proposed`, `done`, `error`. The full field-by-field mapping is in
[`../docs/m7-sse-5a-spec.md`](../docs/m7-sse-5a-spec.md). A proposed mutating action arrives as
`command.proposed` carrying a confirmation token the client later posts to `/confirm`.

## Authentication & authority

Discord OAuth → bearer session tokens (the SPA is a separate origin, so bearer not cookies):

1. SPA → `GET /auth/login` → the service returns a Discord authorize URL (PKCE, single-use state).
2. Browser approves at Discord, redirects back to the SPA, which posts `{code,state}` to
   `/auth/callback`.
3. The service exchanges the code server-side (the caller's token is discarded), verifies guild
   membership via the **bot token**, and mints a session bearer.
4. *Whether the user may run actions* is derived from a guild **role** (`ActionRoleId`), looked up
   by user id via the bot token and cached (`Auth:RoleCacheTtlSeconds`). Read-only operations are
   open to any guild member.

Required secrets (env-only — see [configuration](../docs/CONFIGURATION.md#secrets--environment-only-never-in-a-file)):
`DiscordOAuth__ClientSecret`, `DiscordOAuth__BotToken`, `Assistant__Confirmation__Key`, plus the
non-secret `ClientId`/`GuildId`/`ActionRoleId`/`RedirectUri` and `Auth__AllowedOrigins`.

**Trusted relay (optional):** a co-located aggregator (kgsm-api) may call on a user's behalf with
`X-Relay-Secret` (matching `Assistant:Relay:Secret`) + `X-Relay-User`. Authority is still derived
from the bot by user id — the relay cannot escalate.

## Run

```bash
# Local smoke (no secrets needed for /health):
dotnet run --project TheKrystalShip.Kgsm.Assistant.Service
curl -fsS http://127.0.0.1:5180/health        # -> {"status":"ok"}
```

For production (publish → systemd → reverse proxy → secrets) follow
[deployment §6](../docs/DEPLOYMENT.md#6--the-service-httpsse-api). It needs a reachable Ollama and
a local kgsm engine (`KGSM:Path`) at runtime; RAG and Tavily are optional and fail closed.
