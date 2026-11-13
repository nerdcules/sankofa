using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Sankofa.Gui;

/// <summary>
/// One picture of the scene, drawn by hand so it moves: rings pulse on what is live or waits on
/// you, packets travel to the hub, a sweep turns over the radar. Clicking anything picks it.
/// </summary>
abstract class SceneView : Control
{
    public Scene? Scene;
    public Palette P = Palette.Windows();
    /// <summary>Seconds on the animation clock; fixed when motion is off or for a render.</summary>
    public double T;
    public bool Motion = true;
    public string? Selected;
    public event Action<Pick>? Picked;
    readonly List<(Rect R, Pick P)> hits = [];

    public abstract string Title { get; }
    public abstract string Hint { get; }

    protected void Hit(Rect r, Pick p) => hits.Add((r, p));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var at = e.GetPosition(this);
        for (var i = hits.Count - 1; i >= 0; i--)
            if (hits[i].R.Contains(at)) { Picked?.Invoke(hits[i].P); return; }
    }

    public sealed override void Render(DrawingContext c)
    {
        hits.Clear();
        var size = Bounds.Size;
        c.FillRectangle(B(P.Bg), new Rect(size));
        if (Scene is null) return;
        Draw(c, size);
        Label(c, Title, new Point(20, 16), 15, P.Fg, bold: true);
        Label(c, Hint, new Point(20, 38), 12, P.Dim);
    }

    protected abstract void Draw(DrawingContext c, Size s);

    protected static IBrush B(Color c, double a = 1) => new SolidColorBrush(c, a);
    protected static IPen Line(Color c, double w, double a = 1, DashStyle? dash = null) => new Pen(B(c, a), w, dash);

    protected FontFamily Family(bool mono) =>
        Palette.Family(mono ? P.Mono : P.Font);

    protected FormattedText Text(string s, double size, Color col, bool bold = false, bool mono = false, double maxW = 0, double alpha = 1)
    {
        var t = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Family(mono || P.Look == Look.Omarchy), FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, B(col, alpha));
        if (maxW > 0) { t.MaxTextWidth = maxW; t.MaxLineCount = 1; t.Trimming = TextTrimming.CharacterEllipsis; }
        return t;
    }

    /// <summary>Draw text; <paramref name="align"/> 0 = from p, 0.5 = centred on p, 1 = ending at p.</summary>
    protected Size Label(DrawingContext c, string s, Point p, double size, Color col, bool bold = false, bool mono = false, double align = 0, double maxW = 0, double alpha = 1)
    {
        var t = Text(s, size, col, bold, mono, maxW, alpha);
        c.DrawText(t, new Point(p.X - t.Width * align, p.Y));
        return new Size(t.Width, t.Height);
    }

    protected static double Seed(string key)
    {
        uint h = 2166136261;
        foreach (var ch in key) h = (h ^ ch) * 16777619;
        return h % 1000 / 1000.0;
    }

    /// <summary>0..1, repeating every <paramref name="period"/> seconds, offset per key so rings do not beat together.</summary>
    protected double Phase(string key, double period) { var x = T / period + Seed(key); return x - Math.Floor(x); }

    /// <summary>A ring that grows and fades out from a dot.</summary>
    protected void Pulse(DrawingContext c, Point at, double r, Color col, string key, double period = 2.2, double reach = 1.8)
    {
        var ph = Phase(key, period);
        var rr = r * (1 + reach * ph);
        c.DrawEllipse(B(col, .5 * (1 - ph)), null, at, rr, rr);
    }

    /// <summary>A slow swell between dim and full, for what is uncertain rather than urgent.</summary>
    protected double Breathe(string key, double period = 2.4) => .6 + .4 * (.5 + .5 * Math.Cos(Phase(key, period) * Math.PI * 2));

    protected static Color OnColor(Color c)
    {
        static double L(byte v) { var s = v / 255.0; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        return .2126 * L(c.R) + .7152 * L(c.G) + .0722 * L(c.B) > .22 ? Color.Parse("#16161A") : Color.Parse("#FFFFFF");
    }

    protected static bool Live(double age) => age < 120;

    protected void Legend(DrawingContext c, Size s, params Kind[] kinds)
    {
        var x = 20.0;
        foreach (var k in kinds)
        {
            c.DrawEllipse(B(P.Of(k)), null, new Point(x + 5, s.Height - 22), 5, 5);
            x += 16 + Label(c, Palette.Describe(k), new Point(x + 14, s.Height - 30), 12, P.Dim).Width + 14;
        }
    }

    protected void Empty(DrawingContext c, Size s, string text) =>
        Label(c, text, new Point(s.Width / 2, s.Height / 2 - 8), 14, P.Dim, align: .5);
}

