# gemma4:12b as the KGSM assistant — evaluation (2026-06-17)

Hands-on evaluation of the local model (`gemma4:12b`, temp 0.3) driving the
`kgsm-assistant` against a real host, for the prompts a user managing game servers
would actually send. Method: 14 single-turn prompts + 1 multi-turn flow through the
CLI with conversation recording on; every reply judged together with its **tool
trajectory** (from the transcript corpus). One target server (`terraria-eval`,
Terraria, running) was installed for the run and removed after; `factorio-test`
(stopped) pre-existed.

**Rubric (primary → secondary):** (A) never fabricate status/capability · (B) routing
correctness · (C) propose-only narration ("proposed", never "done") · (D) clarify vs
guess · (E) scope discipline · (F) tone.

---

## Headline verdict

gemma4:12b is a **solid fit** for this assistant. It does the load-bearing things
right: it **calls `get_status` instead of inventing run-state**, it **narrates staged
commands as proposals awaiting confirmation** (never claims it did them), and it
**keeps web-search to outside facts**. Its weaknesses are routing-shaped and
**prompt-tunable** — proven below by a single preamble edit that fixed four of them.

The biggest trust problem in the whole run is **not the model** — it's a kgsm
run-state bug the assistant faithfully relays (a *running* server reported as
*stopped*). That undermines the assistant more than any model flaw and should be
fixed first.

---

## What the model does well

- **(A) No fabricated status.** Every "is X up / which are running" call routed
  through `get_status`; the model reported exactly what the tool returned. It never
  answered run-state from the injected instance list (which carries only
  `name (game)`, never state).
- **(C) Propose-only narration is excellent.** "I've staged … awaiting your
  confirmation" for set-config (`auto_update`), backup, restart, and install — every
  staged command narrated as a proposal, never as done. This is the security-critical
  behavior and it's reliable.
