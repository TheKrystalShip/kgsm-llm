# Assistant CLI — Integration Plan

> **STATUS: ✅ IMPLEMENTED.** Drafted + built 2026-06-17. All four branching decisions
> locked (§1) and all 7 steps (§6) shipped. A **new terminal surface** onto the existing
> kgsm assistant — a thin console host, parallel to the Discord bot and the HTTP/SSE
> service — plus one structural refactor (the kgsm-lib adapters extracted into a shared
> infrastructure project). No changes to the LLM library. Binary: `kgsm-assistant`. Full
> suite green (244 tests). Committed to `kgsm-llm` main (`69f8dfa` → `255883d`).
>
> **Implementation progress (§6):**
> - ✅ **Step 0 — Extract infra lib** (commit `69f8dfa`). `TheKrystalShip.Kgsm.Assistant.Infrastructure`
>   + `.Tests` created; adapters + options moved; `AddKgsmAdapters` seam, `IInventoryInvalidation`,
>   `Invocation.ForCli` added; Service `Program.cs` collapsed to the one-liner; tests migrated.
>   Full suite green (216 tests), no behavior change. One refinement vs the plan: the Service's
>   `/events` webhook reaches the now-internal `KgsmServerInventory` via a new **public
>   `IInventoryInvalidation`** interface rather than `InternalsVisibleTo` (cleaner, and the CLI
>   needs the same seam). Infra deps floored at **9.0.0** not 8.0.0 (kgsm-lib requires ≥9.0.0;
>   8.0.0 is an NU1605 downgrade).
> - ✅ **Step 1 — CLI scaffold** (commit `b1f5998`). `TheKrystalShip.Kgsm.Assistant.Cli` Exe;
>   three-call composition root; config resolution (defaults→file→env→`--model`); quiet
>   stderr logging (`--verbose` opts up); `Invocation.ForCli`; friendly startup errors;
>   `CliOptions`. Note: `LogToStandardErrorThreshold` is a `ConsoleLoggerOptions` (not formatter)
>   setting — used `AddSimpleConsole` + `Configure<ConsoleLoggerOptions>`; the plan's
>   `AddSimpleConsole(o => o.LogToStandardErrorThreshold)` wouldn't compile.
> - ✅ **Step 2 — Renderer** (commit `d006733`). `TerminalRenderer` maps the stream to the
>   terminal; status lines gate on stdout-is-TTY (so `… | cat` is clean); `--no-color`/`NO_COLOR`
>   disables both renderer + framework-log color; Ctrl-C cancels. Verified live (0 ANSI in piped stdout).
> - ✅ **Step 3 — Confirm** (commit `c31b229`). `ConfirmationFlow` (y/N, ConfirmAsync, post-action
>   Invalidate; non-TTY never executes, L8). `Cli.Tests` added with the L8 safety test + 4 more.
> - ✅ **Step 4 — Args/modes** (commit `827968e`). stdin-pipe one-shot + REPL (`/exit /reset /help`,
>   Ctrl-D); `TurnInterruptor` (per-turn Ctrl-C); `CliRunner`/`Repl` split. All three forms verified live.
> - ✅ **Step 5 — Packaging** (commit `e398fa5`). Self-contained single-file publish (linux-x64,
>   no trim; symbols embedded). Verified: the ~70MB binary runs a live query under `env -i`
>   (no SDK/PATH). Install = copy `kgsm-assistant` to `/usr/local/bin`.
> - ✅ **Step 6 — Tests & docs** (commit `255883d`). Renderer + arg-parse tests + env-gated live
>   smoke (28 CLI tests; live smoke verified to pass with `KGSM_LIVE_OLLAMA=1`). CLI README;
>   keystone surface table gains a `cli` row + edge; `architecture.html §3·d` adds `cli` to the
>   audit origin set (L7 — done, not just flagged).

---

## 0. Goal & premise

Make the assistant usable **directly from the terminal** on a kgsm host —
`kgsm-assistant "is terraria up?"` for one-shot, or a REPL for a conversation —
without going through Discord or a browser.

