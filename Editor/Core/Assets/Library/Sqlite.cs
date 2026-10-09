using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Editor.Core.Assets.Library
{
    /// <summary>
    /// A deliberately small SQLite binding for the global asset library (#53): it binds the SQLite library the OS already
    /// ships — <c>winsqlite3.dll</c> on Windows 10/11, <c>/usr/lib/libsqlite3.dylib</c> on macOS, <c>libsqlite3.so.0</c>
    /// on Linux — so neither editor needs a NuGet package or a bundled native binary. <c>VORTEX_SQLITE_LIB</c> overrides
    /// the library path. Compiles for the .NET Framework WPF editor (C# 9) and for Vortex.Core (.NET 10).
    /// </summary>
    internal static class SqliteNative
    {
        public const int OK = 0, BUSY = 5, LOCKED = 6, ROW = 100, DONE = 101;
        public const int TypeInteger = 1, TypeFloat = 2, TypeText = 3, TypeBlob = 4, TypeNull = 5;
        public const int OpenReadWrite = 0x2, OpenCreate = 0x4, OpenFullMutex = 0x10000;
        public static readonly IntPtr Transient = new IntPtr(-1);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int OpenV2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int CloseV2(IntPtr db);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate IntPtr ErrMsg(IntPtr db);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int BusyTimeout(IntPtr db, int ms);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int Exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, IntPtr errmsg);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int PrepareV2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int StmtFn(IntPtr stmt);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int BindInt64(IntPtr stmt, int index, long value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int BindDouble(IntPtr stmt, int index, double value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int BindText(IntPtr stmt, int index, byte[] text, int bytes, IntPtr destructor);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int BindNull(IntPtr stmt, int index);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int ColumnInt(IntPtr stmt, int column);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate long ColumnInt64(IntPtr stmt, int column);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate double ColumnDouble(IntPtr stmt, int column);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate IntPtr ColumnPtr(IntPtr stmt, int column);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate int DbInt(IntPtr db);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate long DbInt64(IntPtr db);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate IntPtr NoArgPtr();

        public static OpenV2 sqlite3_open_v2;
        public static CloseV2 sqlite3_close_v2;
        public static ErrMsg sqlite3_errmsg;
        public static BusyTimeout sqlite3_busy_timeout;
        public static Exec sqlite3_exec;
        public static PrepareV2 sqlite3_prepare_v2;
        public static StmtFn sqlite3_step, sqlite3_reset, sqlite3_finalize, sqlite3_clear_bindings, sqlite3_column_count;
        public static BindInt64 sqlite3_bind_int64;
        public static BindDouble sqlite3_bind_double;
        public static BindText sqlite3_bind_text;
        public static BindNull sqlite3_bind_null;
        public static ColumnInt sqlite3_column_type, sqlite3_column_bytes;
        public static ColumnInt64 sqlite3_column_int64;
        public static ColumnDouble sqlite3_column_double;
        public static ColumnPtr sqlite3_column_text;
        public static DbInt sqlite3_changes;
        public static DbInt64 sqlite3_last_insert_rowid;
        public static NoArgPtr sqlite3_libversion;

        private static readonly object Gate = new object();
        private static bool _tried;
        private static string _error;
        private static IntPtr _lib;

        /// <summary>The loaded library's version ("3.51.0"), or null.</summary>
        public static string Version { get; private set; }
        /// <summary>The path/name the library was loaded from.</summary>
        public static string LoadedFrom { get; private set; }

        /// <summary>Load the OS SQLite once. False (with <paramref name="error"/>) when no usable library exists.</summary>
        public static bool TryLoad(out string error)
        {
            lock (Gate)
            {
                if (_tried) { error = _error; return _lib != IntPtr.Zero; }
                _tried = true;
                foreach (var candidate in Candidates())
                {
                    IntPtr h = LoadLibrary(candidate);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        Bind(h);
                        _lib = h;
                        LoadedFrom = candidate;
                        Version = Utf8(sqlite3_libversion());
                        _error = null;
                        error = null;
                        return true;
                    }
                    catch (Exception ex) { _error = candidate + ": " + ex.Message; }
                }
                if (_error == null) _error = "No SQLite library found (tried " + string.Join(", ", Candidates()) + "). Set VORTEX_SQLITE_LIB to a sqlite3 library.";
                error = _error;
                return false;
            }
        }

        private static IEnumerable<string> Candidates()
        {
            var env = Environment.GetEnvironmentVariable("VORTEX_SQLITE_LIB");
            if (!string.IsNullOrEmpty(env)) yield return env;
            if (IsWindows)
            {
                yield return "winsqlite3.dll";
                yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "sqlite3.dll");
                yield return "sqlite3.dll";
            }
            else if (IsMac)
            {
                yield return "/usr/lib/libsqlite3.dylib";
                yield return "libsqlite3.dylib";
            }
            else
            {
                yield return "libsqlite3.so.0";
                yield return "libsqlite3.so";
            }
        }

        private static void Bind(IntPtr h)
        {
            sqlite3_open_v2 = Fn<OpenV2>(h, "sqlite3_open_v2");
            sqlite3_close_v2 = Fn<CloseV2>(h, "sqlite3_close_v2");
            sqlite3_errmsg = Fn<ErrMsg>(h, "sqlite3_errmsg");
            sqlite3_busy_timeout = Fn<BusyTimeout>(h, "sqlite3_busy_timeout");
            sqlite3_exec = Fn<Exec>(h, "sqlite3_exec");
            sqlite3_prepare_v2 = Fn<PrepareV2>(h, "sqlite3_prepare_v2");
            sqlite3_step = Fn<StmtFn>(h, "sqlite3_step");
            sqlite3_reset = Fn<StmtFn>(h, "sqlite3_reset");
            sqlite3_finalize = Fn<StmtFn>(h, "sqlite3_finalize");
            sqlite3_clear_bindings = Fn<StmtFn>(h, "sqlite3_clear_bindings");
            sqlite3_column_count = Fn<StmtFn>(h, "sqlite3_column_count");
            sqlite3_bind_int64 = Fn<BindInt64>(h, "sqlite3_bind_int64");
            sqlite3_bind_double = Fn<BindDouble>(h, "sqlite3_bind_double");
            sqlite3_bind_text = Fn<BindText>(h, "sqlite3_bind_text");
            sqlite3_bind_null = Fn<BindNull>(h, "sqlite3_bind_null");
            sqlite3_column_type = Fn<ColumnInt>(h, "sqlite3_column_type");
            sqlite3_column_bytes = Fn<ColumnInt>(h, "sqlite3_column_bytes");
            sqlite3_column_int64 = Fn<ColumnInt64>(h, "sqlite3_column_int64");
            sqlite3_column_double = Fn<ColumnDouble>(h, "sqlite3_column_double");
            sqlite3_column_text = Fn<ColumnPtr>(h, "sqlite3_column_text");
            sqlite3_changes = Fn<DbInt>(h, "sqlite3_changes");
            sqlite3_last_insert_rowid = Fn<DbInt64>(h, "sqlite3_last_insert_rowid");
            sqlite3_libversion = Fn<NoArgPtr>(h, "sqlite3_libversion");
        }

        private static T Fn<T>(IntPtr lib, string name) where T : class
        {
            IntPtr p = GetSymbol(lib, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        private static IntPtr LoadLibrary(string name)
        {
            try { return NativeLibrary.TryLoad(name, out IntPtr h) ? h : IntPtr.Zero; } catch { return IntPtr.Zero; }
        }
        private static IntPtr GetSymbol(IntPtr lib, string name)
        {
            try { return NativeLibrary.TryGetExport(lib, name, out IntPtr p) ? p : IntPtr.Zero; } catch { return IntPtr.Zero; }
        }

        /// <summary>NUL-terminated UTF-8 → string (Marshal.PtrToStringUTF8 does not exist on .NET Framework).</summary>
        public static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0) n++;
            return Utf8(p, n);
        }

        public static string Utf8(IntPtr p, int bytes)
        {
            if (p == IntPtr.Zero) return null;
            if (bytes <= 0) return "";
            var buf = new byte[bytes];
            Marshal.Copy(p, buf, 0, bytes);
            return Encoding.UTF8.GetString(buf);
        }

        public static byte[] Z(string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            var z = new byte[b.Length + 1];
            Buffer.BlockCopy(b, 0, z, 0, b.Length);
            return z;
        }
    }

    /// <summary>A failed SQLite call (result code + the library's message).</summary>
    public sealed class SqliteException : Exception
    {
        public int Code { get; }
        public SqliteException(int code, string message) : base("SQLite error " + code + ": " + message) { Code = code; }
    }

    /// <summary>One connection. Not thread-safe by itself — <see cref="GlobalAssetDatabase"/> serialises access.</summary>
    internal sealed class SqliteDb : IDisposable
    {
        private IntPtr _db;
        public string Path { get; }

        private SqliteDb(IntPtr db, string path) { _db = db; Path = path; }

        public static SqliteDb Open(string path, int busyTimeoutMs = 10000)
        {
            if (!SqliteNative.TryLoad(out string err)) throw new DllNotFoundException(err);
            int rc = SqliteNative.sqlite3_open_v2(SqliteNative.Z(path), out IntPtr db,
                SqliteNative.OpenReadWrite | SqliteNative.OpenCreate | SqliteNative.OpenFullMutex, IntPtr.Zero);
            if (rc != SqliteNative.OK)
            {
                string msg = db != IntPtr.Zero ? SqliteNative.Utf8(SqliteNative.sqlite3_errmsg(db)) : "open failed";
                if (db != IntPtr.Zero) SqliteNative.sqlite3_close_v2(db);
                throw new SqliteException(rc, msg + " (" + path + ")");
            }
            SqliteNative.sqlite3_busy_timeout(db, busyTimeoutMs);
            return new SqliteDb(db, path);
        }

        internal IntPtr Handle => _db;
        public string LastError => SqliteNative.Utf8(SqliteNative.sqlite3_errmsg(_db));
        public long LastInsertRowId => SqliteNative.sqlite3_last_insert_rowid(_db);
        public int Changes => SqliteNative.sqlite3_changes(_db);

        public void Check(int rc)
        {
            if (rc != SqliteNative.OK && rc != SqliteNative.ROW && rc != SqliteNative.DONE) throw new SqliteException(rc, LastError);
        }

        /// <summary>Run one or more statements without results (schema, pragmas).</summary>
        public void ExecScript(string sql) => Check(SqliteNative.sqlite3_exec(_db, SqliteNative.Z(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

        public SqliteStmt Prepare(string sql)
        {
            var bytes = Encoding.UTF8.GetBytes(sql);
            int rc = SqliteNative.sqlite3_prepare_v2(_db, bytes, bytes.Length, out IntPtr stmt, IntPtr.Zero);
            if (rc != SqliteNative.OK) throw new SqliteException(rc, LastError + " in: " + sql);
            return new SqliteStmt(this, stmt);
        }

        /// <summary>INSERT/UPDATE/DELETE with positional parameters (?1…); returns the changed row count.</summary>
        public int Execute(string sql, params object[] args)
        {
            using (var s = Prepare(sql)) { s.BindAll(args); s.Step(); }
            return Changes;
        }

        public List<T> Query<T>(string sql, Func<SqliteStmt, T> map, params object[] args)
        {
            var list = new List<T>();
            using (var s = Prepare(sql))
            {
                s.BindAll(args);
                while (s.Step()) list.Add(map(s));
            }
            return list;
        }

        public object Scalar(string sql, params object[] args)
        {
            using (var s = Prepare(sql))
            {
                s.BindAll(args);
                if (!s.Step()) return null;
                return s.Value(0);
            }
        }

        public long ScalarLong(string sql, params object[] args)
        {
            var v = Scalar(sql, args);
            return v == null ? 0 : Convert.ToInt64(v);
        }

        /// <summary>Run <paramref name="body"/> in a write transaction (BEGIN IMMEDIATE: takes the write lock up front so
        /// two editor instances serialise instead of failing half-way).</summary>
        public T Write<T>(Func<T> body)
        {
            ExecScript("BEGIN IMMEDIATE");
            try { var r = body(); ExecScript("COMMIT"); return r; }
            catch { try { ExecScript("ROLLBACK"); } catch { } throw; }
        }

        public void Write(Action body) => Write(() => { body(); return 0; });

        public void Dispose()
        {
            if (_db != IntPtr.Zero) { SqliteNative.sqlite3_close_v2(_db); _db = IntPtr.Zero; }
        }
    }

    internal sealed class SqliteStmt : IDisposable
    {
        private readonly SqliteDb _db;
        private IntPtr _s;

        internal SqliteStmt(SqliteDb db, IntPtr s) { _db = db; _s = s; }

        public void BindAll(object[] args)
        {
            if (args == null) return;
            for (int i = 0; i < args.Length; i++) Bind(i + 1, args[i]);
        }

        public void Bind(int index, object v)
        {
            int rc;
            if (v == null || v is DBNull) rc = SqliteNative.sqlite3_bind_null(_s, index);
            else if (v is string str)
            {
                var b = Encoding.UTF8.GetBytes(str);
                rc = SqliteNative.sqlite3_bind_text(_s, index, b, b.Length, SqliteNative.Transient);
            }
            else if (v is bool bo) rc = SqliteNative.sqlite3_bind_int64(_s, index, bo ? 1 : 0);
            else if (v is double || v is float || v is decimal) rc = SqliteNative.sqlite3_bind_double(_s, index, Convert.ToDouble(v));
            else if (v is Enum) rc = SqliteNative.sqlite3_bind_int64(_s, index, Convert.ToInt64(v));
            else rc = SqliteNative.sqlite3_bind_int64(_s, index, Convert.ToInt64(v));
            _db.Check(rc);
        }

        /// <summary>Advance; true while a row is available.</summary>
        public bool Step()
        {
            int rc = SqliteNative.sqlite3_step(_s);
            if (rc == SqliteNative.ROW) return true;
            if (rc == SqliteNative.DONE) return false;
            throw new SqliteException(rc, _db.LastError);
        }

        public void Reset() { SqliteNative.sqlite3_reset(_s); SqliteNative.sqlite3_clear_bindings(_s); }

        public bool IsNull(int col) => SqliteNative.sqlite3_column_type(_s, col) == SqliteNative.TypeNull;
        public long Long(int col) => SqliteNative.sqlite3_column_int64(_s, col);
        public long? LongOrNull(int col) => IsNull(col) ? (long?)null : Long(col);
        public int Int(int col) => (int)SqliteNative.sqlite3_column_int64(_s, col);
        public int? IntOrNull(int col) => IsNull(col) ? (int?)null : Int(col);
        public double Double(int col) => SqliteNative.sqlite3_column_double(_s, col);
        public double? DoubleOrNull(int col) => IsNull(col) ? (double?)null : Double(col);
        public bool Bool(int col) => Long(col) != 0;

        public string Text(int col)
        {
            if (IsNull(col)) return null;
            IntPtr p = SqliteNative.sqlite3_column_text(_s, col);
            int n = SqliteNative.sqlite3_column_bytes(_s, col);
            return SqliteNative.Utf8(p, n);
        }

        public object Value(int col)
        {
            switch (SqliteNative.sqlite3_column_type(_s, col))
            {
                case SqliteNative.TypeInteger: return Long(col);
                case SqliteNative.TypeFloat: return Double(col);
                case SqliteNative.TypeNull: return null;
                default: return Text(col);
            }
        }

        public void Dispose()
        {
            if (_s != IntPtr.Zero) { SqliteNative.sqlite3_finalize(_s); _s = IntPtr.Zero; }
        }
    }
}
