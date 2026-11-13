# How this came about

I am a developer, and the thing I love most about this moment is that an idea
I would once have shelved as "too much work for one person" can now be built
in an evening. Sankofa is one of those ideas. This is where it came from.

## The first half of 2026

By the spring of 2026 I was not writing code the way I used to. I was running
AI coding sessions: a main one in Claude Code, a couple of subagents it had
spun off, Codex in a second terminal because it was better at some things,
and a scheduled run in the cloud that pushed fixes while I slept. All of them
on the same repository. I was less a programmer than a technician driving a
small team that never spoke to each other.

And they really did not speak to each other. The channels between them were
plain text: a prompt in, a report out. Nothing durable sat between them.

The same things kept happening.

- I would clear a session, or it would compact, or crash, and the next one
  did not know that the package had been built but not installed, because
  installing needed my sudo password. It would rebuild. It would ask me
  questions I had answered on Tuesday.
- Two subagents would each edit a shared header. Both reports said "done".
  The build found out.
- Codex and Claude Code had no idea of each other. Two instruction files, two
  hook systems, no shared memory of any kind.
- Something landed at three in the morning from a cloud run, and my morning
  session started from stale assumptions.
- My desktop and my laptop each had their own opinion about who was working
  on what.

What I wanted was small. A place every session goes back to before it starts
and writes to before it stops. What was done. What is open. What is waiting
on me. Who is working on which files right now. That is also what the Akan
word *sankofa* means: go back and fetch what was left behind. The bird in the
symbol walks forward with its head turned back. That was the name before
there was a line of code.

## What it had to be, and not be

I wrote the rules down before building anything, because I knew what I would
be tempted to build instead. It must never decide anything. It is not an
orchestrator, not a chat bus between agents, not a replacement for git. It
records and it answers. Every output is a record or an answer to a question.
Enforcement, when there is any, lives at the edge: a hook that refuses an
edit, a git hook that refuses a commit. The daemon itself blocks nothing.

Those rules are still in [OBJECTIVE.md](OBJECTIVE.md), and the situations
that drove them are in [SCENARIOS.md](SCENARIOS.md). Every one of the
scenarios happened on my machine.

## Building it

The idea sat in my head for months. The build took days, in November 2026,
and that is the part I still find a little hard to believe. I did not build
it alone; I built it the way the problem itself describes: with several AI
sessions, on one codebase, which is also how it got tested for real, because
the sessions building it were stepping on each other until the edit gate
existed.

A few things I decided on the way that turned out to matter:

- **Iterations, each with tests, each used before the next.** The roadmap
  has thirteen so far. Nothing moved on until the thing before it worked.
- **Simulate the week instead of waiting for it.** "Use it for a week" was
  the gate for each iteration, and a week is a long time. So each iteration
  has a simulation that drives the real code through a week in minutes:
  sessions that crash, daemons killed mid-write, laptops with the lid shut,
  two people picking up the same handoff at once. The numbers in the README
  come from those. They found real bugs every single time.
- **Shell first, then one small binary.** The ledger is a text file and the
  CLI is POSIX shell, so it works with nothing installed. The daemon is one
  native binary that starts in a few milliseconds, because it runs on every
  edit.
- **Plain words.** Lease, holder, gate, handoff. If I could not explain a
  part in a sentence to a friend, it was probably the wrong part.

## Where it is going

It runs on Linux today because that is what I use. A Mac port of the daemon
is the next big piece. After that: the same idea for people who do not write
code at all, who run several AI sessions on documents and plans rather than
repositories, and who have exactly the same problem.

If you are the kind of person who has a folder of ideas that were always too
big for one person, go and look at that folder again. That is really the
whole story.
