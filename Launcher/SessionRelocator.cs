using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

internal static class SessionRelocator
{
    private const int SqliteOk = 0;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadWrite = 2;
    private const int SqliteOpenCreate = 4;
    private const int MoveReplaceExisting = 1;
    private const int MoveWriteThrough = 8;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
    private static extern int SqliteOpen(byte[] filename, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close")]
    private static extern int SqliteClose(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_busy_timeout")]
    private static extern int SqliteBusyTimeout(IntPtr database, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_exec")]
    private static extern int SqliteExec(IntPtr database, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_free")]
    private static extern void SqliteFree(IntPtr memory);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_init")]
    private static extern IntPtr SqliteBackupInit(IntPtr destination, byte[] destinationName, IntPtr source, byte[] sourceName);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_step")]
    private static extern int SqliteBackupStep(IntPtr backup, int pages);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_finish")]
    private static extern int SqliteBackupFinish(IntPtr backup);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
    private static extern bool MoveFileEx(string source, string destination, int flags);

    internal static void ReplaceFileAtomically(string source, string destination)
    {
        if (!MoveFileEx(source, destination, MoveReplaceExisting | MoveWriteThrough))
            throw new IOException("Atomic file replacement failed: " + Marshal.GetLastWin32Error());
    }

    internal static void Prepare(string root, string data, Action<string> log)
    {
        string marker = Path.Combine(data, "run", "last-root.txt");
        string databasePath = Path.Combine(data, "sessions", "opencode", "opencode.db");
        PortableAgent.RejectLinkedPath(marker);
        PortableAgent.RejectLinkedPath(databasePath);
        PortableAgent.RejectLinkedPath(databasePath + "-wal");
        PortableAgent.RejectLinkedPath(databasePath + "-shm");
        string current = NormalizeRoot(root);
        string previous = null;
        try
        {
            if (File.Exists(marker)) previous = NormalizeRoot(File.ReadAllText(marker, Encoding.UTF8).Trim());
            if (previous == null)
            {
                if (File.Exists(databasePath) && new FileInfo(databasePath).Length > 0)
                    throw new AgentError("PCA130", "已有会话数据库但缺少盘符迁移记录；为保护会话，未自动改写。请先备份整个 Data。");
                WriteMarker(marker, current);
                log("已记录本次 U 盘根路径，供以后变更盘符时修复会话。");
                return;
            }
            if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(databasePath))
            {
                WriteMarker(marker, current);
                log("U 盘路径已改变；尚无会话数据库。");
                return;
            }

            string backup = databasePath + ".before-relocation-" +
                DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
            PortableAgent.RejectLinkedPath(backup);
            MigrateDatabase(databasePath, backup, previous + "/Workspace", current + "/Workspace");
            WriteMarker(marker, current);
            log("U 盘路径已改变；已备份数据库并修复 Workspace 会话路径。");
            Console.WriteLine("检测到 U 盘路径变化；已备份并修复 Workspace 会话路径。");
        }
        catch (AgentError) { throw; }
        catch (DllNotFoundException)
        {
            throw new AgentError("PCA130", "系统缺少 Windows SQLite 组件 winsqlite3.dll；未迁移会话，请在个人电脑修复或更换兼容电脑。");
        }
        catch (Exception error)
        {
            log("PCA130 会话迁移失败：" + error.GetType().Name + "，" + error.Message);
            throw new AgentError("PCA130", "会话路径迁移失败；已停止启动以保护旧数据。请备份 Data 并查看 Logs。");
        }
    }

    private static string NormalizeRoot(string root)
    {
        string result = Path.GetFullPath(root).Replace('\\', '/');
        while (result.Length > 3 && result.EndsWith("/", StringComparison.Ordinal))
            result = result.Substring(0, result.Length - 1);
        if (!Regex.IsMatch(result, "^[A-Za-z]:/"))
            throw new AgentError("PCA130", "U 盘根路径或上次根路径不是有效的 Windows 盘符路径。");
        return result;
    }

    private static void WriteMarker(string marker, string current)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(marker));
        string temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, current + Environment.NewLine, new UTF8Encoding(false));
            ReplaceFileAtomically(temporary, marker);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] Utf8(string value)
    {
        return new UTF8Encoding(false).GetBytes(value + "\0");
    }

    private static IntPtr OpenDatabase(string path, int flags)
    {
        IntPtr database;
        int result = SqliteOpen(Utf8(path.Replace('\\', '/')), out database, flags, IntPtr.Zero);
        if (result != SqliteOk)
        {
            if (database != IntPtr.Zero) SqliteClose(database);
            throw new IOException("SQLite open failed: " + result);
        }
        SqliteBusyTimeout(database, 3000);
        return database;
    }

    private static void Execute(IntPtr database, string sql)
    {
        IntPtr error;
        int result = SqliteExec(database, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out error);
        if (error != IntPtr.Zero) SqliteFree(error);
        if (result != SqliteOk) throw new IOException("SQLite statement failed: " + result);
    }

    private static void Backup(IntPtr source, string backupPath)
    {
        IntPtr destination = OpenDatabase(backupPath, SqliteOpenReadWrite | SqliteOpenCreate);
        try
        {
            IntPtr backup = SqliteBackupInit(destination, Utf8("main"), source, Utf8("main"));
            if (backup == IntPtr.Zero) throw new IOException("SQLite backup initialization failed.");
            int step = SqliteBackupStep(backup, -1);
            int finish = SqliteBackupFinish(backup);
            if (step != SqliteDone || finish != SqliteOk)
                throw new IOException("SQLite backup failed: " + step + "/" + finish);
        }
        finally { SqliteClose(destination); }
    }

    private static string Quote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string UpdatePath(string table, string column, string oldWorkspace, string newWorkspace)
    {
        string oldValue = Quote(oldWorkspace);
        string newValue = Quote(newWorkspace);
        string normalized = "replace(" + column + ",char(92),'/')";
        return "UPDATE " + table + " SET " + column + " = " + newValue +
            " || substr(" + normalized + ",length(" + oldValue + ")+1)" +
            " WHERE substr(" + normalized + ",1,length(" + oldValue + ")) = " + oldValue + " COLLATE NOCASE" +
            " AND (length(" + normalized + ") = length(" + oldValue + ")" +
            " OR substr(" + normalized + ",length(" + oldValue + ")+1,1) = '/');";
    }

    private static void MigrateDatabase(string path, string backupPath, string oldWorkspace, string newWorkspace)
    {
        IntPtr database = OpenDatabase(path, SqliteOpenReadWrite);
        try
        {
            Backup(database, backupPath);
            Execute(database, "BEGIN IMMEDIATE;");
            try
            {
                Execute(database, UpdatePath("session", "directory", oldWorkspace, newWorkspace));
                Execute(database, UpdatePath("session", "path", oldWorkspace, newWorkspace));
                Execute(database, UpdatePath("project", "worktree", oldWorkspace, newWorkspace));
                Execute(database, UpdatePath("project_directory", "directory", oldWorkspace, newWorkspace));
                Execute(database, UpdatePath("workspace", "directory", oldWorkspace, newWorkspace));
                Execute(database, "COMMIT;");
            }
            catch
            {
                try { Execute(database, "ROLLBACK;"); } catch {}
                throw;
            }
        }
        finally { SqliteClose(database); }
    }
}