/// <summary>Windows: sessions around the ledger, linked to it, pulsing while they work.</summary>
sealed class ConstellationView : SceneView
{
    public override string Title => "Session constellation";
    public override string Hint => "each session around the ledger · a pulse means active in the last 2 minutes";

    protected override void Draw(DrawingContext c, Size s)
    {
        var sc = Scene!;
        var centre = new Point(s.Width / 2, s.Height / 2 + 6);
        var nodes = sc.Nodes;
        var rx = Math.Max(160, s.Width * .36);
        var ry = Math.Max(120, (s.Height - 150) * .42);
        var spots = nodes.Select((n, i) =>
        {
            var a = -Math.PI / 2 + i * 2 * Math.PI / Math.Max(1, nodes.Count) + (nodes.Count == 2 ? Math.PI / 2 : 0);
            return new Point(centre.X + Math.Cos(a) * rx, centre.Y + Math.Sin(a) * ry);
        }).ToList();

        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var dash = new DashStyle([2, 5], Motion ? -T * 4 : 0);
            c.DrawLine(Line(P.Of(n.Kind), 1.5, Live(n.Age) ? .75 : .35, Live(n.Age) || n.Leases > 0 ? dash : null), centre, spots[i]);
        }

        // the ledger in the middle
        if (Motion) Pulse(c, centre, 20, P.Accent, "centre", 2.6, 1.4);
        c.DrawEllipse(B(P.Accent), Line(P.Bg, 4), centre, 20, 20);
        Label(c, "sankofa", new Point(centre.X, centre.Y + 28), 13, P.Fg, bold: true, align: .5);
        Label(c, $"{sc.Repos.Count} repo{(sc.Repos.Count == 1 ? "" : "s")} · {sc.Events} events", new Point(centre.X, centre.Y + 46), 11, P.Dim, align: .5);

        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var p = spots[i];
            var r = 10 + Math.Min(n.Leases, 4) * 2;
            var col = P.Of(n.Kind);
            var a = n.Age < 3600 ? 1 : .55;
            if (Live(n.Age) && Motion) Pulse(c, p, r, col, n.Key);
            c.DrawEllipse(B(col, a), Line(P.Bg, 3), p, r, r);
            if (n.Pick.Key == Selected) c.DrawEllipse(null, Line(P.Fg, 2), p, r + 5, r + 5);
            var l1 = Label(c, n.Label, new Point(p.X, p.Y + r + 6), 12, P.Fg, bold: true, align: .5, alpha: a);
            var l2 = Label(c, $"{n.Sub} · {Scene.Ago(n.Age)}", new Point(p.X, p.Y + r + 22), 11, P.Dim, align: .5, maxW: 220);
            var half = Math.Max(r + 6, Math.Max(l1.Width, l2.Width) / 2);
            Hit(new Rect(p.X - half, p.Y - r - 6, half * 2, r * 2 + 44), n.Pick);
        }

        if (sc.Overlap is { } path)
        {
            var t = Text($"overlap · {path}", 12, P.Amber, bold: true, maxW: 420);
            var box = new Rect(s.Width / 2 - t.Width / 2 - 12, 64, t.Width + 24, t.Height + 8);
            c.DrawRectangle(B(P.Amber, .16), Line(P.Amber, 1, .5), box, 12, 12);
            c.DrawText(t, new Point(box.X + 12, box.Y + 4));
        }
        if (nodes.Count == 0) Label(c, "No sessions in the ledger yet", new Point(centre.X, centre.Y + 80), 14, P.Dim, align: .5);
        Legend(c, s, Kind.Held, Kind.Half, Kind.Word);
    }
}

/// <summary>macOS: the devices sharing the ledger, packets flowing to the hub while they write.</summary>
sealed class MapView : SceneView
{
    public override string Title => "Device map";
    public override string Hint => "every device writing to the ledger · packets are events in the last 5 minutes";

