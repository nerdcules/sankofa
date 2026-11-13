#!/usr/bin/env python3
"""Accelerated week for the hub: one real sankofa-hub, two devices (desk and lap, each a real
sankofad with the real CLI and edit gate), and a cloud session speaking MCP over HTTP.

Two backends run the same days and the same checks:

  local   (default) every process on this machine; each device reaches the hub through a TCP
          proxy the simulation controls
  docker  (--docker) the hub, the desk and the lap are separate containers on their own
          network, each set up by install.sh (--hub on the hub box) and sankofa hub join,
          daemons started from the units install.sh wrote. Nothing is shared but the network.

The network fails the way real ones do:

  cut        connections refused or reset (wifi off, VPN down); in docker the box is
             unplugged from the network (docker network disconnect)
  blackhole  connections stay open and nothing comes back (a dead route: no reset, silence);
             in docker a firewall rule in the box drops every packet
  sleep      the laptop shuts its lid: silent like a blackhole and every process frozen
             (docker pause; locally SIGSTOP), then it wakes with its connections stale

Each simulated day: sessions on both devices write notes, todos, awaiting items and dones,
claim paths through the edit gate, and end (finishing their leases); the laptop spends part of
the day offline (cut, blackhole or asleep); now and then the desktop drops too, the hub restarts
(a service restart, or in docker a crash that kills the whole box), or the laptop's sankofad is
killed and the CLI falls back to the file (in docker, sometimes the whole laptop reboots).
With --docker --skew N the laptop's clock runs N seconds ahead (libfaketime).

Checks, per run:
  exact      every acknowledged write is on the hub exactly once (by nonce), whether it went
             straight there or was queued offline; no queued (q...) ids remain on any device
  converge   after the last day, desk, lap and the hub hold the same events in the same order
  offline    every offline claim on a path another device held is flagged on the hub
  latency    edit gate through the hub (online), and while offline (allow-and-flag)

Usage: sim/hub.py [--seeds 3] [--days 7] [--docker [--skew 90]] [--out FILE]
"""
import argparse, json, os, random, shutil, signal, socket, statistics, subprocess, sys, tempfile, threading, time, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = os.path.join(ROOT, "bin", "sankofa")
D = os.environ.get("SANKOFA_DAEMON_BIN", os.path.join(ROOT, "bin", "out", "sankofad"))
H = os.path.join(ROOT, "bin", "out", "sankofa-hub")
PATHS = [f"src/{m}/{f}.c" for m in ("net", "ui", "db", "core") for f in ("a", "b", "c")] + ["docs/plan.md", "README.md"]
REPO = "github.com/me/project"
# compressed time: the hub's timers run in seconds rather than minutes
SPEED = dict(SANKOFA_HUB_BREAKER="0.3", SANKOFA_HUB_RETRY_MAX="0.5", SANKOFA_HUB_TIMEOUT="1", SANKOFA_HUB_SILENCE="1.5",
             SANKOFA_HUB_KEEPALIVE="0.3", SANKOFA_RESCAN="3600")


def free_port():
    s = socket.socket(); s.bind(("127.0.0.1", 0)); p = s.getsockname()[1]; s.close(); return p


def payload(session, repo, path):
    return json.dumps({"session_id": session, "cwd": repo, "hook_event_name": "PreToolUse", "tool_name": "Edit",
                       "tool_input": {"file_path": f"{repo}/{path}", "old_string": "a", "new_string": "b"}})


def parse(text):
    return [json.loads(l) for l in text.splitlines() if l.strip()]


# ---------------------------------------------------------------- local backend

