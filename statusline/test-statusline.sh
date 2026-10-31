#!/usr/bin/env bash
# Integration tests for the per-session quota persistence of statusline.sh.
# Runs the real script with HOME pointed at a temporary directory.
# Usage: bash test-statusline.sh <statusline.sh under test> [<reference statusline.sh>]
# With a reference script, also checks that the displayed line is unchanged.
set -uo pipefail

SCRIPT=${1:?usage: test-statusline.sh <statusline.sh> [<reference statusline.sh>]}
REFERENCE=${2:-}
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
export HOME="$WORK"
export QUOTA_WIDGET_DATA="$WORK/widget"
mkdir -p "$HOME/.claude/state"
STATE="$HOME/.claude/state/claude-quotas.d"
LEGACY="$HOME/.claude/state/claude-quotas.json"
FAILS=0

check() { # check <label> <condition...>
  local label=$1; shift
  if "$@"; then printf 'ok   %s\n' "$label"; else printf 'FAIL %s\n' "$label"; FAILS=$((FAILS + 1)); fi
}

payload() { # payload <session_id or ""> <api_ms> <input_tokens> <rate_limits json or "">
  local sid=$1 api=$2 tok=$3 rl=$4
  jq -cn --arg sid "$sid" --argjson api "$api" --argjson tok "$tok" --arg rl "$rl" '
    {model: {display_name: "Opus"}, workspace: {current_dir: "C:/work/example"},
     cost: {total_api_duration_ms: $api},
     context_window: {total_input_tokens: $tok, total_output_tokens: 10, context_window_size: 1000000, used_percentage: 5}}
    + (if $sid == "" then {} else {session_id: $sid} end)
    + (if $rl == "" then {} else {rate_limits: ($rl | fromjson)} end)'
}

render() { bash "$SCRIPT" >"$WORK/out.txt" 2>"$WORK/err.txt"; }
field() { jq -r "$2" "$STATE/$1.json"; }
iso_utc() { grep -Eq '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$' <<<"$1"; }

BOTH='{"five_hour":{"used_percentage":24.5,"resets_at":1793353800},"seven_day":{"used_percentage":66,"resets_at":1793563200}}'
WEEK='{"seven_day":{"used_percentage":67,"resets_at":1793563200}}'

# 1. First render of a session: per-session file, legacy file, display; age unknown.
payload s-1 1000 500 "$BOTH" | render
check "1 display shows quotas" grep -q "quota 5h 24% / 7j 66%" "$WORK/out.txt"
check "1 per-session file written" test -f "$STATE/s-1.json"
check "1 legacy file written" test "$(jq -r '.rate_limits.seven_day.used_percentage' "$LEGACY")" = "66"
check "1 legacy file has its timestamp" grep -Eq '^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}$' <<<"$(jq -r '.at' "$LEGACY")"
check "1 decimal percentage preserved" test "$(field s-1 '.rate_limits.five_hour.used_percentage')" = "24.5"
check "1 counters stored" test "$(field s-1 '[.api_duration_ms, .input_tokens] | @csv')" = "1000,500"
check "1 first render: measured_at null" test "$(field s-1 '.measured_at')" = "null"
FIRST_RENDERED=$(field s-1 '.rendered_at')
check "1 rendered_at is ISO UTC" iso_utc "$FIRST_RENDERED"

# 2. Idle re-render (same API counter): measured_at stays null, rendered_at moves.
sleep 1.1
payload s-1 1000 500 "$BOTH" | render
check "2 idle render keeps null measured_at" test "$(field s-1 '.measured_at')" = "null"
check "2 rendered_at moves on idle render" test "$(field s-1 '.rendered_at')" != "$FIRST_RENDERED"

# 3. New API response (counter up): measured_at set, new values stored.
payload s-1 1800 900 "$WEEK" | render
M3=$(field s-1 '.measured_at')
check "3 API counter change sets measured_at" iso_utc "$M3"
check "3 single window stored" test "$(field s-1 '.rate_limits | keys | join(",")')" = "seven_day"
check "3 display with one window" grep -q "quota 5h ?% / 7j 67%" "$WORK/out.txt"

