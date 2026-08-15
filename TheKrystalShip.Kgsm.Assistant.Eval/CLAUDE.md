# CLAUDE.md — kgsm-assistant-eval

Operating manual for a future me maintaining this benchmark. The **README** is the user-facing
how-to (run it, add a case, the check kit); **this** file is the design integrity — the invariants
that are easy to break by accident and the gotchas that cost real time. Read the README's *Quick
guide* for mechanics; read this before changing how scoring works.

## What this is, in one breath

A console app (`kgsm-assistant-eval`) that drives the **real** assistant in-process (the CLI's
`AddLocalLlm` + `AddKgsmAssistant` + `AddKgsmAdapters` backend, with the conversation store pointed at a
throwaway temp DB) over a fixed prompt corpus, reads each turn's tool trajectory back from the store
(the canonical history IS the per-turn record now), and scores it. It is the codified
version of the one-off hand-eval (`~/tks/gemma-assistant-eval.md`); the memory
`assistant-eval-harness.md` is the durable summary. It's a **leaf** in the ecosystem (see
`~/tks/system-architecture.md`) — depends only on the assistant + kgsm-lib, never the API/Service.

## Invariants — do NOT break these (they ARE the design)

1. **Score routing/staging, never a world fact.** A run-state answer is scored "did it call
   `get_status`", not "was the answer true". kgsm has a P0 run-state bug (a *running* server can
   report `status:false`), so any `FinalMatches(/running|up/)`-style check would stay red forever
   regardless of tuning, and the scorecard's noise floor would become the ecosystem's bugs. If you
   catch yourself asserting reality, stop — assert the trajectory instead.
   **What a turn PRODUCED is not a world fact.** `StagesFaithfulFileEdit` reads the file a staged
   `write_file` edited and re-derives the payload from it and the call's own arguments — the
   assertion is that the staged content is that file with that replacement, which is a property of
   the turn's own output, not of whether anything the model said about the world is true. It is also
   the one place a host read belongs in scoring: the harness stages and never confirms
   (invariant #2), so the file still holds the pre-image the edit was resolved against. A payload
   that cannot be verified fails — an unverifiable staged write is what the check exists to catch.
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

### `--diagnose` — the Phase 6 retrieval read (and why no lever was built)

`--diagnose` answers the question Phase 6 hinges on: *where does the with-RAG → oracle gap live, and is it
worth a retriever change?* It is a measurement of **retrieval**, never of the answer. For each with-rag
question it captures the raw cosine top-k (`EvalRetrieval.LastRawHits`, stashed **before** the MinScore
filter so recall is honest), measures each chunk's lexical **gold coverage** (`TextOverlap`, a recall-flavoured
token-overlap with the item's gold passage — diagnosis only, never scoring), and detects which chunks **survived**
the `MaxContextChars` cap (`grounding.Contains(chunk.Text)`). From that it reports **recall@k across all
questions** (the *powered* metric — temp-0 greedy means reps buy no statistical power, so the handful of
accuracy-gap questions can't drive a decision) and buckets the gap into the advisor's three failure modes,
each implying a *different* lever:

1. **(a) gold missed top-k** → recall failure → a BM25+vector hybrid / larger k.
2. **(b) gold in top-k but dropped from context** → top-k / `MaxContextChars` / re-rank.
3. **(c) gold in context, model still wrong** → a model ceiling; **no retrieval lever helps.**

Design rules that keep the read honest (don't regress them):
- **Raw top-k is captured separately from post-cap survivors** — that's the only way (a) and (b) don't blur.
- **Unparsed/timeout with-rag replies are split out as "inconclusive," never bucketed** as a recall miss.
  An unparsed reply is a measurement defect (a model error/timeout), not a retrieval signal. (This bit:
  Q7 ran the generation past even a 900 s ceiling — a real gemma4:12b runaway on one prompt — and would
  otherwise have masqueraded as a recall failure.)
- **The verdict refuses to recommend a lever below `MinActionableGap` (3) attributable questions** — the
  advisor's "you can't drive a lever off two data points." A 1–2 question gap reads "WITHIN NOISE."

**Phase 6 outcome (gemma4:12b, embeddinggemma, `--seed 42`): NO-GO on building a retriever.** recall@5 is
84% — imperfect — yet **3 of the 5 top-k misses and all 3 context-cap drops were answered correctly anyway**,
so the model is robust to imperfect retrieval. The retrieval-attributable, *parsed* accuracy gap is **one
borderline question** (Q31, gold coverage 0.44 vs the 0.5 cut, and its *right doc was retrieved* — an
intra-doc ranking near-miss). Oracle sits ~1 question above with-rag, capping what any retriever could buy.
Building a hybrid here would be the speculative machinery the plan's §8 forbids. **The Phase 6 deliverable
is this diagnosis mode plus the data-backed decision NOT to build** — the lever stays deferred until the
corpus is re-powered (general expansion for statistical power, *not* identifier-fishing: the read found no
lexical-recall failure to justify it). `TextOverlap` + the bucket classifier are unit-tested in
`McqDiagnosisTests`.

## `voice` mode (spoken-reply length) — a THIRD instrument

`voice` mode measures what the other two never look at: **how long the reply is**. It exists because a
spoken surface is paid in duration — speech runs at a fixed rate and cannot be skimmed, so a reply's
length *is* the time a listener spends on it, and a paragraph that reads fine is unbearable aloud.

It asks each question in `VoiceSuite.Cases` **twice on fresh conversations** — once as
`ReplyStyle.Default` and once as `ReplyStyle.Voice` — and reports characters, sentences and
speakable-markup counts for each, plus the totals. Written goes first on purpose: a voice-shaped
answer sitting in the transcript is an example the next turn imitates, and it would flatter the
baseline.

Its design rules:

1. **Length alone is a trap — every case carries a trajectory floor.** The shortest possible reply is
   an empty one, and a reply that got shorter by no longer calling a tool is a fabrication, not a win.
   Each case names tools of which one must be called; the mutating case must also STAGE a confirmation
   and the reply must still say so. A row with a red floor is a regression that looks like an
   improvement. This is invariant #1 kept, not bent: the floor asserts what the turn DID, never
   whether the answer was true about the world.
2. **It reports characters, never seconds.** The chars→seconds rate belongs to the synthesiser and the
   voice, measured on the bot's surface; converting here would be a metric this process did not
   measure. The markup column is the other half of the read — asterisks and bullets a synthesiser
   reads out are a defect on a speaker and nothing at all on a screen.
3. **Non-destructive by the same construction as the routing harness** (invariant #2): `RunAsync`
   only, which stages. There is no path from here to `ConfirmAsync` and there must not be.
4. **Run it with `--shipped-prompts`.** The style is a compiled-in prompt segment, and a stale
   `voice.md`/`preamble.md` in the CLI's prompt dir would measure that instead — the failure mode that
   makes a prompt change read as 0/5.
5. **Numbers move run to run even seeded.** Tool output feeds the context and the host is live (a
   memory reading is different a minute later), so the reduction is a range, not a constant. Read the
   `--transcript` before believing a single figure — the transcript decides, here as everywhere.

## Build / test / run

```bash
dotnet build TheKrystalShip.Llm.slnx                                   # whole solution
dotnet test  TheKrystalShip.Kgsm.Assistant.Eval.Tests/*.csproj         # 30 logic tests, no live deps
# A live ROUTING run needs Ollama + a kgsm host with ≥1 instance:
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- --kgsm ~/tks/kgsm/kgsm.sh --shipped-prompts --transcript
# A live MCQ run needs Ollama only (chat + embedder), no kgsm:
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- mcq --seed 42            # the lift chart
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- mcq --sweep min-score    # tune a knob
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- mcq --diagnose           # recall@k + gap buckets (Phase 6 read)
# A live VOICE run needs the same things a routing run does (Ollama + a kgsm host):
dotnet run --project TheKrystalShip.Kgsm.Assistant.Eval -- voice --shipped-prompts --transcript
```

The unit tests cover the deterministic core (checks, compare math, options, fixture resolution) and
run in CI without a model. A live run is the only thing that exercises the model.

## Gotchas that cost time

- **Never override `XDG_DATA_HOME`** when driving the assistant — kgsm's instance registry lives under
  `~/.local/share/kgsm`, so overriding it hides EVERY instance and the model truthfully says "no
  servers installed". The eval points the conversation store at a throwaway temp DB (so eval turns
  never touch the user's real corpus), but anything you script around it is not immune. This cost a
  full battery once. The preflight aborting on empty is the guardrail.
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
| `Harness.cs` | DI wiring (3 calls + a throwaway temp-DB conversation store), the run loop, scoring aggregation, config resolution. Reads each turn's `ConversationTurnRecord` back from the store (the canonical history) to score the trajectory |
| `Scorecard.cs` / `Transcripts.cs` | the two output renderers (summary table / full conversations) |
| `EvalResult.cs` | the stamped JSON result DTOs + (de)serialization |
| `Compare.cs` | diff two result files into regressions/improvements |
| `EvalOptions.cs` / `Program.cs` | arg parsing + entry point (routing run vs `mcq` vs `compare`, `--filter`) |
| `Mcq*.cs` + `AnswerParser`/`SweepGrid`/`EvalRetrieval`/`NoWebSearch`/`TextOverlap` | the ground-truth MCQ mode (separate harness, flat with the routing files): `McqRunner` (3-condition runner + retrieval-diagnostic capture), `McqCorpus`/`McqItem` (loader + types), `AnswerParser`, `SweepGrid`, `McqScorecard` (lift chart + `RenderDiagnosis`), `McqResult` (DTOs incl. `RetrievalDiagnostic`/`RetrievalBucket`/`RetrievalDiagnosis`), `EvalRetrieval`/`NoWebSearch` (drive the real `SearchAggregator`), `TextOverlap` (gold-coverage for the `--diagnose` read) |
| `mcq/questions.json` + `mcq/corpus/` | the committed ground-truth corpus — hand-authored MCQs + the real-docs snapshot they're drawn from (copied next to the binary) |
| `VoiceSuite.cs` + `VoiceReport.cs` | the `voice` mode: the spoken-style corpus + its trajectory floors, and the length table / transcript renderer. The run loop is `Harness.RunVoiceAsync` |

## Ecosystem rules that apply here

- Work directly on **main** in the `kgsm-*` repos; commit there (don't auto-branch).
- **Never shell out to `kgsm.sh` from C#.** The eval respects this by going *through* the assistant →
  kgsm-lib for everything, including the `is-active` run-state read in fixture resolution. If you need
  more kgsm data, extend a kgsm-lib method / an assistant port — don't add a shell-out here.
- Don't fabricate a metric or status — the same invariant the whole ecosystem holds, and the reason
  invariant #1 exists.
