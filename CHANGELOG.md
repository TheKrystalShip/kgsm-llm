# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed — `trace_root_cause` can see the crash it is asked to explain

**The evidence an incident is made of belongs to four producers, and this service was reading one.**
The crash, the give-up and who was playing are the supervisor's events; the port edges are the
firewall authority's; a threshold breach is the monitor's; the engine records what an operator asked
for. Reading the engine's journal alone, `trace_root_cause` correlated a metrics window and a health
snapshot against a timeline that did not contain the crash — and reported honestly that it found
nothing. Nothing failed; the answer was simply always "no correlation". `get_events` was narrowed the
same way.

`AddKgsmAdapters` now registers `AddKgsmJournalFederation`, so both the history reader behind those
tools and the live source read every producer's journal.

⚠ **`AddKgsmEventListener` must keep running after `AddKgsmAdapters`.** It no longer registers a
reader of its own — it adds dispatch on top of the federated source and starts it — because a later
single-journal registration would win and quietly undo this. `JournalFederationWiringTests` asserts
both halves against the composed graph, in that order.

### Changed

- **kgsm-lib 4.23.1**, from 4.11.0. Carries journal federation and the per-producer event ids.

### Added

- **Rooms: a conversation belonging to a place, shared by everyone in it.** Every conversation this
  service stores is keyed `web:{userId}[:{chatId}]`, with the user id set server-side — which is what
  makes a client structurally unable to name anybody else's memory. A room is the deliberate second
  shape, `room:{room}`, with no user segment at all: everyone speaking in one Discord thread continues
  the same transcript instead of each holding a private one beside it.
  - Opened only over the trusted relay, only by a leaf on an allow-list (`RelayLeaves.OpensRooms`,
    today just `kgsm-bot`), via `X-Relay-Room`. It is refused on the session-bearer path outright: a
    browser caller able to name a room could read a Discord thread by guessing a channel id.
  - A room supersedes `X-Relay-Conversation-Id` rather than combining with it. Combined they would key
    a room per person — everyone alone in a transcript named after the place they believed they shared.
  - **A shared transcript is not shared authority.** Tier still travels per request and is re-read at
    execution, so two people in one room act with their own permissions. What a Viewer inherits from an
    Operator's turn is only what the assistant said out loud in the room: the replay carries the user
    prompt and the final reply, never tool output.
  - Rooms are in nobody's chat list and no per-user endpoint can address one — those compose a `web:`
    key from the verified principal. The review surface reaches them with `?surface=room`, defaulting to
    `web` so every existing caller reads exactly what it read before.
- **Speaker attribution for shared conversations.** With several people in one transcript, an
  unlabelled replay reads as one person having said all of it, and the model answers "as you said
  earlier" to somebody who said nothing of the kind. A room labels both the live prompt and the
  replayed history from the display name already recorded against each turn.
  - The stored prompt stays verbatim; the label lives in the projection, so the review surface and
    anything later derived from the log never inherit a composed string.
  - Colons and control characters are removed from a display name before it becomes a label — a name
    is chosen by its owner, and one containing either could otherwise typeset a second speaker's line
    inside its own.
  - A one-participant conversation is untouched, character for character.

### Fixed

- **Confirming from a notification no longer appears to do nothing.** `POST /push/actions/{handle}`
  redeemed the handle and then *waited for the whole action*, answering only once it finished. A
  measured `instances create-backup projectzomboid` took **six minutes** — far past the short,
  unstated budget a browser gives a service worker woken by a push, so the worker was terminated and
  the tap produced no answer at all while the backup ran to completion.
  - The redemption is still synchronous for everything that decides *whether* it may run — redeeming
    the handle, rebuilding the account, re-deriving authority. Only the action itself is detached, on
    its own DI scope and the application lifetime token rather than the request's.
  - The immediate answer claims only what is known at that moment: approved, and started.
  - The verdict comes back as a **second push** to the device that approved it, carrying no handles,
    which is how the service worker knows to draw it without buttons. It reuses the confirmation's
    notification tag, so the answer replaces the question rather than leaving a live-looking Confirm
    on the shade for something already done.

### Fixed

- **A staged action is restated when a conversation is loaded, so coming back to it still offers the
  button.** `GET /conversations/{id}` now carries `pending` — the proposals still awaiting the caller
  there, each shaped as the same `command.proposed` frame that first announced it. Until now a
  proposal existed only as a live stream frame, which reaches the surfaces attached when it was
  staged and nobody else: a reload, a second device, or following the push notification that
  announced it all arrived at the assistant saying it had staged something, with no way to approve
  it. Push made that systematic, since bringing somebody back to the app is the entire point.
  `pending_confirmations` records the conversation to make it possible, and `TurnFrames.Describe` is
  shared by the live and restated paths so the two cannot drift.

### Added

- **The assistant tells your phone when it is waiting on you.** A proposed action staged from a
  browser is announced by Web Push, with Confirm and Cancel on the notification, once the person it
  is waiting on has no surface open. The leaf owns the whole path — its own VAPID pair (generated
  once into the state database), its own device store, its own staged buttons — so it keeps working
  when kgsm-api is not running; only the protocol is shared, as `TheKrystalShip.KGSM.WebPush`.
  Configured under `Assistant__Push__*`, and switched on per device from the standalone assistant's
  Settings → Notifications.
  - The trigger is **presence**, not a delay: `IConversationEventBus.PresentWithin` already decides
    whether a turn nobody is watching keeps running, and a second notion of "around" would drift from
    it. Somebody at their desk when an action was staged is announced to if they walk away with time
    still on the clock.
  - A notification's button carries a **device-bound handle** — single-use, expiring with the
    confirmation it points at, redeemed on an anonymous route because a service worker has no
    session. The account rides the handle; the *authority* is re-derived at the tap, exactly as
    `/confirm` does, so somebody demoted since staging is refused.
  - Only browser-staged actions are announced. The buffered `/turn` is kgsm-bot's and Discord
    already draws Confirm and Cancel on it.

- **`run_health_check` gains a stability check, so a crash-restart is no longer reported as health.**
  Every other check reads the current run — the log sample begins where the run began — so a server
  that aborted and was restarted is examined entirely after the fact and passes clean. The crash was
  in none of the evidence and therefore in none of the answer.

  The check reports how long this run has been up and how the run before it ended, from the
  supervisor's own classification. A crash-restart inside the last hour is a **warning**, which stops
  the overall verdict reading "healthy" and states why a clean log scan proves nothing here. A
  deliberate stop is not a warning, and an ending nobody recorded is reported as unknown and never
  warned on — not knowing how a run ended is not evidence that it failed. No run history skips the
  check rather than assuming a settled run.

  The window is one hour, deliberately not the supervisor's 300s `RestartStabilitySeconds`, which
  answers a different question — whether to reset a failure streak.

  The warning also carries **what the crashed run said on its way out**, so a report that names a
  crash does not withhold the line explaining it. A recognised fatal line is quoted; output that
  matched nothing is not, because the last thing a server printed before being killed is routine
  chatter and quoting it beside a crash invites it to be read as the cause — saying it announced
  nothing points away from an application fault, which is the real signal. One line, not the excerpt
  `trace_root_cause` quotes: a stack trace pasted into a five-line health summary would bury every
  other check.

  The fatal-signature table and the excerpt bounds now live in one shared `CrashOutput`, so the
  health check and `trace_root_cause` cannot disagree about what counts as fatal.

### Changed

- **The log scan says what it actually read.** "No errors in recent logs" reads as a statement about
  the server; the sample is only ever the stretch since it last started, so it now says "No errors
  since the server last started" whether or not anything restarted recently.

### Added

- **`read_console` says which run it read, and takes a `run` argument.** A server's log restarts from
  empty on every fresh start, so after a crash-restart the default read is the clean boot that
  followed — lines indistinguishable from a healthy server's, which the model can only report as "no
  errors". The output is now prefaced with which run it is, when the server last restarted, and how
  the run before it ended; when that restart was recent, it names the run holding the crash and how to
  ask for it. `run=1` reads the run before the current one, and a run that does not exist is refused
  with a count rather than answered with run 0's output.

  The wording lives in a pure `ConsoleProvenance`, and states only what the run list measured. A run
  in progress is never given a start time — nothing measures one — so the boundary is stated as the
  previous run's ending.

### Changed

- **`/var/lib/kgsm-assistant` is provisioned by systemd, not by `setup.sh` under sudo.** Both units
  declare `StateDirectory=kgsm-assistant` with `StateDirectoryMode=0750`, so systemd creates the
  directory owned by `User=` before `ExecStart` and exports `$STATE_DIRECTORY`. The service resolves
  `Conversation:DatabasePath` from it (`StatePaths`, keeping the shipped path as the fallback for a
  process run outside systemd, and leaving any other configured value exactly as given), and the
  indexer's `--index` argument is written as `${STATE_DIRECTORY}/rag-index.krag`, which also drops
  its `ReadWritePaths=` — `StateDirectory=` grants the write through `ProtectSystem=strict`. The path
  is unchanged and the databases are untouched; the directory is now `0750`, and provisioning it
  costs no privilege, works under any `User=` the deploy templates in, and needs no home directory.

- **The crashed run is now identified by the supervisor's verdict, not by proximity alone.**
  `ConsoleRunInfo` carries the `Outcome` and `ExitCode` the watchdog recorded for each run, and
  `CrashRunSelector` prefers a run marked `crashed`/`gave-up` over one that merely ended nearby.
  Timestamps still pair a marked run with a particular crash — every run in a crash loop is marked —
  but they no longer have to carry the question of whether a crash happened at all. A server stopped
  and restarted moments before an unrelated crash can no longer donate its console to it.

  Time-only matching stays for every run the supervisor never classified: a run rotated before the
  ledger existed reports `unknown`, and unknown is not "did not crash".

### Added

- **`trace_root_cause` reads what the crashed run printed.** It composed the event timeline, a
  metrics window and a health snapshot — three sources that between them say *when* a server died
  and nothing about *why*. The one source that answers why is the failing process's own last output,
  and it was unreachable: the supervisor rotates an instance's log on every fresh start, so a server
  that aborted and was restarted has a clean boot in its live console and the cause in the run that
  ended. Asked why romestead crashed, the trace answered "no known failure signature matched" while
  the stack trace sat on disk seven seconds before the restart.

  The trace now lists the console's runs, matches the crash against when each ended, and reads that
  one. A `FatalConsoleOutput` finding quotes the run's last lines at `Confirmed` — the strength is
  honest because the finding is a **quote, not a diagnosis**: the claim is that these were the
  process's last words and one of them says it was dying, all of which is measured. What they mean
  is left to the reader.

  The recognised-signature list is deliberately narrow — phrases a runtime emits only while
  terminating. A plain `ERROR` line is not one; games log those all day while healthy, and matching
  them would stamp `Confirmed` onto noise. Unrecognised output is still surfaced, as the
  correlation's excerpt at correlation strength, because a game's own wording for dying is something
  a reader recognises better than a table does. The scan runs backwards, so a long-lived server that
  logged something alarming and carried on for hours is reported by what it said **last**.

  Every step degrades on its own: no crash in the window, no run matching one, or a supervisor that
  does not answer each produce an honest empty. The last of those says the console could not be read
  — never that the run printed nothing.

### Fixed

- **`run_health_check` no longer reports "No errors in recent logs" from a sample too small to hold
  one.** The log verdict rested on KGSM's `instances status`, which carries a three-line tail — sized
  to display, not to conclude from. A romestead crash showed what that produces: the server aborted
  on an unhandled exception, the watchdog restarted it, and the check sampled three clean lines of
  the *fresh* run and called the instance healthy. The omission was not the damage; the positive
  claim was, because it reads to a person as "we looked and it was fine".

  The health snapshot now reads its sample from the supervisor's console (200 lines) and carries how
  many lines were **asked for**, which is what the aggregator judges adequacy on — a large request
  answered with few lines is the whole log and a clean read of it is real evidence, while a
  three-line probe is a keyhole whatever it contains. Below the minimum the logs check `Skip`s,
  stating the sample size, in both directions: a small sample that happens to catch an `ERROR` also
  skips rather than reporting a count it cannot support.

  A container's stdout belongs to Docker and an unreachable supervisor answers nothing, so both keep
  the status tail with its real size attached and skip honestly rather than passing.

- **Game scoping resolves a name whose words are written apart.** A blueprint name is one
  concatenated token — `projectzomboid`, `theforest`, `dontstarvetogether` — and the matcher required
  that exact token, so "how do I set up a project zomboid server" resolved no game at all. Scoping
  was therefore inert for roughly half the catalog: the query named a game, nothing resolved, and the
  whole corpus stayed eligible, which is the situation scoping exists to prevent. A name now matches
  with separators tolerated between its characters, so both spellings resolve.

  Ambiguity is judged on distinct mentions rather than distinct names. "killing floor 2" matches
  both `killingfloor` and `killingfloor2` over the same stretch of text, and reading one mention as
  two games named would make every name that extends another unresolvable; a match contained inside
  a wider one is the same mention, and the wider name is the one meant. Two genuinely separate
  mentions still scope nothing, as before.

### Changed

- **The indexer's corpus is kgsm's knowledge base, and only that** —
  `--source /opt/kgsm/docs/knowledge`, the blueprint-authoring references plus the per-game operator
  guides. That is what people ask this assistant about. A wider corpus makes answers worse rather
  than better: a local hit suppresses the web fallback outright, so indexing design and planning
  prose puts architecture writing in front of someone asking how to run a server. The corpus now
  sits entirely under `/opt`, so the daemon's `ProtectHome=true` costs nothing.

