#!/usr/bin/env bash
# Legacy optional Bash status line. New Windows installations can use claude-statusline.ps1.
# Official input schema: https://code.claude.com/docs/en/statusline
set -euo pipefail

INPUT=$(cat)

MODEL=$(echo "$INPUT" | jq -r '.model.display_name // "?"')
CWD=$(echo "$INPUT" | jq -r '.workspace.current_dir // ""')
IN_TOK=$(echo "$INPUT" | jq -r '.context_window.total_input_tokens // 0')
OUT_TOK=$(echo "$INPUT" | jq -r '.context_window.total_output_tokens // 0')
WINDOW=$(echo "$INPUT" | jq -r '.context_window.context_window_size // 0')
# used_percentage est pre-calcule par Claude Code (input + cache_creation + cache_read,
# hors output). Peut etre null en debut de session et apres /compact.
PCT=$(echo "$INPUT" | jq -r '.context_window.used_percentage // empty' | cut -d. -f1)

# Display only the current folder name.
PROJET=$(basename "$CWD" 2>/dev/null || echo "?")

# Fallback si used_percentage absent : calcul local sur la meme base (input seul)
if [[ -z "$PCT" ]]; then
  if (( WINDOW > 0 )); then
    PCT=$(( IN_TOK * 100 / WINDOW ))
  else
    PCT=0
  fi
fi
(( WINDOW > 0 )) || WINDOW=200000

# Couleurs ANSI
RED=$'\033[1;31m'; YLW=$'\033[0;33m'; GRN=$'\033[0;32m'; CYN=$'\033[0;36m'; RST=$'\033[0m'
if   [ "$PCT" -gt 90 ]; then COLOR="$RED"
elif [ "$PCT" -gt 70 ]; then COLOR="$YLW"
else                         COLOR="$GRN"; fi

printf "%s[%s]%s %s%s%s | %sctx %d%% (%d/%d)%s" \
  "$CYN" "$PROJET" "$RST" "$GRN" "$MODEL" "$RST" \
  "$COLOR" "$PCT" "$((IN_TOK + OUT_TOK))" "$WINDOW" "$RST"

# Quotas d'abonnement : presents uniquement pour les comptes Pro/Max, apres la
# premiere reponse API, et chaque fenetre peut etre absente independamment.
RL5=$(echo "$INPUT" | jq -r '.rate_limits.five_hour.used_percentage // empty' | cut -d. -f1)
RL7=$(echo "$INPUT" | jq -r '.rate_limits.seven_day.used_percentage // empty' | cut -d. -f1)
if [[ -n "$RL5" || -n "$RL7" ]]; then
  printf " | %squota 5h %s%% / 7j %s%%%s" "$CYN" "${RL5:-?}" "${RL7:-?}" "$RST"
  # Optional legacy quota cache, replaced atomically.
  LEGACY="$HOME/.claude/state/claude-quotas.json"
  LEGACY_TMP="$LEGACY.$$.$RANDOM"
  if echo "$INPUT" | jq -c --arg at "$(date '+%Y-%m-%d %H:%M')" '{at: $at, rate_limits: .rate_limits}' \
      > "$LEGACY_TMP" 2>/dev/null; then
    mv -f "$LEGACY_TMP" "$LEGACY" 2>/dev/null || rm -f "$LEGACY_TMP"
  else
    rm -f "$LEGACY_TMP"
  fi
fi