The premise (verified): **the assistant is already host-agnostic.** `IServerAssistant`
exposes `RunAsync` (buffered) and `RunStreamAsync` (genuine token streaming via an
internal channel), and the library README explicitly names "a CLI" as a valid host.
The only host-specific code is the wiring that satisfies the assistant's ports
(`IServerInventory`, `IServerOperations`, `IWebSearch`) from kgsm-lib + Tavily. So a CLI
is **another thin host**: it reuses the entire model + tool stack and supplies the same
adapters, minus the HTTP/OAuth/SSE machinery the Service carries.

**Conformance with the ecosystem invariants** (`tks/CLAUDE.md`,
`system-architecture.md §4`): the CLI is a **new leaf surface**. It depends only on the
assistant library + the shared infra library (which depends on kgsm-lib) — never on a
sibling leaf (monitor/bot) or the web API. It is **independently deployable**: runs
co-located with a `kgsm`, needing only kgsm-lib + a local Ollama + (optionally) a Tavily
key. The keystone surface table should gain a `cli` row at implementation time.

---

## 1. Locked decisions (2026-06-17)

| # | Decision | Choice | Why |
|---|---|---|---|
| D1 | **Action authority default** | **Authorized by default; `--read-only` opt-down** | The person at the terminal already has shell + direct `kgsm.sh` access — gating them would be theater, and admin is the common case. `--read-only` demotes a session to reads when wanted. |
| D2 | **web_search in the CLI** | **Enabled** (reuse the Tavily adapter) | Parity with the Service for near-zero extra work — the adapter just gets registered in the CLI host too. Spends Tavily credits; the per-day cap still guards (the CLI process has its own `DailyCallBudget`). |
| D3 | **Distribution** | **Self-contained single-file binary** on PATH | One binary, no runtime dependency, works even where .NET isn't installed; matches the ecosystem's binary-on-host pattern (monitor/firewall/watchdog). |
| D4 | **Output rendering** | **Plain stdout, TTY-aware color** | Zero new dependencies; pipe/redirect-friendly (`assistant "…" \| grep`). Color + dim tool-status lines **only when stdout is a TTY**; markdown printed as-is. |

**Locked by engineering (no user call needed):**

- **L1 — Extract a shared infra library** for the kgsm-lib adapters (§2). This is
  *not* DRY-for-its-own-sake: `Program.cs:43-66` deliberately hand-registers **only**
  the `IKgsmCommandExecutor` graph and **skips `AddKgsmServices`**, because the full
  registration auto-starts kgsm-lib's Unix-socket event listener and "would contend with
  the bot for the single kgsm event socket." A naive CLI calling `AddKgsmServices` would
  step on exactly that. Extraction captures the socket-safe partial registration **once**,
  so the CLI inherits the correctness constraint for free.
- **L2 — In-process confirmations** (drop `ConfirmationTokenService`). The web service
  needs signed stateless tokens because confirm arrives on a *separate request from a
  possibly different principal*. A CLI is one interactive process: hold the staged
  `PendingConfirmation` objects in memory, prompt `y/N`, call `ConfirmAsync` directly.
  The entire token/OAuth/bearer/CORS layer drops out.
- **L3 — Streaming, with Ctrl-C → cancel.** Consume `RunStreamAsync`; cancellation
  aborts the underlying Ollama generation (the box's single GPU is reserved away from
  the game servers, so an abandoned turn that kept generating is a real cost).
- **L4 — Logs to stderr.** stdout carries only the assistant's reply, so piping stays clean.
- **L5 — Two modes:** one-shot (arg or stdin) and interactive REPL.
- **L6 — Inventory freshness = TTL only.** No webhook receiver in the CLI. The cache
  self-expires (instances 300s / blueprints 600s) and `KgsmServerInventory.Invalidate()`
  is public, so the CLI force-refreshes after a confirmed mutating action. REPL staleness
  is bounded to the TTL; one-shot is moot (the process exits).
- **L7 — Provenance `cli:<osuser>`.** Add `Invocation.ForCli(osUser)` so the kgsm audit
  trail (keystone O3) distinguishes terminal actions from Discord/web. **One small open
  point:** the `origin` tag — confirm `cli` against the origin set in `architecture.html
  §3·d` (it currently enumerates `assistant`); this is a one-word cross-doc contract
  addition, low-risk, flagged not silently invented.