    static Point Bez(Point a, Point c, Point b, double t) =>
        new((1 - t) * (1 - t) * a.X + 2 * (1 - t) * t * c.X + t * t * b.X, (1 - t) * (1 - t) * a.Y + 2 * (1 - t) * t * c.Y + t * t * b.Y);

    protected override void Draw(DrawingContext c, Size s)
    {
        var sc = Scene!;
        var dot = B(P.Border);
        for (var x = 12.0; x < s.Width; x += 24)
            for (var y = 12.0; y < s.Height; y += 24)
                c.FillRectangle(dot, new Rect(x, y, 1.6, 1.6));

        var hub = new Point(s.Width / 2, s.Height / 2 + 10);
        var R = Math.Max(140, Math.Min(s.Width, s.Height - 120) * .38);
        c.DrawEllipse(null, Line(P.Border, 1), hub, R * .62, R * .62);
        c.DrawEllipse(null, Line(P.Border, 1, .6), hub, R * 1.05, R * 1.05);

        var locals = sc.Devices.Where(d => !d.Remote).ToList();
        var placed = new List<(Device D, Point P)>();
        for (var i = 0; i < locals.Count; i++)
        {
            var a = Math.PI * .85 + i * 2 * Math.PI / Math.Max(1, locals.Count);
            placed.Add((locals[i], new Point(hub.X + Math.Cos(a) * R * 1.05, hub.Y + Math.Sin(a) * R * .8)));
        }
        foreach (var d in sc.Devices.Where(d => d.Remote))
            placed.Add((d, new Point(hub.X + R * 1.15, hub.Y - R * .72)));

        foreach (var (d, p) in placed)
        {
            var mid = new Point((p.X + hub.X) / 2, (p.Y + hub.Y) / 2);
            var dx = hub.X - p.X; var dy = hub.Y - p.Y;
            var ctl = new Point(mid.X - dy * .18, mid.Y + dx * .18);
            var active = d.Age < 300 || (d.Remote && d.Age < 3600);
            var col = d.Remote ? P.Cyan : P.Accent;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(p, false);
                ctx.QuadraticBezierTo(ctl, hub);
                ctx.EndFigure(false);
            }
            c.DrawGeometry(null, active ? Line(col, 2, .7) : Line(P.Dim, 1.5, .6, new DashStyle([3, 4], 0)), g);
            if (active && Motion)
                for (var k = 0; k < 2; k++)
                {
                    var t = Phase(d.Name + k, 2.6);
                    t = (t + k * .5) % 1;
                    var at = Bez(p, ctl, hub, t);
                    var fade = Math.Min(1, Math.Min(t, 1 - t) * 8);
                    c.DrawEllipse(B(col, .3 * fade), null, at, 8, 8);
                    c.DrawEllipse(B(P.Fg, fade), null, at, 3.5, 3.5);
                }
        }

        if (Motion) Pulse(c, hub, 18, P.Accent, "hub", 2.6, 1.6);
        c.DrawEllipse(B(P.Accent), Line(P.Bg, 4), hub, 18, 18);
        Label(c, sc.Hub ? "sankofa-hub" : "ledger", new Point(hub.X, hub.Y + 26), 13, P.Fg, bold: true, align: .5);
        Label(c, sc.Hub ? "one ledger for every device" : "this machine's ledger", new Point(hub.X, hub.Y + 44), 11, P.Dim, align: .5);

