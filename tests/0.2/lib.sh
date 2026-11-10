# shared helpers for 0.2 (watch); builds on the 0.1 helpers
. "$(dirname "$0")/../0.1/lib.sh"
W=$ROOT/bin/sankofa-watch
export XDG_CONFIG_HOME=$T/config XDG_STATE_HOME=$T/state TOASTS=$T/toasts
unset SANKOFA_TOAST_TYPES SANKOFA_TOAST_BURST SANKOFA_WATCH_TIMEOUT || true
mkdir -p "$T/fakebin"
cat > "$T/fakebin/notify-send" <<'FAKE'
#!/bin/sh
# records one toast per line: urgency <tab> title <tab> body (newlines in body become |)
urg=normal
while [ $# -gt 2 ]; do case $1 in -u) urg=$2; shift 2 ;; -a) shift 2 ;; *) shift ;; esac; done
printf '%s\t%s\t%s\n' "$urg" "$1" "$(printf '%s' "$2" | tr '\n' '|')" >> "$TOASTS"
FAKE
chmod +x "$T/fakebin/notify-send"
export PATH=$T/fakebin:$PATH
: > "$TOASTS"
toasts() { grep -c . "$TOASTS" || true; }
mkrepo() { mkdir -p "$T/$1" && (cd "$T/$1" && "$S" init >/dev/null); }
