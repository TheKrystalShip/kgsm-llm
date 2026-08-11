# kgsm-assistant-eval

Scriptable plumbing for evaluating the kgsm assistant. It drives the **real** assistant in-process
(the same `AddLocalLlm` + `AddKgsmAssistant` + `AddKgsmAdapters` backend the CLI uses), runs a fixed
corpus of the prompts a server-managing user actually sends, and captures each turn's full
trajectory — so you can re-run it after a prompt edit, against a different model, and judge what
changed.

Two ways to read a run, use whichever fits:

- **`--transcript`** prints every case's full conversation (prompt → tools → staged ops → reply) for
  reading by eye or by a model. This is the "run it and evaluate live" surface — the transcript is
  the ground truth.
- **The scorecard** auto-scores each turn's trajectory against a typed rubric, as a quick guide to
  where behavior moved. The checks are deliberately conservative (routing/staging, not prose) — they
  flag *where to look*, the transcript decides *whether it's actually good*.

It is the codified, repeatable version of the one-off hand-evaluation in
`~/tks/gemma-assistant-eval.md`.

## Quick guide

### Run it

Needs live Ollama + a kgsm host with ≥ 1 instance. From the repo root:

```bash
A="dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval --"

# Whole suite, shipped prompts, read every conversation:
$A --kgsm ~/tks/kgsm/kgsm.sh --shipped-prompts --transcript

# Just the cases you care about (id, id-prefix, or dimension letter), fewer reps for speed:
$A --kgsm ~/tks/kgsm/kgsm.sh --filter G --transcript --reps 2     # the whole G group
$A --kgsm ~/tks/kgsm/kgsm.sh --filter C8 --reps 5                 # one case

# Another model:
$A --kgsm ~/tks/kgsm/kgsm.sh --model qwen3.5:9b --shipped-prompts

# Did my prompt edit help? Edit the prompt file, run twice, diff:
$A --kgsm ~/tks/kgsm/kgsm.sh --out eval-results/before.json       # before your edit (no --shipped-prompts → live prompts)
#   ...edit ~/.config/kgsm-assistant/prompts/preamble.md...
$A --kgsm ~/tks/kgsm/kgsm.sh --out eval-results/after.json
$A compare eval-results/before.json eval-results/after.json
```

(If you've set up the assistant CLI, `KGSM:Path` is inherited and you can drop `--kgsm`. `--help`
lists every flag.)

### Add a case

Cases live in `BenchmarkSuite.cs`. A single-turn case is one statement:

```csharp
Single("B6", "is <game> overloaded?", authorized: true, new[] { FixtureRole.UniqueGame },
    "is {unique_game} struggling with load?",         // the user's prompt (placeholders → real games)
    C.CalledTool(LlmTools.RunHealthCheck, "checks health"),
    C.ResolvedNotAsked(FixtureRole.UniqueGame)),       // …one or more checks
```

- **id** — any free label. Convention: `A`–`E` closed cases, `G` ambiguous-diagnosis. Group-runnable
  by id-prefix (`--filter B`).
- **roles** — the fixtures the prompt needs (below). A case whose role can't be filled on the host is
  **skipped with a reason**, never failed.
- **prompt** — use `{unique_game}` / `{never_game}` so it stays host-portable; the preflight fills them.
- **checks** — the assertions, from the kit below. For an **open-ended** prompt, give it a robust floor
  (`AnyOf(...)` + `DoesNotAskWhichServer()`) and judge the rest by reading `--transcript`.

Multi-turn (clarify → follow-up on one conversation): use the full `new BenchmarkCase(id, title, auth,
roles, new[]{ new BenchmarkStep("turn 1", checks), new BenchmarkStep("turn 2", checks) })` form — see `M1`.

Roles: `UniqueGame` (the only instance of its game), `Running`, `Stopped`, `AnyInstance`,
`NeverInstalledGame` (installable but absent), `MultipleInstances` (≥ 2 instances),
`ModeratableGame` / `NoModerationGame` (a unique-game instance whose blueprint does / doesn't declare
player-moderation commands).

