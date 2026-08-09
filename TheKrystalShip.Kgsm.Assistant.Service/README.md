# TheKrystalShip.Kgsm.Assistant.Service

The **HTTP/SSE turn API** — the assistant surface the web control panel (kgsm-web / kgsm-api)
talks to. An ASP.NET Core minimal-API app that drives the same assistant brain as the CLI, adds
Discord-OAuth auth, and streams turns over Server-Sent Events.

- Stand it up: [`../docs/DEPLOYMENT.md`](../docs/DEPLOYMENT.md) §6
- Every config key: [`../docs/CONFIGURATION.md`](../docs/CONFIGURATION.md)
- systemd unit + env template: [`../deploy/`](../deploy/)
- The public wire contract in detail: [`../docs/wire-contract.md`](../docs/wire-contract.md)

## At a glance

- Binds **plain HTTP on `127.0.0.1:5180`** by default (loopback). TLS terminates at a reverse
  proxy in front — see [deployment §7](../docs/DEPLOYMENT.md#7--reverse-proxy--tls).
- **Single-instance, and its state is on disk.** Sessions and conversations live in one SQLite
  file, so a restart signs nobody out and a revocation outlives the process that made it. An
  in-flight sign-in needs no server-side state at all — it rides in a cookie. Confirmation tokens
  are stateless HMACs and survive a restart **iff** the signing key is stable.
- Logs via `AddSystemdConsole()` (journald `<N>` priority prefixes).
- Boots without any integration configured; only authenticated turns need the secrets.

## Endpoints

**Public:**

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | Liveness → `{"status":"ok"}` |
| GET | `/auth/discord/start` | Begins sign-in: sets the handshake cookie, 302s to Discord |
| GET | `/auth/discord/callback` | The sign-in landing → `{verdict, tier, token, refresh, …}` |
| POST | `/auth/session/refresh` | Trades a refresh token for a fresh pair |
| POST | `/events` | kgsm webhook (HMAC-signed via `X-KGSM-Signature`); invalidates the inventory cache |

`/auth/session/refresh` takes no bearer on purpose — it must work once the access token has lapsed,
and the refresh token in its body is the credential.

**Authenticated** (`Authorization: Bearer <token>`, or the trusted-relay headers):

| Method | Path | Purpose | SSE |
|--------|------|---------|-----|
| GET | `/auth/me` | Identity, live tier, and `canPerformActions` | — |
| GET | `/tools` | The tool picker the user is allowed to see (authority + availability filtered) | — |
| POST | `/turn` | **Run a turn** — `{prompt, think?, tools?}` | ✅ if `Accept: text/event-stream` |
| POST | `/confirm` | Execute a staged mutating action — `{token}` | — |
| POST | `/auth/logout` | Revoke the session — the bearer stops working at once | — |

### `/turn` and SSE

`POST /turn` with `Accept: text/event-stream` streams typed events; without it, you get a buffered
`{text, confirmations[], usage}` JSON response. The streamed event types (each an SSE `event:` line
with a matching in-band `type`): `text.delta`, `thinking.delta` (opt-in), `tool.start`,
`tool.result`, `progress`, `command.proposed`, `done`, `error`. A proposed mutating action arrives
as `command.proposed` carrying a confirmation token the client later posts to `/confirm`, which
answers buffered — or, for a blueprint finalize, streams `progress` and heartbeats to a terminal
`result`. The field-by-field contract, and the rules a client may rely on, are in
[`../docs/wire-contract.md`](../docs/wire-contract.md).

## Authentication & authority

Discord OAuth → session JWTs this service mints and can revoke. It runs the whole flow itself, with
its own application credentials and its own session store, so it authenticates people with no
Control Panel API in front of it.

1. Browser → `GET /auth/discord/start` → the service writes one HttpOnly cookie carrying the CSRF
   `state` **and** the PKCE `code_verifier`, then 302s to Discord.
2. Discord returns the browser to `/auth/discord/callback`. The state it echoes back must equal the
   one in that browser's cookie — a login begun anywhere else is refused before any code is
   exchanged.
3. The service exchanges the code server-side holding the client secret (the caller's Discord token
   is used once, for identity, and discarded), finds the KGSM account that identity proves — or
   creates an unapproved one for it to prove — and mints an access + refresh pair carrying a session
   id.
4. Every request re-checks that the session is still alive, which is what makes signing out and
   revoking mean something: a signed token is otherwise valid until it expires.

**The two cookie halves defend different attacks.** `state` stops login CSRF — without it an
attacker starts their own login and sends the victim a callback link carrying the *attacker's* code,
handing the victim a session for the attacker's identity. PKCE stops code interception. Neither
substitutes for the other, and the state is in a **cookie** rather than a server-side set because
only a browser-bound value proves the login started *here*: a set of issued states admits the
attacker's own login too.

**Authority is re-derived, never read off the token.** The tier is on the caller's KGSM account
(`admin ⊇ operator ⊇ viewer`), read from the shared account store and cached for
`Auth:RoleCacheTtlSeconds`. Acting needs `operator`; reading someone else's conversations needs
`admin`. A tier lowered in the Control Panel therefore stops working within the cache TTL rather than
surviving until the token expires, and a disabled account's sessions stop being accepted at all. A
store that cannot be *read* denies the check and is not cached — "we could not ask" is not "the
answer is no".

Required secrets (env-only — see [configuration](../docs/CONFIGURATION.md#secrets--environment-only-never-in-a-file)):
`KgsmAuth__ClientSecret`, `Auth__SigningKey`, `Assistant__Confirmation__Key`, plus the non-secret
`KgsmAuth__ClientId`, `DiscordOAuth__RedirectUri` and `Auth__AllowedOrigins`. The `KgsmAuth__*`
application is the host's, shared with the Control Panel API.

**Trusted relay (optional):** a co-located aggregator (kgsm-api) may call on a user's behalf with
`X-Relay-Secret` (matching `Assistant:Relay:Secret`) + `X-Relay-User`. Authority is still read from
that user's KGSM account — the relay cannot escalate.

## Run

```bash
# Local smoke (no secrets needed for /health):
dotnet run --project TheKrystalShip.Kgsm.Assistant.Service
curl -fsS http://127.0.0.1:5180/health        # -> {"status":"ok"}
```

For production (publish → systemd → reverse proxy → secrets) follow
[deployment §6](../docs/DEPLOYMENT.md#6--the-service-httpsse-api). It needs a reachable Ollama and
a local kgsm engine (`KGSM:Path`) at runtime; RAG and Tavily are optional and fail closed.