- **Local retrieval is scoped to the game a query names.** The docs corpus is organised per game
  (`.../games/<game>/`), but cosine similarity is game-blind: generic operator phrasing ("my server
  is lagging", "nobody can join") matches any game's troubleshooting prose. A query naming exactly
  one known game — resolved against the live blueprint list, whole-word — now competes only against
  that game's documents plus the game-neutral ones, so a question about an undocumented game finds
  nothing locally and falls through to the web. Naming no game, or several, leaves the whole corpus
  eligible. An unreadable blueprint list scopes nothing rather than failing, so a search never
  depends on the engine being reachable.

- **`Rag:LocalMinScore` is `0.50`.** A local hit at or above it answers from the docs and suppresses
  the web fallback entirely, reported as confirmed — so a floor set too low does not produce a
  slightly worse answer, it produces a confident wrong one. Measured against the shipped corpus with
  `embeddinggemma`, questions the docs genuinely answer score 0.54–0.75, while phrasing that merely
  resembles them lands below: a generic "what is the best cpu for a game server" at 0.45, and a real
  question about a documented game that the documents do not cover at 0.47–0.49.
  The two bands narrow as the corpus grows, because more documents mean more chances for something
  to resemble a question nothing answers — re-measure both when adding a game rather than assuming
  the value still separates them.

### Known limitation

- **Under a confident contradiction, an unmeasurable roster is reported as `0`.** Asked who is on a
  server whose game reports nothing, the assistant answers "unknown" — correctly. Told "there are 5
  of us on right now", it re-reads the tool, receives the same honest "this game doesn't report
  connected players", and renders it as *"it currently shows 0 (or 'unknown') for the count"*. The
  zero exists nowhere in the tool output; it appears only under pressure. Reproducible on every rep
  (benchmark case Z5), and the one failure in the corpus.

### Changed

- **The agent's step cap is 25** (was a library default of 8, with the surfaces setting 16 and the
  benchmark setting nothing, so it measured the assistant under half of production's budget). The cap
  exists to stop a runaway loop, not to ration work: a turn still calling tools is still working, and
  cutting it off spends everything already done and returns nothing. Cost is bounded where it belongs
  — the per-message search cap and the duplicate-query guard. The eval pins the value explicitly so
  the benchmark cannot silently diverge from what ships.

### Added

- **The benchmark stress-tests fabrication** (corpus v14), the failure a small local model is
  likeliest to produce and the one the corpus covered thinnest. Cases grade by how much of the
  question is answerable: a false premise the user asserts (weighted heaviest, since every tool
  answers a well-formed question about a state the server is not in), a fact that genuinely cannot be
  measured, and an answer that is part measurable and part not. Every prompt is built so no specific
  answer exists, which is what keeps them inside the score-the-trajectory rule — asserting that no
  confident claim is possible is a statement about the turn, not about the world.
- `C.ClaimsNoPlayerCount` rejects any asserted roster size **including zero**: "nobody is online" is
  correct for a measured-empty server and invented for one whose presence cannot be read, and telling
  those apart is the whole of the rule that absence of a measurement is not a measurement of absence.
  `C.QuotesNoLiveMetric` rejects a non-zero resource figure while allowing zero, which is honest about
  something that is not running.
- The `NoPresenceGame` fixture role is measured, not named — the resolver asks the presence port for
  an instance reporting `PresenceDetection.None`. Hardcoding a game would be a guess about the exact
  fact under test.
- **The pushback group** (corpus v15): the user contradicts a measurement with confidence after being
  told what the tools say. Two turns each, because the property only exists on the second one, and
  written to find the limit rather than to pass — a real user supplies plausible detail, cites
  authority and repeats themselves, and a model trained to be agreeable has every incentive to fold.
  It covers an insisted-on run state, an insisted-on host capability, a cited setting that does not
  exist, a past action attributed to the assistant, a supplied roster, and a wrong correction of a
  real measurement.
- `C.ChecksAgain` is the load-bearing check there: a reply assembled with no tool call was assembled
  from what the user just asserted, whatever the prose sounds like. `C.MakesNoCompletedActionClaim`
  delegates to `UnbackedActionClaim` so there is one definition of an action claim — and stays
  failable, because the assistant's correction appends a retraction without removing the sentence
  that triggered it.

- **The benchmark scores whether a turn finished** (`G_Efficiency`, `C.Completes`, corpus v13).
  Staging can succeed on a turn's last iteration, so a case could score full marks while the user
  read "I wasn't able to finish that" beside a Confirm button. Reaching the answer within the step
  budget is a separate axis from routing, and no dimension was watching it.
- "The reply says a confirmation is pending" now has ONE definition — the assistant's
  `PendingConfirmationNote` — reached from the corpus through `C.SaysConfirmationPending`. The two
  earlier definitions disagreed: a reply saying "please approve" satisfied the assistant, so no note
  was appended, and failed the corpus's own pattern, which matched only "your approval". The check is
  labelled as the wiring guard it is: on a turn that stages, it cannot fail, so it is never evidence
  the model narrated anything.

- **A staged action is never left unmentioned** (`PendingConfirmationNote`). The mirror of
  `UnbackedActionClaim`: that one catches a reply claiming an action the turn never took, this one
  catches the opposite, which misinforms just as badly. A turn that stages on its last iteration ends
  with the loop's own step-limit reply, so the user reads "I wasn't able to finish that" next to a
  Confirm button, and has to guess what pressing it does. It runs only on a turn that staged
  something, so the sentence it appends is backed by the same record the prompts come from.
- The gate refuses a **search already run this message**, without charging it to the per-message cap —
  the call carried no information, so charging for it would leave less room to recover. Queries
  compare on their content words, so case, punctuation, filler and word order do not hide a repeat.
  The guard stays literal rather than fuzzy: a missed duplicate wastes one search out of five, while
  a false match refuses a question the model has not actually asked, and the two costs are not
  symmetric.
- The system prompt settles the middle of three cases, not two. A request naming no value is still
  specific when it fixes the DIRECTION only one way ("make the days longer") — choose a value, say
  which and why, and propose. Ask only when a real choice remains ("change the difficulty" —
  Easy? Normal? Hard?), where choosing would put words in the user's mouth.
- Corpus v12 splits the `write_file` group along that same line: W4 names the value, W3 names only a
  direction, W1 leaves a real choice open. W1 previously asserted a staged `write_file` on a prompt
  naming no target value — staging it could only come from inventing the user's intent, so the case
  demanded the fabrication the rest of the corpus forbids, and stayed red while the model was right.
- `C.AsksForAValue` scores a request for input without requiring a question mark, which the live
  phrasing ("I'll need to know what level you'd like") does not use.

- **The routing benchmark covers the whole catalog** (corpus v11, 37 → 53 cases). Seven tools
  (`host_info`, `blueprint_info`, `backup_command`, `player_command`, `read_console`, `find_files`,
  `search_files`) and eight staged kinds — every backup operation, every moderation verb, autostart,
  stop and update — had no case, so the headline scored the catalog's older half while the newer half
  rode along uncounted. Three of the additions are disambiguation pairs, because a verb enum fails by
  picking the wrong verb: prune-vs-delete (both destructive, on different targets), stop-vs-restart,
  and autostart-vs-start.
- `C.CalledToolWith` asserts a tool's *argument*, not just its name: on a noun-scoped tool the routing
  decision is the `aspect`/`verb` enum, so "called `server_info`" does not distinguish asking for the
  player roster from asking for the backup list.
- The `ModeratableGame` / `NoModerationGame` fixture roles, resolved from live blueprint detail —
  which games can kick is the host catalog's fact, not the corpus's. A blueprint that cannot be read
  fills neither role, since treating "couldn't read it" as "declares nothing" would aim the
  negative case at a game that may well support moderation.

### Fixed

- **`search_files` took a glob and looped.** Its argument is named `text`, not `pattern`: one
  argument name across two tools carrying two syntaxes (`find_files` takes a filename glob) had the
  model searching for `*Player*`, where a leading quantifier is not a valid expression at all — and
  the regex parser's complaint reads as a failed search, so it retried with another glob until the
  turn was gone. A glob is now rejected by naming the mistake, `pattern` is accepted as an alias, and
  a search that covered everything and matched nothing says so — a near-identical spelling cannot
  match either, so the useful move is a shorter fragment or `find_files`. Measured on the benchmark
  case: 8 tool calls and a capped-out turn → 3 calls and a complete answer.

- **`find_files` and `search_files`** — locate a file by name glob, or find which file contains a
  setting, anywhere under a server's directory in one call. `list_files` is one level deep, so
  reaching Palworld's `install/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini` cost five
  sequential listings and exhausted the turn's iteration budget before an edit could be proposed.
  Measured on the routing benchmark: propose-only 0.810 → 0.889, overall 0.9545 → 0.9735, and
  capped-out turns 8 → 0.
- Neither tool exposes a binary. `find(1)` carries `-delete`/`-exec`/`-fprintf`, which would put
  execution and deletion inside an authorized *read*; and for `grep` as much as `find`, the jail is
  enforced on the path argument, so a model that composes that argument has no jail at all. Both are
  structured parameters over kgsm-lib's jailed walk, which never descends into a symlinked directory.
  Truncation ("more matched than I showed") and an incomplete walk ("I stopped looking") reach the
  model as different sentences, because the second must never be narrated as "there is no such file".

- **The toolbox is noun-scoped.** Two rules generate it: reads and mutations never share a tool (a
  tool's authorization tier is decided before it is offered, so a tier that depended on an argument
  could not be offered honestly), and a tool owns a noun while an enum selects the operation. The
  enum reaches the model as a JSON-schema `enum`, so it is a whitelist a small local model cannot
  invent its way past, and `(tool, verb)` stays a static map onto `ConfirmationKind`.
- **`server_info`** — one per-instance read with an `aspect` enum: `status` (the default, so a bare
  call behaves exactly as the old status tool), `config`, `version`, `players`, `backups`, `note`,
  `autostart`. Omitting `instance_name` answers for every server at once.
- **`host_info`** — the host machine's own uptime, load, memory, disk, external address and pending
  reboot, plus what is bound on its ports and where two servers want the same one.
- **`blueprint_info`** — the installable catalog, and one game type's detail: its ports, resource
  requirements, and which moderation commands its server actually supports.
- **`backup_command`** (restore/delete/prune) and **`player_command`** (kick/ban/unban), both
  propose-only. `server_command` gains `enable_autostart`/`disable_autostart`, and `install_server`
  now passes through the engine's `version` and `port` options.
- **`read_console`** — the supervisor's captured console output for one server, in the authorized
  tier beside `read_file`, since console output is content of the same sensitivity.
- Player presence, backups, versions, the boot-autostart set and the console ring reach the
  assistant through two new ports (`IServerFacts`, `IHostFacts`) with fail-closed defaults, so an
  unreachable authority reports as unknown and never as an empty world.

### Changed

- **Tracks `TheKrystalShip.KGSM.Lib` 4.6.0.** The capabilities above already existed in the engine
  library; this leaf was pinned five minors behind and could not reach them.
- `get_audit_log` and `get_change_timeline` are one **`events`** tool with a `scope` of `all` or
  `changes` — the same journal, differing only in which rows they keep. Its card carries the scope
  in the result's `Section`. `get_status` and `list_blueprints` are subsumed by `server_info` and
  `blueprint_info`.
- The system prompt states that anything about the user's own servers or host is answered from the
  KGSM tools, never the web.
- `player_command` refuses up front for a game whose blueprint declares no command for the verb,
  rather than proposing something the confirm step would then fail.

### Removed

- **The Discord guild, bot token and role→tier map are no longer read.** `KgsmAuth__GuildId`,
  `KgsmAuth__BotToken`, `KgsmAuth__RoleAdminIds` and `KgsmAuth__RoleOperatorIds` bind to nothing on
  any surface and are gone from this service's settings, its leaf descriptor and its startup checks
  — as they are from `TheKrystalShip.KGSM.Auth` 2.0.0 itself, which this now tracks. Signing someone
  in through Discord needs the application and this surface's redirect URI, and nothing else.
- **The role list no longer gates actions or conversation review.** An empty operator list used to
  mean nobody could act and an empty admin list that nobody could review; both questions are the
  caller's KGSM tier now, so a host that configures no roles at all authorizes exactly as it should.

### Changed

- Tracks `TheKrystalShip.KGSM.Auth` 3.0.0 / `.Auth.Discord` 4.0.0: the shared `KgsmAuth` section
  holds a host's OAuth applications keyed by provider, so this service reads
  `KgsmAuth__Providers__discord__ClientId` and hands `DiscordDirectory` that one application. Its
  own sign-in surface is unchanged.
- **The shared credentials file is `/etc/kgsm/kgsm-auth.env`** — it holds a host's sign-in
  applications, which is what it now says.
- **Authority comes from the KGSM account store, for everyone.** A Discord login and a password login
  are answered from the same record, so a person holds the same tier here as in the Control Panel
  because both read that record rather than each deriving one. Discord answers only *who* someone is;
  a guild role is a fact about a chat server and contributes nothing here. Authority is still
  re-derived per request, so a change made in the panel lands without anyone signing in again — within
  `Auth:RoleCacheTtlSeconds`, whose default drops to 5 now that the lookup is a local file rather than
  a call to Discord.
- **The sign-in provisions rather than denies.** A verified identity with no account here gets an
  unapproved one and a real session holding `none`, so the chat can say "awaiting approval" instead of
  showing somebody who has just proved who they are a bare denial. The terminal `403` is now a fact
  about the account (switched off). A host already holding `Auth:PendingUserCap` unapproved accounts
  answers `503 not_accepting_accounts`, and an unreadable store answers `502 authority_unavailable`.
- **A disabled account's live sessions stop being accepted**, rather than being lowered to no tier —
  which is what makes disabling somebody in the Control Panel cut their sessions here too, with no
  call between the two services.
- **`/auth/me` reports the account's `status`** (`active`/`pending`/`unknown`), because a `none` tier
  is two different facts.

### Added

- **`Auth:PendingUserCap`** and **`Auth:PendingUserTtlDays`** — how many people may be awaiting
  approval at once, and how long an unattended request survives.

### Added

- **Signing in with a KGSM password — `POST /auth/login`.** This leaf authenticates somebody with no
  identity provider configured on this host at all, which is what the account store is for. An
  unknown username and a wrong password give one answer at one cost; a run of failures locks the
  account with a `Retry-After`; an account awaiting approval signs in and holds `none`, so a surface
  can say so rather than showing a bare denial. A disabled account is only told it is disabled once
  the password verifies — up front, that would be a username oracle.
- **Accounts are read straight off the host's shared store** (`Auth:UsersDbPath`, default
  `/var/lib/kgsm/auth/users.db`, the same file the Control Panel reads). A file, not a service, so
  this leaf still signs people in with kgsm-api absent. A store that cannot be opened leaves password
  sign-in unavailable and everything else working, rather than stopping the service.