        foreach (var (d, p) in placed)
        {
            var idle = d.Age > 3600 && !d.Remote;
            var col = d.Remote ? P.Cyan : idle ? P.Dim : P.Accent;
            if (d.Queued > 0) c.DrawEllipse(null, Line(P.Amber, 1.5, Motion ? Breathe(d.Name) : 1, new DashStyle([2, 2], 0)), p, 20, 20);
            else if (!idle && d.Age < 600 && Motion) Pulse(c, p, 12, col, d.Name, 2.4, 2);
            c.DrawEllipse(B(col), Line(P.Bg, 3), p, 12, 12);
            if (d.Pick.Key == Selected) c.DrawEllipse(null, Line(P.Fg, 2), p, 17, 17);
            var sub = d.Remote ? $"landed {Scene.Ago(d.Age)} ago"
                : $"{d.Sessions} session{(d.Sessions == 1 ? "" : "s")} · {d.Leases} held" + (d.Queued > 0 ? $" · {d.Queued} queued" : "") + $" · {Scene.Ago(d.Age)}";
            var l1 = Label(c, d.Name, new Point(p.X, p.Y + 22), 13, P.Fg, bold: true, align: .5);
            var l2 = Label(c, sub, new Point(p.X, p.Y + 40), 11, d.Queued > 0 ? P.Amber : P.Dim, align: .5);
            var half = Math.Max(20, Math.Max(l1.Width, l2.Width) / 2);
            Hit(new Rect(p.X - half, p.Y - 20, half * 2, 76), d.Pick);
        }
        Legend(c, s, Kind.Word, Kind.Landed);
        Label(c, "dashed ring = writes queued while the hub was away · dashed line = quiet", new Point(s.Width - 20, s.Height - 30), 12, P.Dim, align: 1);
    }
}

/// <summary>Linux: every held path as a tile, coloured by state; contested ones beat, uncertain ones breathe.</summary>
sealed class HeatmapView : SceneView
{
    public override string Title => "Lease heatmap";
    public override string Hint => "every path someone holds, or held and may have left half-finished";

    protected override void Draw(DrawingContext c, Size s)
    {
        var sc = Scene!;
        if (Motion)
        {
            var x = ((T / 3) % 1) * s.Width * 1.25 - s.Width * .25;
            c.FillRectangle(B(P.Accent, .8), new Rect(x, 0, s.Width * .25, 2));
        }
        if (sc.Tiles.Count == 0) { Empty(c, s, "No leases held. Paths show up here as sessions claim them."); return; }

        const double gap = 8, h = 88, min = 210;
        var inner = s.Width - 40;
        var cols = Math.Max(1, (int)((inner + gap) / (min + gap)));
        var w = (inner - gap * (cols - 1)) / cols;
        var y = 68.0;
        foreach (var group in sc.Tiles.GroupBy(t => t.Repo))
        {
            var count = group.Count();
            Label(c, $"{Scene.Name(group.Key)}  ·  {count} path{(count == 1 ? "" : "s")}", new Point(20, y), 13, P.Fg, bold: true);
            y += 24;
            var i = 0;
            foreach (var t in group)
            {
                var r = new Rect(20 + i % cols * (w + gap), y + i / cols * (h + gap), w, h);
                var col = P.Of(t.State);
                var rad = Math.Min(P.Radius, 6);
                Color fg;
                switch (t.State)
                {
                    case Kind.Contested:
                        var beat = Motion ? .5 + .5 * Math.Abs(Math.Sin(Phase(t.Pick.Key, 1.6) * Math.PI)) : 1;
                        c.DrawRectangle(B(col, .14), Line(col, 2, beat), r, rad, rad);
                        fg = P.Fg; break;
                    case Kind.Overlap:
                        c.DrawRectangle(B(col, .16), Line(col, 1.5, .8, new DashStyle([3, 2], 0)), r, rad, rad);
                        fg = P.Fg; break;
                    case Kind.Half:
                        c.DrawRectangle(B(col, Motion ? Breathe(t.Pick.Key) : 1), null, r, rad, rad);
                        fg = OnColor(col); break;
                    default:
                        c.DrawRectangle(B(col), null, r, rad, rad);
                        fg = OnColor(col); break;
                }
                if (t.Pick.Key == Selected) c.DrawRectangle(null, Line(P.Fg, 2), r.Inflate(3), rad + 2, rad + 2);
                var fit = Math.Max(8, (int)((r.Width - 24) / (13 * .62)));
                var glob = t.Glob.Length > fit ? "…" + t.Glob[^(fit - 1)..] : t.Glob;   // the end of a path says the most
                Label(c, glob, new Point(r.X + 12, r.Y + 12), 13, fg, bold: true, mono: true, maxW: r.Width - 24);
                Label(c, t.Who.Length > 0 ? t.Who : "nobody holds it", new Point(r.X + 12, r.Y + 36), 11, fg, maxW: r.Width - 24, alpha: .9);
                Label(c, $"{Palette.Describe(t.State)} · {Scene.Ago(t.Age)}", new Point(r.X + 12, r.Y + 56), 11, fg, maxW: r.Width - 24, alpha: .85);
                Hit(r, t.Pick);
                i++;
            }
            y += (count + cols - 1) / cols * (h + gap) + 16;
        }
        Legend(c, s, Kind.Held, Kind.Contested, Kind.Half, Kind.Queued, Kind.Overlap);
    }
}

