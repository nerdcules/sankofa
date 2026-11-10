namespace Sankofa;

/// <summary>
/// Repo-relative path globs: <c>*</c> and <c>?</c> stay inside one segment, <c>**</c> spans
/// segments, a trailing <c>/</c> means everything below. Overlap between two globs is
/// conservative: when it cannot be decided cheaply it says yes.
/// </summary>
public static class Glob
{
    public static string Normalize(string p)
    {
        p = p.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        if (p.EndsWith('/')) p += "**";
        if (p is "" or ".") p = "**";
        return p.TrimStart('/');
    }

    public static bool HasWildcard(string g) => g.AsSpan().IndexOfAny('*', '?') >= 0;

    public static bool Match(string glob, string path) =>
        MatchSegments(Normalize(glob).Split('/'), 0, Normalize(path).Split('/'), 0);

    static bool MatchSegments(string[] g, int gi, string[] p, int pi)
    {
        while (gi < g.Length)
        {
            if (g[gi] == "**")
            {
                if (gi == g.Length - 1) return true;
                for (int k = pi; k <= p.Length; k++)
                    if (MatchSegments(g, gi + 1, p, k)) return true;
                return false;
            }
            if (pi >= p.Length || !MatchSegment(g[gi], p[pi])) return false;
            gi++; pi++;
        }
        return pi == p.Length;
    }

    static bool MatchSegment(string g, string s)
    {
        int gi = 0, si = 0, star = -1, mark = 0;
        while (si < s.Length)
        {
            if (gi < g.Length && (g[gi] == '?' || g[gi] == s[si])) { gi++; si++; }
            else if (gi < g.Length && g[gi] == '*') { star = gi++; mark = si; }
            else if (star >= 0) { gi = star + 1; si = ++mark; }
            else return false;
        }
        while (gi < g.Length && g[gi] == '*') gi++;
        return gi == g.Length;
    }

    /// <summary>Could some path match both globs?</summary>
    public static bool Overlap(string a, string b)
    {
        a = Normalize(a); b = Normalize(b);
        bool wa = HasWildcard(a), wb = HasWildcard(b);
        if (!wa && !wb) return a == b;
        if (!wa) return Match(b, a);
        if (!wb) return Match(a, b);
        var sa = a.Split('/'); var sb = b.Split('/');
        if (!a.Contains("**", StringComparison.Ordinal) && !b.Contains("**", StringComparison.Ordinal))
        {
            // same depth required; then every segment pair must be able to meet
            if (sa.Length != sb.Length) return false;
            for (int i = 0; i < sa.Length; i++) if (!SegmentOverlap(sa[i], sb[i])) return false;
            return true;
        }
        // with **: compare the literal directory prefixes; equal-or-nested means overlap
        var pa = LiteralPrefix(a); var pb = LiteralPrefix(b);
        int n = Math.Min(pa.Length, pb.Length);
        for (int i = 0; i < n; i++) if (pa[i] != pb[i]) return false;
        return true;
    }

    static bool SegmentOverlap(string x, string y)
    {
        bool wx = HasWildcard(x), wy = HasWildcard(y);
        if (!wx) return MatchSegment(y, x);
        if (!wy) return MatchSegment(x, y);
        // both wild: the literal heads must agree, and so must the literal tails
        string hx = x[..x.AsSpan().IndexOfAny('*', '?')], hy = y[..y.AsSpan().IndexOfAny('*', '?')];
        string tx = x[(x.AsSpan().LastIndexOfAny('*', '?') + 1)..], ty = y[(y.AsSpan().LastIndexOfAny('*', '?') + 1)..];
        bool heads = hx.StartsWith(hy, StringComparison.Ordinal) || hy.StartsWith(hx, StringComparison.Ordinal);
        bool tails = tx.EndsWith(ty, StringComparison.Ordinal) || ty.EndsWith(tx, StringComparison.Ordinal);
        return heads && tails;
    }

    static string[] LiteralPrefix(string g)
    {
        var segs = g.Split('/');
        int i = 0;
        while (i < segs.Length && !HasWildcard(segs[i])) i++;
        return segs[..i];
    }
}
