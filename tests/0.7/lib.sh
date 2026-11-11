# helpers for the 0.7 hub tests: one hub, two devices (desk, lap), each with its own sankofad
. "$(dirname "$0")/../0.4/lib.sh"
HUBDIR=$TD/hub
PORT=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])')
HUB=http://127.0.0.1:$PORT
HPID=""; APID=""; BPID=""
export SANKOFA_HUB_TOKENS=$HUBDIR/tokens SANKOFA_HUB_DATA=$HUBDIR/data SANKOFA_HUB_BREAKER=0.3 SANKOFA_HUB_RETRY_MAX=0.5
start_hub() {
  "$SANKOFAD" hub serve --listen "$HUB/" --db "$HUBDIR/hub.db" 2>>"$TD/hub.log" & HPID=$!
  i=0; until curl -fs "$HUB/healthz" >/dev/null 2>&1; do
    i=$((i + 1)); [ $i -lt 200 ] || { cat "$TD/hub.log" >&2; fail "hub did not start"; }; sleep 0.02
  done
}
stop_hub() { [ -n "$HPID" ] && { kill "$HPID"; wait "$HPID" 2>/dev/null || :; HPID=""; }; }
# dev <a|b> <command...>: run with that device's daemon and hub.conf
dev() {
  d=$1; shift
  SANKOFA_SOCKET=$TD/$d.sock SANKOFA_HUB_CONF=$TD/$d.conf "$@"
}
start_dev() { # start_dev a|b
  SANKOFA_HUB_CONF=$TD/$1.conf "$SANKOFAD" serve --socket "$TD/$1.sock" --db "$TD/$1.db" 2>>"$TD/$1.log" &
  eval "${1}PID=\$!"
  i=0; until printf '{"verb":"ping"}' | "$SANKOFAD" send --socket "$TD/$1.sock" >/dev/null 2>&1; do
    i=$((i + 1)); [ $i -lt 200 ] || { cat "$TD/$1.log" >&2; fail "sankofad $1 did not start"; }; sleep 0.01
  done
}
stop_dev() { p=$(eval "printf '%s' \"\${${1}PID}\""); [ -n "$p" ] && { kill "$p"; wait "$p" 2>/dev/null || :; eval "${1}PID=''"; }; }
hub_exit() { stop_dev a; stop_dev b; stop_hub; on_exit; }
trap hub_exit EXIT
# wait_for <seconds> <shell condition>
wait_for() { n=$(( $1 * 20 )); shift; i=0; until eval "$@"; do i=$((i + 1)); [ $i -lt $n ] || return 1; sleep 0.05; done; }
count() { jq -c "select($2)" "$1" 2>/dev/null | grep -c . || :; }
setup_two() { # hub, tokens, two devices with a repo each attached as "demo"
  mkdir -p "$HUBDIR"
  ta=$("$SANKOFAD" hub token add desk 2>/dev/null); tb=$("$SANKOFAD" hub token add lap 2>/dev/null)
  start_hub
  dev a "$S" hub join "$HUB" "$ta" desk >/dev/null
  dev b "$S" hub join "$HUB" "$tb" lap >/dev/null
  start_dev a; start_dev b
  mkdir -p "$T/desk/repo" "$T/lap/repo"
  (cd "$T/desk/repo" && "$S" init >/dev/null && dev a "$S" note "desk history before the hub" && dev a "$S" hub on demo >/dev/null)
  (cd "$T/lap/repo" && "$S" init >/dev/null && dev b "$S" note "lap history, kept aside" && dev b "$S" hub on demo >/dev/null)
  A=$T/desk/repo/.sankofa/ledger.jsonl; B=$T/lap/repo/.sankofa/ledger.jsonl
}
