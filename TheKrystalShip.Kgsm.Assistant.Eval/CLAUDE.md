# CLAUDE.md — kgsm-assistant-eval

Operating manual for a future me maintaining this benchmark. The **README** is the user-facing
how-to (run it, add a case, the check kit); **this** file is the design integrity — the invariants
that are easy to break by accident and the gotchas that cost real time. Read the README's *Quick
guide* for mechanics; read this before changing how scoring works.

## What this is, in one breath

A console app (`kgsm-assistant-eval`) that drives the **real** assistant in-process (the CLI's
`AddLocalLlm` + `AddKgsmAssistant` + `AddKgsmAdapters` backend + an in-memory `CapturingRecorder`)
over a fixed prompt corpus, captures each turn's tool trajectory, and scores it. It is the codified
version of the one-off hand-eval (`~/tks/gemma-assistant-eval.md`); the memory
`assistant-eval-harness.md` is the durable summary. It's a **leaf** in the ecosystem (see
`~/tks/system-architecture.md`) — depends only on the assistant + kgsm-lib, never the API/Service.

## Invariants — do NOT break these (they ARE the design)

1. **Score routing/staging, never a world fact.** A run-state answer is scored "did it call
   `get_status`", not "was the answer true". kgsm has a P0 run-state bug (a *running* server can
   report `status:false`), so any `FinalMatches(/running|up/)`-style check would stay red forever
   regardless of tuning, and the scorecard's noise floor would become the ecosystem's bugs. If you
   catch yourself asserting reality, stop — assert the trajectory instead.
2. **Non-destructive by construction.** The harness only ever calls `IServerAssistant.RunAsync`
   (which *stages* destructive ops); it must **never** call `ConfirmAsync`. There is no code path to
   execution and there must not be — a full run touches no server. Do not add one "to test confirm".
3. **The transcript is ground truth; auto-checks are a conservative floor.** Checks assert robust
   signals (tool calls, staged `PendingConfirmation`s, did-not-fabricate), not prose quality. `F`/tone
   is deliberately **uncovered** — the scorecard says so; never fake a green for it. Open-ended cases
   (`G` group) get a *floor* (`AnyOf(...tools..., FinalHas(...))` + `DoesNotAskWhichServer`) and are
   judged by reading `--transcript`. The score guides; the transcript decides.
4. **Don't chase brittle prose regexes.** The D11 lesson: the model was 3/3 *correct* ("I don't see a
   Minecraft server", "isn't installed", "I can install one") and the regex kept missing the
   phrasings — so it got dropped to robust trajectory checks. If a prose check is red while the
   transcript is clearly fine, **the check is wrong** — broaden to a robust signal or move the
   judgment to the transcript. Don't tune the regex until it's green; that's overfitting.
5. **The bar lives in code, on purpose.** Cases + checks are C# in `BenchmarkSuite.cs`/`Checks.cs`
   because changing the *bar* should be deliberate and reviewed. The fast-iteration surface is the
   prompt files (`~/.config/kgsm-assistant/prompts/*`), NOT the eval. Don't make the corpus
   data-driven/hot-editable — that was considered and rejected.
