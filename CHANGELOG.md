# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