class Proxy:
    """TCP proxy in front of the hub: up, cut (refuse and reset) or blackhole (accept, never answer)."""

    def __init__(self, target):
        self.target, self.port, self.mode = target, free_port(), "up"
        self.conns, self.lock, self.lsock = [], threading.Lock(), None
        self.listen()

    def listen(self):
        self.lsock = socket.socket(); self.lsock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.lsock.bind(("127.0.0.1", self.port)); self.lsock.listen(64)
        threading.Thread(target=self.accept, args=(self.lsock,), daemon=True).start()

    def accept(self, ls):
        while True:
            try: c, _ = ls.accept()
            except OSError: return
            if self.mode == "blackhole":
                with self.lock: self.conns.append(c)
                continue
            try: u = socket.create_connection(("127.0.0.1", self.target), timeout=2)
            except OSError: c.close(); continue
            with self.lock: self.conns += [c, u]
            for a, b in ((c, u), (u, c)): threading.Thread(target=self.pipe, args=(a, b), daemon=True).start()

    def pipe(self, a, b):
        try:
            while True:
                d = a.recv(65536)
                if not d: break
                if self.mode == "up": b.sendall(d)
                elif self.mode == "blackhole": continue   # swallowed: the sender hears nothing back
                else: break
        except OSError: pass
        for s in (a, b):
            try: s.close()
            except OSError: pass

    def drop_all(self):
        with self.lock:
            for c in self.conns:
                try: c.close()
                except OSError: pass
            self.conns = []

    def set(self, mode):
        if mode == self.mode: return
        if self.mode == "cut": self.listen()
        self.mode = mode
        if mode == "cut":
            self.lsock.close(); self.drop_all()
        elif mode == "blackhole":
            pass   # established connections go silent: stop forwarding without closing (a dead route)
        else:
            self.drop_all()


class LocalHub:
    def __init__(self, base, env):
        self.base, self.env, self.port, self.p = base, env, free_port(), None
        self.log = open(os.path.join(base, "hub.log"), "a")
        self.mcp_url = f"http://127.0.0.1:{self.port}/mcp"

    def token(self, device):
        return subprocess.run([H, "token", "add", device], env=self.env, capture_output=True, text=True).stdout.strip()

    def start(self):
        self.p = subprocess.Popen([H, "serve", "--listen", f"http://127.0.0.1:{self.port}/", "--db", os.path.join(self.base, "hub.db")],
                                  env=self.env, stderr=self.log)
        for _ in range(300):
            try: urllib.request.urlopen(f"http://127.0.0.1:{self.port}/healthz", timeout=1); return
            except OSError: time.sleep(0.02)
        raise SystemExit("hub did not start")

    def stop(self, how="service"):
        self.p.send_signal(signal.SIGKILL if how == "crash" else signal.SIGTERM); self.p.wait()

    def alive(self): return self.p is not None and self.p.poll() is None

    def close(self):
        if self.alive(): self.stop()

    def ledger(self):
        with open(os.path.join(self.base, "hubdata", "repos", REPO, ".sankofa", "ledger.jsonl")) as f: return parse(f.read())


class LocalDevice:
    def __init__(self, name, base, hub, token, env):
        self.name, self.base = name, base
        self.proxy = Proxy(hub.port)
        self.sock, self.db = os.path.join(base, f"{name}.sock"), os.path.join(base, f"{name}.db")
        self.conf = os.path.join(base, f"{name}.conf")
        self.repo = os.path.join(base, name, "repo"); os.makedirs(self.repo)
        self.env = dict(env, SANKOFA_SOCKET=self.sock, SANKOFA_HUB_CONF=self.conf)
        self.p, self.asleep = None, False
        self.log = open(os.path.join(base, f"{name}.log"), "a")
        self.cli("hub", "join", f"http://127.0.0.1:{self.proxy.port}", token, name)

    @property
    def mode(self): return "sleep" if self.asleep else self.proxy.mode
    @property
    def running(self): return self.p is not None

    def setup(self):
        self.start()
        self.cli("init"); r = self.cli("hub", "on", REPO)
        assert r.returncode == 0, r.stderr

    def start(self):
        self.p = subprocess.Popen([D, "serve", "--socket", self.sock, "--db", self.db], env=self.env, stderr=self.log)
        for _ in range(300):
            if self.send({"verb": "ping"}).returncode == 0: return
            time.sleep(0.01)
        raise SystemExit(f"sankofad {self.name} did not start")

    def kill(self, reboot=False):
        self.p.send_signal(signal.SIGKILL); self.p.wait(); self.p = None

    def stop(self):
        if self.p: self.p.send_signal(signal.SIGTERM); self.p.wait(); self.p = None

    def send(self, req):
        return subprocess.run([D, "send", "--socket", self.sock], input=json.dumps(req), text=True, capture_output=True)

    def cli(self, *a, session="-"):
        return subprocess.run([S, *a], cwd=self.repo, env=dict(self.env, SANKOFA_SESSION=session), capture_output=True, text=True, timeout=30)

    def gate(self, session, path):
        t = time.perf_counter()
        r = subprocess.run([D, "gate", "claude-code"], input=payload(session, self.repo, path), env=self.env, capture_output=True, text=True)
        return (time.perf_counter() - t) * 1000, ("deny" in r.stdout)

    def net(self, mode): self.proxy.set(mode)

    def sleep(self):
        self.proxy.set("blackhole"); self.asleep = True
        if self.p: self.p.send_signal(signal.SIGSTOP)

    def wake(self):
        if self.p: self.p.send_signal(signal.SIGCONT)
        self.asleep = False; self.proxy.set("up")

    def ledger(self):
        with open(os.path.join(self.repo, ".sankofa", "ledger.jsonl")) as f: return parse(f.read())