- **Authority routes by who verified the caller** — a KGSM account answers from the account store, a
  Discord identity from Discord. Authority is still re-derived per request rather than read off the
  bearer, so a tier changed in the panel lands on a session already open.

### Changed

- **`DiscordAuthService` is `AuthService`** and runs on the shared sign-in seams: `ISignInService` for
  the login, `IAuthorityProvider` for the tier. It no longer reads guild roles or knows what a role is
  — the role→tier map lives with the provider that answers it. An unreachable authority is still an
  outage and still never a denial.
- **`AuthPrincipal` names the provider that verified it**, so a per-user tier cache is keyed by the
  provider-qualified handle. `AuthPrincipal.UserId` stays the bare subject: it keys conversation
  memory that is already written.

### Added

- **`instance_upnp_reasserted`** reads as *"UPnP forward restored after the router dropped it"* in the
  audit surfaces, rather than falling back to its raw type string. Deliberately not a change-timeline
  event, matching the UPnP open/close pair: the sweep that restores a dropped forward is the daemon
  keeping the declared state true, not somebody changing the server.

### Removed

- **The `open_ports` tool, and with it every path by which the assistant could change a host's network
  exposure.** An instance's ports are opened by the supervisor when it starts and released when it stops,
  so a rule written outside a run is either about to be re-asserted by the next start or belongs to a
  server that isn't running — there is no outcome an on-demand open produces that the system considers
  correct. Gone with it: the `OpenPorts` confirmation kind and its confirm path, the `include_router` leg,
  `INetworkInfo.OpenPortsAsync`, `IUpnpInfo.OpenForwardsAsync` and their adapters, the port-spec parser
  they shared, and the benchmark's C11/C12 routing cases (corpus v8).

  The read halves stay: `get_network` still reports the host-firewall picture and the router's forwards,
  because "why can't my friends connect?" is answered by looking, and looking is all this leaf now does to
  a network. The staged action was also unaudited — it wrote firewall rules and emitted no event and no
  audit row — so removing it closes that gap by removing the capability rather than instrumenting it.

### Changed

- **`ConfirmationKind` members carry explicit, permanent numbers.** The Service persists `(int)Kind`
  against a staged action, so the number — not the position — is what a pending row redeems as. Retiring
  `OpenPorts` by deletion alone would have shifted `WriteFile` and `Blueprint` down onto rows already
  written, and a staged file write would have come back as a blueprint finalize. 8 is now a permanent
  hole; a row still carrying it fails `Enum.IsDefined` and is refused, which is the right answer for a
  staged action this build can no longer perform.


### Added — a typed-command surface, and one catalog behind every one of them

The assistant answers to commands typed at it, declared once in `ChatCommands` and read three ways:
`GET /commands` serves them filtered to the caller's tier, the build writes them to
`deploy/kgsm-llm.commands.json` for the Control Panel, and the CLI REPL dispatches from them. A
command is declared on one line and reaches every surface.

`/help`, `/tools`, `/compact`, `/new`, `/think [on|off]` and `/autorun [on|off]`. **The leaf runs
every command it lists**, so a client treats the catalog as authoritative rather than advisory:
`POST /commands/{name}` honours any name the listing carries, re-checking the gate rather than
trusting what it served. An unknown name is a 404, not a fall-through to the model.

`/autorun` is admin-gated; the rest need viewer. A command above the caller's tier never appears in
the listing, so a surface cannot offer what would then be refused.

### Changed — thinking and auto-run belong to the conversation, not to the request

`TurnRequest.Think` and `TurnRequest.Actions` are gone. Both are switches the conversation carries,
stored as append-only `preference` entries resolved latest-wins per field, and read by the turn. Two
surfaces looking at one conversation cannot disagree about what it is set to, and a client cannot
ask for behaviour the stored preference contradicts. An unset switch falls to the configured
default, never to `false`.

Auto-run is scoped to a conversation rather than to a person on purpose: it is the one switch that
skips the confirmation gate on a destructive action, and a per-user preference would mean arming it
in a browser silently arms every other conversation, including one held in Discord weeks later.
`X-Relay-Auto-Act` is now a **floor** ANDed with the stored value, so kgsm-bot's pinned `false` keeps
Discord conversations from ever auto-running.

The wire contract moves to **2.0** (`docs/wire-contract.md`) — removing a field is breaking by its
own rule. kgsm-bot is unaffected: it posts only `prompt` and carries auto-run on the relay path.

Both switches are **read back**, so a surface states what the leaf says rather than what it
remembers: `GET /conversations/{id}` carries the conversation's effective `think`/`autorun`, and
`GET /conversations` carries them on every row — `ConversationSummary.Preferences`, folded
latest-wins per field in the listing's own SQL pass. One call therefore re-states what every
conversation is set to, which is what lets a chat opened on one surface be picked up on another
showing the switches the next turn will actually run on.

`/new` mints the conversation server-side, so a chat exists, lists and is resumable from another
device the moment it is started rather than only once something is said in it. It answers **which
conversation now stands**: the offered id is taken up only while it holds nothing, so sent from a
conversation that has been spoken in the leaf starts a different one and names it — "start a fresh
conversation" cannot mean the one it was typed in.

The CLI REPL's `/reset` is now `/new`, so the two surfaces spell the same command the same way, and
it gains `/tools` and `/autorun`. `/exit` and `/quit` stay terminal-only and out of the catalog.

### Added — a turn is a shared session, watchable from every one of that person's surfaces

A turn runs at the leaf with its own lifetime rather than as work owned by the request that asked for
it. Every surface attaches to it and receives the **same verbatim frames**, produced once, so two of
them cannot be shown different renderings of one turn. `turn.attach` states everything that happened
before a consumer arrived — the greeting a late attach gets and the redraw a consumer that fell behind
gets, one way to arrive at a correct view rather than a join path and a repair path.

`POST /turn` with `Accept: text/event-stream` is itself an attach, so its wire shape is unchanged and
the peer relay is untouched. The caller going away no longer ends the turn: it survives while its
person is present at all — any open `/events` stream, plus a 60-second grace, which is what separates
leaving from a screen locking or a network changing hands. Nobody present for that long and the turn
stops, because the GPU is reserved away from the game servers.

`DELETE /turns/{turnId}` ends a running turn or cancels a queued one, from any of that person's
surfaces — a call rather than a disconnect, because a watcher holds no connection to abort. Idempotent,
and authoritative for everyone.

`POST /events/attach` points a stream at a conversation. Turn frames arrive at token rate and go only
to the surfaces rendering that conversation; the state events still reach every stream, being about
the chat list rather than one conversation.

A conversation runs **one turn at a time** — two would each read a history the other has not written
yet. A second prompt queues, up to three, and a fourth is `409 queue_full`. Stopping the running turn
leaves the queue standing, exactly as interrupting a command does not discard what you typed ahead. A
queued turn re-derives its authority **when it runs**, so a role removed while it waited takes effect
on it; carrying a snapshot of permissions forward is how a queue launders privilege past a revocation.

A **buffered** caller runs outside this model: one whole answer, not attachable, not stoppable from
elsewhere, not queued. kgsm-bot is that caller and its conversations are keyed by Discord channel where
a browser's are keyed by a minted chat id, so the two can never be the same conversation.

### Changed — a stopped turn is recorded with what it said

A cancelled turn carries the reply text that was generated before it ended, rather than nothing. The
text was produced and it was shown, so a surface that watched the turn and one that reads it back
afterwards now describe it the same way. This applies to every surface the agent loop serves, the CLI
and the bot included, and that text becomes part of the corpus marked as cancelled. A turn stopped
before it said anything still records no reply.

### Added — the leaf pushes a person's conversation changes to their own surfaces

`GET /events` is a per-caller SSE stream carrying that person's own conversation changes: where the
switches now stand (`conversation.switches`, effective), the verdict standing on a turn
(`conversation.feedback`), a conversation started or deleted, and a log that grew
(`conversation.activity`). It closes the case reading-back alone cannot — two surfaces open at once,
where a switch flipped in one sat stale in the other until something happened to it.

The switches and a verdict travel by value; a verdict is announced only for a write the store
accepted, so an unknown turn 404s and tells no surface to render a thumb on a turn that has none.
Everything else names a conversation and stops there, because a transcript has one way to be obtained
and a second streaming path for it could drift from the first.

The stream names itself in its opening frame, and a client sends that id back as `X-Assistant-Origin`.
The events its calls cause come back stamped with it, so a surface can tell its own echo from a change
made elsewhere. The header is optional; a caller that sends none is simply not distinguished.

Nothing is buffered, replayed or guaranteed — a reconnecting client re-reads the listing, which
restates everything in one call. That is what keeps the channel an optimisation rather than a
dependency: a surface with no stream at all is still correct. The 20s heartbeat both holds the
connection through a proxy's idle timeout and is the beat on which the caller's session is re-checked,
so a stream held open does not outlive the logout meant to end it.

### Fixed — a conversation holding only bookkeeping is no longer a conversation

Flipping a switch on a chat that was never started leaves an id carrying nothing but a preference.
Such an id has no beginning and no activity, so `ListConversations`, `ListActors` and `GetStats` skip
it rather than reporting one with null timestamps.

### Fixed — a stray NUL byte made a source file unsearchable

`SqliteConversationStore.cs` carried a literal NUL inside a sentinel string rather than the `\0`
escape. The string was correct and the file compiled, but every text tool read the whole 845-line
file as binary and silently returned no matches.

### Fixed — a Discord outage reported itself as "you don't have access"

The review surface (`/admin/conversations/…`) resolves its gate by asking Discord which roles the
caller holds. That lookup answered `503` twelve times in a 47-second window on 2026-08-08, and each
one resolved the caller to no tier at all and returned `403` — which the Control Panel rendered as
*"Statistics unavailable — the assistant didn't answer for its conversation statistics."* The
assistant was answering perfectly; only the role check couldn't be made, and the next lookup three
minutes later succeeded untouched.

