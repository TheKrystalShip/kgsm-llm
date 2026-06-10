#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
#  verify-tiers.sh — drive the Tier-1 (and the automatable slice of Tier-2)
#  verification of TheKrystalShip.Kgsm.Assistant.Service end to end.
#
#  It automates everything that can be automated and pauses ONLY for the steps
#  that genuinely need a human:
#     • the one-time Developer-Portal values (sourced from scripts/tier.env or
#       prompted; the client secret silently),
#     • the browser "Authorize" click + pasting back the redirect URL,
#     • (opt-in) the destructive install/uninstall confirm,
#     • the Discord-side role-revocation toggle (opt-in live check).
#
#  TIER 1  = local, real Discord, no tunnel — the auth/role/confirm + SSE path
#            that actually matters. This is what the script drives.
#  TIER 2  = github.io origin over HTTPS. Only the CORS preflight is automatable
#            (the script does it); the HTTPS-reachable box / tunnel / cert /
#            real github.io deploy are an infra checklist the script prints.
#
#  SECRETS: the OAuth client secret is read from the environment or prompted
#  silently. It is never written to disk, never echoed, and is handed to the
#  service as an environment variable (not a CLI arg — `ps` would expose those).
#  There is deliberately no `set -x`.
# ─────────────────────────────────────────────────────────────────────────────

set -uo pipefail   # NOT -e: a failed secondary check records FAIL/SKIP and the run continues.

# ── Layout ───────────────────────────────────────────────────────────────────
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_DIR=$(cd -- "$SCRIPT_DIR/.." && pwd)
SERVICE_DIR="$REPO_DIR/TheKrystalShip.Kgsm.Assistant.Service"
SERVICE_CSPROJ="$SERVICE_DIR/TheKrystalShip.Kgsm.Assistant.Service.csproj"
SOLUTION="$REPO_DIR/TheKrystalShip.Llm.slnx"
ENV_FILE="$SCRIPT_DIR/tier.env"

# ── Defaults / flags ───────────────────────────────────────────────────────────
BASE_URL="${BASE_URL:-http://localhost:5180}"
# A tool-round turn is one model generation PER instance the model checks against live kgsm
# (~3s/instance + ~13s for the first generation's big-prompt eval), so it can run to a minute-plus
# on a many-instance box. The buffered path has no client cap; give the SSE check real headroom.
SSE_TIMEOUT="${SSE_TIMEOUT:-300}"
RUN_TESTS=1
RUN_TIER2=1
ALLOW_DESTRUCTIVE=0
REUSE_EXISTING=0

usage() {
  cat <<EOF
Usage: scripts/verify-tiers.sh [options]

  --skip-tests          Don't run 'dotnet test' (Tier-0) first.
  --no-tier2            Skip the Tier-2 CORS preflight check.
  --allow-destructive   Enable the opt-in install→confirm→uninstall path
                        (still asks interactively before doing anything).
  --reuse               Reuse a service already listening on $BASE_URL instead
                        of launching one (advanced; that instance must have been
                        started with the same OAuth/Actions env or auth will fail).
  -h, --help            This help.

Config is sourced from scripts/tier.env (copy scripts/tier.env.example).
Anything left blank there is prompted for at runtime.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --skip-tests) RUN_TESTS=0 ;;
    --no-tier2) RUN_TIER2=0 ;;
    --allow-destructive) ALLOW_DESTRUCTIVE=1 ;;
    --reuse) REUSE_EXISTING=1 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
  esac
  shift
done

# ── Pretty output ──────────────────────────────────────────────────────────────
if [ -t 1 ]; then
  C_RESET=$'\e[0m'; C_DIM=$'\e[2m'; C_BOLD=$'\e[1m'
  C_RED=$'\e[31m'; C_GRN=$'\e[32m'; C_YEL=$'\e[33m'; C_BLU=$'\e[34m'; C_CYN=$'\e[36m'
else
  C_RESET=; C_DIM=; C_BOLD=; C_RED=; C_GRN=; C_YEL=; C_BLU=; C_CYN=
fi

step()  { printf '\n%s━━ %s %s\n' "$C_BOLD$C_BLU" "$*" "$C_RESET"; }
info()  { printf '%s•%s %s\n' "$C_CYN" "$C_RESET" "$*"; }
note()  { printf '%s  %s%s\n' "$C_DIM" "$*" "$C_RESET"; }
warn()  { printf '%s!%s %s\n' "$C_YEL" "$C_RESET" "$*"; }

