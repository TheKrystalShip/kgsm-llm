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

## What it measures (and what it deliberately doesn't)

It scores **what the model did** — routing, propose-only staging, clarify-vs-resolve — never a
world fact. A turn that asks "is it up?" is scored on *did it call `get_status`* (routing), not on
*was the answer true*. This is on purpose: kgsm has a run-state bug (a running server can report
`status: false`), so "is it really up?" would stay red no matter how well the model behaves. Scoring
routing/consistency keeps the tuning signal clean and independent of upstream bugs.

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

## Safety

The harness only ever calls `IServerAssistant.RunAsync`, which **stages** destructive operations; it
never calls `ConfirmAsync`, so nothing it runs can start, stop, install, or delete a server. There is
no code path here to execution. A full run is non-destructive.

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