/// <summary>Omarchy: everything in the ledger by age, now at the centre, under a turning sweep.</summary>
sealed class RadarView : SceneView
{
    public override string Title => "Radar";
    public override string Hint => "centre = now · edge = a day ago · a pulse means it waits on you or just happened";

    Point At(Point c, double deg, double r) => new(c.X + Math.Sin(deg * Math.PI / 180) * r, c.Y - Math.Cos(deg * Math.PI / 180) * r);

    protected override void Draw(DrawingContext c, Size s)
    {
        var sc = Scene!;
        var R = Math.Max(120, Math.Min(s.Width - 80, s.Height - 150) / 2);
        var o = new Point(s.Width / 2, s.Height / 2 + 8);
        c.DrawEllipse(B(P.Panel), Line(P.Border, 1), o, R, R);
        foreach (var (age, label) in new[] { (600.0, "10m"), (3600.0, "1h"), (21600.0, "6h") })
        {
            var rr = Scene.Distance(age) * R;
            c.DrawEllipse(null, Line(P.Border, 1), o, rr, rr);
            Label(c, label, new Point(o.X + 4, o.Y - rr - 15), 10, P.Dim, mono: true);
        }
        Label(c, "1d+", new Point(o.X + 4, o.Y - R - 15), 10, P.Dim, mono: true);
        c.DrawLine(Line(P.Border, 1, .7), new Point(o.X - R, o.Y), new Point(o.X + R, o.Y));
        c.DrawLine(Line(P.Border, 1, .7), new Point(o.X, o.Y - R), new Point(o.X, o.Y + R));

        // the sweep: a fan of thin wedges, brightest at its leading edge
        var sweep = Motion ? T * 90 % 360 : 300;
        const int slices = 28; const double span = 70;
        for (var i = 0; i < slices; i++)
        {
            var a0 = sweep - span + i * span / slices;
            var a1 = a0 + span / slices + .8;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(o, true);
                ctx.LineTo(At(o, a0, R));
                ctx.ArcTo(At(o, a1, R), new Size(R, R), 0, false, SweepDirection.Clockwise);
                ctx.EndFigure(true);
            }
            c.DrawGeometry(B(P.Accent, .32 * Math.Pow((i + 1.0) / slices, 2)), null, g);
        }
        c.DrawLine(Line(P.Accent, 1.5, .8), o, At(o, sweep, R));
        c.DrawEllipse(B(P.Fg), null, o, 4, 4);

        var labels = sc.Blips.Count <= 40;
        foreach (var b in sc.Blips.OrderBy(b => b.Pick.Key == Selected))
        {
            var p = At(o, b.Angle, b.Distance * (R - 10));
            var col = P.Of(b.Kind);
            var behind = (sweep - b.Angle + 360) % 360;
            var glow = Motion ? Math.Max(0, 1 - behind / 50) : 0;
            if (Motion && (Live(b.Age) || b.Kind is Kind.Waiting or Kind.Handoff)) Pulse(c, p, 5, col, b.Key, 1.8, 2.2);
            if (glow > 0) c.DrawEllipse(B(col, .45 * glow), null, p, 9, 9);
            var on = b.Pick.Key == Selected;
            c.DrawEllipse(B(col), on ? Line(P.Fg, 2) : null, p, on ? 7 : 5, on ? 7 : 5);
            var right = p.X >= o.X - 4;
            var sz = labels || on ? Label(c, b.Label, new Point(p.X + (right ? 10 : -10), p.Y - 8), 11, col, mono: true, align: right ? 0 : 1) : new Size();
            Hit(new Rect(right ? p.X - 9 : p.X - 12 - sz.Width, p.Y - 10, sz.Width + 22, 20), b.Pick);
        }
        if (sc.Blips.Count == 0) Empty(c, s, "Nothing in the ledger yet");
        Legend(c, s, Kind.Waiting, Kind.Handoff, Kind.Held, Kind.Half, Kind.Landed, Kind.Word);
    }
}