- **(E) Scope discipline.** "latest version of Terraria" → `web_search` (correct, an
  outside fact); "what's the weather" → **declined without searching** ("my expertise
  is your game servers"). The web/host boundary held.
- **(D) Doesn't fabricate for the genuinely-unknown.** "is the valheim server
  running?" → called `get_status(valheim)`, got "no such instance", and replied with
  the real instance list + an offer — no invented server.
- **(D, multi-turn) Uses the clarification.** "something's wrong with one of my
  servers" → asked which (correct; truly ambiguous) → "the terraria one" → resolved
  it from context and ran the health check. It acts on the answer, not just asks.
- **(F) Tone** is on-brief: friendly, concise, "friends running servers" register.

## Where it falls short (all prompt-tunable)

1. **Over-clarifies on a unique match — the main UX wart.** It inconsistently asks
   "which server?" when exactly one matches:
   - "restart terraria for me" → *asked which Terraria* (no action) — yet "back up
     terraria" and "turn on auto-updates for terraria" both resolved `terraria-eval`
     and staged correctly. Same phrasing, different behavior (temp-0.3 nondeterminism).
   - "the factorio one — is it doing ok?" → *asked which Factorio* — never tried the
     resolver, which matches by game type (`factorio` → `factorio-test`).
2. **Capability underclaim / wrong tool for "what port?"** It called
   `view_config_file` (the `.config.ini`, which doesn't carry the listen port), didn't
   find it, and answered *"I don't have access to the port through my tools"* — false
   (the port is in `get_status`). When a tool doesn't surface an answer it
   over-generalizes to "I can't", instead of trying `get_status`.
3. **Mild over-tooling.** Simple yes/no questions ("is terraria up?", a clarified
   health check) often fire `get_status` **and** `run_health_check` (2–3 iterations
   where 1 would do). Harmless but slower, and burns toward the 8-iteration cap.
4. **Minor leaks.** Install reply said "once you **click confirm**" (invents a UI verb
   — the prompt is deliberately host-agnostic; the CLI uses y/N). It also called the
   plain instance list "currently **active** servers" once (implies running when it
   doesn't know).

---

## The critical issue is NOT the model: a run-state bug

`terraria-eval` was **genuinely running** (`is-active` = active, process listening on
`:7777`, logs show "Server started") — but the assistant reported it **stopped** on
every fleet status call. The disagreement originates in kgsm itself: its `status`
command returns `"status": false` while `is-active` returns true for the same
instance. The assistant relays the `status` field, so it inherits the wrong run-state.

This is the single highest-impact problem for the assistant's usefulness — it makes
*correct* model behavior produce *wrong* answers — and it's a **kgsm / kgsm-lib
run-state-authority bug**, not a model or prompt issue. It connects directly to the
ecosystem's "never fabricate a status / metric-presence is never a status" invariant
and the run-state-façade work. **Fix this before any prompt tuning.**

Secondary ecosystem finding: `run_health_check` **skips the log scan when the
instance isn't running** ("Log scan skipped — instance not running"). But a *crashed*
server is, by definition, stopped — so the #1 troubleshooting case (diagnose a crash
from its logs) can never scan logs. Combined with the run-state bug (running servers
seen as stopped), health checks routinely skip logs that exist.

---

## Evidence: the model weaknesses ARE prompt-fixable

Per the tuning + recording loop, the recommendations below aren't hopeful — they're
tested. Appending **one paragraph** to `preamble.md` (no code change, applied on the
next turn):

> When a user refers to a server by its game type or a partial name (e.g. "terraria",
> "the factorio one") and it matches exactly ONE installed instance, treat it as that
> instance and act directly — that is NOT ambiguous, so do not ask which one. Only ask
> when the reference matches two or more instances. To check whether a server is
> running, or to find its port or network details, call `get_status` for that instance
> rather than saying you cannot.

| Prompt | Default preamble | After the edit |
|---|---|---|
| "how do I change max players on terraria?" | asked *which server?* | resolved `terraria-eval`, acts |
| "restart terraria for me" | asked *which server?* | **stages `server_command(restart)`** |
| "the factorio one — is it doing ok?" | asked *which Factorio?* | **`run_health_check(factorio-test)`** |
| "what port is my terraria server on?" | "I don't have access" | **`get_status` → "port 7777 TCP/UDP"** |

**4/4 fixed.** This also settles an open question from the prompt-externalization work:
persona-prose edits **do** move gemma-12B when the instruction is concrete and
task-relevant (resolve-and-act), in contrast to arbitrary style directives ("reply in
French") which it ignored. So the system prompt is a real lever here — for *behavioral
routing guidance*, not cosmetics.

---

## Recommendations, prioritized

**P0 — ecosystem, not the model (do first):**
1. Fix the run-state read: kgsm `status` returns `false` for a running instance while
   `is-active` returns true. Trace the `status` path through kgsm-lib's run-state
   façade (native → watchdog). Until fixed, the assistant confidently reports wrong
   run-state.
2. Let `run_health_check` scan the most recent logs even when the instance is
   stopped — that's exactly the crash-diagnosis case.

**P1 — system prompt (`preamble.md`), all validated above:**
3. Add the "unique match ⇒ act, don't ask" + "use `get_status` for run-state/ports"
   paragraph. Biggest single UX win.

**P2 — tool descriptions (`tools.json`):**
4. `get_status`: state that it returns run-state **and network ports** for an
   instance (steers "what port?" to it, and discourages the redundant
   `get_status`-before-`run_health_check`).
5. `run_health_check`: "this already covers run-state — don't call `get_status`
   first" to cut the 2–3-iteration over-tooling.
6. `view_config_file`: note it shows the `.config.ini` only (not the live listen
   port), so the model stops reaching for it to answer port questions.

**P3 — minor:**
7. Reinforce host-agnostic phrasing ("after you confirm", not "click confirm").
8. Consider whether the per-message web-search cap / the configured Tavily key are
   set for the deployment (search was unconfigured during the eval; the model handled
   it gracefully).

**Method note for next time:** keep `XDG_DATA_HOME` at its real value when driving the
assistant — kgsm's instance registry lives under it, so overriding it hides every
instance. Isolate the transcript corpus with `Recording__Directory` instead.
