using System.Runtime.InteropServices;

namespace Sankofa;

/// <summary>Direct calls into the system libsqlite3. No ADO.NET: fewer moving parts, fast AOT start.</summary>
internal static partial class Native
{
    const string Lib = "libsqlite3.so.0";
    public const int OK = 0, ROW = 100, DONE = 101, NULL = 5, INTEGER = 1;
    public static readonly nint Transient = -1;

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sqlite3_open_v2(string filename, out nint db, int flags, nint vfs);
    [LibraryImport(Lib)] public static partial int sqlite3_close_v2(nint db);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sqlite3_prepare_v2(nint db, string sql, int nbyte, out nint stmt, nint tail);
    [LibraryImport(Lib)] public static partial int sqlite3_step(nint stmt);
    [LibraryImport(Lib)] public static partial int sqlite3_reset(nint stmt);
    [LibraryImport(Lib)] public static partial int sqlite3_clear_bindings(nint stmt);
    [LibraryImport(Lib)] public static partial int sqlite3_finalize(nint stmt);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sqlite3_bind_text(nint stmt, int i, string v, int n, nint destructor);
    [LibraryImport(Lib)] public static partial int sqlite3_bind_int64(nint stmt, int i, long v);
    [LibraryImport(Lib)] public static partial int sqlite3_bind_null(nint stmt, int i);
    [LibraryImport(Lib)] public static partial int sqlite3_column_count(nint stmt);
    [LibraryImport(Lib)] public static partial int sqlite3_column_type(nint stmt, int i);
    [LibraryImport(Lib)] public static partial long sqlite3_column_int64(nint stmt, int i);
    [LibraryImport(Lib)] public static partial nint sqlite3_column_text(nint stmt, int i);
    [LibraryImport(Lib)] public static partial nint sqlite3_errmsg(nint db);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sqlite3_exec(nint db, string sql, nint cb, nint arg, out nint err);
    [LibraryImport(Lib)] public static partial void sqlite3_free(nint p);
    [LibraryImport(Lib)] public static partial int sqlite3_busy_timeout(nint db, int ms);
    [LibraryImport(Lib)] public static partial int sqlite3_changes(nint db);
}

public sealed class SqliteException(string message) : Exception(message);

/// <summary>One connection, cached prepared statements. Not thread-safe; the Store serialises access.</summary>
public sealed class Db : IDisposable
{
    readonly nint handle;
    readonly Dictionary<string, nint> cache = [];

    public Db(string path)
    {
        const int ReadWrite = 0x2, Create = 0x4, FullMutex = 0x10000;
        if (Native.sqlite3_open_v2(path, out handle, ReadWrite | Create | FullMutex, 0) != Native.OK)
            throw new SqliteException($"cannot open {path}: {Error()}");
        Native.sqlite3_busy_timeout(handle, 5000);
    }

    string Error() => Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(handle)) ?? "unknown error";

    public void Exec(string sql)
    {
        if (Native.sqlite3_exec(handle, sql, 0, 0, out var err) != Native.OK)
        {
            var msg = Marshal.PtrToStringUTF8(err) ?? Error();
            Native.sqlite3_free(err);
            throw new SqliteException(msg);
        }
    }

    nint Prepare(string sql, object?[] args)
    {
        if (!cache.TryGetValue(sql, out var stmt))
        {
            if (Native.sqlite3_prepare_v2(handle, sql, -1, out stmt, 0) != Native.OK)
                throw new SqliteException($"{Error()} in: {sql}");
            cache[sql] = stmt;
        }
        Native.sqlite3_reset(stmt);
        Native.sqlite3_clear_bindings(stmt);
        for (int i = 0; i < args.Length; i++)
        {
            int rc = args[i] switch
            {
                null => Native.sqlite3_bind_null(stmt, i + 1),
                string s => Native.sqlite3_bind_text(stmt, i + 1, s, -1, Native.Transient),
                long l => Native.sqlite3_bind_int64(stmt, i + 1, l),
                int n => Native.sqlite3_bind_int64(stmt, i + 1, n),
                bool b => Native.sqlite3_bind_int64(stmt, i + 1, b ? 1 : 0),
                var o => throw new ArgumentException($"cannot bind {o.GetType().Name}"),
            };
            if (rc != Native.OK) throw new SqliteException(Error());
        }
        return stmt;
    }

    /// <summary>Run a statement; returns rows changed.</summary>
    public int Run(string sql, params object?[] args)
    {
        var stmt = Prepare(sql, args);
        int rc = Native.sqlite3_step(stmt);
        Native.sqlite3_reset(stmt);
        if (rc != Native.DONE && rc != Native.ROW) throw new SqliteException($"{Error()} in: {sql}");
        return Native.sqlite3_changes(handle);
    }

    public List<object?[]> Query(string sql, params object?[] args)
    {
        var stmt = Prepare(sql, args);
        var rows = new List<object?[]>();
        int rc;
        while ((rc = Native.sqlite3_step(stmt)) == Native.ROW)
        {
            int n = Native.sqlite3_column_count(stmt);
            var row = new object?[n];
            for (int i = 0; i < n; i++)
                row[i] = Native.sqlite3_column_type(stmt, i) switch
                {
                    Native.NULL => null,
                    Native.INTEGER => Native.sqlite3_column_int64(stmt, i),
                    _ => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(stmt, i)),
                };
            rows.Add(row);
        }
        Native.sqlite3_reset(stmt);
        if (rc != Native.DONE) throw new SqliteException($"{Error()} in: {sql}");
        return rows;
    }

    public object? Scalar(string sql, params object?[] args)
    {
        var rows = Query(sql, args);
        return rows.Count > 0 ? rows[0][0] : null;
    }

    public void Dispose()
    {
        foreach (var s in cache.Values) Native.sqlite3_finalize(s);
        cache.Clear();
        Native.sqlite3_close_v2(handle);
    }
}