# ── Result accounting ────────────────────────────────────────────────────────────
declare -a SUMMARY=()
FAILED=0
pass() { SUMMARY+=("PASS|$1|${2:-}"); printf '%s✔ PASS%s %s %s%s%s\n' "$C_GRN" "$C_RESET" "$1" "$C_DIM" "${2:-}" "$C_RESET"; }
fail() { SUMMARY+=("FAIL|$1|${2:-}"); FAILED=$((FAILED+1)); printf '%s✘ FAIL%s %s %s%s%s\n' "$C_RED" "$C_RESET" "$1" "$C_DIM" "${2:-}" "$C_RESET"; }
skip() { SUMMARY+=("SKIP|$1|${2:-}"); printf '%s‒ SKIP%s %s %s%s%s\n' "$C_YEL" "$C_RESET" "$1" "$C_DIM" "${2:-}" "$C_RESET"; }

# ── Temp + cleanup ───────────────────────────────────────────────────────────────
TMP=$(mktemp -d)
LAUNCHED=0
SERVICE_PID=""
SERVICE_LOG="$TMP/service.log"

kill_tree() {   # TERM a pid and all its descendants (dotnet run spawns the real app as a child)
  local pid=$1 child
  for child in $(pgrep -P "$pid" 2>/dev/null); do kill_tree "$child"; done
  kill -TERM "$pid" 2>/dev/null || true
}

cleanup() {
  if [ "$LAUNCHED" = "1" ] && [ -n "$SERVICE_PID" ]; then
    info "Stopping the service we launched (pid $SERVICE_PID)…"
    kill_tree "$SERVICE_PID"
    wait "$SERVICE_PID" 2>/dev/null || true
  fi
  # Keep the diagnostics (SSE captures, service.log, test.log) when something failed — they're
  # exactly what you need to tell a timeout from an error from a cutoff. Otherwise tidy up.
  if [ "$FAILED" -gt 0 ] || [ "${KEEP_ARTIFACTS:-0}" = "1" ]; then
    printf '\n%sDiagnostics kept in %s%s (service.log, test.log, sse_prose, sse_tool)\n' \
      "$C_YEL" "$TMP" "$C_RESET"
  else
    rm -rf "$TMP"
  fi
}
trap cleanup EXIT INT TERM

# ── HTTP helper: sets HTTP_CODE / HTTP_BODY / HTTP_HEADERS ────────────────────────
HTTP_CODE=""; HTTP_BODY=""; HTTP_HEADERS=""
req() {   # req METHOD PATH BODY [extra curl args...]
  local method=$1 path=$2 body=$3; shift 3
  local args=( -sS -o "$TMP/body" -D "$TMP/headers" -w '%{http_code}' -X "$method" )
  if [ -n "$body" ]; then args+=( -H 'Content-Type: application/json' --data-raw "$body" ); fi
  args+=( "$@" "$BASE_URL$path" )
  HTTP_CODE=$(curl "${args[@]}" 2>"$TMP/curlerr") || HTTP_CODE=000
  HTTP_BODY=$(cat "$TMP/body" 2>/dev/null || true)
  HTTP_HEADERS=$(cat "$TMP/headers" 2>/dev/null || true)
}

body_prompt() { jq -nc --arg p "$1" '{prompt:$p}'; }   # safely-quoted {"prompt":"…"}

expect_code() {   # expect_code EXPECTED-CODE "check name"  — uses the last req's HTTP_CODE
  if [ "$HTTP_CODE" = "$1" ]; then pass "$2"; else fail "$2" "got $HTTP_CODE"; fi
}

classify_sse() {   # classify_sse OUTFILE CURL-RC "label"  — distinguish done / error / timeout / cutoff
  local out=$1 rc=$2 label=$3
  if grep -q '^event: error' "$out" 2>/dev/null; then
    local msg; msg=$(grep -A1 '^event: error' "$out" | sed -n 's/^data: //p' | jq -r '.error' 2>/dev/null)
    fail "$label" "stream emitted event: error — ${msg:-unknown}"
  elif grep -q '^event: done' "$out" 2>/dev/null; then
    local dtext; dtext=$(grep -A1 '^event: done' "$out" | sed -n 's/^data: //p' | jq -r '.text' 2>/dev/null)
    if printf '%s' "$dtext" | grep -q "able to finish that after a few steps"; then
      # SSE plumbing is fine (frames flowed to done), but the answer is the agent's give-up reply:
      # the turn hit LlmAgent:MaxIterations. On a many-instance box a per-instance tool loop does.
      fail "$label" "reached 'done' but the answer is the iteration-cap apology — LlmAgent:MaxIterations too low for a per-instance tool loop (not an SSE fault)"
    else
      local pre=""
      grep -q '^event: status' "$out" && pre="status (tool round) → "
      grep -q '^event: token'  "$out" && pre="${pre}tokens → "
      pass "$label" "${pre}done"
    fi
  elif [ "$rc" = "28" ]; then
    fail "$label" "curl hit --max-time ${SSE_TIMEOUT}s before 'done' — a many-instance status query is slow; raise SSE_TIMEOUT. Frames so far: $(grep -c '^event:' "$out" 2>/dev/null)"
  else
    fail "$label" "no done/error frame (curl rc=$rc). Frames seen: $(grep '^event:' "$out" 2>/dev/null | sort -u | tr '\n' ' ')"
  fi
}