# 4. Only input tokens changed: not a new measurement (tokens are information only).
sleep 1.1
payload s-1 1800 950 "$WEEK" | render
check "4 token change alone keeps measured_at" test "$(field s-1 '.measured_at')" = "$M3"
check "4 tokens still stored" test "$(field s-1 '.input_tokens')" = "950"

# 5. Idle re-render after a measurement: the measured value is kept as is.
sleep 1.1
payload s-1 1800 950 "$WEEK" | render
check "5 idle render keeps measured_at" test "$(field s-1 '.measured_at')" = "$M3"

# 6. Counter goes down (resumed session): still a new measurement.
payload s-1 400 950 "$WEEK" | render
M6=$(field s-1 '.measured_at')
check "6 lower API counter sets measured_at" test "$M6" != "$M3"
check "6 measured_at still ISO UTC" iso_utc "$M6"

# 7. No rate limits (before first API response): no per-session file, display intact.
payload s-2 0 0 "" | render
check "7 no file without rate_limits" test ! -e "$STATE/s-2.json"
check "7 display without quotas" grep -q "ctx 5%" "$WORK/out.txt"

# 8. Missing session_id: legacy file still written, no per-session file.
rm -f "$LEGACY"
payload "" 10 10 "$BOTH" | render
check "8 legacy file without session_id" test -f "$LEGACY"
check "8 no unnamed per-session file" test "$(find "$STATE" -name '.json' | wc -l)" -eq 0

# 9. Unsafe session_id is refused (no path traversal).
payload "../evil" 10 10 "$BOTH" | render
check "9 unsafe session_id refused" test ! -e "$HOME/.claude/state/evil.json"

# 10. Truncated JSON: no state written, no temporary file left.
BEFORE=$(cat "$STATE/s-1.json")
printf '{"session_id":"s-1","rate_limits":{"five_hour":' | render
check "10 truncated payload leaves file untouched" test "$(cat "$STATE/s-1.json")" = "$BEFORE"

# 11. Corrupt previous file: replaced, no usable reference so age unknown.
printf 'not json' >"$STATE/s-3.json"
payload s-3 5 5 "$BOTH" | render
check "11 corrupt previous file replaced" test "$(field s-3 '.input_tokens')" = "5"
check "11 corrupt previous file: measured_at null" test "$(field s-3 '.measured_at')" = "null"

# 12. Previous file without a numeric counter: treated as no reference.
printf '{"api_duration_ms":"x","measured_at":"2026-01-01T00:00:00Z"}' >"$STATE/s-5.json"
payload s-5 5 5 "$BOTH" | render
check "12 non-numeric previous counter: measured_at null" test "$(field s-5 '.measured_at')" = "null"

# 13. Unwritable state directory: display unaffected.
rm -rf "$STATE"; printf 'file' >"$STATE"
payload s-4 5 5 "$BOTH" | render
check "13 display survives unwritable state dir" grep -q "quota 5h 24% / 7j 66%" "$WORK/out.txt"
rm -f "$STATE"

# 13b. A nested "session_id" before the real one: nothing written under the wrong name.
jq -c '{wrapper: {session_id: "other"}} + .' <<<"$(payload s-6 5 5 "$BOTH")" | render
check "13b nested session_id: no file under the wrong name" test ! -e "$STATE/other.json"
check "13b nested session_id: display intact" grep -q "quota 5h 24% / 7j 66%" "$WORK/out.txt"

# 14. No temporary files left anywhere.
check "14 no temporary files left" test "$(find "$HOME/.claude/state" \( -name ".*.*" -o -name "claude-quotas.json.*" \) | wc -l)" -eq 0

# 15. Marker block present exactly once (uninstall removes only this block).
check "15 one opening marker" test "$(grep -c '^# >>> quota-widget' "$SCRIPT")" -eq 1
check "15 one closing marker" test "$(grep -c '^# <<< quota-widget' "$SCRIPT")" -eq 1