The check kit (`Checks.cs`, all via `C.`):

| Check | Asserts |
|-------|---------|
| `CalledTool(LlmTools.X, "…")` | the model called tool X (`ServerInfo`, `RunHealthCheck`, `Search`, `ServerCommand`, `FindFiles`, …) |
| `CalledToolWith(X, "aspect", "players", "…")` | called X **and** passed that argument — on a noun-scoped tool the routing decision is the enum, so the tool name alone under-measures it |
| `DidNotCallTool(X, dim, "…")` · `NoToolCalls("…")` | it didn't call X · it called nothing (e.g. declines an off-topic ask) |
| `ReferencedRole(role, tool?, dim, "…")` | a tool call targeted that role's server |
| `RoutedThroughStatusOrHealth()` | consulted a status/health tool (didn't invent run-state) |
| `Stages(ConfirmationKind.X)` · `StagesNothing(dim, "…")` | staged X for confirmation (`Restart`/`Backup`/`Install`/`SetConfig`/…) · staged nothing |
| `SaysConfirmationPending()` | the reply tells the user something is waiting on them. Delegates to the assistant's own `PendingConfirmationNote` so there is one definition of the property. **On a staging turn this cannot fail** — the assistant appends the sentence when the model omits it — so it guards that wiring, and is never evidence the model narrated anything. Pair it with `Completes()` |
| `Completes()` · `WithinIterations(n)` | the turn answered instead of exhausting its step budget · it took at most `n` steps. The failable half of a staging case: staging can succeed on the last iteration and still leave the user reading "I couldn't finish that" |
| `ResolvedNotAsked(role)` | acted on the unique match without asking which |
| `DoesNotAskWhich()` · `DoesNotAskWhichServer()` | didn't ask any "which?" · didn't re-ask which *server* (diagnostic follow-ups still allowed) |
| `Clarifies()` | asked which, took no action (for a *genuinely* ambiguous prompt) |
| `FinalHas(regex, "…", dim)` · `FinalLacks(regex, "…", dim)` | the reply matches / doesn't match a pattern |
| `AnyOf(dim, "…", checkA, checkB, …)` | passes if any sub-check passes (multiple acceptable trajectories) |

Two rules: **score routing/staging, never a world fact** (the kgsm run-state bug would peg it red), and
if you change an **existing** case's checks, **bump `BenchmarkSuite.Version`** so old result files
compare honestly (adding a new case already does this when you mean to).

## What it measures (and what it deliberately doesn't)

It scores **what the model did** — routing, propose-only staging, clarify-vs-resolve — never a
world fact. A turn that asks "is it up?" is scored on *did it call `get_status`* (routing), not on
*was the answer true*. This is on purpose: kgsm has a run-state bug (a running server can report
`status: false`), so "is it really up?" would stay red no matter how well the model behaves. Scoring
routing/consistency keeps the tuning signal clean and independent of upstream bugs.

The corpus has two styles of case:

- **Closed cases** (`A`–`E`) — "is X up?", "what port?", "restart X" — have a right trajectory, so the
  auto-checks carry most of the weight.
- **Ambiguous cases** (`G`) — "minecraft is not working", "why can't I connect to valheim?", "my friend
  can't join satisfactory but I can" — are how real, non-technical users actually talk. There's no
  single right answer, so these are **primarily transcript-judged**: the auto-checks only assert the
  robust floor (don't fabricate, engage the problem, name the real failure mode, don't re-ask which
  server on a unique match), and you read the conversation to judge whether the guidance would actually
  help someone who doesn't know the "right" question. Run the group with `--filter G --transcript`.

The rubric, A–F (from the hand-eval):

| Dim | Name | Scored by |
|-----|------|-----------|
| A | No-fabrication | a run-state/port answer consulted a status/health tool (didn't invent it) |
| B | Routing | the right tool for the ask, referencing the right server |
| C | Propose-only | destructive ops are **staged** (a `PendingConfirmation`), narrated as awaiting confirmation |
| D | Clarify-vs-guess | resolve a unique match directly; ask only on genuine ambiguity |
| E | Scope | web only for outside facts; host tools for host questions |
| F | Tone | **not auto-scored** — the scorecard marks it uncovered; read transcripts to judge |

Each check is tagged with its dimension and lives in code (`Checks.cs` / `BenchmarkSuite.cs`),
because the *bar* should change deliberately and in review — the fast-tuning surface is the prompt
files, not the eval.

## MCQ mode — ground-truth answer accuracy (the RAG lift + tuning)

Everything above scores **routing** ("did it call the right tool?"). `mcq` mode scores something the
routing harness deliberately can't: **answer correctness**. It reproduces the retrieval lift chart —
**closed-book → with-RAG → oracle** — that motivated the RAG work, *for our own corpus*, and is the
knob-tuning surface for chunk size / TopK / MinScore.

It needs **Ollama only** (chat + embedder) — **no kgsm host**. It calls the model directly with no
tools, so there is no agent loop and no path to any action.

```bash
A="dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval --"

# The lift chart over the shipped corpus (greedy, reproducible):
$A mcq --seed 42

# Read every question's reply per condition:
$A mcq --seed 42 --transcript

# Tune a query-time knob — sweep it over a grid, holding the rest at baseline (with-rag):
$A mcq --sweep min-score
$A mcq --sweep top-k

# Diagnose WHERE the with-rag→oracle gap lives before touching retrieval (Phase 6 read):
$A mcq --diagnose

# Point it at the full real docs instead of the shipped snapshot, or a different embedder:
$A mcq --corpus ~/tks --embed-model embeddinggemma
```

**The three conditions:**

| Condition | The model is given… | Measures |
|-----------|---------------------|----------|
| `closed-book` | nothing — just the question | the base model's parametric knowledge (the baseline) |
| `with-rag` | whatever the **real `SearchAggregator`** retrieves from an index built in-process | end-to-end retrieval quality |
| `oracle` | the gold passage shipped in the question | the ceiling (retrieval is perfect) |

`with-rag` drives the *production* `SearchAggregator` over an index the runner builds from `--corpus`
with the current chunk knobs — so the chunk/TopK/MinScore/MaxContextChars values a sweep picks
**transfer to what ships**. The reading is: `closed → with-rag` is the retrieval *win*; `with-rag →
oracle` is the *gap left to close* (Phase 6's target).

**`--diagnose`** is the Phase 6 read: instead of guessing a lever, it measures retrieval. It reports
**recall@k** (did the gold passage make the raw top-k, across *all* questions — the metric a retriever
actually moves) and buckets the `with-rag → oracle` gap into *gold missed top-k* (recall — a hybrid/larger-k
job), *gold dropped from context* (the `MaxContextChars` cap / re-rank), or *gold in context but answered
wrong* (a model ceiling — no retriever helps). Unparsed/timeout replies are set aside as "inconclusive,"
and the verdict refuses to recommend a lever off a 1–2 question gap. On the shipped corpus the verdict is
**WITHIN NOISE** — recall is imperfect (84%) but the model answers correctly through most misses, so no
retriever was built; see the eval's `CLAUDE.md` for the full finding.

**The corpus** (`mcq/questions.json` + `mcq/corpus/`) is committed on purpose — a fixed baseline of
**real** ecosystem docs (the base model genuinely lacks them) so the lift is *measured*, not
manufactured. Each question carries its own gold passage, so `oracle` is independent of retrieval and
stable across doc edits. Add a question by appending to `questions.json` (id, topic, question,
choices, answer letter, source filename, gold passage); the loader validates every answer key, gold,
and source on load, and `McqCorpusTests` asserts it.

**Honesty:** the model is asked to end with `Answer: X`; an unparseable reply is scored **wrong** and
also counted in a separate **parse-failure** line, so "the model won't follow the format" never hides
as "the model is wrong." A built-in **sanity** line flags the one failure that means the corpus can't
measure retrieval — `with-rag ≈ oracle ≈ 100%` (no spread left). Oracle near 100% on its own is
**expected and good**: it confirms every gold passage entails its keyed answer.

> Greedy by default (`--temp 0`, `--reps 1`) for a stable number — override with `--temp`/`--reps`.

## Safety

The routing harness only ever calls `IServerAssistant.RunAsync`, which **stages** destructive
operations; it never calls `ConfirmAsync`, so nothing it runs can start, stop, install, or delete a
server. There is no code path here to execution. A full run is non-destructive. (`mcq` mode is
simpler still — it calls `ILlmClient` directly with no tools, no dispatcher, and no kgsm.)

It also prints a **loud inventory preflight** and aborts on an empty inventory — the one trap that
silently turns "no servers installed" into a green run (usually a stray `XDG_DATA_HOME` override
hiding kgsm's registry under `~/.local/share/kgsm`).

## Fixtures

Cases speak in **roles**, not hardcoded games, so the same corpus runs on any host over time. The
preflight resolves each role from live inventory and prints the mapping; a case whose role can't be
filled is **skipped with a reason**, never silently failed.

| Role | Filled by |
|------|-----------|
| `unique_game` | a game type with exactly one instance (so a bare game word is unambiguous) |
| `running` / `stopped` | an instance in that authoritative `is-active` state |
| `never_game` | an installable blueprint with no instance |
| `multiple` | ≥ 2 instances exist (precondition for a genuinely ambiguous reference) |
| `moderatable` / `no_moderation` | a unique-game instance whose blueprint declares / doesn't declare kick-ban-unban commands. Read from live blueprint detail, because which games can moderate is the host catalog's fact; an unreadable blueprint fills neither |

To exercise the run-state and multi-turn-ambiguity cases you want at least two instances, one of
them running. With a single stopped instance the routing/staging/resolution cases still run fully
(they don't depend on run-state); `multiple`-gated cases skip.

## Usage

```bash
# Run the suite against the shipped default prompts, 3 reps/prompt, write a result file.
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- --shipped-prompts

# Read every conversation to judge behavior yourself (the live-eval workflow).
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- --transcript --reps 1

# Tune a prompt file, then re-run only the affected cases for a fast loop.
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- --filter D,C8 --reps 5

# Benchmark a different model (already pulled in Ollama).
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- --model qwen3.5:9b --out eval-results/qwen.json

# Reproducible single-path run (fixed sampling seed) instead of pass-rate-over-N.
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- --seed 7 --reps 1

# Diff two runs: what improved, what regressed.
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- compare eval-results/before.json eval-results/after.json
```

Key options: `--model`, `-n/--reps`, `--seed`, `--temp`, `--filter <ids|dims>`, `--prompts <dir>`,
`--shipped-prompts`, `--kgsm <path>`, `--endpoint <url>`, `--out <path>`. `--help` for the full list.

**Prompts under test:** by default the eval reads the *same* prompt-override files the CLI tunes
(`$XDG_CONFIG_HOME/kgsm-assistant/prompts`), so it measures your live edits. `--shipped-prompts`
points it at an empty dir to test the in-code defaults (`KgsmAssistantPrompts`). Either way the
result file stamps the system-prompt template hash, so every run records exactly what it tested.

**Config:** `KGSM:Path` and the Ollama endpoint are inherited from the CLI's
`$XDG_CONFIG_HOME/kgsm-assistant/appsettings.json` (or env / flags) — so on a box where the assistant
CLI is set up, the eval just works.

## Noise & comparison

At `--reps N`, each check's score is a pass-rate over N. A single flip is `1/N` (33% at N=3), so
`compare` only flags per-check moves of ≥ 0.34 as real, and warns when corpus version, model, or rep
count differ between the two runs. Results carry the **raw per-rep trajectory** (tools, staged ops,
full reply, per-check verdict), so a diff shows *how* a case changed — "C8 went from
staging-restart to asking-which" — not merely that a rate moved.

Bump `BenchmarkSuite.Version` whenever a case changes, so older result files are compared honestly.