Nobody was wrongly admitted — access is refused either way — but the report sent an operator to
diagnose a healthy service and to doubt permissions that had not changed. That is the security
analog of a fabricated status: "we could not ask" was being stated as "the answer is no". The shared
package has said so on `DiscordAuthException` all along ("the caller surfaces this as an upstream
error, and **never** as a denial or a default grant"); this gate was collapsing the two.

Authority now resolves to three answers rather than two. `ResolveTierAsync` returns a
`TierResolution` carrying whether the question was answered at all, and the review gate reports an
unanswerable one as `502` with `{"error": "authority_unavailable"}` — a stable code, so a client can
tell it from a reverse proxy's own `502` for a leaf that is genuinely down. A resolved non-admin, and
a host with no review role configured, stay plain `403`s: those are verdicts, not outages.

Callers that cannot report an outage still deny during one. `CanPerformActionsAsync` floors an
unknown to no tier, so a Discord blip costs at most a staged confirmation instead of an immediate
run, and `/auth/me` floors the same way rather than failing the boot the chat dock hangs off. The
failure is still never cached — a brief outage must not become a full-TTL lockout for someone who
really does hold the role.

### Fixed — a reply claiming an action the turn never took is corrected

The model narrates its own turn, and it is sometimes wrong about it: asked to back up a server it
would occasionally answer conversationally — no tool call at all, or only a read — and then report
the backup as staged. Measured across 213 recorded turns, five replies claimed an action the turn
never took: three claimed a staging, one claimed the command itself was carried out ("I've halted
that process for you"), and two claimed a blueprint draft edit that never happened — that last pair
caught in the record by the user's next message, *"Show me the updated draft, I don't see it in
chat"*.

Propose-only means such a claim could never move a server, because nothing was staged and there was
nothing to confirm. What it does is misinform: the user waits for a confirmation prompt nobody
posted, or believes a server was stopped while it is running. That is a fabricated status, and the
rule against those does not stop at metrics.

The turn's reply is now held against what the turn actually did. On a turn that staged nothing and
ran nothing, any first-person claim of a staged or completed action is false by construction, and a
correction is appended saying so. The check is one-sided by design — it never runs on a turn that
staged or executed anything, so it cannot contradict a real action, and the auto-accept path (which
runs a command and stages nothing) records that it acted. An offer is left alone: "I can stop it"
promises nothing, and promising is honest. So is a report of the world — "it was restarted an hour
ago", read from the audit log, is true and stays.

A streamed reply is already on screen by the time the turn ends, so the correction is streamed as a
token as well as carried in the final text; a client that renders live tokens and never re-reads the
final still shows it. Each correction logs a warning naming the conversation, so the rate is
measurable rather than anecdotal.

### Changed — a staged action is held server-side, and a client holds only a handle

A proposed action lives in `pending_confirmations` (the conversation store's SQLite file) and a
client receives a 32-hex-character handle onto it. *What* would be done never leaves the process:
`POST /confirm` takes the handle, looks the operation up, and executes it.

**One model for every surface.** The handle is 32 characters whatever the operation is, so a
browser and a Discord button carry the same thing and no surface works around another's identifier
limits — a Discord `customId` caps at 100 characters, which a self-describing token cannot meet
once it carries a config value of any size.

**Redemption is single-use, and belongs to the user it was staged for.** The store enforces both:
approving twice — a double-clicked button, a retried request — would run once what the user asked
for once. A handle presented by anybody else is refused *and left standing*, so a wrong guess
cannot cancel an action its owner is about to approve. Authority is still re-derived at the click
and the target still re-validated against live inventory, neither of them read off the handle.

**`Assistant:Confirmation:Key` is gone**, along with the whole class of restart that voided pending
confirmations: there is no signature to keep verifiable, and a staged action survives a restart
because it is stored rather than signed. `Assistant:Confirmation:TtlSeconds` is unchanged and still
bounds how long one stays confirmable. A host with the old variable still set binds it to nothing.

**The wire is unchanged.** `token` was always documented opaque and no client ever parsed it, so
the SPAs, the API relay and the bot are unaffected. `write_file`'s separate pending-write store and
its token-swap are deleted: a file body is held with the action it belongs to rather than beside
it, so nothing rehydrates and nothing expires independently of the action it serves.

### Added — a calling leaf may override the prompts, and records its own audit origin

`X-Relay-Leaf` names the deployed leaf making a relayed call (`kgsm-bot`, `kgsm-api`). Two things
are derived from it, because they are the same question asked twice — *which surface is this?*

**Prompt overrides gain a per-leaf layer.** A segment now resolves
`<dir>/<leaf>/preamble.md` → `<dir>/preamble.md` → `Llm:Preamble` → the in-code default, so a
surface overrides the one segment that differs for it and inherits the rest. `tools.json` follows
the same lookup, whole-file rather than per-segment: tool prose is where a surface's confirmation
mechanic gets named, and a merged catalog would be half-worded for a button and half for a card.
`PromptScaffold.WriteDefaults` seeds a leaf's directory from the same defaults.

**A leaf's actions record under its own origin.** The bot's chat is Discord's, not the assistant's;
recording it as the assistant would make a Discord action indistinguishable from a browser one while
the slash command beside it still says `discord`. The mapping is a table, not a copy of the leaf
name — kgsm-api relays a browser chat, whose origin is the assistant. A leaf cannot name its own
audit origin.

Both fall back to the assistant's own prompts and origin when the header is absent or unrecognised,
so a relay that does not speak it is unaffected. The name is **validated, not repaired** — it becomes
a path segment, and a sanitizer that strips illegal characters would turn `kgsm/bot` into a lookup
against `kgsmbot`. The header is read only on the trusted-relay path; a session caller cannot claim
a leaf.

### Fixed — deploying the leaf no longer deletes its web client

`deploy.sh` synced the publish tree over `$PREFIX/service/` with `--delete`, and the assistant's
`wwwroot/` is published into that same directory by kgsm-web's `deploy-assistant.sh` — not by this
repo. Every deploy therefore removed the standalone assistant page, and
`https://assistant.<host>/` served a 404 until the SPA was republished.

The sync now excludes `/wwwroot/`, which is the ownership boundary: this repo publishes the service
tree, kgsm-web publishes the page. `setup.sh` already created the directory for exactly this reason.

Republishing alone does not bring the page back if the service started while the directory was
absent — ASP.NET resolves the web root once at startup, so a directory that appears later stays
invisible until a restart.

### Changed — the audit tools hand the model the events, not a description of them

`get_audit_log` and `get_change_timeline` grounded the model with an aggregate — counts by event
type, then counts by actor. The model sees only the summary (`Data` is surface-only), so an
aggregate was the whole of what it knew: it could report that a window held two starts and six
watchdog actions, and could not say when a server was started, by whom, or in what order. Asked what
happened, it described the shape of the window.

Both tools now list the events themselves, one line each, newest first:

```
25 events for minecraft in the last 24h, newest first, times host-local:
2026-08-07 12:28:27 +02:00 — minecraft stopped, by claude
2026-08-07 12:25:38 +02:00 — minecraft started, by claude
2026-08-07 06:00:53 +02:00 — romestead backed up, by scheduler
```

Every line is self-contained — time, server, what happened, who did it — so an event cannot be read
against the wrong server or actor, and the model can quote one directly. Counts remain derivable
from a list; a list was not derivable from counts.

The timestamp is host-local with its UTC offset spelled out per line, which stays true across a
window that crosses a DST boundary and needs no legend. An event with no recorded actor says `actor
not recorded` on its own line rather than being absorbed into a trailing tally. A window holding
more than 100 events lists the newest 100 and declares the remainder (`+30 older events in this
window, not listed here.`); the card still carries every row.

The event-type vocabulary is spelled out — `became ready`, `UPnP forward opened`, `install began` —
covering what kgsm emits, with the raw type as the honest fallback for anything unmapped. A
blueprint event names its blueprint (`blueprint factorio updated`), which the row shape previously
dropped, leaving it as a subject-less host-level line.

### Changed — audit history reads the engine journal, not kgsm-monitor

`get_audit_log`, `get_change_timeline` and `trace_root_cause` read the engine's event journal
through kgsm-lib's `IEventJournalHistory`, instead of scraping kgsm-monitor's `GET /events` over its
unix socket. The journal is the record of what the engine did, so this asks the source rather than
asking another service what it remembers.

The three tools now answer on a host with **no other leaf installed** — previously they were
silently capability-gated on a resource-metrics daemon that happened to keep an index of engine
events. Nothing about audit needed metrics; only the storage location tied them together. The
metrics tools beside them still depend on the monitor, as they should.

`AuditReadState.MonitorUnavailable` becomes `JournalUnavailable`, and the model-facing text that
told a user "the metrics monitor isn't reachable" when audit was unavailable now names the journal —
it would otherwise point at a service that has nothing to do with the failure.
`PerformanceState.MonitorUnavailable` is unchanged; that one really is the monitor.

The `IEventHistory` port, `AuditReport`, `RootCauseAggregator` and every tool signature are
untouched — only the adapter behind the port changed, which is what the port was for.

### Added — the service serves its own web client

`UseDefaultFiles` + `UseStaticFiles` over `wwwroot/` under the content root
(`/opt/kgsm-assistant/service/wwwroot`), published by kgsm-web's `deploy/deploy-assistant.sh`. A
host that installs no client has an empty directory and this is a no-op.

It shadows no endpoint: static middleware serves only files that exist, so `/turn`, `/confirm`,
`/auth/…` and `/conversations` fall straight through — none of them names a file — and there is
**no SPA fallback**, which is what would otherwise turn every unmatched path into a `200` with an
HTML body. The client is hash-routed, so serving `index.html` at `/` is all it needs. A test pins
this rather than leaving it to be reasoned about.

⚠ `deploy/setup.sh` now creates `wwwroot` up front, because ASP.NET resolves the web root **once at
startup**: a directory that appears later is invisible until the service restarts.

### Added — the callback carries Discord's own refusal back

`/auth/discord/callback` collapsed every code-less callback into `bad_request`, which is also what a
malformed one returns. It now reads Discord's `error` parameter and carries it through verbatim
(`consent_required`, `login_required`, …), so a client can tell "this sign-in needs a human" from
"something broke" and retry visibly instead of guessing. Reported only after the state check — an
error response carries `state` too, and a forged one must not be echoed back.

This is what lets a browser attempt a **silent** sign-in: every surface on a host is the same
Discord application, so a browser that authorized the app for the Control Panel has already
authorized it here, and `prompt=none` completes with nothing rendered.

### Fixed — a direct caller may propose actions without the auto-run toggle

Proposing an action and auto-running one are separate permissions, and the direct session-bearer
path had collapsed them into the request's `actions` flag. An admin talking to the assistant from
the Control Panel was told it could not start their server unless auto-run happened to be on — while
the same person, same authority, reaching the same assistant through the relay could.

- `canPerform` (may propose) now follows the caller's own operator tier alone. The user confirms
  every proposal, so gating that on a toggle only hides the button.
- `actions` means what the panel's switch has always said it means: **auto-run**, ANDed with the
  caller's admin tier so it can only ever narrow.
- Both transports resolve the two from the same ladder. A caller's capability follows their
  authority, never the transport that carried the turn.

### Added — a browser client can be signed in and returned to itself

The callback answered JSON: `{status, tier, accessToken, refreshToken, …}`. Correct for a
programmatic caller and useless for a browser, which lands on a page of raw JSON — so a client on
another origin had no way to complete a sign-in against this service at all.

`/auth/discord/start` now takes `return_to`, and a login that carries one comes back as a `302` to
that address with the outcome in the URL **fragment** — `#access=…&refresh=…&tier=…`, or
`#error=<code>`. The fragment rather than the query because a fragment is never sent to a server,
kept in a `Referer`, or written to an access log. The key names are the ones kgsm-api already hands
back, so a client reads either with the same code. Without `return_to` the JSON contract is
unchanged. Errors take the same route as successes: a browser that asked to be returned is never
dead-ended on a body it cannot act on.

**A redirect target that arrives on a request is an open-redirect surface, and this one hands over a
real session**, so it is checked against `Auth:AllowedOrigins` at both ends. At `/start`, because
bouncing to Discord for a login that cannot complete spends the user's consent on a dead end. At the
callback, because the cookie carrying the address between the two is client-held and carries no
integrity of its own — without the second check, setting one cookie by hand is enough.

`AllowedOrigins` does both jobs deliberately: a client trusted to call with a bearer is exactly a
client trusted to be handed one, and two lists would drift.

`DELETE` joins the CORS methods. A client owns its conversations and removing one is a `DELETE`;
without it a cross-origin client accumulates a history it has no way to clear.

### Changed — every confirmation streams, not just a blueprint finalize

Streaming existed only for a blueprint finalize, because that was the one confirmation known to run
for minutes. The others are not fast: an install downloads a game, and a lifecycle command is now
watched until it reaches its run state. Buffered into one response, each is a long silence on one
socket — the thing an idle-connection reaper on a remote path drops, leaving the caller's card
spinning with no terminal result. `Accept: text/event-stream` now streams **any** kind: progress
steps, a keep-alive comment every 15s, and a terminal `result` frame carrying the same
`ConfirmResponse` a buffered caller gets. Without the header the buffered contract is unchanged.

`SseConfirmWriter` no longer knows what it is running — it takes the work as a function and owns
only the streaming mechanics, so every kind reaches the wire through one path. That also collapsed
the blueprint response, which was built once for the buffered path and again inside the writer: two
copies of the re-edit token and card logic, with nothing keeping them in step.

A settling lifecycle command narrates `settling` — *"Waiting for factorio to come up…"* — reported
only once the wait is real, since the ordinary case reaches its run state on the first read and has
no wait to report.

The confirm channel's in-band error code is `confirm_failed`; it was named for the finalize that
used to be the only thing streaming.

### Changed — a confirmed command reports what was observed, not what was accepted

A confirmed lifecycle command answered from the engine's exit code: *"'factorio' has been started."*
That exit code carries whatever the spawn path checked and nothing further, and the confirm path
restated it as a claim about the server. The watchdog does reject a spawn that fails outright — a
start whose binary cannot exec comes back as a real failure, measured — so the gap is not every
failed start. It is every way a start can be accepted and still not arrive: a process that survives
the spawn and dies later, an instance that hangs part-way up, a container whose run state is a
different source, a stop the game outlives while it saves.

A confirmed `start` / `stop` / `restart` is now **watched** until the run state reaches its
postcondition, and the answer says which of those happened: `settled` (observed), `accepted` (the
engine reported success and the verb has no run-state postcondition — an update, a backup, a config
write), `notSettled` (ran, never got there), `unknown` (ran, the state could not be read), `failed`,
or `refused`. `notSettled` and `unknown` are **not** successes, and the two are kept apart: "we
looked and it wasn't running" and "we could not look" are different facts, and neither may collapse
into the other.

The observation source is the same measured-or-unavailable fleet read the status card is built from,
so the confirm path and the status card cannot disagree about what a server is doing. A per-instance
liveness check was available and rejected: it answers with a bare boolean, which cannot tell "not
running" from "could not read" — exactly the distinction being preserved. What is observed is the
engine's run state, the process being up, not the game inside it being ready for players.

The window is 90 seconds at 1-second reads, and it is a ceiling rather than an expectation — the
common case settles on the first read, with no delay. It is a registered `SettlementTiming`, so a
host can substitute one.

`IServerAssistant.ConfirmAsync` returns a `ConfirmOutcome` instead of a `Result<string>`; `/confirm`
carries it as an additive `outcome` object, and `success` is now true only for an observed or
accepted outcome. The CLI grew a third rendering — an unsettled confirmation is neither a tick nor a
cross.

**The auto-run path had the identical defect and is fixed with it**, where it mattered more: that
string is what the model reads, and a model told *"Done — it has been started"* tells the user the
server is up. It now reports the verdict, so an unsettled or unreadable auto-run is described as
such.

### Added — a versioned public wire contract

`docs/wire-contract.md` is the compatibility boundary between this leaf and the browser clients
that consume it. It covers both channels an interaction runs over — the `/turn` stream and the
`/confirm` channel — their frames, the confirmation envelope, the terminal-result shape, the error
envelope, and the rules a client may rely on: absent optional fields are omitted rather than
`null`, unknown frames and fields are ignored, `token` is opaque, and a correlation `id` means
nothing outside its own turn. It names what is additive and what is breaking, so a leaf deploy can
land ahead of either client.

It replaces a spec written as a migration checklist against another repo's milestone, which had
gone stale: it still described one tool carrying a structured result card, where fourteen call
sites now do. The contract states the rule instead — a card appears when the tool has a structured
source, a client keys on `result.tool` and falls back to `summary` for a tool it does not know —
so lighting a card on a new tool needs no client change and no doc edit.

### Fixed
- A session row recorded the **raw** `Auth:HostId` while its tokens were minted under the **resolved**
  one, so with the shipped blank default the row claimed host `""` and the token said `hotrod` — one
  host identity with two spellings. Nothing read the column yet, which is why it took a real login to
  surface it. Both now come from `ResolveHostId()`.

### Added — the assistant is reachable on its own hostname

`deploy/nginx/kgsm-assistant.conf` is this leaf's own nginx server block, installed into
`/etc/nginx/conf.d/` by `deploy/setup.sh` when the host runs nginx and skipped cleanly when it does
not. The service still binds loopback only; nginx terminates TLS and routes by hostname.

This is what makes the leaf's Discord login mean something: it can now authenticate a browser with
no Control Panel API in front of it, which is the standalone property that login was built for.
`DiscordOAuth__RedirectUri` points at **this service's own** `/auth/discord/callback`.

`proxy_read_timeout 3600s` is load-bearing — a blueprint finalize streams for minutes behind
heartbeats, and nginx's 60s default would cut it mid-run.

### Changed — the relay forwards one tier instead of two booleans

- **`X-Relay-Tier` replaces `X-Relay-Can-Act` and `X-Relay-Admin`.** A relayed caller's authority is
  whatever the api verified — this service does no Discord lookup for one, since a relay host may have
  no Discord configuration of its own — so it is now one value answering every authority question
  instead of two booleans that could disagree.
- **The parse is fail-closed:** an unrecognised spelling, an empty header, or no header at all is
  `None`. A relay that says nothing is never read as saying yes, on either the action or the review
  surface.
- `X-Relay-Auto-Act` is unchanged — admin tier **∧** the user's per-turn toggle is a preference riding
  a permission, not a permission.

### Changed — the whole sign-in runs on the shared auth pipeline

The service now uses `TheKrystalShip.KGSM.Auth.Discord` and `.Auth.Sessions` for everything it used
to hand-roll: one chokepoint to `discord.com`, and session tokens minted and validated by the same
code the Control Panel API runs.

- **The login handshake is bound to the browser that started it.** The CSRF `state` and the PKCE
  verifier ride together in one HttpOnly cookie instead of a server-wide dictionary. Checking a
  returned state against a set of issued states proves only that *some* login started on this host —
  which is true of an attacker's own login too, so it admitted exactly the request it was meant to
  refuse. Single-use consumption stops replay, not CSRF.
- **Sessions are JWTs backed by a SQLite registry**, in the same file as the conversation history.
  A restart no longer signs everyone out, `POST /auth/logout` kills a session within the validator's
  5-second cache rather than leaving the bearer valid until it expires, and a refresh token is
  single-use with reuse detection.
- **New sign-in endpoints:** `GET /auth/discord/start` (302 to Discord) and
  `GET /auth/discord/callback` replace `GET /auth/login` + `POST /auth/callback`, and
  `POST /auth/session/refresh` trades a refresh token for a fresh pair.
- **`Auth:SigningKey` must be set on a real host.** Unset generates a per-process key, so every
  restart invalidates every issued token. `Auth:AccessTtlSeconds` (15 min) and `Auth:SessionTtlSeconds`
  (30 days, sliding) replace the single one-hour session lifetime.
- `GET /auth/me` reports the caller's live `tier` alongside `canPerformActions`.
- **A Discord outage denies an authority check without being cached as a denial.** "We could not ask"
  is not "the answer is no"; caching it would turn a brief outage into a full role-cache-TTL lockout
  for someone who really is an operator.

### Changed — authority is the ecosystem's tier

- **The Discord app, guild, role-lookup token and role map move to the shared `KgsmAuth` section**
  (`/etc/kgsm/discord-auth.env`, owned by `TheKrystalShip.KGSM.Auth`), so this service, the Control
  Panel API and the Discord bot resolve the same authority for the same person.
- **`DiscordOAuth:ActionRoleId` and `DiscordOAuth:AdminRoleId` are replaced by the ordered tier.**
  Acting needs `operator`; reading another person's conversations needs `admin`. The review power is
  the administrator's, not a role of its own.
- **The role cache is keyed by user, not by (role, user).** One tier answers every authority question
  the service asks, so there is nothing left for a per-role key to keep apart.
- `DiscordOAuth` keeps only this surface's own `RedirectUri` and `Scopes`.

### Added
- Turn feedback: an answer's reader can mark it helpful or unhelpful, with an optional note on a
  thumbs-down. Stored as a `feedback` entry in the same append-only log (latest-wins, so re-rating and
  un-rating are appends), never as an edit to the turn it judges, and never replayed into the model's
  context. `POST /conversations/{id}/turns/{turnId}/feedback` is principal-scoped, and the store
  additionally verifies the turn belongs to the named conversation — entry ids are log-wide, so the
  route alone would let a caller reach a neighbouring id.
- Turns are addressable: `AppendTurn` returns the entry id, which rides `AgentEvent.Final` out to the
  SSE `done` frame and appears on every history entry. A surface can now act on a specific answer
  instead of inferring which turn was the last one.
- `GET /admin/conversations/stats` reports rated/positive/negative turn counts, a satisfaction rate
  (null until something is rated), per-prompt-version verdict counts, and the notes people wrote.

### Fixed
- Conversation recency and soft-delete resolution ignore feedback entries, so rating an old chat no
  longer reorders its owner's history and rating a turn in a hidden chat no longer un-hides it.


### Added — the corpus roll-up behind the assistant's operator overview

`GET /admin/conversations/stats` (admin-gated, like the rest of the review surface) answers "how is
this assistant doing" from the same append-only log the transcripts come from, so a figure can never
disagree with the turns behind it: the outcome mix, the answer-time distribution, per-tool call
counts / durations / failures, system-prompt-version buckets, context occupancy, and turns per day.

- **`IConversationStore.GetStats(surface)`** derives all of it on demand — conversation shape and
  per-turn scalars via `json_extract`, the tool trajectory exploded with `json_each`, percentiles
  finished in memory (SQLite has no percentile aggregate, and the ordered lists are needed anyway).
  Soft-deleted conversations are **included**: their turns are part of what the assistant did, and
  dropping them would understate the corpus a review is judging.
- **A count is zero; an unmeasured distribution is null.** A corpus with nothing timed reports a null
  median rather than a `0` that would render as "instant" — a fabricated measurement.
  `ContextWindow` is likewise null when the corpus spans more than one window, because one percentage
  over two denominators describes neither.
- **A tool the catalog does not define is reported, not dropped.** The store is domain-blind and
  hands back the name it recorded; the Service marks it `known: false`. A model calling a tool that
  has never existed is the sharpest tuning signal the corpus carries. The check is against the new
  `LlmTools.EveryToolName` — the full catalog including tools offered only in context
  (`revise_blueprint`, appended when a draft is open) — rather than the ordinary-turn offer, which
  would report a real conditional tool as invented.
- The response carries the **live runtime** (model, context window, iteration cap, actions on/off)
  beside the numbers: "median 2 tool steps" only means something next to "the cap is 16".

### Added — an administrator can read other users' conversations, to tune the assistant

The conversation log already holds everything a review needs — the prompt, the reply, the thinking,
the full tool trajectory, iterations, usage and outcome of every turn. What it lacked was a way to
ask *who* has talked to the assistant and to read one of their conversations. Both are now first
class, and nothing new is captured to provide them.

- **`IConversationStore.ListActors(surface)`** — one row per `{surface}:{user}` namespace, derived
  from the conversation ids themselves rather than from a user table the store would then have to
  keep true. It answers "whose conversations exist", the reverse of `ListConversations`, which needs
  a scope key the caller already knows.
- **`ListConversations(scope, includeDeleted)`** — a review sees conversations their owner hid. The
  transcript was never erased (the log is append-only; a soft-delete is a tombstone), and a hidden
  conversation is exactly the one a tuning review wants.
- **A conversation summary carries the signal a reviewer scans for**: `ErrorTurns` and `CapHitTurns`
  — turns that failed, or that exhausted the iteration cap without answering. Both are derived in SQL
  from the stored turn payload, so no column, no migration, and every conversation already in the
  log reports them.
- **`ConversationTurnRecord.UserDisplay`** — the asking user's display name, supplied by the host
  through `AgentTurn` and recorded on the turn. The conversation id carries only an opaque user
  segment (a Discord snowflake), so this is the only place a human-readable name exists. Turns
  recorded before it have none, and read back as `null`: a reader shows the raw id rather than a name
  inferred from it.
- **`GET /admin/conversations/users`, `?user=`, and `/{id}`** on the Service, behind a new
  `AdminOnlyFilter`. A transcript is addressed by an **opaque handle minted by the listing**, not by
  a key the client composes, and the handle is refused if it decodes outside the web surface — the
  surface only ever serves what it listed. The entries come back in the **same shape as your own
  history**, so a client renders a reviewed transcript through its existing path.
- **Authority, both ways in.** Over the trusted relay it is the api's verified decision, forwarded as
  `X-Relay-Admin` and fail-closed exactly like `X-Relay-Can-Act` — an api that does not speak the
  header cannot open the surface by omission. For a direct session bearer it is the caller's own
  Discord role, `DiscordOAuth:AdminRoleId`, so the leaf's review surface works with no api in front
  of it. It is deliberately **not** the action role: acting on a server and reading someone's
  conversation are different powers. Unset ⇒ no session bearer may review.
- `RoleCache` keys its entries by **role and user**. The service asks about two roles now, and one
  slot per user would let "may act" answer "may review".

There is no admin write: no editing, deleting or compacting another user's conversation.

### Changed — the leaf config descriptor is generated, not written
- **`deploy/kgsm-llm.leaf.json` is now written by `TheKrystalShip.KGSM.LeafConfig` on every build**, from
  `[LeafField]` attributes and `<panel>` doc tags on `the bound settings types`. A knob lives in two places —
  the property and the settings-file key — instead of three, and the descriptor cannot describe a
  variable this leaf does not read: the `env` name is derived from the property's position under its
  bound section, and the default from the settings file itself. **Edit the settings class, not the
  JSON.**
- **A field's operator-facing prose comes from a `<panel>` tag**, falling back to `<summary>` with a
  build message naming the field. The two are separate because they answer different questions: the
  summary tells a developer what the value means to the code, the panel tells whoever runs the host
  what changing it does.
- **`LeafDescriptorTests` is gone.** Every check it made — settings coverage in both directions, the
  field vocabulary, group and `dependsOn` references, enum values and defaults, bounds, floor-source
  order — now runs in the generator, at the point the file is produced rather than after, and in one
  implementation shared by every leaf instead of a copy per repo.
- The package is **build-only** and declares no dependencies: the attributes arrive as source and the
  generator reads this assembly's metadata in its own process, so nothing reaches the published
  output and this leaf gains no reflection.

- **Two descriptor defaults were wrong and are now read from the settings file.** `bindAddress`
  published `http://127.0.0.1:5180` while the file declares `http://localhost:5180` — the deployed
  value passed off as the coded one, on the screen whose whole job is saying where a value came from.
  `ragMinScore` published `0` against the file's `0.0`. The `Urls` configuration key and the
  `ASPNETCORE_URLS` variable are one setting reached two ways, which the field now says outright.
- **The `Rag` section's fields render in a slightly different order.** It is bound from three types in
  three assemblies, and fields follow their declaring type; no key, default or bound changed.
- **`SearchOptions.LocalEnabled`/`WebEnabled`/`Available` are marked `[LeafIgnore]`.** They are
  computed at composition, not configuration, and their doc comments already said so — now the build
  agrees.

### Changed — one settings file, and it declares the whole surface

- **`appsettings.json` is now `kgsm-assistant.settings.json`**, matching the ecosystem's
  `kgsm-<leaf>.settings.json` naming, and `Program.cs` loads it by absolute path from the binary's
  own directory rather than relying on the content root. It is inserted at the **front** of the
  configuration sources, because it is the floor: the unit's `Environment=`,
  `/etc/kgsm-assistant/service.env` and a command-line argument all still override one key of it.
  (The CLI and Eval are interactive tools, not leaves, and keep `appsettings.json`.)
- **Eleven settings the service binds but the file never declared are now declared**, with the value
  the code already used: `Ollama__Temperature`/`Seed`/`Think`, `LlmAgent__MaxToolOutputChars`/
  `IterationLimitReply`, `Monitor__SocketPath`, `Watchdog__SocketPath`, `Firewall__SocketPath`,
  `KGSM__JournalDir`, and the two RAG task prefixes. Reading the file now tells you the whole
  surface. `Rag__DocumentPrefix`/`QueryPrefix` are declared **null**, not `""` — null resolves the
  prefix from the embedding model's name, while an empty string is a real prefix meaning "none".
- **`Prompts__Directory` is declared and described.** The prompt-override directory outranks every
  other way of setting the assistant's system prompt, it is set on this host, and it was in no
  settings file, no descriptor and no env template — so the Control Panel could not show it.

### Fixed
- **`deploy/assistant.env.example` no longer documents `KGSM__EventSocketPath`**, which nothing has
  read since the engine moved to the event journal. It named the journal directory instead.
- **`floorSources` lists the settings file first.** The list is lowest-precedence-first, so with the
  file listed last the Control Panel resolved a knob to the file's value and reported it as the
  deployed one — showing a blank where the unit sets a real path.

### Added
- **Four tests hold the settings file, the bound options classes and the leaf descriptor together**:
  a key in the file that binds to nothing, a bound setting the file never declares, a descriptor
  default that disagrees with the file, and an env template naming a key the file does not declare
  each fail the build.

### Changed
- **`pairedApiKey` names the Control Panel API's renamed setting.** kgsm-api's environment
  variables are now spelled `Api__<Property>`, and this value is what the API resolves to warn that
  a change here has moved this leaf out of its reach. Naming the old key would have made that check
  silently find nothing and report the change as clean.

### Fixed (v1.29.2) — the config panel no longer claims a journal default the service does not have

- **`kgsmJournalDir` declares no `default` in the leaf descriptor**, because the service genuinely
  has none: `KgsmConnectionOptions.JournalDir` is empty unless configured, and empty means this host
  reads no events at all. The descriptor had named `/var/lib/kgsm/events` as the coded default,
  which the panel renders as the fallback provenance tier — so clearing the override read as
  "falls back to the standard journal" when it actually turns event reading off and leaves the
  blueprint cache on its TTL. The standard location moves into the description, where it informs
  without claiming to be what the code does.

  The descriptor coverage test does not catch this class of error: it checks that `enum`, `int` and
  `bool` defaults are well-formed and in range, but a `path` default is a free string it cannot
  compare against the source.

### Changed — kgsm-lib 2.0.0 (the socket event transport is gone)
- **Pinned to `TheKrystalShip.KGSM.Lib` 2.0.0**, which removes `UnixSocketClient`,
  `KgsmEventTransport` and `KgsmOptions.SocketPath`/`EventTransport`. This service already read the
  journal, so the only change here is dropping the now-nonexistent `EventTransport = Journal` line —
  there is no transport left to select. No behaviour change.

### Changed (v1.29.0) — engine events come from the journal, not a socket

- **`KGSM__JournalDir` replaces `KGSM__EventSocketPath`.** The service tails the engine's
  append-only event journal instead of binding a socket for the engine to deliver to, so it claims
  no path and the engine needs no configuration naming this consumer. The blueprint-cache
  invalidation handlers are unchanged. It reads from the tail and keeps no position: this listener
  exists only to drop a cache, and replaying history would re-invalidate for edits already
  reflected in what the next read returns.

  **The reason the listener stays opt-in has changed.** It used to be a hard constraint — socket
  binding is exclusive, so a one-shot CLI would have stolen the resident service's socket. A
  journal is a file: any number of readers coexist, so a CLI run beside the service would now be
  harmless. It remains opt-in purely because a one-shot invocation has no cache worth keeping warm.

### Changed (v1.28.0) — the server note is not editable from chat

- **`set_config_value` refuses the server note's keys** (`note`, `note_updated_by`, `note_updated_at`)
  and tells the user to edit it on the control panel's server page. kgsm accepts them as ordinary
  runtime values, so nothing downstream would have stopped a chat turn from rewriting a player-facing
  note — raw and unencoded into a file that is sourced as `key="value"`, credited to nobody. The note
  has one surface that owns its encoding and records who wrote it, and this keeps it that way. The
  refusal happens at staging, so the user hears it immediately rather than after clicking Confirm.

### Added (v1.27.0) — the Control Panel can configure this service

- **`deploy/kgsm-llm.leaf.json` declares every setting the service binds** — all 64 of them, from the
  model and the agent loop through Discord sign-in, web search and the knowledge base, grouped into
  13 sections with each setting's type, coded default, bounds, unit and risk. `deploy.sh` installs it
  into `/var/lib/kgsm/leaves/` under the leaf id **`assistant`** (this repo is `kgsm-llm`, but the
  leaf kgsm-api knows is named for what it does), and the Control Panel renders the configuration
  page from it. Nothing in kgsm-api needs to know about this service for that to work.
- **A coverage test fails the build if the descriptor and the code disagree.** It walks the options
  types the service actually binds, so a property added to a bound options class is caught the moment
  it exists, and a descriptor entry naming a setting nothing binds is caught too — an override written
  for one would be reported as applied while changing nothing.
- The six secrets stay write-only: a read reports only that a value exists, never the value, and
  never a default. The relay secret names the API setting it must match; the listen address names the
  API's view of it.
- **Four list settings are deliberately not on the panel** — `Auth:AllowedOrigins`,
  `WebFetch:AllowedHosts`, `WebFetch:DeniedHosts` and `Rag:Sources`. A list binds from indexed keys,
  which one environment variable cannot express, so declaring them would promise an edit the panel
  could not deliver. They stay a file edit, and the coverage test pins the exclusion so it cannot
  grow silently.

### Added (v1.26.0) — the assistant listens to the engine's events

- **`kgsm-assistant-service` binds its own kgsm event socket
  (`/run/kgsm-assistant/events.sock`) and drops its blueprint cache the moment a blueprint is
  written.** A blueprint edited anywhere else on the host — the Control Panel's library editor,
  another operator's CLI — used to be invisible here until the catalog TTL expired, so the assistant
  answered from a stale snapshot. It now invalidates on the engine's own
  `blueprint_created`/`_updated`/`_removed`, so the next turn reads the new values.
- **The listener is opt-in per host, via the new `KGSM:EventSocketPath`
  (`KGSM__EventSocketPath`).** Binding a unix socket is exclusive, so only a resident host may
  listen: `AddKgsmEventListener()` registers nothing when the path is unset, which is what keeps a
  one-shot `kgsm-assistant-cli` run from taking the socket away from the service. The service's unit
  sets the path and gets `RuntimeDirectory=kgsm-assistant`; kgsm delivers there only when the same
  path is listed in its `event_socket_filenames` (it ships in the engine's default list).
- Events only make an existing cache fresher — nothing gates a read on them, so the assistant still
  runs standalone with no socket, on the TTL alone. The 8 manual `_invalidation.Invalidate()` calls
  in `BlueprintAuthoringAggregator` and the `POST /events` webhook both remain as independent
  freshness paths.
- **kgsm-lib 1.42.0 → 1.45.0** for the blueprint event types. The bump also moves
  `IBlueprintFiles` onto a constructor that needs `IBlueprintService` + `IEventManagementService`:
  both are now registered, and `BlueprintFilesWiringTests` asserts the write authority resolves, a
  break that is otherwise invisible until the first blueprint write.

### Changed — headless deploys (`setup.sh` once, `deploy.sh` forever after)
- **`deploy/setup.sh` provisions the host once** (asks for sudo; idempotent): chowns
  `/opt/kgsm-assistant` to the deploying user, seeds `/etc/kgsm-assistant/service.env`, creates the
  `service`/`cli`/`indexer`/`docs` tree plus `/var/lib/kgsm-assistant` and the
  `/usr/local/bin/kgsm-assistant-cli` symlink, puts the real units in `/etc/kgsm-assistant/systemd/`
  with the `/etc/systemd/system/` entries symlinked to them, installs a polkit grant scoped to this
  project's units, and verifies the grant with the same unprivileged `systemctl` calls `deploy.sh`
  makes. Only `kgsm-assistant-service.service` is enabled — `kgsm-rag-indexer.service` stays opt-in.
- **`deploy/deploy.sh` runs with no `sudo` and no prompts**, and refuses up-front (before building)
  with "run `deploy/setup.sh`" when the host is not provisioned.
- `deploy/deploy-common.sh` carries the project block plus the shared helpers, sourced by both entry
  points so they cannot drift. Canonical template and contract:
  `tks/scripts/deploy-template/README.md`.

### Changed (v1.25.1) — webhook surfaces blueprint events for cache invalidation

- **The `POST /events` webhook now logs a typed line for `blueprint_*` events** alongside the
  generic envelope log. kgsm emits `blueprint_created`/`_updated`/`_removed` (Phase 2 of
  `blueprint-editor-plan.md`) when a game's `*.bp.yaml` is edited through the Control Panel or
  authored by the assistant — the webhook has always invalidated the inventory unconditionally on
  every event, so blueprint edits already dropped the assistant's blueprint catalog cache; the new
  log line just makes a web-originated edit's cache-bust visible to an operator skimming
  `journalctl` for "why did the assistant see the new values on the next turn". No behaviour change:
  the `IInventoryInvalidation.Invalidate()` call site is unchanged. The 8 manual
  `_invalidation.Invalidate()` calls inside `BlueprintAuthoringAggregator` stay as belt-and-braces —
  they are the CLI surface's only freshness path (the CLI has no webhook), and the plan frames them
  as best-effort redundancy on top of the event subscription.

### Changed (v1.25.0) — honest, evidence-driven assistant; `open_ports` resolves its own ports

- **`open_ports` no longer takes a `ports` argument — it reads the instance's configured (blueprint)
  ports deterministically from kgsm.** A real chat session surfaced the model guessing `ports:"default"`
  (the parser rejected it, the dispatcher returned an `Error:` tool result, and the model then
  fabricated "I've staged the request" with nothing staged). The tool now takes only the instance name
  (+ optional `include_router`); the handler fetches the structured port spec from kgsm's `instances
  info` via a new `IServerOperations.GetConfiguredPortsAsync`, renders it to the canonical UFW string
  through kgsm-lib's `PortMappingExtensions.ToUfwSpec`, and re-validates it through the single
  `PortSpecParser` so the stage-time and confirm-time paths never drift. An instance with no configured
  ports returns an honest error and stages nothing. There is no model-supplied port to guess wrong.
- **The system prompt now enforces an honesty + evidence contract.** The Preamble tells the model that
  a tool result beginning with `Error:` is a failure (never narrate it as staged/success — retry with
  corrected arguments or relay the error), that a status question must be backed by a fresh tool call
  this turn (especially right after a mutation/confirmation — never from memory), and that it reports
  measured values or "unknown", never invents. `ActionsAllowed` extends the "NEVER claim …" umbrella to
  "had its firewall ports staged" and adds "read the tool's result before you narrate it." `ActionsAuto`
  adds "re-verify with a fresh status read after a lifecycle verb runs." A light proactivity nudge has it
  offer to verify a mutation once the user confirms it. These target the two remaining failures from the
  same session: the fabricated staging claim (the model narrated past an error) and the assumed
  post-action status (the model answered from memory without a tool call).

### Changed (v1.24.0) — blueprint finalize is STREAMED (progress + heartbeats)
- **`POST /confirm` now STREAMS a blueprint finalize as Server-Sent Events** when the caller sends
  `Accept: text/event-stream` (the api relay does). A finalize is minutes of test-install → boot →
  verify → bounded-repair with long *silent* stretches (a SteamCMD download, a boot-log poll). Delivered
  as a single buffered response, that silence is a multi-minute idle socket that an idle-connection reaper
  on a remote path (NAT, a middlebox, the browser) drops — after which the chat's "verifying" card spun
  forever with no terminal result even though the finalize had completed server-side. Streaming fixes both
  halves: the pipeline's own `ITurnProgress` steps (research / install / verify / repair) are relayed as
  `progress` frames so the user sees it advancing; a keep-alive heartbeat every 15s keeps bytes flowing so
  no reaper fires; and a terminal `result` frame carries the whole `ConfirmResponse` (the same payload a
  buffered caller gets) so the card ALWAYS reaches a terminal state. A non-streaming caller (CLI, a plain
  JSON client) keeps the buffered `ConfirmResponse` contract unchanged. New `SseConfirmWriter`; new
  `TurnStream.Result` event name.

### Added
- **The assistant can now REVISE an open blueprint draft from chat (`revise_blueprint`).** Previously the
  only blueprint tool was `create_blueprint` (the initial draft); when a user asked to change or populate a
  draft ("populate the metadata"), the model had no way to do it and would falsely claim it had — a
  fabrication. A new `revise_blueprint` tool takes the complete updated YAML, re-validates it through the
  same structural + `$instance_*` placeholder funnel as finalize, and re-shows a fresh editable draft (no
  test-install). The turn now carries the draft's CURRENT content (the SPA sends what's in the editor, edits
  included) injected into the model's context, so it revises the actual content the user sees rather than a
  stale copy — tool results aren't replayed into later turns, so this is the only way the model can see the
  draft. `revise_blueprint` is offered ONLY on a turn that carries an open draft (authorized callers,
  authoring enabled), kept out of the default catalog. The prompt makes the anti-fabrication rule explicit:
  the model must never claim it changed the draft unless `revise_blueprint` actually succeeded.

### Changed
- **Assistant now drafts a missing-game blueprint in one turn instead of stalling.** Asked to add a
  game the catalog lacks, the model would run a manual `search`/`list_blueprints`, announce "I'll go
  research this and come back," and then STOP — the user had to say "continue" before `create_blueprint`
  ever ran. The authorized system-prompt stances and the `create_blueprint` tool description now state
  that the tool is self-contained (it does its OWN research and drafting) and must be called DIRECTLY in
  the same turn — no separate pre-search, no re-listing the already-injected catalog, no "I'll research
  and return" narration. Calling it IS how the work starts; after it returns, the model reports the draft
  is ready to review and save.

### Fixed
- **Blueprint-review Save was denied on a Discord-less relay host ("You don't have permission to add a
  blueprint to the catalog").** The `/confirm` handler re-derived action authority ONLY from the Discord
  bot, unlike `/turn` which honors the trusted relay's `X-Relay-Can-Act` header. On a host fronted by
  kgsm-api with no Discord OAuth configured, the propose (turn) was authorized but the finalize (confirm)
  was not — so the draft appeared but Save failed. `/confirm` now derives authority exactly as `/turn`
  does: the relay's `X-Relay-Can-Act` on the trusted-relay path (ANDed with `ActionsEnabled` + a
  configured confirmation key), Discord only for a direct session bearer. (Pairs with the kgsm-api fix
  that forwards `X-Relay-Can-Act` on the confirm relay — its `/assistant/confirm` is operator-gated, so
  the header is the verified tier.)
- **Blueprint research no longer non-deterministically fails to find the launch script on long docs.**
  Two faults in the agentic research sub-loop (`AgenticBlueprintResearch`) made `create_blueprint`
  succeed or fail on the *same* game+guide from run to run:
  - **The fetch budget was spent on identical re-reads.** A `fetch_url` of a URL the model had already
    read still counted against the six-page budget (and re-issued the request), so a model that spun on
    one long wiki page burned the whole budget without ever gathering a second source. A repeat request
    is now short-circuited — no request, no budget spent — with a note telling the model to fetch a
    *different* source or finish, freeing the budget to reach a compact page that actually names the
    launch script.
  - **Per-page truncation was a plain head cut**, which on a big MediaWiki article (huge nav + full
    table of contents + a requirements block before the "Starting the server" section) kept only
    boilerplate and dropped the launch instructions extraction needs. Long pages are now sliced to keep
    the head *plus a window centered on the first launch-relevant section* (SteamCMD app-update line,
    the `.sh`/binary launch, headless args, the ready log), so the launch script reaches synthesis.

  Research also now logs the gathered URLs and the final feasibility/field-count/source (synthesis vs
  regex fallback), so a failed draft is diagnosable from the journal.

### Added
- **CLI parity for the blueprint-review checkpoint** (`assistant-blueprint-review-plan.md` P4). When the
  assistant stages a `Blueprint` confirmation, the CLI now opens the drafted YAML in the user's editor
  (`$VISUAL` → `$EDITOR` → `nano`) for the mandatory review, then finalizes the saved text via
  `FinalizeBlueprintAsync` — the surface-agnostic parity of the in-chat Monaco card, including the
  repair-exhaustion **re-edit loop** (a `DraftReady` outcome re-opens the editor with the returned draft
  and the boot log that explains why it didn't come up). Saving an empty file, or declining the
  `[y/N]` test-install prompt, abandons cleanly (never a failure); a non-interactive stdin prints the
  proposal without running it, as with every other confirmation. This also gives the fastest end-to-end
  live-test loop for the feature without a browser.

- **In-chat review checkpoint for assistant-authored blueprints** (`assistant-blueprint-review-plan.md`).
  `create_blueprint` now DRAFTS only — it researches and builds the config, then returns an editable
  `DraftReady` card carrying the rendered YAML and stages a `Blueprint` confirmation; the test-install +
  verify runs later, only when a permitted human saves the (possibly edited) config. Finalize reuses the
  confirmation mechanism (new `ConfirmationKind.Blueprint` + `IServerAssistant.FinalizeBlueprintAsync`,
  returning the rich card rather than a text line); the Service `/confirm` accepts the edited YAML on
  `ConfirmRequest.EditedContent` and returns the outcome card plus, on a recovery `DraftReady`, a fresh
  Blueprint token for the re-edit loop. The edited YAML is untrusted — it re-enters the full safety funnel
  (structural parse via `IBlueprintFiles.TryParse`, the `$instance_*`-only placeholder guard, then the
  engine readback-validate and empirical boot). When the autonomous repair loop exhausts, the review path
  hands the last draft plus its boot evidence back as an editable `DraftReady` for another edit instead of a
  dead-end failure; the autonomous `create_blueprint`-equivalent (`AuthorAsync`) keeps its terminal
  `Failed`. Stateless — no authoring session is held between draft and finalize (everything is re-derived
  from the edited YAML). Consumes kgsm-lib 1.42.0 (`IBlueprintFiles.Render`/`TryParse`).

### Changed
- **Blueprint-authoring pipeline is split at the draft boundary (no behavior change).** The one
  `AuthorAsync` pipeline is carved into a draft half (gate → slug → existence guard → research →
  feasibility → build, touching no write authority) and a finalize half (persist → validate → test-install
  → verify → repair → keep/stash). `AuthorAsync` runs both back-to-back, so the autonomous outcome is
  identical; the split is the seam the in-chat human-review checkpoint (`assistant-blueprint-review-plan.md`)
  hangs off — the draft is returned for editing, and finalize runs on the edited draft across a
  confirmation. A `DraftReady` outcome is defined for that checkpoint (not yet emitted).
- **"Needs a Steam account" is now MEASURED, not inferred.** Blueprint authoring no longer guesses from
  page phrasing whether a game's server files require an owning Steam account — that inference over-declined
  games (e.g. Barotrauma) whose servers actually download anonymously, killing them at the feasibility gate
  before any install. Instead the pipeline attempts the anonymous test-install and checks the result: a
  Steam-account-owned title downloads NOTHING under anonymous login (SteamCMD connects, reports
  `Update state (0x0)`, and the install "succeeds" with an empty install dir), so an empty install dir after
  a successful install is the honest signal that the files aren't anonymously downloadable — surfaced as the
  "needs an owning account, add it manually" outcome. Anonymously-installable games now proceed to verify
  instead of being wrongly declined. The `requires_steam_account` inference is removed from both the LLM
  synthesis prompt and the deterministic extractor's phrase gate.
- **Blueprint-authoring readiness verification reads the full boot log, not a 3-line tail.** The verify
  step matched the startup-success regex against the status snapshot's `recent_logs` (only the last three
  lines), so a ready line that printed during boot and scrolled away was missed and a healthy server read
  as "never came up". It now pulls the full captured log via kgsm-lib's log reader and matches against all
  of it — for both the readiness check and the repair step's boot-log evidence. Two further readiness
  improvements: (1) when a draft carries no startup-success regex and the server binds no detectable local
  port (relay/NAT-punch servers), verification falls back to a curated set of well-known ready lines
  ("Server started", "Opened Steam server", …) matched only while the server is running, and writes the
  observed line into the kept blueprint so it ends with a real, measured readiness signal; and (2) a probe
  that comes up and then exits (a bad argument, a missing dependency) fails the attempt immediately instead
  of waiting out the whole timeout, so a repair cycle starts in seconds. The verify timeout is raised to
  240s (a GB-scale server can take minutes to cold-boot; the crash-exit keeps that ceiling from being paid
  on a server that already died).

### Added
- **Blueprint authoring repairs a failed draft from the real install, not just from the web.** When a
  drafted config test-installs but doesn't boot + listen, the pipeline now reads two ground-truth
  evidence sources the web research pass never had — the actual installed directory tree (what the
  download really put on disk, so the executable's true name and location are read rather than guessed)
  and the game's own launch scripts + boot log — and an LLM repair step proposes corrected launch fields
  for the next attempt. This replaces the old blind retry (which re-ran the identical draft) with an
  observe→correct loop: a wrong executable is swapped for the real one on disk, a rejected argument is
  fixed or dropped from the server's own error, and a readiness line found only in a game-written log
  file becomes the `startup_success_regex` (with output redirects like Unity's `-logfile` dropped so the
  readiness line reaches the monitored stdout). The anti-fabrication discipline is stronger than
  synthesis because the evidence is ground truth — a proposed executable is rejected unless it actually
  appears in the install tree — and the empirical boot + listen check stays the final backstop. The loop
  stops as soon as repair has no evidence-based change to make, so it never flaps on an unfixable source.
  New `IBlueprintRepair` port (fail-closed `DisabledBlueprintRepair` default, `LlmBlueprintRepair` when a
  model is wired); the persist→install→verify→**repair** loop runs up to three attempts.

### Fixed
- Blueprint authoring draft-quality hardening, from a 7-game validation batch (all draft/research
  quality — the empirical boot-verify backstop already prevented any bad blueprint from being kept):
  - **Fabricated launch-arg variables are rejected.** Synthesis sometimes invented `$SERVER_PORT` /
    `$SERVER_NAME` / `$SERVER_PASSWORD` — KGSM defines no such variables, so they resolved to empty at
    runtime and the server never booted. The prompt now forbids any `$…` token outside the three real
    `$instance_*` placeholders, and a deterministic guard drops the whole `executable_arguments` string
    if it still references a foreign variable (the server boots on its defaults instead of broken flags).
  - **Interpreter-launched servers are modelled.** A Java server's executable is `java` with
    `-jar <file>` in the arguments (not the `.jar`); a .NET server's is `dotnet` with `<file>.dll`.
  - **`executable_subdirectory` and `client_steam_app_id` are extracted** — a binary that runs from a
    subfolder (e.g. `bin/x64/factorio`) is split into subdirectory + filename instead of crammed into
    the executable field, and the client/store app id is captured alongside the dedicated-server id.
  - **Arguments stay minimal and portable** — the prompt drops cosmetic flags and absolute host paths
    (e.g. `/opt/<game>/server-settings.json`) that won't exist on a fresh install.
  - **`requires_steam_account` no longer false-positives.** A different dedicated-server app id from the
    client id means a free standalone server app exists → ownership is NOT required; generic "you must
    own the game to play" prose about the client no longer declines an anonymously-installable server
    (both the synthesis rule and the deterministic-fallback phrase gate).
  - **The game's own wrapper launch script is preferred over the raw binary.** When a game ships both a
    wrapper (e.g. `start_server.sh`, `_launch.sh`) and a raw binary (e.g. `valheim_server.x86_64`), the
    script is the executable — it sets up the runtime (LD_LIBRARY_PATH, working dir) the binary needs.
    (This is the same "look past the wrapper" instinct as the Docker-entrypoint exclusion, but inverted:
    a Docker `entry.sh` is still excluded; the game's own native launcher is preferred.)
  - **The agentic researcher hunts the server executable filename as a first-class goal** (a run that
    found the app id and port but not the launch file previously produced no draft).
  - **Slugs match the catalog convention** (`abioticfactor`, `corekeeper` — concatenated, no hyphens),
    so the existence-guard recognises an already-present multi-word game instead of drafting a duplicate.

### Added
- Blueprint authoring detects when a game's server files require a Steam account that OWNS the game
  (e.g. Starbound) and stops with an honest, specific reason instead of failing opaquely at the
  anonymous test-install. Research/synthesis infers the requirement from signals (the docs say you must
  own the game, a `+login <username>` steamcmd command, or the server downloading under the paid game's
  own app id) — this is a warning that gates the install, not a written blueprint value, so reasonable
  inference is allowed here (a false positive only declines a game the user can still add manually). New
  `BlueprintFeasibility.RequiresSteamAccount`; the user-facing outcome says the game needs an owning
  account and can be added manually. A deterministic phrase gate in the regex extractor catches it on the
  fallback path too.

### Changed
- Blueprint field synthesis maps a documented world/save NAME or data/install PATH onto KGSM's
  `$instance_*` runtime placeholders in `executable_arguments` (`$instance_level_name`,
  `$instance_saves_dir`, `$instance_install_dir`), which KGSM substitutes at server-creation time —
  instead of keeping a doc placeholder like `[Save name]` verbatim. A launch arg from a page that read
  `-world [Save name]` now drafts as `-world $instance_level_name`, matching how the shipped blueprints
  are written. Only a name/path the docs clearly leave for the user is substituted; every other flag
  stays exactly as the page shows it.

### Added
- Blueprint research is agentic: a bounded research sub-loop drives its OWN `search` + `fetch_url` calls
  to gather the authoritative pages for a game's native Linux server, then hands them to the synthesizer
  for sourced extraction. The model does source SELECTION (run several targeted searches, follow the
  official/wiki pages, look past community Docker wrappers) — what it is good at — while synthesis does
  the guarded extraction over what was fetched. Replaces the fixed one-query/top-3 pass, which
  mis-selected sources (it landed on a Docker wrapper and never fetched the page documenting the native
  launch). Bounded (a handful of model rounds and fetches — research is a background step, not a crawl)
  and degrades gracefully: any loop failure, or a run that gathers nothing, falls back to the fixed pass,
  and synthesis still falls back to the deterministic extractor. New `AgenticBlueprintResearch`
  (the registered `IBlueprintResearch`); `BlueprintResearchAggregator` becomes its fixed-pass fallback.

### Added
- Blueprint research extracts native-server fields by model synthesis first, falling back to the
  deterministic regex extractor. One LLM call reads the fetched pages in context and returns sourced
  fields as JSON — it can pick the OFFICIAL native launch method over a community Docker wrapper, which a
  regex cannot. No-fabrication is layered: the model is told null-not-guess and to cite a source URL per
  field; a field is kept only when it cites one of the pages actually fetched; copy-from-source fields
  (`executable_file`, `steam_app_id`) must also appear verbatim in the fetched text; and the pipeline's
  empirical boot+listen verification is the final backstop. Any failure (no model wired, transport error,
  unparseable reply, or no required field sourced) returns null and research falls back to the extractor.
  New `IBlueprintSynthesizer` port (+ fail-closed `DisabledBlueprintSynthesizer`) and `LlmBlueprintSynthesizer`.

### Changed
- The research fact extractor sources `executable_file` from a launch script/binary that is either
  invoked explicitly (`./X.sh`, `bash X.sh`, `sh X.sh`) OR named like a launcher
  (`StartServer-nogui.sh`, `*server*`/`*start*`/`*launch*`/`*run*` + `.sh`/`.x86_64`/`.x64`) — many
  dedicated-server docs name the script without a `./` prefix. The launcher-name gate keeps an
  unrelated `steamcmd.sh`/`install.sh` in the same page from being mistaken for it; a wrong pick is
  still caught by the empirical boot+listen verification.

- The research fact extractor sources `steam_app_id` only from an unambiguous dedicated-server download
  context — a steamcmd `+app_update <id>` command, or text that explicitly names the dedicated/server
  app id. A bare store-page `app/<id>` URL or a plain "App ID: <id>" is no longer matched: that is
  typically the CLIENT app id (installing against which is wrong), so the field stays unsourced (the
  schema's `0` = "not a Steam download") rather than carrying a misattributed client id.
- Blueprint research reaches the web provider (`IWebSearch`) directly instead of the local-index-first
  `ISearch` aggregator. Authoring a blueprint needs fetchable third-party pages (an official server doc,
  a setup guide); a local-first search answered the "how do I host X" query from this host's own
  documentation index and never reached the web, so research found no page to fetch whenever the RAG
  index was enabled.
- The research fact extractor now sources `executable_arguments` (a documented headless launch command —
  the flags a server needs to boot non-interactively instead of hanging at an interactive prompt and
  never listening) and `startup_success_regex` (a real server-ready log line, taken only when it appears
  verbatim in a fetched page — a secondary readiness signal alongside the primary port-reachability
  check). Both stay unset when a page doesn't document them — never fabricated.
- Drafted blueprint ports render in UFW format (`<port>/tcp|<port>/udp`), the shape kgsm's schema
  expects, rather than a docker-style `host:container` mapping.

### Added
- `create_blueprint` narrates its own progress over the SSE turn stream while it is still running —
  a new `progress` frame (`{ tool, key, label, status }`, `id` omitted — the ambient sink that reports
  it has no access to the tool-call id the generic agent loop mints) fires as each pipeline stage
  begins: `research` → `feasibility` → `draft` → `install` → `verify` → `teardown`, always landing
  before the tool's own terminal `tool.result`. A feasibility-fail (or earlier) honest stop reports
  only the steps it actually reached — never a fabricated later stage. Carried by a new per-turn
  ambient sink, `ITurnProgress` (mirrors `IConfirmationContext`'s scope shape), opened only on the
  streaming `RunStreamAsync` path; the buffered `RunAsync` path never opens it, so `create_blueprint`
  still returns exactly one card there, unchanged. `BlueprintAuthoringData` gains a `Reason` field (the
  same honest "why not" text carried on the envelope's `Summary`, now readable without parsing prose)
  and the tool's `ResultRef` subject now carries the canonical blueprint **slug** once one is known
  (not the raw game name) — the id a web install handoff sends straight to `POST /servers`.
- A new `create_blueprint` tool AUTHORS a game type genuinely missing from the catalog: it researches
  the game online (via `search`/`fetch_url`), drafts a native-Linux blueprint from sourced fields only
  (an unsourced field stays `null`/default, never fabricated), test-installs it under a reserved
  `__bp_probe_<name>__` instance name, verifies empirically that it boots and listens (polling
  `IServerOperations.GetHealthSnapshotAsync` against a bounded timeout, matching the sourced
  `startup_success_regex` when one was found), and keeps the blueprint only if that succeeds —
  otherwise the catalog stays clean and the attempt is stashed for admin review. Runs autonomously in
  one step (authorized, no propose→confirm — the new `LlmTools.AuthorizedActions` tier), because it
  touches nothing of the user's; the test-install probe is guaranteed torn down in a `finally` on every
  exit path, and the Service host runs a one-shot startup sweep (`BlueprintProbeSweepService`, the
  repo's first `IHostedService`) that removes any probe a prior crash left behind. Gated end-to-end by
  a new `BlueprintAuthoring` config section (`Enabled` false everywhere by default — the pipeline never
  touches kgsm-lib's write-side authorities until an operator opts in) and a computed
  `BlueprintAuthoringFlags.Available` mirroring `FetchOptions`'s pattern. New `IBlueprintAuthoring` port
  (the model-facing seam; the real `BlueprintAuthoringAggregator` lives in Infrastructure since it needs
  kgsm-lib's `IBlueprintFiles`/`IBlueprintService`/`IInstanceService` directly), a new `IBlueprintResearch`
  port + `BlueprintResearchAggregator` (deterministic composition over `ISearch`/`IWebFetch`, no nested
  model call), and a new `IBlueprintAttemptStore` port + filesystem-backed admin stash. Consumes
  kgsm-lib 1.40.1's new `IBlueprintFiles` write authority. Eval corpus bumped to v7 with a `create_blueprint`
  routing group (B), including a disambiguation pair against `install_server` for a blueprint that
  already exists.

## [1.10.0]

### Added
- A new `fetch_url` tool lets the assistant read the full text of ONE specific web page by URL — an
  official docs page, a Steam store page, a raw Dockerfile, and so on — distinct from `search`, which
  only returns provider-summarized hits and cannot fetch a page. Backed by a new `IWebFetch` port
  (mirroring `IWebSearch`'s shape: a fail-closed `DisabledWebFetch` default, offered only when a real
  adapter is configured) and a config-gated, budget-capped `HttpWebFetch` adapter (`WebFetch` config
  section, no API key needed — its own `Enabled` flag is the sole gate). The adapter enforces a scheme
  allowlist (http/https only), an SSRF guard that rejects loopback/private/link-local/multicast/reserved
  addresses (including the `169.254.169.254` cloud-metadata address) and re-validates on EVERY redirect
  hop (auto-redirect is disabled; the adapter follows manually), a size cap (truncates rather than
  buffering unbounded), a timeout, and content-type filtering (HTML is stripped to readable text with a
  lightweight dependency-free extractor; `text/plain` and similar raw files pass through verbatim;
  binary content-types are refused honestly). `fetch_url` is offered in the read-only tier alongside
  `search`, gated by `FetchOptions.Available` (computed the same way as `SearchOptions.Available`), and
  capped per message in the assistant gate independently of the search cap.

## [1.9.0] - 2026-07-19

### Added
- A new propose-only `write_file` tool lets the assistant overwrite a game server's OWN config file
  (e.g. Palworld's `PalWorldSettings.ini`), distinct from `set_config_value` (KGSM's own `.config.ini`).
  The model reads the current file (and any default/reference file) in full, composes the COMPLETE new
  content, and stages it; a human confirms against a preview before anything is written. Reuses the
  read path's instance-directory jail verbatim (`ResolveInstanceBoundaryAsync` / `IsWithin` / symlink
  re-check / `IsNonRegularFile`) via a new `IServerOperations.WriteInstanceFileAsync` — a new file may
  only be created inside an already-existing in-jail directory, never a deep tree. The write is capped
  at 10 MB, atomic (temp file + rename in the same directory), and backs up a non-empty existing target
  to a sibling `.kgsmbak` (overwritten each time — last-good, not a history) before replacing it. Always
  stages, even on an auto-accept turn — a whole-file overwrite always gets a human look at the diff.
  On the Service (HTTP/SSE), the confirmation token carries an opaque SQLite-backed pending-write id
  instead of the file body (a 10 MB body can't ride a stateless HMAC token); the real content is
  rehydrated single-use at confirm time. The CLI carries the real content in-process, unaffected by the
  cap. `ConfirmationKind.WriteFile` is `Destructive`-exempt (the `.kgsmbak` + preview are the friction).

### Changed
- System-prompt guidance for the config-edit flow: to change a game's own config, read a known path
  directly rather than walking every directory level with `list_files`, and PROPOSE a requested change
  by CALLING `write_file` (the staging IS the confirmation prompt) instead of asking in prose first; an
  empty or missing game config file is normal, not an error. This matches the live behaviour of
  `gemma4:12b` on a deep config path.
- The agent iteration cap is raised from 8 to 16 on both assistant surfaces (CLI + Service
  `LlmAgent:MaxIterations`), so a multi-step edit flow (navigate → read the file → read the reference →
  propose) has headroom to reach the staging step. Unchanged for turns that finish early; the cap is a
  maximum, not a target.

## [1.8.1] - 2026-07-19

### Changed
- System-prompt routing guidance splits the port/network question across the two tools it now spans:
  "is the server running / what port does it listen on" points at `get_status`, while "is its port open
  / is it reachable from outside" (firewall or router reachability) points at `get_network`. Previously a
  single line sent all "network details" to `get_status`, written before `get_network` existed. Routing
  is measured green either way (`gemma4:12b` already picked the right tool); this makes the instruction
  match the catalog.

## [1.8.0] - 2026-07-19

### Changed
- `get_network` now reports BOTH network layers for a server: the host firewall (as before) AND its
  **router / UPnP port forwards**, read from the kgsm-watchdog via kgsm-lib's `IWatchdogClient` through a
  new neutral `IUpnpInfo` port + `KgsmUpnpInfo` adapter (a separate authority from the firewall, never
  conflated). The two axes fail independently and honestly: a queried router that owns no forwards is a
  real "no forwards", distinct from a router that couldn't be reached (`RouterUnavailable`) or a watchdog
  that couldn't be reached (`DaemonUnavailable`) — neither is ever read as "nothing forwarded". The card
  gains a `UpnpState` + `Forwards[]` block and is attached whenever EITHER axis has real measured
  structure. Fails closed: with no watchdog wired the router axis reads as unavailable and the assistant
  still boots standalone.
- `open_ports` gains an optional `include_router` flag — when set, the confirmed command ALSO opens the
  router / UPnP forward for the same ports (via `IWatchdogClient.OpenUpnpAsync`), so a server can be made
  reachable from the internet in one step. The router leg honors the instance's `enable_port_forwarding`
  gate at the watchdog (a gated-off server is honestly `skipped`, never a fabricated forward) and reports
  its outcome (applied / skipped / failed / watchdog-unavailable) as a separate clause alongside the
  firewall outcome. Default (flag off) is host firewall only. The opt-in rides the existing
  confirmation token (on `ConfigKey`), so there is no new token field and no new `ConfirmationKind`.

## [1.7.0] - 2026-07-19

### Added
- A `get_network(instance_name)` read-only tool — reports one instance's HOST-FIREWALL picture: the
  ports KGSM has opened for it (via the kgsm-firewall authority), the active backend, and whether it's
  enforcing. Backed by the neutral `INetworkInfo` port and the pure `Network.NetworkReport` composer
  (no nested model call); the `KgsmNetworkInfo` adapter reaches the firewall authority through
  kgsm-lib's `IFirewallService` (`ListOwnedAsync` + `BackendAsync`) and fails closed — an unreachable
  authority reads as an honest "firewall unavailable", never a fabricated "nothing open". Covers the
  host firewall ONLY: router/UPnP port forwarding is not observable from the host, so it is never
  reported or implied. Offered to everyone (reveals no file contents); only an `Available` read carries
  a `NetworkData` card, an unavailable read stays summary-only.
- An `open_ports(instance_name, ports)` propose-only staged command — opens HOST-FIREWALL ports for an
  instance on human confirmation. Staged like every command (never runs in a turn); on confirm it calls
  kgsm-lib `IFirewallService.EnsureOpenAsync` through `INetworkInfo`, mapping the authority's precise
  outcome (applied / applied-but-not-enforcing / no-op / unsupported / unreachable) to an honest result,
  never a fabricated open. The `ports` argument accepts `port`, `port/proto`, `start:end`, and
  `start:end/proto` forms (comma/pipe separated; a missing protocol opens both tcp and udp), parsed once
  by `Network.PortSpecParser` and carried on the confirmation token as a canonical string. Opens the
  host firewall only — it does NOT configure router/UPnP port forwarding.
- `run_health_check` gains a fifth **port-reachability** check — for a running instance, whether its
  configured ports are currently bound (host-local `ss` probe via kgsm-lib `watcher ports test`).
  Reachable → pass; running-but-unbound → warn (it may still be starting), never a hard fail; a stopped
  server, an instance with no ports configured, or a failed probe → skip, never a fabricated pass. This
  is host-local port binding, distinct from `get_network`'s firewall-rule view.

## [1.6.0] - 2026-07-18

### Added
- A `trace_root_cause(instance_name, range?)` tool — the toolbox's capstone aggregator (plan §3.4/
  §7·Q1): a DETERMINISTIC composition of one instance's engine event timeline, a metrics window, and
  its health/status snapshot, run through a fixed rules table of known KGSM failure signatures. No
  nested model call anywhere in the path — the pure `RootCause.RootCauseAggregator` fetches nothing
  itself and reasons about nothing; the dispatcher fans the three reads out in parallel
  (`IEventHistory` + `IServerMetrics` + `IServerOperations.GetHealthSnapshotAsync`, the same neutral
  inputs `get_audit_log`/`get_performance`/`run_health_check` already use) and the aggregator only
  pattern-matches over what came back. `instance_name` is REQUIRED (unlike the audit tools — root
  cause is always about one server); `range` defaults to `24h`. The rules table: a start/restart that
  crashed or failed within 3 minutes without reaching "ready" reads as a port-conflict/bind-failure
  shape (`Likely` — kgsm's event log records THAT a start didn't take, not WHY); one or more crashes
  within 15 minutes of an update finishing reads as update-triggered (`Likely` for one, `Confirmed`
  "crash loop" for two or more); the health snapshot's own critical-disk verdict coinciding with a
  deploy/download/uninstall failure reads as disk-full (`Confirmed` — reuses `HealthCheckAggregator`'s
  disk judgment rather than a second threshold); the most recent run-state event saying
  started/restarted/ready while the live snapshot reads not-running is a split-brain (`Confirmed` — a
  direct, measured contradiction, not an inference). When nothing matches, the result is an honest
  ranked correlation of the most salient recent events at `Possible` — never a guessed cause. Every
  source degrades independently: an unreachable event timeline means no rule can be evaluated at all
  (honestly worded, not a fabricated "nothing happened"); a failed health snapshot only disables the
  two rules that need it (disk-full, split-brain); an unreachable metrics window only empties the
  supporting CPU/memory context facts. Emits the shared `ToolResult<RootCauseData>` envelope with a
  ranked `Findings` list (each carrying its own evidence — the specific events, metric facts, and
  health checks that produced it) and a deterministic summary authored from the top finding — the
  model narrates it, it never authors it.

### Changed
- `Metrics.PerformanceReport`'s `Stats`/`FormatBytes` helpers are now `internal` (were `private`) so
  `RootCauseAggregator` reuses the exact same avg/peak computation and byte formatting for its
  metrics-window evidence instead of a second copy.

## [1.5.0] - 2026-07-18

### Added
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

## [1.4.0] - 2026-07-18

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