- **L8 — Non-interactive never auto-confirms.** If stdin is not a TTY (piped/scripted)
  and an action is staged, the CLI **prints the proposal and exits without executing**.
  A `--yes`/`--confirm-all` scripting escape hatch is deferred to V2 (explicit, dangerous).

---

## 2. Structural change: extract `TheKrystalShip.Kgsm.Assistant.Infrastructure`

Today the kgsm-lib-backed adapters live `internal` inside the **Service** (a Web-SDK
project). A console host can't cleanly reference a Web-SDK executable, and duplicating
the socket-safe DI block invites L1's contention bug. So move the host-neutral
infrastructure into a new plain class library both hosts reference.

**New project:** `TheKrystalShip.Kgsm.Assistant.Infrastructure` (`Microsoft.NET.Sdk`,
net9.0). References: `TheKrystalShip.Kgsm.Assistant` + `TheKrystalShip.Llm` + kgsm-lib.

**Moves into it (from the Service):**

| File | Notes |
|---|---|
| `Kgsm/KgsmServerInventory.cs` | unchanged; stays `internal` |
| `Kgsm/KgsmServerOperations.cs` | unchanged; stays `internal` |
| `Kgsm/InvocationContext.cs` | `Invocation` / `IInvocationContext` / `AsyncLocalInvocationContext`; **add `Invocation.ForCli(osUser)`** |
| `Search/TavilyWebSearch.cs` | unchanged; stays `internal` |
| `Search/DailyCallBudget.cs` | already `public` |
| Options: `InventoryCacheOptions`, `WebSearchOptions`, `KgsmConnectionOptions` | move out of the Service's `Configuration/ServiceOptions.cs`; **web-only options stay** (`AssistantServiceOptions`, `DiscordOAuthOptions`, `AuthOptions`) |

**New in it — the single socket-safe registration seam:**

```csharp
// TheKrystalShip.Kgsm.Assistant.Infrastructure/Extensions/ServiceCollectionExtensions.cs
public static IServiceCollection AddKgsmAdapters(this IServiceCollection services, IConfiguration config)
{
    services.Configure<InventoryCacheOptions>(config.GetSection(InventoryCacheOptions.Section));
    services.Configure<WebSearchOptions>(config.GetSection(WebSearchOptions.Section));

    var kgsm = config.GetSection(KgsmConnectionOptions.Section).Get<KgsmConnectionOptions>()
        ?? throw new InvalidOperationException("KGSM configuration section is missing.");

    // PARTIAL kgsm-lib graph ONLY — deliberately NOT AddKgsmServices(): that auto-starts the
    // Unix-socket event listener (IKgsmClient/IEventService), which would contend with the bot
    // for the single kgsm event socket. We register only the IKgsmCommandExecutor graph the
    // inventory/operations need; nothing here binds or follows the socket. (Was Program.cs:43-66.)
    services.AddSingleton(new KgsmOptions { KgsmPath = kgsm.Path });
    services.AddSingleton<IProcessRunner, ProcessRunner>();
    services.AddSingleton<IKgsmCommandExecutor, KgsmCommandExecutor>();
    services.AddSingleton<ILogSubscriptionService, LogSubscriptionService>();
    services.AddSingleton<ILifecycleService, LifecycleService>();
    services.AddSingleton<IInstanceService, InstanceService>();
    services.AddSingleton<IBlueprintService, BlueprintService>();
    services.AddSingleton<ISystemService, SystemService>();

    services.AddSingleton<KgsmServerInventory>();
    services.AddSingleton<IServerInventory>(sp => sp.GetRequiredService<KgsmServerInventory>());
    services.AddSingleton<IInvocationContext, AsyncLocalInvocationContext>();
    services.AddSingleton<IServerOperations, KgsmServerOperations>();

    // web_search (Tavily). ENV-only key (WebSearch__ApiKey); fails closed with no key.
    services.AddSingleton<DailyCallBudget>();
    services.AddHttpClient<IWebSearch, TavilyWebSearch>((sp, client) =>
    {
        var o = sp.GetRequiredService<IOptions<WebSearchOptions>>().Value;
        client.BaseAddress = new Uri("https://api.tavily.com/");
        client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds <= 0 ? 10 : o.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(o.ApiKey))
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", o.ApiKey);
    });

    return services;
}
```