class Local:
    name = "local (one machine, a proxy per device)"

    def __init__(self, tmp): self.tmp = tmp

    def world(self, seed):
        base = tempfile.mkdtemp(prefix=f"hub{seed}-", dir=self.tmp)
        env = dict(os.environ, SANKOFA_HUB_TOKENS=os.path.join(base, "tokens"), SANKOFA_HUB_DATA=os.path.join(base, "hubdata"), **SPEED)
        for k in ("CLAUDE_CODE_SESSION_ID", "SANKOFA_NO_DAEMON", "SANKOFA_AGENT", "SANKOFA_REQUIRE_NOTE"): env.pop(k, None)
        hub = LocalHub(base, env)
        tokens = {d: hub.token(d) for d in ("desk", "lap", "cloud")}
        hub.start()
        devs = [LocalDevice(n, base, hub, tokens[n], env) for n in ("desk", "lap")]
        for d in devs: d.setup()
        return hub, devs, tokens["cloud"], lambda: shutil.rmtree(base, ignore_errors=True)

    def close(self): pass


# ---------------------------------------------------------------- docker backend

def dk(*a, input=None, check=True, timeout=120):
    r = subprocess.run(["docker", *a], input=input, capture_output=True, text=True, timeout=timeout)
    if check and r.returncode != 0: raise SystemExit(f"docker {' '.join(a[:3])}: {r.stderr.strip()}")
    return r


def shq(s):
    return "'" + str(s).replace("'", "'\\''") + "'"


class Box:
    """One container: a machine of its own (filesystem, network stack, processes)."""

    def __init__(self, world, name, env, net_admin=False):
        self.c, self.world = f"{world.prefix}-{name}", world
        flags = ["--cap-add", "NET_ADMIN"] if net_admin else []
        envs = [x for k, v in env.items() for x in ("-e", f"{k}={v}")]
        dk("run", "-d", "--init", "--name", self.c, "--hostname", name, "--network", world.net, "--network-alias", name,
           *flags, *envs, world.image)

    def sh(self, cmd, user="dev", input=None, env=None, check=True, cwd=None, detach=False, timeout=60):
        a = ["exec", "-u", user] + (["-i"] if input is not None else []) + (["-d"] if detach else [])
        a += [x for k, v in (env or {}).items() for x in ("-e", f"{k}={v}")] + (["-w", cwd] if cwd else [])
        return dk(*a, self.c, "sh", "-c", cmd, input=input, check=check, timeout=timeout)

    def ip(self):
        return dk("inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", self.c).stdout.strip()

    def install(self, *args, env=None):
        return self.sh("cd /opt/sankofa && ./install.sh " + " ".join(args),
                       env=dict(SANKOFA_NO_SYSTEMD="1", SANKOFA_NO_MCP="1", SANKOFA_NO_BUILD="1", **(env or {}))).stdout

    def wait_gone(self, pattern):
        for _ in range(200):
            if self.sh(f"pgrep -f '{pattern}'", check=False).returncode != 0: return
            time.sleep(0.05)
        raise SystemExit(f"{self.c}: {pattern} did not stop")

    def close(self): pass


class DockerHub(Box):
    def __init__(self, world, env):
        super().__init__(world, "hub", env)
        out = self.install("--hub", env=dict(SANKOFA_HUB_LISTEN="http://+:8787/"))
        assert "sankofa-hub listening on http://+:8787/" in out, out
        self.up = False

    @property
    def mcp_url(self): return f"http://{self.ip()}:8787/mcp"

    def token(self, device): return self.sh(f"sankofad hub token add {device} 2>/dev/null").stdout.strip()

    def start(self):
        self.sh("start-unit sankofa-hub.service", detach=True)
        url = f"http://{self.ip()}:8787/healthz"
        for _ in range(300):
            try: urllib.request.urlopen(url, timeout=1); self.up = True; return
            except OSError: time.sleep(0.03)
        raise SystemExit("hub did not start: " + self.sh("cat ~/sankofa-hub.service.log", check=False).stdout)

    def stop(self, how="service"):
        self.up = False
        if how == "crash":
            dk("kill", self.c); dk("start", self.c)   # the box dies with everything on it, then boots
        else:
            self.sh("pkill -TERM -f 'sankofa-hub serve'"); self.wait_gone("sankofa-hub serve")

    def alive(self): return self.up

    def ledger(self): return parse(self.sh(f"cat ~/.local/share/sankofa/hub/repos/{REPO}/.sankofa/ledger.jsonl").stdout)


