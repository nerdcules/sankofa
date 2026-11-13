using System.Text;

namespace Sankofa;

/// <summary>
/// Ledger text is written by other sessions, other devices and, through the bridge, by anyone who
/// can push to a remote. Before it is shown in a terminal or offered as a command to paste, it is
/// made harmless here.
/// </summary>
public static class Safe
{
    /// <summary>Drops control characters (escape sequences included); line breaks become spaces.</summary>
    public static string Printable(string s)
    {
        if (s.AsSpan().IndexOfAnyInRange('\0', '\u001f') < 0 && !s.Contains('\u007f') && s.AsSpan().IndexOfAnyInRange('\u0080', '\u009f') < 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\n' or '\r' or '\t') sb.Append(' ');
            else if (c < ' ' || c == '\u007f' || c is >= '\u0080' and <= '\u009f') continue;
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// One word for a shell command line: as is when it is plainly safe, else in single quotes,
    /// so an id, a glob or a file name pasted after <c>sankofa done</c> or <c>sankofa ask</c> is
    /// an argument and nothing more.
    /// </summary>
    public static string ShellWord(string s)
    {
        s = Printable(s);
        if (s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or '*' or '?' or '@' or ':' or ',' or '+' or '=' or '%'))
            return s;
        return "'" + s.Replace("'", "'\\''") + "'";
    }
}
