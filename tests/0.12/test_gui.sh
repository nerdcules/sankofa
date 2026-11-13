#!/bin/sh
# 0.12: the dashboard in a window (sankofa-gui, Avalonia). The scene it draws, from the
# fixtures and from a live ledger; every look and picture rendered headless to a PNG; the
# Omarchy look following the theme file. Needs no display.
. "$(dirname "$0")/../0.1/lib.sh"
GUI=$ROOT/bin/out/sankofa-gui
[ -x "$GUI" ] || fail "build sankofa-gui first (make build)"
FX=$ROOT/tests/fixtures
NOW=2026-11-11T10:00:00.000Z
export XDG_CONFIG_HOME=$T/config

# the scene: one node per session (a hub-stamped event and a local one are the same session)
sc=$("$GUI" scene --now $NOW "$FX/api.jsonl" "$FX/web.jsonl")
assert_contains "$sc" "repos 2 · events 16 · waiting 2 · open 2 · leases 4 · alerts 2" "scene: counts"
assert_eq "$(printf '%s\n' "$sc" | grep -c '^node .* s-7f2a ')" 1 "scene: one node per session"
assert_contains "$sc" "device  local     laptop" "scene: a device from the hub's @device"
assert_contains "$sc" "device  remote    remote" "scene: a landed branch is the remote"
assert_contains "$sc" "tile    Contested api src/billing/**" "scene: a held path an overlap touches is contested"
assert_contains "$sc" "tile    Half      api src/auth/session.ts" "scene: an expired dirty lease may be half-finished"
assert_contains "$sc" "tile    Held      web src/components/Chart.tsx" "scene: a held lease"
assert_contains "$sc" "blip    Waiting   e6 waits" "scene: what waits on you is on the radar"
assert_contains "$sc" "overlap src/billing/tax.ts" "scene: the overlap path"

# every look and every picture renders, headless, at the size asked
png_size() { python3 -c 'import sys,struct; d=open(sys.argv[1],"rb").read(24); assert d[:8]==b"\x89PNG\r\n\x1a\n"; print("%dx%d" % struct.unpack(">II", d[16:24]))' "$1"; }
for look in windows mac breeze omarchy; do
  for pic in constellation map heatmap radar; do
    "$GUI" render --out "$T/$look-$pic.png" --look $look --picture $pic --now $NOW --size 1100x700 "$FX/api.jsonl" "$FX/web.jsonl" >/dev/null
    assert_eq "$(png_size "$T/$look-$pic.png")" "1100x700" "render $look $pic"
  done
done
"$GUI" render --out "$T/sel.png" --select e6 --now $NOW "$FX/api.jsonl" >/dev/null || fail "render with a row selected"

# a live ledger: what the CLI writes shows up in the next look, and a done takes it away
mkdir repo && cd repo && "$S" init >/dev/null
"$S" awaiting "approve the schema change" >/dev/null
id=$("$S" todo "write the migration")
sc=$("$GUI" scene)
assert_contains "$sc" "waiting 1 · open 1" "live: the CLI's writes are in the scene"
assert_contains "$sc" "blip    Todo      $id" "live: the todo is on the radar"
"$S" done "$id" "written" >/dev/null
assert_contains "$("$GUI" scene)" "waiting 1 · open 0" "live: done takes it off"
cd "$T"

# Omarchy: the look is read from the current theme's colors.toml (pixel at the stage's corner)
pixel() { python3 - "$1" "$2" "$3" <<'EOF'
import sys, zlib, struct
f, x, y = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
d = open(f, "rb").read(); i = 8; idat = b""
while i < len(d):
    n, t = struct.unpack(">I4s", d[i:i+8]); c = d[i+8:i+8+n]
    if t == b"IHDR": w, h, depth, ct = struct.unpack(">IIBB", c[:10])
    if t == b"IDAT": idat += c
    i += 12 + n
bpp = 4 if ct == 6 else 3; raw = zlib.decompress(idat); stride = w * bpp; prev = bytearray(stride); rows = []
for r in range(h):
    ft = raw[r*(stride+1)]; line = bytearray(raw[r*(stride+1)+1:(r+1)*(stride+1)])
    for k in range(stride):
        a = line[k-bpp] if k >= bpp else 0; b = prev[k]; c2 = prev[k-bpp] if k >= bpp else 0
        if ft == 1: line[k] = (line[k] + a) & 255
        elif ft == 2: line[k] = (line[k] + b) & 255
        elif ft == 3: line[k] = (line[k] + (a + b) // 2) & 255
        elif ft == 4:
            p = a + b - c2; pa, pb, pc = abs(p-a), abs(p-b), abs(p-c2)
            line[k] = (line[k] + (a if pa <= pb and pa <= pc else b if pb <= pc else c2)) & 255
    rows.append(line); prev = line
px = rows[y][x*bpp:x*bpp+3]
print("#%02X%02X%02X" % tuple(px))
EOF
}
mkdir -p "$T/home/.local/state/omarchy/current/theme"
printf 'accent = "#ff8800"\ndark_background = "#102030"\nbackground = "#203040"\nforeground = "#e0e0e0"\n' \
  > "$T/home/.local/state/omarchy/current/theme/colors.toml"
HOME=$T/home "$GUI" render --out "$T/om.png" --look omarchy --picture heatmap --now $NOW --size 900x600 "$FX/api.jsonl" >/dev/null
assert_eq "$(pixel "$T/om.png" 200 590)" "#102030" "omarchy: the stage takes the theme's dark background"
HOME=$T/nohome "$GUI" render --out "$T/tn.png" --look omarchy --picture heatmap --now $NOW --size 900x600 "$FX/api.jsonl" >/dev/null
assert_eq "$(pixel "$T/tn.png" 200 590)" "#16161E" "omarchy without a theme file: Tokyo Night"

mkdir "$T/empty"; set +e; err=$(cd "$T/empty" && "$GUI" 2>&1); rc=$?; set -e
assert_eq "$rc" 1 "no repo here and none watched"
assert_contains "$err" "sankofa watch add" "says how to get one"
assert_contains "$("$GUI" version)" "sankofa-gui " "version"
echo "ok gui"