class DockerDevice(Box):
    REPO_DIR = "/home/dev/project"

    def __init__(self, world, name, env, token):
        super().__init__(world, name, env, net_admin=True)
        self.name, self.mode_, self.p_running, self.asleep = name, "up", False, False
        self.install()
        r = self.sh(f"sankofa hub join http://hub:8787 {token} {name}", check=False)
        assert r.returncode == 0, r.stderr

    @property
    def mode(self): return "sleep" if self.asleep else self.mode_
    @property
    def running(self): return self.p_running

    def setup(self):
        self.start()
        self.sh(f"mkdir -p {self.REPO_DIR} && cd {self.REPO_DIR} && git init -q && sankofa init >/dev/null")
        r = self.cli("hub", "on", REPO)
        assert r.returncode == 0, r.stderr

    def start(self):
        self.sh("start-unit sankofad.service", detach=True)
        for _ in range(300):
            if self.send({"verb": "ping"}).returncode == 0: self.p_running = True; return
            time.sleep(0.03)
        raise SystemExit(f"sankofad {self.name} did not start: " + self.sh("cat ~/sankofad.service.log", check=False).stdout)

    def kill(self, reboot=False):
        if reboot:
            dk("restart", "-t", "0", self.c)   # the whole laptop: power off and on
            if self.mode_ == "blackhole": self.mode_ = "up"   # the firewall rule did not survive the reboot
        else:
            self.sh("pkill -KILL -f 'sankofad serve'"); self.wait_gone("sankofad serve")
        self.p_running = False

    def stop(self):
        if self.p_running: self.sh("pkill -TERM -f 'sankofad serve'"); self.wait_gone("sankofad serve"); self.p_running = False

    def send(self, req): return self.sh("sankofad send", input=json.dumps(req), check=False)

    def cli(self, *a, session="-"):
        return self.sh(" ".join(["sankofa", *map(shq, a)]), env=dict(SANKOFA_SESSION=session), cwd=self.REPO_DIR, check=False, timeout=30)

    def gate(self, session, path):
        r = self.sh("timed-gate", input=payload(session, self.REPO_DIR, path), check=False)
        us, _, out = r.stdout.partition("\n")
        return int(us) / 1000, ("deny" in out)

    def net(self, mode):
        if mode == self.mode_: return
        if self.mode_ == "cut": dk("network", "connect", "--alias", self.name, self.world.net, self.c)
        if self.mode_ == "blackhole": self.sh("net up", user="root")
        if mode == "cut": dk("network", "disconnect", self.world.net, self.c)
        if mode == "blackhole": self.sh("net blackhole", user="root")
        self.mode_ = mode

    def sleep(self):
        self.net("up"); self.sh("net blackhole", user="root"); dk("pause", self.c); self.asleep = True

    def wake(self):
        dk("unpause", self.c); self.sh("net up", user="root"); self.asleep = False

    def ledger(self): return parse(self.sh(f"cat {self.REPO_DIR}/.sankofa/ledger.jsonl").stdout)