# >>> quota-widget managed block
# Per-session Claude quotas: one file per session, replaced atomically. measured_at is
# the time of the last API response this session saw: it moves only when
# cost.total_api_duration_ms changes (either way, a resumed session can restart lower),
# stays as it was otherwise, and is null when no usable previous file exists (first
# render: age unknown). Token counters are stored for information only. Sessions are
# never merged here: the widget Core does that. One jq call and builtins only, because
# each process costs about 30 ms under Git Bash.
persist_session_quotas() {
  local sid dir file tmp prev=""
  [[ "$INPUT" =~ \"session_id\"[[:space:]]*:[[:space:]]*\"([A-Za-z0-9_-]+)\" ]] || return 0
  sid=${BASH_REMATCH[1]}
  dir="$HOME/.claude/state/claude-quotas.d"
  [[ -d "$dir" ]] || mkdir -p "$dir" || return 0
  file="$dir/$sid.json"
  if [[ -f "$file" ]]; then IFS= read -r -d '' prev <"$file" || true; fi
  tmp="$dir/.$sid.$$.$RANDOM"
  # jq checks the regex match against the parsed session_id and writes nothing on mismatch.
  jq -c --arg sid "$sid" --arg prev "$prev" '
    select(.session_id == $sid)
    | (now | floor | todate) as $now
    | (.cost.total_api_duration_ms // 0) as $api
    | ($prev | try fromjson catch null) as $p
    | (if ($p | type) == "object" and ($p.api_duration_ms | type) == "number" then
         (if $p.api_duration_ms == $api then ($p.measured_at | strings // null) else $now end)
       else null end) as $measured
    | {session_id: .session_id, rendered_at: $now, measured_at: $measured, api_duration_ms: $api,
       input_tokens: (.context_window.total_input_tokens // null), rate_limits: .rate_limits}' \
    <<<"$INPUT" >"$tmp" || { rm -f "$tmp"; return 0; }
  if [[ -s "$tmp" ]]; then mv -f "$tmp" "$file" || rm -f "$tmp"; else rm -f "$tmp"; fi
}
if [[ -n "$RL5" || -n "$RL7" ]]; then
  persist_session_quotas 2>/dev/null || true
fi

# Read the compact Core view with builtins only. Split fields explicitly because IFS
# treats adjacent tabs as whitespace and would lose unknown reset/measurement fields.
render_codex_quotas() {
  local data file version now row rest index valid label used reset reset_text measured
  local age unit segment output="" epoch
  local -a fields
  if [[ ${QUOTA_WIDGET_DATA+x} ]]; then
    data=$QUOTA_WIDGET_DATA
  else
    data=${LOCALAPPDATA:-}
    [[ -n "$data" ]] || return 0
    data="${data//\\//}/QuotaWidget"
  fi
  file="$data/statusline.tsv"
  [[ -f "$file" && -r "$file" ]] || return 0
  printf -v now '%(%s)T' -1
  {
    IFS= read -r version || return 0
    [[ "$version" == v1 ]] || return 0
    while IFS= read -r row; do
      fields=()
      rest=$row
      valid=1
      for ((index=0; index<5; index++)); do
        if [[ "$rest" != *$'\t'* ]]; then valid=0; break; fi
        fields+=("${rest%%$'\t'*}")
        rest=${rest#*$'\t'}
      done
      (( valid )) || continue
      [[ "$rest" != *$'\t'* ]] || continue
      fields+=("$rest")
      [[ ${fields[0]} == codex ]] || continue
      label=${fields[1]}; used=${fields[2]}; reset=${fields[3]}
      reset_text=${fields[4]}; measured=${fields[5]}
      [[ "$label" =~ ^[0-9]{1,12}\ (min|h|j)$ || "$label" == "durée inconnue" ]] || continue
      [[ "$used" =~ ^[0-9]{1,6}$ || -z "$used" ]] || continue
      [[ -z "$reset" || "$reset" =~ ^-?[0-9]{1,11}$ ]] || continue
      [[ -z "$measured" || "$measured" =~ ^-?[0-9]{1,11}$ ]] || continue
      [[ "$reset_text" =~ ^[[:alpha:].[:space:]0-9:]*$ && ${#reset_text} -le 24 ]] || continue
      printf -v segment ' | %scodex %s %s%%' "$CYN" "$label" "${used:-?}"
      if [[ -n "$reset" ]]; then
        if [[ "$reset" == -* ]]; then epoch=$((-10#${reset#-})); else epoch=$((10#$reset)); fi
        if (( epoch <= now )); then
          segment+=" (remise passée)"
        elif [[ -n "$reset_text" ]]; then
          segment+=" (remise $reset_text)"
        fi
      fi
      if [[ -n "$measured" ]]; then
        if [[ "$measured" == -* ]]; then epoch=$((-10#${measured#-})); else epoch=$((10#$measured)); fi
        age=$((now - epoch))
        if (( age > 1800 )); then
          if (( age >= 86400 )); then age=$((age / 86400)); unit=j
          elif (( age >= 3600 )); then age=$((age / 3600)); unit=h
          else age=$((age / 60)); unit=min; fi
          segment+=" · mesure il y a $age $unit"
        fi
      fi
      output+="$segment$RST"
    done
  } <"$file"
  printf '%s' "$output"
}
render_codex_quotas 2>/dev/null || true
# <<< quota-widget

[ "$PCT" -gt 90 ] && printf " %s!! /compact !!%s" "$RED" "$RST"