6. **Fixtures by role + loud preflight.** Cases template `{unique_game}`/`{never_game}`, resolved
   from live inventory; the preflight prints the inventory and **aborts on empty**. Keep it loud — a
   silent empty-inventory run that "passes" is the single worst failure mode (see gotcha #1).

## The acceptance test

If you change scoring, prove it still reproduces the hand-eval: run `--shipped-prompts` on gemma4:12b
and confirm the four tuning-fixed cases pass (**B5** port, **C6** max-players, **C8** restart-staging,
**D12** "the factorio one") plus the known-good ones. **If the harness disagrees with the hand-eval,
the harness is wrong** — the hand-eval is ground truth.

## MCQ mode (ground-truth accuracy) — a SECOND, separate harness

`mcq` mode (the `Mcq*.cs` files) is a different instrument from the routing benchmark above, added in
Phase 5 of the RAG work. The routing harness scores *what tool the model called* (and deliberately
**never** a world fact — invariant #1). The lift chart the RAG work needs is **100% world-fact**: is
the answer correct, closed-book vs with-RAG vs oracle. That can't be bolted onto the routing scorer
without breaking invariant #1, so it's a parallel mode with its own design rules:

1. **Bare `ILlmClient`, no kgsm, no tools.** `McqRunner` composes only `AddLocalLlm` (+ the RAG core's
   embed client / `IndexBuilder`); it calls `ChatAsync` directly. There is **no dispatcher and no
   agent loop**, so invariant #2 (no path to execution) holds even more strongly here than in the
   routing harness — there's nothing to confirm. It needs Ollama, **not** a kgsm host; `Program`
   branches to it *before* the kgsm check.
2. **with-RAG drives the REAL `SearchAggregator`.** The runner builds an index in-process (so chunk
   size is a real, tunable knob — query-time knobs don't need a rebuild) and queries it through the
   production `SearchAggregator` over a faithful eval-local `IRetrieval` (`EvalRetrieval`, a mirror of
   `RagRetrieval`) + a fail-closed `IWebSearch` (`NoWebSearch`, local-only). This is the load-bearing
   choice: tune through the **same** knobs production uses (TopK, MinScore, MaxContextChars) or the
   winning values won't transfer. Don't "simplify" with-RAG to a bespoke retrieve-and-concat.
3. **The corpus must be REAL docs at volume.** `mcq/corpus/` is a committed snapshot of real ecosystem
   docs; `mcq/questions.json` is hand-authored against them, each item shipping its own gold passage.
   **Invented docs would make `closed-book ≈ 25%` and `with-rag ≈ oracle ≈ 100%` by construction — a
   manufactured lift that measures nothing.** If you regenerate the corpus, keep it real; bump
   `questions.json`'s `version`.
4. **oracle ≈ 100% is the GOOD signal, not a defect.** It confirms every gold passage entails its
   keyed answer (a wrong key would make oracle dip) — the reference benchmark's oracle was 99.3%. The
   real "corpus too easy" alarm is `with-rag ≈ oracle ≈ 100%` (no spread left to attribute to
   retrieval); that's the only thing the sanity line flags. Don't add hard questions just to push
   closed-book down — that inflates the headline lift without improving retrieval (teaching to the
   test). Widen the lift by **tuning retrieval**, which is what the sweep is for.
5. **Unparseable = wrong AND counted.** `AnswerParser` reads the last `Answer: X` (reasoning before it
   is allowed and helps a 12B); an out-of-range/absent letter is a parse FAILURE — scored wrong and
   reported on a separate parse-failure line, never guessed.

The deterministic core (parser, corpus load/validate, sweep grid, scoring math) is unit-tested with no
model; `McqLiveTests` (gated `KGSM_LIVE_OLLAMA=1`) smokes the whole pipeline on a 2-question subset.
**Acceptance for the mode:** a live `mcq --seed 42` reproduces the reference chart *shape* (closed <
with-rag ≤ oracle≈100%) and at least one `--sweep` knob moves with-RAG accuracy.

## Build / test / run

```bash
dotnet build TheKrystalShip.Llm.slnx                                   # whole solution
dotnet test  TheKrystalShip.Kgsm.Assistant.Eval.Tests/*.csproj         # 30 logic tests, no live deps
# A live ROUTING run needs Ollama + a kgsm host with ≥1 instance:
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- --kgsm ~/tks/kgsm/kgsm.sh --shipped-prompts --transcript
# A live MCQ run needs Ollama only (chat + embedder), no kgsm:
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- mcq --seed 42            # the lift chart
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- mcq --sweep min-score    # tune a knob
```

The unit tests cover the deterministic core (checks, compare math, options, fixture resolution) and
run in CI without a model. A live run is the only thing that exercises the model.

## Gotchas that cost time

- **Never override `XDG_DATA_HOME`** when driving the assistant — kgsm's instance registry lives under
  `~/.local/share/kgsm`, so overriding it hides EVERY instance and the model truthfully says "no
  servers installed". Isolate test corpora via `Recording__Directory` instead; the eval uses its own
  in-memory recorder and is immune, but anything you script around it is not. This cost a full battery
  once. The preflight aborting on empty is the guardrail.
- **This host has only `factorio-test` (stopped).** So `MultipleInstances`-gated `M1` and any
  run-state-dependent richness skip or converge on "it's stopped". To exercise ambiguity + live
  run-state, install/start a second instance first. Cases that don't need run-state run fully anyway.
- **`Ollama:Seed` lives in the LLM project, not here.** The optional reproducible-seed knob is
  `OllamaOptions.Seed` + threaded in `OllamaLlmClient.BuildBody` (sent only when set). A cross-project
  change — rerun the `TheKrystalShip.Llm.Tests` if you touch it.
- **Result files are gitignored** (`eval-results/`). They're generated artifacts; don't commit them.
  Each is stamped with model + `sysHash` (prompt-template hash) + `corpusVersion` + seed + reps.
- **Corpus version discipline:** bump `BenchmarkSuite.Version` when you change an EXISTING case's
  checks (old result files then compare honestly; `compare` warns across the change). Pure additions
  are also a bump-worthy change to "overall", but per-check diffs still line up by id+label.
- **Known model finding (corpus v2):** gemma4:12b handles the ambiguous `G` cases well;
  **qwen3.5:9b intermittently returns an EMPTY reply after tool calls** on open-ended diagnostic
  prompts (CLI-confirmed, not a harness artifact) and misses network-exposure reasoning. The
  auto-score *understated* this (empty-after-tool still trips tool-based checks) — the transcript
  showed the real gap. A live reminder of invariant #3.

## File map

| File | Role |
|------|------|
| `BenchmarkSuite.cs` | the corpus — cases + their checks + `Version`. **The thing you edit to add/change cases.** |
| `Checks.cs` | the check kit (`C.*` factories) + `TurnObservation` + the `Rubric` dimensions |
| `Fixtures.cs` | role resolution from live inventory (`IServerInventory` + `IsActiveAsync`) + the loud preflight |
| `Harness.cs` | DI wiring (3 calls + recorder swap), the run loop, scoring aggregation, config resolution |
| `CapturingRecorder.cs` | in-memory `IConversationRecorder` — the only seam exposing the tool trajectory |
| `Scorecard.cs` / `Transcripts.cs` | the two output renderers (summary table / full conversations) |
| `EvalResult.cs` | the stamped JSON result DTOs + (de)serialization |
| `Compare.cs` | diff two result files into regressions/improvements |
| `EvalOptions.cs` / `Program.cs` | arg parsing + entry point (routing run vs `mcq` vs `compare`, `--filter`) |
| `Mcq*.cs` + `AnswerParser`/`SweepGrid`/`EvalRetrieval`/`NoWebSearch` | the ground-truth MCQ mode (separate harness, flat with the routing files): `McqRunner` (3-condition runner), `McqCorpus`/`McqItem` (loader + types), `AnswerParser`, `SweepGrid`, `McqScorecard`, `McqResult` (DTOs), `EvalRetrieval`/`NoWebSearch` (drive the real `SearchAggregator`) |
| `mcq/questions.json` + `mcq/corpus/` | the committed ground-truth corpus — hand-authored MCQs + the real-docs snapshot they're drawn from (copied next to the binary) |

## Ecosystem rules that apply here

- Work directly on **main** in the `kgsm-*` repos; commit there (don't auto-branch).
- **Never shell out to `kgsm.sh` from C#.** The eval respects this by going *through* the assistant →
  kgsm-lib for everything, including the `is-active` run-state read in fixture resolution. If you need
  more kgsm data, extend a kgsm-lib method / an assistant port — don't add a shell-out here.
- Don't fabricate a metric or status — the same invariant the whole ecosystem holds, and the reason
  invariant #1 exists.