**Service `Program.cs` shrinks:** the whole hand-wired block (KGSM.Lib graph + the two
port adapters + the Web-search block, ~lines 43-83) is replaced by one line:
`builder.Services.AddKgsmAdapters(builder.Configuration);`. The Service keeps everything
web-only (Discord OAuth, `ConfirmationTokenService`, bearer filter, CORS, endpoints, SSE,
the `/events` webhook → `inventory.Invalidate()`). `IInvocationContext.Begin(...)` at
`/turn`/`/confirm` stays; it now resolves the infra-lib type.

**Test migration:** `internal`-visibility tests that assert on `TavilyWebSearch` move with
it. Grant `InternalsVisibleTo("…Infrastructure.Tests")`. Specifically:
- `TavilyWebSearchTests` (stub handler) → `Infrastructure.Tests`.
- `WebSearchWiringTests` (DI resolves `IWebSearch` → `TavilyWebSearch`, `DailyCallBudget`
  singleton) → rewrite as a **plain `ServiceCollection` + `AddKgsmAdapters`** test in
  `Infrastructure.Tests` (no `WebApplicationFactory` needed — faster, and it's really
  testing the seam, not the web host). Optionally the Service keeps a thin smoke that the
  composed app still resolves Tavily (defense-in-depth), but it's redundant.
- The Service's HTTP/auth/SSE/endpoint tests stay put.

**Net:** the Service becomes a thin HTTP host; the CLI becomes a thin console host; both
sit on the same socket-safe infra seam.

---

## 3. New project: `TheKrystalShip.Kgsm.Assistant.Cli`

`Microsoft.NET.Sdk` (Exe, net9.0). References: `TheKrystalShip.Kgsm.Assistant` +
`TheKrystalShip.Kgsm.Assistant.Infrastructure` + `TheKrystalShip.Llm`. No ASP.NET.

### 3.1 Composition root

```csharp
var builder = Host.CreateApplicationBuilder(args);   // config + DI + logging

// Logs → stderr so stdout carries only the reply (pipe-clean). Quiet by DEFAULT:
// SetMinimumLevel(Warning) — NOT just redirection. `LogToStandardErrorThreshold = Trace`
// only *routes* every level to stderr; without a minimum level the Service's inherited
// "TheKrystalShip": "Debug" would flood stderr mid-reply. `--verbose` opts the floor up.
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Warning);
builder.Logging.AddSimpleConsole(o => { o.LogToStandardErrorThreshold = LogLevel.Trace; });

builder.Services.AddLocalLlm(builder.Configuration);   // Ollama client, conversation store, agent loop
builder.Services.AddKgsmAssistant();                   // prompt builder, dispatcher, policy, IServerAssistant
builder.Services.AddKgsmAdapters(builder.Configuration); // kgsm-lib graph + ports + Tavily (socket-safe)
```

That is the **entire** backend wiring — three calls. Everything else in the CLI project
is the front end (arg parsing, the turn loop, the renderer, the confirm prompt).

### 3.2 Config resolution (self-contained binary)

**As built (commit `1465e4d`):** every knob lives in ONE canonical `appsettings.json` — no
defaults are hardcoded in C#. (The original plan put defaults "in code" and warned a single-file
binary's content root is an extraction temp dir; in practice a modern .NET single-file does NOT
self-extract managed assets, so `AppContext.BaseDirectory` points at the binary's own dir and a
co-located file IS reliable — and the same JSON is also embedded as a manifest resource, so the
lone binary never depends on a sidecar at all.) Precedence (low → high):

1. **Embedded defaults** — `appsettings.json` baked in as a manifest resource
   (`kgsm-assistant.appsettings.json`), loaded via `AddJsonStream`. The lone binary stands alone.
2. **Sidecar `appsettings.json`** next to the binary (`AppContext.BaseDirectory`, optional) —
   the editable, shipped template; overrides the embedded defaults host-wide.
3. **Operator file** at a discoverable path: `$KGSM_ASSISTANT_CONFIG` / `--config`, else
   `~/.config/kgsm-assistant/appsettings.json` (added via `AddJsonFile(optional: true)`).
