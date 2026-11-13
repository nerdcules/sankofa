#!/bin/sh
# What other people write reaches you through the remote (a handoff letter, the tagger's name) and
# through the ledger. Here it is text: never part of a program sankofa runs, and never a control
# sequence that reaches your terminal.
. "$(dirname "$0")/../0.4/lib.sh"
start_daemon
as() { n=$1; shift; GIT_AUTHOR_NAME=$n GIT_AUTHOR_EMAIL=$n@x GIT_COMMITTER_NAME=$n GIT_COMMITTER_EMAIL=$n@x SANKOFA_SESSION=s-${n%%/*} "$@"; }
git init -q --bare -b main remote.git
as Alice git clone -q remote.git alice 2>/dev/null
cd alice && printf '.sankofa/\n' > .gitignore && echo 'int main;' > parse.c && git add -A && as Alice git commit -qm init && git push -q origin main 2>/dev/null
"$S" init >/dev/null
as Bob git clone -q "$T/remote.git" "$T/bob" 2>/dev/null
(cd "$T/bob" && "$S" init >/dev/null)

# a tagger whose name holds a slash, quotes, a backslash and an ampersand, and an open item with
# every character sed or a shell cares about
who='Alice/Ops "the one" \1 & co'
item='fix *.c; see /etc/hosts & "$HOME" (50% done) \2'
esc=$(printf 'see \033[31mred\033[0m then \033]0;title\007 end')
as "$who" git switch -qc parser
echo 'int parse;' >> parse.c && as "$who" git commit -qam "parse numbers"
as "$who" "$S" todo "$item" >/dev/null
as "$who" "$S" awaiting "$esc" >/dev/null
as "$who" "$S" handoff "numbers parse" >/dev/null
case $("$S" last 3) in *"$(printf '\033')"*) fail "sankofa last passed an escape sequence to the terminal" ;; esac
assert_contains "$("$S" last 3)" "see [31mred[0m then ]0;title end" "last: the control bytes are gone, the words stay"

cd "$T/bob"
out=$(as Bob "$S" pickup 2>&1)
assert_contains "$out" "picked up parser from $who" "the tagger's name is shown as written"
case $out in *"$(printf '\033')"*) fail "pickup printed the letter with its escape sequences" ;; esac
assert_contains "$out" "see [31mred[0m then ]0;title end" "pickup: the letter is printed without control bytes"
open=$("$S" open)
assert_contains "$open" "$item (from $who, was " "the open item is carried over as text, every character kept"
assert_eq "$(printf '%s\n' "$open" | grep -c 'from Alice/Ops')" 2 "both items came over, through a tagger name with a slash"
echo "ok hostile text"