class Docker:
    def __init__(self, tmp, skew):
        self.name = "docker (hub, desk and lap each a container" + (f"; the lap's clock {skew:+d} s" if skew else "") + ")"
        self.skew, self.prefix, self.image = skew, f"sksim-{os.getpid()}", f"sankofa-sim:{os.getpid()}"
        for b in ("sankofad", "sankofa-mcp", "sankofa-hub"):
            if not os.access(os.path.join(ROOT, "bin", "out", b), os.X_OK): raise SystemExit(f"build first (make build): bin/out/{b}")
        # the build context: the tracked files plus the built binaries (docs left out)
        files = subprocess.run(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.split("\0")
        files = [f for f in files if f and not f.startswith("docs/") and os.path.isfile(os.path.join(ROOT, f))]
        files += [f"bin/out/{b}" for b in ("sankofad", "sankofa-mcp", "sankofa-hub")]
        ctx = os.path.join(tmp, "context")
        for f in files:
            os.makedirs(os.path.dirname(os.path.join(ctx, f)), exist_ok=True)
            shutil.copy2(os.path.join(ROOT, f), os.path.join(ctx, f))
        shutil.copy2(os.path.join(ROOT, "tests/0.7/docker/Dockerfile"), os.path.join(ctx, "Dockerfile"))
        print(f"building {self.image} ...", file=sys.stderr)
        r = subprocess.run(["docker", "build", "-q", "-t", self.image, ctx], capture_output=True, text=True)
        if r.returncode != 0: raise SystemExit("docker build failed:\n" + r.stderr[-3000:])
        self.net = None

    def world(self, seed):
        self.net = f"{self.prefix}-s{seed}"
        dk("network", "create", self.net)
        boxes = []
        def cleanup():
            for b in boxes: dk("rm", "-f", b.c, check=False)
            dk("network", "rm", self.net, check=False)
        try:
            env = dict(SPEED)
            hub = DockerHub(self, env); boxes.append(hub)
            tokens = {d: hub.token(d) for d in ("desk", "lap", "cloud")}
            hub.start()
            lap_env = dict(env, LD_PRELOAD="/usr/lib/faketime/libfaketime.so.1", FAKETIME=f"{self.skew:+d}",
                           FAKETIME_DONT_FAKE_MONOTONIC="1", FAKETIME_NO_CACHE="1") if self.skew else env
            desk = DockerDevice(self, "desk", env, tokens["desk"]); boxes.append(desk)
            lap = DockerDevice(self, "lap", lap_env, tokens["lap"]); boxes.append(lap)
            for d in (desk, lap): d.setup()
            if self.skew:
                got = int(lap.sh("date +%s").stdout) - int(time.time())
                assert abs(got - self.skew) <= 2, f"the lap's clock is {got:+d} s, wanted {self.skew:+d}"
        except BaseException:
            if not os.environ.get("SKSIM_KEEP"): cleanup()
            raise
        return hub, [desk, lap], tokens["cloud"], cleanup

    def close(self):
        dk("rmi", "-f", self.image, check=False)


# ---------------------------------------------------------------- the simulation

def mcp(url, token, calls):
    sess = None
    def rpc(method, params, id):
        nonlocal sess
        h = {"Authorization": f"Bearer {token}", "Content-Type": "application/json"}
        if sess: h["Mcp-Session-Id"] = sess
        body = {"jsonrpc": "2.0", "method": method, "params": params}
        if id is not None: body["id"] = id
        with urllib.request.urlopen(urllib.request.Request(url, json.dumps(body).encode(), h), timeout=5) as r:
            sess = r.headers.get("Mcp-Session-Id") or sess
            d = r.read()
            return json.loads(d) if d else None
    rpc("initialize", {"protocolVersion": "2025-06-18", "clientInfo": {"name": "cloud-routine"}, "capabilities": {}}, 1)
    return [rpc("tools/call", {"name": n, "arguments": a}, 10 + i)["result"] for i, (n, a) in enumerate(calls)]


def one(seed, days, backend):
    rnd = random.Random(seed)
    hub, devs, cloud_token, cleanup = backend.world(seed)
    try:
        return run(seed, days, rnd, hub, devs, cloud_token, isinstance(backend, Docker))
    finally:
        for d in devs:
            if d.asleep: d.wake()
            d.stop()
        hub.close()
        if not os.environ.get("SKSIM_KEEP"): cleanup()


def run(seed, days, rnd, hub, devs, cloud_token, docker):
    desk, lap = devs
    acked = {}           # nonce-less writes are matched by their unique text
    st = dict(writes=0, queued=0, claims=0, denied=0, offline_claims=0, gate_online=[], gate_offline=[], cuts=0,
              blackholes=0, sleeps=0, hub_restarts=0, hub_crashes=0, daemon_kills=0, reboots=0, mcp_calls=0)
    open_items = []      # (text, device) of todos/awaiting that were acknowledged
    n = 0
    t_start = time.time()

    def write(d, kind, session):
        nonlocal n
        n += 1
        text = f"s{seed} {d.name} {kind} {n}"
        if kind == "done":
            if not open_items: return
            i = rnd.randrange(len(open_items)); t, _ = open_items[i]
            ids = [e["id"] for e in d.ledger() if e.get("text") == t]
            if not ids: return
            r = d.cli("done", ids[-1], text, session=session)
            if r.returncode == 0: open_items.pop(i)
        else:
            r = d.cli(kind, text, session=session)
            if r.returncode == 0 and kind in ("todo", "awaiting"): open_items.append((text, d.name))
        if r.returncode == 0:
            acked[text] = d.name; st["writes"] += 1
            if d.mode != "up" or not d.running: st["queued"] += 1

    for day in range(days):
        # the laptop's day: some of it offline
        lap_off = rnd.random() < 0.8
        off_at, on_at = rnd.randint(10, 40), rnd.randint(50, 80)
        lap_mode = rnd.choice(["cut", "blackhole", "sleep"])
        desk_blip = rnd.random() < 0.3
        restart_hub = rnd.random() < 0.25
        hub_how = rnd.choice(["service", "crash"]) if docker else "service"
        kill_lap = rnd.random() < 0.2
        reboot_lap = docker and rnd.random() < 0.5
        for step in range(90):
            if lap_off and step == off_at:
                if lap_mode == "sleep": lap.sleep(); st["sleeps"] += 1
                else: lap.net(lap_mode); st["cuts" if lap_mode == "cut" else "blackholes"] += 1
            if lap_off and step == on_at:
                if lap.asleep: lap.wake()
                else: lap.net("up")
            if desk_blip and step == 30: desk.net("cut"); st["cuts"] += 1
            if desk_blip and step == 36: desk.net("up")
            if restart_hub and step == 60:
                hub.stop(hub_how); st["hub_crashes" if hub_how == "crash" else "hub_restarts"] += 1
            if restart_hub and step == 63: hub.start()
            if kill_lap and step == 20 and lap.running and not lap.asleep:
                lap.kill(reboot=reboot_lap); st["reboots" if reboot_lap else "daemon_kills"] += 1
            if kill_lap and step == 26 and not lap.running and not lap.asleep: lap.start()
            d = rnd.choice([x for x in devs if not x.asleep])
            session = f"{d.name}-d{day}-{rnd.randint(0, 2)}"
            r = rnd.random()
            if r < 0.35: write(d, rnd.choice(["note", "note", "todo", "awaiting"]), session)
            elif r < 0.5: write(d, "done", session)
            elif r < 0.85:
                path = rnd.choice(PATHS)
                offline = d.mode != "up" or not hub.alive()
                if not d.running: continue
                ms, denied = d.gate(session, path)
                st["claims"] += 1; st["denied"] += denied
                (st["gate_offline"] if offline else st["gate_online"]).append(ms)
                if offline: st["offline_claims"] += 1
            elif r < 0.92:
                if d.running: d.send({"verb": "finish", "session": session, "text": "session ended"})
            else:
                if hub.alive():
                    try:
                        mcp(hub.mcp_url, cloud_token, [["note", {"repo": REPO, "text": f"s{seed} cloud note {n}"}],
                                                       ["claim", {"repo": REPO, "glob": rnd.choice(PATHS)}]])
                        acked[f"s{seed} cloud note {n}"] = "cloud"; st["mcp_calls"] += 2; n += 1
                    except OSError: pass
        for dv in devs:
            if dv.asleep: dv.wake()
            dv.net("up")
        if not lap.running: lap.start()
        if not hub.alive(): hub.start()
        print(f"  seed {seed} day {day}: {st['writes']} writes, {st['claims']} claims, {time.time() - t_start:.0f} s", file=sys.stderr)

    # quiesce: everyone online, wait for the queues to drain and the replicas to catch up
    deadline = time.time() + 60
    while time.time() < deadline:
        hl = [e["id"] for e in hub.ledger()]
        if all([e["id"] for e in d.ledger()] == hl for d in devs): break
        time.sleep(0.3)
    hl = hub.ledger()
    texts = [e.get("text") for e in hl]
    res = dict(st)
    res["acknowledged"] = len(acked)
    res["lost"] = sum(1 for t in acked if texts.count(t) == 0)
    for t in acked:
        if texts.count(t) == 0: print(f"  LOST {t} ({acked[t]})", file=sys.stderr)
    res["doubled"] = sum(1 for t in acked if texts.count(t) > 1)
    res["q ids left"] = sum(1 for d in devs for e in d.ledger() if e["id"].startswith("q"))
    res["diverged"] = sum(1 for d in devs if [e["id"] for e in d.ledger()] != [e["id"] for e in hl])
    queued = [e for e in hl if e["type"] in ("claim", "renew", "contend") and "was offline)" in e.get("text", "")]
    contends = [e for e in queued if e["type"] == "contend"]
    flags = [e for e in hl if e["type"] == "flag" and "without a lease" in e.get("text", "")]
    res["queued claims replayed"] = len(queued)
    res["offline claims on held paths"] = len(contends)
    res["flagged"] = len(flags)
    res["hub events"] = len(hl)
    res["devices on events"] = sorted({e.get("device", "-") for e in hl})
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=3)
    ap.add_argument("--days", type=int, default=7)
    ap.add_argument("--docker", action="store_true", help="hub, desk and lap as separate containers")
    ap.add_argument("--skew", type=int, default=0, help="with --docker: the lap's clock runs this many seconds ahead")
    ap.add_argument("--out")
    a = ap.parse_args()
    if a.skew and not a.docker: ap.error("--skew needs --docker")
    tmp = tempfile.mkdtemp(prefix="sk-hub-", dir=os.environ.get("XDG_RUNTIME_DIR") or "/tmp")
    t0 = time.time()
    backend = Docker(tmp, a.skew) if a.docker else Local(tmp)
    runs = []
    try:
        for s in range(a.seeds):
            r = one(s, a.days, backend)
            print(f"seed {s}: " + ", ".join(f"{k} {v if not isinstance(v, list) or not v or isinstance(v[0], str) else len(v)}" for k, v in r.items()), file=sys.stderr)
            runs.append(r)
    finally:
        if not os.environ.get("SKSIM_KEEP"): backend.close()
        shutil.rmtree(tmp, ignore_errors=True)
    tot = lambda k: sum(r[k] for r in runs)
    allv = lambda k: [x for r in runs for x in r[k]]
    q = lambda v, p: sorted(v)[min(len(v) - 1, int(len(v) * p))] if v else float("nan")
    rows = [("acknowledged writes (CLI, gate flags, MCP)", tot("acknowledged")), ("of those written while offline", tot("queued")),
            ("lost", tot("lost")), ("doubled", tot("doubled")), ("queued ids left on a device", tot("q ids left")),
            ("devices whose replica differs from the hub", tot("diverged")),
            ("edit-gate claims", tot("claims")), ("denied (another holder, any device)", tot("denied")),
            ("claims made while the sim had the network down", tot("offline_claims")),
            ("queued claims replayed on the hub", tot("queued claims replayed")),
            ("of those, on paths another holder had meanwhile", tot("offline claims on held paths")),
            ("flagged on the hub", tot("flagged")),
            ("network cuts", tot("cuts")), ("blackholes (silent dead routes)", tot("blackholes")),
            ("laptop sleeps (frozen, silent)", tot("sleeps")),
            ("hub service restarts", tot("hub_restarts")), ("hub box crashes (everything killed)", tot("hub_crashes")),
            ("laptop sankofad killed", tot("daemon_kills")), ("laptop reboots", tot("reboots")),
            ("cloud MCP calls", tot("mcp_calls")), ("hub events", tot("hub events"))]
    on, off = allv("gate_online"), allv("gate_offline")
    out = [f"# Accelerated week (hub): {a.seeds} seeds x {a.days} days, desk + lap + a cloud MCP session", "",
           f"Backend: {backend.name}", "", "| measure | total |", "|---|---|"] + [f"| {k} | {v} |" for k, v in rows] + ["",
           f"Edit gate through the hub, online: median {statistics.median(on):.1f} ms, p99 {q(on, .99):.1f} ms over {len(on)} edits",
           f"Edit gate offline (allow-and-flag): median {statistics.median(off):.1f} ms, p99 {q(off, .99):.1f} ms over {len(off)} edits" if off else "",
           "", f"Wall time {time.time() - t0:.0f} s"]
    text = "\n".join(out) + "\n"
    print(text)
    if a.out:
        with open(a.out, "w") as f: f.write(text)
    sys.exit(1 if tot("lost") + tot("doubled") + tot("q ids left") + tot("diverged")
             + abs(tot("flagged") - tot("offline claims on held paths")) else 0)


if __name__ == "__main__":
    main()