4. **Environment variables** — the secret path, matching the ENV-only-secrets convention:
   `WebSearch__ApiKey`, plus `Ollama__Model`, `KGSM__Path`, `Ollama__Endpoint`, etc.
5. **`--model`** flag — wins for the model tag.

The CLI's `appsettings.json` covers only the console host's surface
(KGSM/Ollama/Conversation/LlmAgent/InventoryCache/WebSearch) — not the Service-only
Assistant/DiscordOAuth/Auth sections.

### 3.3 Authority (D1)

```csharp
var canPerformActions = !args.Contains("--read-only");   // authorized by default
```

`canPerformActions` flows into every `RunAsync`/`RunStreamAsync`/`ConfirmAsync` call —
the same authority axis the Service derives from Discord roles, here derived from the flag.
Provenance is set once per process: `invocation.Begin(Invocation.ForCli(Environment.UserName))`.

### 3.4 Conversation identity / memory (L5, L6)

- **REPL:** mint one `conversationId = $"cli:{Guid}"` at session start, reuse for every
  turn → in-session memory via the in-memory store. `/reset` starts a fresh id.
- **One-shot:** a fresh ephemeral id per process → stateless (no cross-invocation memory).
  Cross-invocation persistence (a file-backed `IConversationStore`) is **deferred** —
  the `IConversationStore` seam makes it a later drop-in if wanted.

---

## 4. The renderer & turn loop (D4, L3)

The CLI analogue of `SseTurnWriter` — consume `RunStreamAsync`, map events to the terminal:

| `AssistantStreamEvent` | Terminal behavior |
|---|---|
| `Token` | write delta to **stdout**, no newline |
| `ToolStart` | dim status line to **stderr** (TTY only): `⚙ get_status(instance=terraria)` |
| `ToolResult` | optional dim one-liner to stderr (TTY only) |
| `Confirmation` | **collect** into a list; do not print yet |
| `Final` | flush trailing newline |
| `Error` | red line to **stderr**; non-zero exit (one-shot) |

TTY-awareness: `Console.IsOutputRedirected` / `Console.IsErrorRedirected`; honor `NO_COLOR`
and `--no-color`. When stdout is redirected → no color, status lines suppressed, only the
reply text emitted (clean for pipes). Ctrl-C cancels the `CancellationToken` feeding
`RunStreamAsync` → aborts Ollama generation (L3).

After the stream ends, drain collected confirmations (§5).

---

## 5. Confirmation flow (L2, L8)

Each staged `PendingConfirmation` is rendered human-readably and gated interactively:

```
⚠ Proposed action: uninstall 'terraria'
  Proceed? [y/N]
```

- On `y` (interactive TTY only): `await assistant.ConfirmAsync(confirmation, canPerformActions, ct)`,
  print the outcome; then `inventory.Invalidate()` so the next inventory read is fresh (L6).
- On anything else: skip it.
- **Non-interactive stdin (L8):** print the proposal, **do not execute**, exit. (Scripting
  `--yes` deferred to V2.)
- `--read-only` sessions can't stage actions in the first place (tools aren't offered),
  so this path only runs for authorized interactive sessions.

No tokens, no signing, no expiry — the staged object lives in process memory for the
seconds between proposal and `y`.

---

## 6. Implementation steps

Each step ends green (`dotnet build` + `dotnet test` on the full solution).