STAGE_TOKEN=""; STAGE_TEXT=""
try_stage_install() {   # try_stage_install PROMPT  → sets STAGE_TOKEN (confirmation token) + STAGE_TEXT (reply)
  req POST /turn "$(body_prompt "$1")" -H "Authorization: Bearer $TOKEN"
  STAGE_TEXT=$(printf '%s' "$HTTP_BODY" | jq -r '.text // empty' 2>/dev/null)
  STAGE_TOKEN=$(printf '%s' "$HTTP_BODY" | jq -r '.confirmations[0].token // empty' 2>/dev/null)
}

open_browser() {   # best-effort; never fatal
  local url=$1 b
  for b in "${BROWSER:-}" xdg-open sensible-browser firefox google-chrome-stable chromium; do
    [ -n "$b" ] && command -v "$b" >/dev/null 2>&1 && { "$b" "$url" >/dev/null 2>&1 & return; }
  done
  return 1
}

# state carried between phases
TOKEN=""; DISPLAY_NAME=""; CAN_PERFORM=""; FIRST_INSTANCE=""

# ═══════════════════════════════════════════════════════════════════════════════
#  PHASE 0 — preflight: tooling, config, build, dependencies
# ═══════════════════════════════════════════════════════════════════════════════
step "Phase 0 — preflight"

