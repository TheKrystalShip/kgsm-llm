# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- A `get_performance` tool that surfaces one running server's LIVE resource usage as a snapshot —
  CPU (as a percentage of one core), memory, network and disk-I/O throughput, on-disk footprint, and
  process count — read from the kgsm-monitor's latest `GET /metrics` frame over its unix socket. It
  emits a structured `PerformanceData` card alongside the model's grounding text (the shared
  `ToolResult<PerformanceData>` envelope): a `live` read carries the measured values and a card; a
  not-running server or an unreachable monitor stays summary-only, worded honestly ("couldn't read",
  never "idle"). An unmeasured axis (`null`) is omitted from the summary rather than shown as 0. The
  capability is additive and fails closed — a fail-closed default reports the monitor unavailable when
  none is wired, so the assistant composes and boots standalone. New `Monitor:SocketPath` config key
  (default `/run/kgsm-monitor/metrics.sock`).
- `get_performance` also answers TREND questions: an optional `range` argument (`1h`/`24h`/`7d`/`30d`)
  switches from the live snapshot to a windowed history read, pulled on demand from the monitor's
  `GET /metrics/history` (the monitor is the single source of truth for metrics history). The card then
  carries the per-metric time series (a chart the surface renders) and the grounding summary states the
  CPU + memory avg/peak over the window (the numbers the model can't read off a chart). An empty window
  is an honest "no history recorded yet", and an unreachable monitor an honest "couldn't read" — neither
  is narrated as idleness.
- Two engine-event-history tools, `get_audit_log` (`instance_name?`, `window?`) and
  `get_change_timeline` (`instance_name?`, `range?`), read directly from the kgsm-monitor's
  `GET /events` over its unix socket — the assistant reads the monitor's engine-event store the same
  way `get_performance` reads its metrics, never through kgsm-api (leaf independence). Both accept an
  OPTIONAL instance (omit for every server on the host) and a window token (`1h`/`24h`/`7d`/`30d`, an
  unrecognized/omitted token honestly falls back to the tool's default — `24h` for the audit log,
  `7d` for the timeline — never an error). `get_audit_log` is the unfiltered "what happened" feed,
  most-recent-first; `get_change_timeline` shares the same source narrowed to durable state changes
  (install/uninstall/update/version-update/backup/port-open/port-close) and excludes routine
  start/stop and player join/leave. Both emit the shared `ToolResult<AuditData>` envelope with a
  deterministic, type-counted grounding summary (e.g. "6 events for factorio-test in the last 24h: 2
  starts, 1 crash, 1 update…") authored by a pure `AuditReport` composer — never the model. An event
  with no recorded actor renders as unknown, never defaulted to a placeholder like "system"; an empty
  window is an honest "no events/changes recorded"; an unreachable monitor is an honest "couldn't
  read", explicitly not a claim that nothing happened. The capability is additive and fails closed —
  a fail-closed `IEventHistory` default reports the monitor unavailable when none is wired, so the
  assistant composes and boots standalone (reuses the existing `Monitor:SocketPath` config key, no
  new configuration).

## [1.3.0] - 2026-07-17

### Added
- The `search` tool now emits a structured `SearchData` card alongside its grounding text, so a
  surface (the web chat) can render the cited passages instead of a bare "searching" pill. The
  aggregator returns the shared `ToolResult<SearchData>` envelope: the model still reads the exact
  same `Summary`; the card carries each passage's provenance (local docs vs web), source
  (doc path / URL), title, snippet, and score, plus which rung answered (`LocalStrong`/`LocalWeak`/
  `Web`). An empty or "couldn't search" result carries no passages and stays summary-only — a card
  always has something to cite (never-fabricate).

## [1.2.0] - 2026-07-17

### Added
- `command.proposed` SSE frames now carry an optional `instanceName` for the `install` verb — the
  custom name a user asked for. `subject.id` is the blueprint for an install, so the name rides its
  own field; a surface that installs via an API endpoint passes it through so a named install lands
  the requested name instead of silently dropping it.

## [1.1.0] - 2026-06-30

### Added
- Initial versioned release.