- **Step 0 — Extract infra lib (§2).** Create `…Infrastructure` + `…Infrastructure.Tests`;
  **move (verbatim, do not re-derive)** the four adapter files + three options classes;
  add `AddKgsmAdapters` with the socket-safe comment; add `Invocation.ForCli`; rewire
  Service `Program.cs` to the one-liner; add the projects to `TheKrystalShip.Llm.slnx` +
  the `InternalsVisibleTo`; **then** migrate the adapter tests. **Sequence matters:** the
  registration block is a *verbatim move* of `Program.cs:43-83`, not a hand-retyped
  re-derivation — that's what guarantees `KgsmServerOperations`/`KgsmServerInventory` keep
  every transitive dependency (copy the working set; don't enumerate it). Get a green build
  on the move *before* touching tests (the test migration is the fiddly part — don't
  entangle them). **Pin first (10-second grep):** confirm `KgsmConnectionOptions` is
  Service-defined (`…Service.Configuration`), **not** a kgsm-lib type, and that nothing in
  the Service still references it after the move (Program.cs's read of it relocates *into*
  `AddKgsmAdapters`). **Acceptance:** full suite green, Service still resolves `IWebSearch`
  → `TavilyWebSearch`, no behavior change.
- **Step 1 — CLI scaffold.** New `…Cli` Exe project; the three-call composition root (§3.1);
  config resolution (§3.2); stderr logging (quiet-by-default, §3.1); provenance `ForCli`.
  **Friendly startup failures:** wrap the misconfig cases into clean one-liners, never a
  stack trace — missing/unset `KGSM__Path` ("KGSM path not configured — set KGSM__Path or
  ~/.config/kgsm-assistant/appsettings.json"), unreachable Ollama (clear message + non-zero
  exit). A missing Tavily key must **only disable search** (the adapter already fails
  closed), never abort. **Acceptance:** `dotnet run -- "list servers"` returns a buffered
  reply against live Ollama + kgsm; a bad `KGSM__Path` prints a one-line error, not a trace.
- **Step 2 — Renderer + streaming loop (§4).** Map `RunStreamAsync` events; TTY-aware plain
  rendering; Ctrl-C cancel. **Acceptance:** tokens stream live; `… | cat` emits clean text,
  no color/status; Ctrl-C stops generation promptly.
- **Step 3 — Confirmation flow (§5).** Interactive `y/N`; `ConfirmAsync`; post-action
  `Invalidate()`; non-TTY never executes (L8). **Acceptance:** a staged uninstall executes
  on `y`, is skipped on `N`, and is only printed (never run) when stdin is piped.
- **Step 4 — Arg surface & modes (L5).** One-shot (positional arg **or** stdin pipe); REPL
  (no arg + TTY) with `/exit`, `/reset`, `/help`; top-level `--help`/`-h` (usage + flags);
  `--read-only`; `--verbose`; `--no-color`/`NO_COLOR`; optional `--model`/`--config`
  overrides. **Acceptance:** all three entry forms work; `--help` prints usage; `--read-only`
  hides action tools (gate refuses a command).
- **Step 5 — Packaging (D3).** `dotnet publish -c Release -r linux-x64 --self-contained
  -p:PublishSingleFile=true` (no trimming — `System.Text.Json` reflection in the Llm client
  makes trim/AOT risky; revisit later). Document dropping the binary at `/usr/local/bin/
  kgsm-assistant`. **Acceptance:** the published binary runs on the host with no SDK present.
- **Step 6 — Tests & docs.** Renderer mapping (inject `TextWriter` + a fake `IServerAssistant`
  yielding a scripted event sequence), arg parsing, the non-TTY-never-confirms safety,
  authority→`canPerformActions` mapping; one env-gated live smoke (mirror the existing
  `KGSM_LIVE_OLLAMA=1` pattern). CLI README; add a `cli` row to the keystone surface table;
  confirm the `cli` origin tag in `architecture.html §3·d` (L7).

---

## 7. What this deliberately does NOT do

- **No new model/tool work.** Same catalog, prompt, caps, staging, web_search — the CLI is
  pure surface.
- **No HTTP, OAuth, bearer, CORS, SSE, or confirmation tokens** — all web-only, all dropped.
- **No webhook receiver** — TTL + post-action `Invalidate()` instead (L6).
- **No cross-invocation persistence for one-shot** — deferred behind `IConversationStore`.
- **Doesn't build the future web frontend.** That almost certainly needs **no new backend**:
  the existing HTTP service already emits the canonical `text.delta`/`tool.start`/
  `command.proposed`/`done` SSE vocabulary with CORS + bearer built for an SPA. The future
  web UI is a JS client of the service that already exists. **The CLI is the only surface
  needing new host code.**

---

## 8. Effort

Small-to-moderate. Step 0 is a mechanical-but-careful refactor of committed Service code
(the test migration is the fiddly part). Steps 1-4 are the actual CLI and are
straightforward given the host-agnostic assistant. Step 5 is csproj/publish settings.
No library changes; no kgsm or kgsm-lib changes.