missing=()
for t in dotnet curl jq openssl; do command -v "$t" >/dev/null 2>&1 || missing+=("$t"); done
if [ ${#missing[@]} -gt 0 ]; then
  fail "tooling" "missing: ${missing[*]}"
  echo "Install the missing tool(s) and re-run." >&2; exit 1
fi
pass "tooling" "dotnet curl jq openssl present"

# Config: source the gitignored env file if present, then fill gaps interactively.
if [ -f "$ENV_FILE" ]; then
  chmod 600 "$ENV_FILE" 2>/dev/null || true
  # shellcheck disable=SC1090
  source "$ENV_FILE"
  info "Loaded config from $ENV_FILE"
else
  note "No $ENV_FILE (copy tier.env.example to pre-fill); will prompt for values."
fi

: "${DiscordOAuth__ClientId:=}"
: "${DiscordOAuth__ClientSecret:=}"
: "${DiscordOAuth__GuildId:=}"
: "${DiscordOAuth__ActionRoleId:=}"
: "${DiscordOAuth__RedirectUri:=http://localhost:5180/health}"
: "${TIER2_ORIGIN:=https://example.github.io}"

prompt_if_blank() {   # prompt_if_blank VARNAME "label"
  local var=$1 label=$2 cur=${!1}
  if [ -z "$cur" ]; then
    read -r -p "  Enter ${label}: " cur
    printf -v "$var" '%s' "$cur"
  fi
}
prompt_if_blank DiscordOAuth__ClientId "Discord ClientId (application id)"
prompt_if_blank DiscordOAuth__GuildId  "Discord GuildId (server id)"
# Secret: env/file first; otherwise prompt silently. Never echoed.
if [ -z "$DiscordOAuth__ClientSecret" ]; then
  read -rs -p "  Enter Discord ClientSecret (hidden): " DiscordOAuth__ClientSecret; echo
fi
# ActionRoleId is optional: blank ⇒ read-only verification only.
if [ -z "$DiscordOAuth__ActionRoleId" ]; then
  warn "No ActionRoleId set — verifying READ-ONLY only (canPerformActions will be false)."
fi

if [ -z "$DiscordOAuth__ClientId" ] || [ -z "$DiscordOAuth__ClientSecret" ] || [ -z "$DiscordOAuth__GuildId" ]; then
  fail "config" "ClientId, ClientSecret and GuildId are all required"
  exit 1
fi
pass "config" "ClientId/GuildId/RedirectUri set; secret provided (hidden)"
note "ClientId=$DiscordOAuth__ClientId  GuildId=$DiscordOAuth__GuildId"
note "ActionRoleId=${DiscordOAuth__ActionRoleId:-(none)}  RedirectUri=$DiscordOAuth__RedirectUri"
note "Tier-2 origin=$TIER2_ORIGIN  BaseUrl=$BASE_URL"

# Tier-0: tests (mocked Discord). Build happens as part of the test build.
if [ "$RUN_TESTS" = "1" ]; then
  info "Running the test suite (dotnet test $SOLUTION)…"
  if dotnet test "$SOLUTION" --nologo -v q >"$TMP/test.log" 2>&1; then
    pass "Tier-0 tests" "$(grep -hoE 'Passed!.*' "$TMP/test.log" | head -1)"
  else
    fail "Tier-0 tests" "see $TMP/test.log (copied below)"
    tail -40 "$TMP/test.log"
    echo "Tests failed — aborting before any live check." >&2; exit 1
  fi
else
  # We still need a build to run the service.
  info "Skipping tests; building the service…"
  if dotnet build "$SERVICE_CSPROJ" --nologo -v q >"$TMP/build.log" 2>&1; then
    pass "build" "service built"
  else
    fail "build" "see $TMP/build.log"; tail -40 "$TMP/build.log"; exit 1
  fi
fi

# Dependencies that only /turn and /confirm need (auth itself needs neither).
if curl -sS -m 3 -o /dev/null "${OLLAMA:-http://localhost:11434}/api/tags" 2>/dev/null; then
  pass "Ollama" "reachable"
else
  skip "Ollama" "unreachable — /turn & SSE will be skipped"
fi
if [ -f "${KGSM_PATH:-/opt/kgsm/kgsm.sh}" ]; then
  pass "kgsm" "present"
  # Grab one instance name so the SSE tool-round check can ask a SINGLE-instance live question
  # (exactly one tool round). "which servers are running?" would fan out to one round PER instance
  # and, on a many-instance box, blow past the agent's MaxIterations cap — see the cap check below.
  FIRST_INSTANCE=$("${KGSM_PATH:-/opt/kgsm/kgsm.sh}" --instances 2>/dev/null | head -1 | tr -d '[:space:]')
  [ -n "$FIRST_INSTANCE" ] && note "Using '$FIRST_INSTANCE' for the single-round SSE tool check."
else
  skip "kgsm" "kgsm.sh not found — tool rounds may fail"
fi

# ═══════════════════════════════════════════════════════════════════════════════
#  PHASE 1 — launch the service (one instance, kept up across the whole run)
# ═══════════════════════════════════════════════════════════════════════════════
step "Phase 1 — service"

# Is something already on the port?
if curl -sS -m 2 -o /dev/null "$BASE_URL/health" 2>/dev/null; then
  if [ "$REUSE_EXISTING" = "1" ]; then
    warn "Reusing the service already on $BASE_URL (its config must match, or auth will 401)."
    pass "service" "reusing existing instance on $BASE_URL"
  else
    fail "service" "$BASE_URL is already serving — stop it, or pass --reuse"
    echo "Something already answers $BASE_URL/health. Stop that instance (so this script can run" >&2
    echo "the service with the OAuth/Actions env it needs), or re-run with --reuse." >&2
    exit 1
  fi
else
  info "Launching the service with verification env (secret passed via env, not argv)…"
  # Export only for the child process. ActionsEnabled + a fresh ephemeral Confirmation key
  # so canPerformActions can be true; the key is generated in memory and never written out.
  ( cd "$SERVICE_DIR" && \
    DiscordOAuth__ClientId="$DiscordOAuth__ClientId" \
    DiscordOAuth__ClientSecret="$DiscordOAuth__ClientSecret" \
    DiscordOAuth__GuildId="$DiscordOAuth__GuildId" \
    DiscordOAuth__ActionRoleId="$DiscordOAuth__ActionRoleId" \
    DiscordOAuth__RedirectUri="$DiscordOAuth__RedirectUri" \
    Assistant__ActionsEnabled=true \
    Assistant__Confirmation__Key="$(openssl rand -base64 32)" \
    Auth__AllowedOrigins__0="$TIER2_ORIGIN" \
    exec dotnet run --project "$SERVICE_CSPROJ" --nologo ) >"$SERVICE_LOG" 2>&1 &
  SERVICE_PID=$!
  LAUNCHED=1

  info "Waiting for $BASE_URL/health (pid $SERVICE_PID, build+boot can take a bit)…"
  up=0
  for _ in $(seq 1 120); do
    if ! kill -0 "$SERVICE_PID" 2>/dev/null; then break; fi
    if curl -sS -m 2 -o /dev/null "$BASE_URL/health" 2>/dev/null; then up=1; break; fi
    sleep 1
  done
  if [ "$up" = "1" ]; then
    pass "service" "up on $BASE_URL (pid $SERVICE_PID)"
  else
    fail "service" "did not become healthy"
    echo "--- last 40 lines of service log ($SERVICE_LOG) ---" >&2
    tail -40 "$SERVICE_LOG" >&2
    exit 1
  fi
fi

# Confirm the startup config didn't silently fall back to read-only.
if grep -q "will run READ-ONLY\|no caller will be authorized" "$SERVICE_LOG" 2>/dev/null; then
  warn "Service logged a read-only/authorization warning — check your DiscordOAuth/Actions config."
fi

# ═══════════════════════════════════════════════════════════════════════════════
#  PHASE 2 — auth bootstrap (the one unavoidable manual step: browser authorize)
# ═══════════════════════════════════════════════════════════════════════════════
step "Phase 2 — Discord OAuth login (browser)"

req GET /auth/login ""
if [ "$HTTP_CODE" = "200" ]; then
  LOGIN_URL=$(printf '%s' "$HTTP_BODY" | jq -r '.url // empty')
fi
if [ -z "${LOGIN_URL:-}" ]; then
  fail "/auth/login" "HTTP $HTTP_CODE — $HTTP_BODY"
  echo "Cannot continue without a login URL." >&2; exit 1
fi
pass "/auth/login" "got authorize URL"

echo
echo "  1) Authorize in a browser logged into Discord:"
printf '     %s%s%s\n' "$C_CYN" "$LOGIN_URL" "$C_RESET"
if open_browser "$LOGIN_URL"; then note "(opened in your browser)"; else note "(open the URL above manually)"; fi
echo "  2) Discord redirects to ${DiscordOAuth__RedirectUri}?code=…&state=…"
echo "     The page shows {\"status\":\"ok\"}; the CODE & STATE are in the address bar."
echo "  3) Paste the FULL redirect URL from the address bar below (within 5 min — state is single-use):"
read -r REDIRECT_URL

# Parse code & state out of the pasted URL; fall back to asking for each.
CODE=""; STATE=""
if [[ "$REDIRECT_URL" == *\?* ]]; then
  qs="${REDIRECT_URL#*\?}"
  CODE=$(printf '%s' "$qs"  | tr '&' '\n' | sed -n 's/^code=//p'  | head -1)
  STATE=$(printf '%s' "$qs" | tr '&' '\n' | sed -n 's/^state=//p' | head -1)
fi
[ -z "$CODE" ]  && { read -r -p "  code: "  CODE; }
[ -z "$STATE" ] && { read -r -p "  state: " STATE; }

req POST /auth/callback "$(jq -nc --arg c "$CODE" --arg s "$STATE" '{code:$c,state:$s}')"
if [ "$HTTP_CODE" = "200" ]; then
  TOKEN=$(printf '%s' "$HTTP_BODY" | jq -r '.token // empty')
  DISPLAY_NAME=$(printf '%s' "$HTTP_BODY" | jq -r '.displayName // empty')
fi
if [ -n "$TOKEN" ]; then
  pass "/auth/callback" "session minted for '$DISPLAY_NAME'"
  # The mock-uncoverable claim: a non-empty displayName means member.user was populated.
  if [ -z "$DISPLAY_NAME" ]; then
    warn "displayName is EMPTY — member.user may be null (the recorded fallback is /users/@me)."
  fi
else
  fail "/auth/callback" "HTTP $HTTP_CODE — $HTTP_BODY"
  echo "  401 here for a known guild member can mean the member.user-null case, or expired/reused state." >&2
  echo "Cannot continue without a session token." >&2
  # No token ⇒ skip everything that needs one, jump to Tier-2 + summary.
fi

# ═══════════════════════════════════════════════════════════════════════════════
#  PHASE 3 — identity & authority
# ═══════════════════════════════════════════════════════════════════════════════
if [ -n "$TOKEN" ]; then
  step "Phase 3 — identity & authority (/auth/me)"
  req GET /auth/me "" -H "Authorization: Bearer $TOKEN"
  if [ "$HTTP_CODE" = "200" ]; then
    CAN_PERFORM=$(printf '%s' "$HTTP_BODY" | jq -r '.canPerformActions')
    pass "/auth/me" "userId=$(printf '%s' "$HTTP_BODY" | jq -r .userId) canPerformActions=$CAN_PERFORM"
    if [ "$CAN_PERFORM" = "true" ]; then
      note "You hold the action role — destructive path is available (opt-in)."
    else
      note "You do NOT hold the action role — destructive path will be skipped."
    fi
  else
    fail "/auth/me" "HTTP $HTTP_CODE — $HTTP_BODY"
  fi
fi

# Whether the assistant endpoints can run at all this session.
ASSISTANT_OK=0
if [ -n "$TOKEN" ] && curl -sS -m 3 -o /dev/null "${OLLAMA:-http://localhost:11434}/api/tags" 2>/dev/null; then
  ASSISTANT_OK=1
fi

# ═══════════════════════════════════════════════════════════════════════════════
#  PHASE 4 — buffered turn (read-only)
# ═══════════════════════════════════════════════════════════════════════════════
step "Phase 4 — buffered /turn (read-only)"
if [ "$ASSISTANT_OK" = "1" ]; then
  # A cheap read-only prompt (NOT the fan-out "which servers are running?", which caps out — that's
  # the dedicated 'iteration cap' check's job). This just proves the buffered endpoint answers 200
  # with no staged confirmations.
  req POST /turn "$(body_prompt 'list the installed game servers')" -H "Authorization: Bearer $TOKEN"
  if [ "$HTTP_CODE" = "200" ]; then
    nconf=$(printf '%s' "$HTTP_BODY" | jq '.confirmations | length')
    pass "/turn buffered" "200, ${nconf} confirmation(s) (read-only ⇒ expect 0)"
    note "reply: $(printf '%s' "$HTTP_BODY" | jq -r '.text' | head -c 200)"
  else
    fail "/turn buffered" "HTTP $HTTP_CODE — $(printf '%s' "$HTTP_BODY" | head -c 200)"
  fi
else
  skip "/turn buffered" "no session or Ollama down"
fi

# ═══════════════════════════════════════════════════════════════════════════════
#  PHASE 5 — SSE token streaming (slice 2): prose + tool round
# ═══════════════════════════════════════════════════════════════════════════════
step "Phase 5 — SSE streaming"
sse_turn() {   # sse_turn PROMPT OUTFILE  — read-only prompts ONLY
  curl -sS -N --max-time "$SSE_TIMEOUT" \
    -H "Authorization: Bearer $TOKEN" -H 'Accept: text/event-stream' \
    -H 'Content-Type: application/json' --data-raw "$(body_prompt "$1")" \
    "$BASE_URL/turn" >"$2" 2>/dev/null
}
if [ "$ASSISTANT_OK" = "1" ]; then
  # Prose: one generation — fast. Tool round: the model checks each instance live against kgsm
  # (one round per instance, ~6s each), so this is the slow one; classify_sse tells a real failure
  # (event: error) from a too-short timeout from a clean done.
  sse_turn 'say hello' "$TMP/sse_prose"; classify_sse "$TMP/sse_prose" $? "SSE prose"
  note "done text: $(grep -A1 '^event: done' "$TMP/sse_prose" 2>/dev/null | sed -n 's/^data: //p' | jq -r '.text' 2>/dev/null | head -c 160)"

  # Single-instance live query = exactly ONE tool round (fast, deterministic, and it won't trip the
  # MaxIterations cap the way a fan-out "which servers are running?" does). Falls back to the generic
  # prompt if we couldn't discover an instance name.
  if [ -n "$FIRST_INSTANCE" ]; then
    tool_prompt="is the $FIRST_INSTANCE server currently running?"
  else
    tool_prompt="which servers are running?"
  fi
  info "Tool-round turn (live kgsm): \"$tool_prompt\""
  sse_turn "$tool_prompt" "$TMP/sse_tool"; classify_sse "$TMP/sse_tool" $? "SSE tool round"
else
  skip "SSE prose" "no session or Ollama down"
  skip "SSE tool round" "no session or Ollama down"
fi

# Iteration-cap reality check: a fan-out status query ("which servers are running?") fans to one
# tool round PER instance. With LlmAgent:MaxIterations=8 and many instances the agent gives up with
# a canned apology — the assistant literally can't answer. Surfaced explicitly so it isn't mistaken
# for a streaming bug. Buffered (no SSE) to keep it simple; skipped on a small box where it can finish.
if [ "$ASSISTANT_OK" = "1" ]; then
  req POST /turn "$(body_prompt 'which servers are running?')" -H "Authorization: Bearer $TOKEN"
  reply=$(printf '%s' "$HTTP_BODY" | jq -r '.text // empty' 2>/dev/null)
  if printf '%s' "$reply" | grep -q "able to finish that after a few steps"; then
    fail "iteration cap" "fan-out status query hit LlmAgent:MaxIterations — assistant gave up. Raise MaxIterations or add a bulk-status tool."
  else
    pass "iteration cap" "fan-out status query completed within MaxIterations"
  fi
else
  skip "iteration cap" "no session or Ollama down"
fi

# ═══════════════════════════════════════════════════════════════════════════════
#  PHASE 6 — destructive confirmation path (OPT-IN; installs then cleans up)
# ═══════════════════════════════════════════════════════════════════════════════
step "Phase 6 — destructive confirm (opt-in)"
KGSM="${KGSM_PATH:-/opt/kgsm/kgsm.sh}"
if [ "$ASSISTANT_OK" != "1" ] || [ "$CAN_PERFORM" != "true" ]; then
  skip "destructive confirm" "needs the action role + a live assistant"
else
  # Stage an install. The model must CHOOSE to call install_server; sometimes it answers in prose
  # instead (more likely after a capped status turn has left "I couldn't finish that" in context),
  # so on a miss we retry once with explicit tool-forcing phrasing and always show the model's reply.
  try_stage_install 'install a factorio server named tier-test'
  if [ -z "$STAGE_TOKEN" ]; then
    note "model reply (no stage): $(printf '%s' "$STAGE_TEXT" | head -c 200)"
    info "No confirmation staged — retrying with explicit tool-forcing phrasing…"
    try_stage_install 'Use the install_server tool now to install a new factorio server named tier-test.'
  fi

  if [ -z "$STAGE_TOKEN" ]; then
    skip "destructive confirm" "model didn't call install_server (reply: $(printf '%s' "$STAGE_TEXT" | head -c 120))"
  elif [ "$ALLOW_DESTRUCTIVE" != "1" ]; then
    # Default: prove the token-minting path WITHOUT executing it.
    pass "confirm token minted" "staged an install; token issued but NOT confirmed (safe)"
    note "Re-run with --allow-destructive to actually install + uninstall a 'tier-test' server."
  else
    warn "Destructive mode: this INSTALLS a real game server, confirms it, then UNINSTALLS it."
    read -r -p "  Proceed with the live install/uninstall of 'tier-test'? [y/N] " ans
    if [[ "${ans,,}" != "y" && "${ans,,}" != "yes" ]]; then
      skip "destructive confirm" "declined at the prompt (an install token was staged but not used)"
    else
      req POST /confirm "$(jq -nc --arg t "$STAGE_TOKEN" '{token:$t}')" -H "Authorization: Bearer $TOKEN"
      if [ "$HTTP_CODE" = "200" ] && [ "$(printf '%s' "$HTTP_BODY" | jq -r .success 2>/dev/null)" = "true" ]; then
        pass "destructive confirm" "install executed: $(printf '%s' "$HTTP_BODY" | jq -r .text | head -c 120)"
      else
        fail "destructive confirm" "HTTP $HTTP_CODE — $(printf '%s' "$HTTP_BODY" | head -c 200)"
      fi

      # Cleanup — must leave nothing behind on the reserved box. Try the assistant's uninstall-confirm
      # path first (it also exercises that round-trip); it may not stage because the inventory cache is
      # stale right after an install (no /events webhook fired here), so fall back to a direct kgsm
      # removal, then verify the instance is actually gone.
      info "Cleaning up 'tier-test'…"
      try_stage_install 'uninstall the tier-test server'
      if [ -n "$STAGE_TOKEN" ]; then
        req POST /confirm "$(jq -nc --arg t "$STAGE_TOKEN" '{token:$t}')" -H "Authorization: Bearer $TOKEN"
      fi
      # Direct kgsm safety net regardless of the assistant path (idempotent if already gone).
      "$KGSM" --uninstall tier-test >/dev/null 2>&1 || true
      if "$KGSM" --instances 2>/dev/null | grep -qx "tier-test"; then
        fail "destructive cleanup" "'tier-test' STILL PRESENT — remove manually: $KGSM --uninstall tier-test"
      else
        pass "destructive cleanup" "tier-test removed (verified absent from kgsm --instances)"
      fi
    fi
  fi
fi

# ═══════════════════════════════════════════════════════════════════════════════
#  PHASE 7 — security / negative checks
# ═══════════════════════════════════════════════════════════════════════════════
step "Phase 7 — negative checks"
req POST /turn "$(body_prompt 'hi')"
expect_code 401 "no-bearer → 401"

req POST /turn "$(body_prompt 'hi')" -H "Authorization: Bearer not-a-real-token"
expect_code 401 "garbage bearer → 401"

if [ -n "$TOKEN" ]; then
  req POST /confirm "$(jq -nc '{token:"deadbeef.not.a.token"}')" -H "Authorization: Bearer $TOKEN"
  if [ "$HTTP_CODE" = "400" ]; then
    pass "bad confirm token → 400" "$(printf '%s' "$HTTP_BODY" | jq -r '.error' 2>/dev/null)"
  else
    fail "bad confirm token → 400" "got $HTTP_CODE"
  fi

  # Empty prompt is rejected with a 400 (the guard runs before any SSE/auth-action work).
  req POST /turn "$(jq -nc '{prompt:""}')" -H "Authorization: Bearer $TOKEN"
  expect_code 400 "empty prompt → 400"
else
  skip "bad confirm token → 400" "no session"
  skip "empty prompt → 400" "no session"
fi

# Live role-revocation (opt-in: needs you to toggle the role in Discord; ~70s wait).
if [ -n "$TOKEN" ] && [ "$CAN_PERFORM" = "true" ]; then
  read -r -p "  Test LIVE role revocation? Remove your action role in Discord, then press y (waits ~70s) [y/N] " ans
  if [[ "${ans,,}" == "y" || "${ans,,}" == "yes" ]]; then
    info "Waiting 70s for the role cache (RoleCacheTtlSeconds=60) to expire…"
    sleep 70
    req GET /auth/me "" -H "Authorization: Bearer $TOKEN"
    if [ "$(printf '%s' "$HTTP_BODY" | jq -r .canPerformActions 2>/dev/null)" = "false" ]; then
      pass "role revocation live" "canPerformActions flipped to false"
      note "Re-add your role in Discord to restore action access."
    else
      fail "role revocation live" "still true — did the role get removed? cache TTL elapsed?"
    fi
  else
    skip "role revocation live" "declined (manual: remove role, wait >60s, re-call /auth/me)"
  fi
else
  skip "role revocation live" "needs the action role"
fi

# ═══════════════════════════════════════════════════════════════════════════════
#  PHASE 8 — logout (and prove the session is dead)
# ═══════════════════════════════════════════════════════════════════════════════
step "Phase 8 — logout"
if [ -n "$TOKEN" ]; then
  req POST /auth/logout "" -H "Authorization: Bearer $TOKEN"
  expect_code 204 "/auth/logout → 204"
  req GET /auth/me "" -H "Authorization: Bearer $TOKEN"
  expect_code 401 "session invalidated → 401"
  TOKEN=""   # consumed
else
  skip "/auth/logout → 204" "no session"
fi

# ═══════════════════════════════════════════════════════════════════════════════
#  PHASE 9 — Tier 2: CORS preflight (automatable) + infra checklist (manual)
# ═══════════════════════════════════════════════════════════════════════════════
if [ "$RUN_TIER2" = "1" ]; then
  step "Phase 9 — Tier 2: CORS preflight"
  req OPTIONS /turn "" \
    -H "Origin: $TIER2_ORIGIN" \
    -H 'Access-Control-Request-Method: POST' \
    -H 'Access-Control-Request-Headers: authorization,content-type'
  acao=$(printf '%s' "$HTTP_HEADERS" | grep -i '^access-control-allow-origin:' | tr -d '\r' | sed 's/^[^:]*: *//')
  if [ "$acao" = "$TIER2_ORIGIN" ]; then
    pass "CORS preflight" "Access-Control-Allow-Origin echoes $TIER2_ORIGIN"
  else
    fail "CORS preflight" "ACAO='$acao' (expected '$TIER2_ORIGIN'); service launched with this origin?"
  fi

  echo
  echo "  ${C_BOLD}Tier-2 manual checklist (the script cannot automate these):${C_RESET}"
  echo "   ▸ Home box reachable over HTTPS from the internet (reverse proxy / tunnel + real cert)."
  echo "   ▸ DiscordOAuth__RedirectUri = the SPA's HTTPS callback (registered in the portal)."
  echo "   ▸ Auth__AllowedOrigins__0  = your exact https://<you>.github.io origin."
  echo "   ▸ Then re-run the Tier-1 flow against the public HTTPS URL (mixed content is blocked from github.io)."
else
  step "Phase 9 — Tier 2"
  skip "CORS preflight" "--no-tier2"
fi

# ═══════════════════════════════════════════════════════════════════════════════
#  Summary
# ═══════════════════════════════════════════════════════════════════════════════
step "Summary"
p=0; f=0; s=0
for row in "${SUMMARY[@]}"; do
  st=${row%%|*}; rest=${row#*|}; name=${rest%%|*}; detail=${rest#*|}
  case "$st" in
    PASS) c=$C_GRN; p=$((p+1)) ;;
    FAIL) c=$C_RED; f=$((f+1)) ;;
    SKIP) c=$C_YEL; s=$((s+1)) ;;
  esac
  printf '  %s%-4s%s %-26s %s%s%s\n' "$c" "$st" "$C_RESET" "$name" "$C_DIM" "$detail" "$C_RESET"
done
printf '\n  %s%d passed%s, %s%d failed%s, %s%d skipped%s\n' \
  "$C_GRN" "$p" "$C_RESET" "$C_RED" "$f" "$C_RESET" "$C_YEL" "$s" "$C_RESET"

[ "$f" -gt 0 ] && exit 1 || exit 0