# 16. Displayed line identical to the reference script for the same payloads.
if [[ -n "$REFERENCE" ]]; then
  for p in "$(payload s-9 1000 500 "$BOTH")" "$(payload s-9 1000 500 "$WEEK")" "$(payload s-9 0 0 "")" \
           "$(jq -c '.context_window.used_percentage = 95' <<<"$(payload s-9 1 1 "$BOTH")")"; do
    a=$(bash "$SCRIPT" <<<"$p" 2>/dev/null); b=$(bash "$REFERENCE" <<<"$p" 2>/dev/null)
    check "16 display identical to reference" test "$a" = "$b"
  done
fi


# 17. Compact Codex view, isolated from the user's data directory.
export QUOTA_WIDGET_DATA="$WORK/widget"
mkdir -p "$QUOTA_WIDGET_DATA"
compact_render() { payload s-compact 1 1 "" | render; }
no_codex() { ! grep -q 'codex ' "$WORK/out.txt"; }
compact_render
check "17 absent compact view" no_codex
check "17 absent compact view is silent" test ! -s "$WORK/err.txt"
printf 'v1\n' >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "18 version alone" no_codex
printf -v NOW '%(%s)T' -1
FUTURE=$((NOW + 86400))
printf 'v1\ncodex\t7 j\t5\t%s\tmer. 07:35\t%s\n' "$FUTURE" "$NOW" >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "19 seven-day window" grep -q 'codex 7 j 5% (remise mer. 07:35)' "$WORK/out.txt"
printf 'v1\ncodex\t5 h\t31\t%s\t12:30\t%s\n' "$FUTURE" "$NOW" >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "20 five-hour window" grep -q 'codex 5 h 31% (remise 12:30)' "$WORK/out.txt"
printf 'v1\ncodex\t7 j\t5\t1\tmer. 07:35\t%s\n' "$NOW" >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "21 passed reset" grep -q 'codex 7 j 5% (remise passée)' "$WORK/out.txt"
printf 'v1\ncodex\t7 j\t5\t\t\t%s\n' "$((NOW - 7205))" >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "22 age over thirty minutes" grep -q 'codex 7 j 5% · mesure il y a 2 h' "$WORK/out.txt"
printf 'v1\ncodex\t7 j\t5\t%s\tmer. 07:35\t\n' "$FUTURE" >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "23 missing measurement keeps reset field" grep -q 'codex 7 j 5% (remise mer. 07:35)' "$WORK/out.txt"
check "23 missing measurement has no age" bash -c '! grep -q "mesure il y a" "$1"' _ "$WORK/out.txt"
printf 'v1\ncodex\t7 j\t5\t' >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "24 truncated row" no_codex
printf 'v2\ncodex\t7 j\t5\t\t\t\n' >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "25 unknown version" no_codex
printf 'v1\ncodex\t5 h\t31\t\t\t\ncodex\t7 j\t5\t\t\t\n' >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "26 multiple windows without optional fields" grep -q 'codex 5 h 31%' "$WORK/out.txt"
check "26 second window" grep -q 'codex 7 j 5%' "$WORK/out.txt"
printf 'v1\ncodex\t7 j\t5\t\t\t%s\n' "$((NOW - 1865))" >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "27 age in minutes" grep -q 'mesure il y a 31 min' "$WORK/out.txt"
printf 'v1\ncodex\t7 j\t5\t\t\t%s\n' "$((NOW - 172805))" >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "28 age in days" grep -q 'mesure il y a 2 j' "$WORK/out.txt"
printf 'v1\ncodex\t7 j\t5\tbad\t\tbad\n' >"$QUOTA_WIDGET_DATA/statusline.tsv"
compact_render
check "29 invalid numeric fields are ignored" no_codex
check "29 malformed view is silent" test ! -s "$WORK/err.txt"
if (( FAILS > 0 )); then printf '%d test(s) en échec\n' "$FAILS"; exit 1; fi
printf 'Tous les tests passent\n'
