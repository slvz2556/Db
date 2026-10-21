#nullable disable
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SLVZ.Db;

/// <summary>
/// Optional diagnostics hook. The database never throws for a single bad field or a torn last row,
/// it reports them here instead (wire it to your logger if you want to see them).
/// </summary>
public static class DbLog
{
    public static Action<string> Log { get; set; }
}

#if DBTEST
// Only compiled when the DBTEST symbol is defined (the test project). Lets tests kill the process at an exact step.
public static class DbFaultInjection
{
    public static Action<string> Hook;
}
#endif

// Makes the rename of a file durable on Linux/Android by flushing the folder itself. Best effort: if it isn't available it does nothing.
// (P/Invoke can't live inside a generic class, that's why this is a separate class.)
internal static class DirectorySync
{
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int sys_open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync")]
    private static extern int sys_fsync(int fd);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int sys_close(int fd);

    private static bool _unavailable;

    public static bool TrySync(string directory)
    {
        if (_unavailable || string.IsNullOrEmpty(directory) || !(OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()))
            return false;

        try
        {
            int fd = sys_open(directory, 0);   // 0 = O_RDONLY, works on directories
            if (fd < 0)
                return false;

            try { return sys_fsync(fd) == 0; }
            finally { sys_close(fd); }
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
            _unavailable = true;
            return false;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// A tiny text database: one row per line, rows look like
///   &lt;db.ID&gt;1&lt;/db.ID&gt;&lt;db.br/&gt;&lt;db.Text&gt;hello&lt;/db.Text&gt;&lt;db.br/&gt;&lt;db.__v&gt;2&lt;/db.__v&gt;
/// The on-disk format is backward compatible with the previous version of this class.
///
/// What makes it safe:
///  - Every operation on a file takes one in-process lock that is shared by ALL DataSet instances of that path
///    (so several "new AppDb()" instances and several threads are fine). Nothing polls or retries on exceptions.
///  - Update/Remove never edit the real file. They write the complete new content to "file.tmp", check it, and swap it in
///    with an atomic rename, so a crash never leaves a half-written database.
///  - Update/Remove first write a small JOURNAL ("file.journal": what to change + a checksum). If the app dies at any point
///    of the work, the first access to that file in the next run finishes the job (the replay is idempotent, so it doesn't
///    matter how far the first attempt got). If the app dies before the journal is complete, nothing happened at all.
///    An operation that throws an exception is NOT redone later: the caller was told it failed.
///  - Add(list) is journaled too (a list is added completely or not at all), and so is every Add of a durable dataset.
///  - durable = true datasets additionally: flush every file to the storage chip (and the folder entry after
///    a rename), and keep the previous version as "file.bak" (used automatically if the main file ever goes missing).
///  - Add only appends. A torn last row (crash while appending) is ignored when reading and cut off before the next append.
///  - Numbers and dates are written with the invariant culture, so changing the phone language/region can't break reading.
///    Old rows (written with the device culture) are still read the old way.
///  - Text may contain \r and \n.
///
/// Known limits (same as before): a text value must not contain the literal markers "&lt;db.br/&gt;", "&lt;db.break/&gt;" or "&lt;db.cr/&gt;".
/// Add does not check for duplicate keys. Every operation reads/rewrites the whole file, so keep files small.
/// </summary>
public partial class DataSet<TModel>
{
    private const string RowSeparator = "<db.br/>";
    private const string LineBreakToken = "<db.break/>";
    private const string CarriageReturnToken = "<db.cr/>";
    private const string FormatTagName = "__v";                  // row format revision marker
    private const string FormatTag = "<db.__v>2</db.__v>";      // 2 = invariant culture + \r escaping
    private const int BufferSize = 60 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly ConcurrentDictionary<string, PathState> States =
        new ConcurrentDictionary<string, PathState>(StringComparer.Ordinal);
    private static ModelInfo _modelInfo;

    private string KeyName { get; set; }
    private string FilePath { get; set; }
    private Type ModelType { get; set; }

    private readonly string _fullPath;
    private readonly PathState _state;
    private readonly bool _durable;
    private readonly ModelInfo _info;


    public DataSet(string filepath) : this(filepath, false) { }

    /// <param name="filepath">Path of the database file.</param>
    /// <param name="durable">
    /// true = every write is flushed to the storage chip before the call returns (survives power loss, a bit slower).
    /// Use it for data that must never be lost (boards list, settings, purchases). false = survives the app being killed,
    /// which is enough for cache data.
    /// </param>
    public DataSet(string filepath, bool durable)
    {
        ModelType = typeof(TModel);
        FilePath = filepath;
        _durable = durable;

        _info = _modelInfo ??= BuildModelInfo(ModelType);
        this.KeyName = _info.Key.Name;

        if (string.IsNullOrEmpty(KeyName) || string.IsNullOrEmpty(FilePath))
            throw new InvalidOperationException("Both Key and FilePath must be set in Configuration().");

        _fullPath = Path.GetFullPath(FilePath);
        _state = States.GetOrAdd(_fullPath, _ => new PathState());
    }


    // ------------------------------------------------------------------------------------------------ public API

    /// <summary>
    /// Adds a new model to the database.
    /// </summary>
    public async Task Add(TModel model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));
        if (model.GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        string row = BuildRow(model);
        await Locked(() => DoAdd(new[] { row })).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a list of new models to the database
    /// </summary>
    public async Task Add(List<TModel> models)
    {
        if (models is null)
            throw new ArgumentNullException(nameof(models));
        if (models.Count == 0)
            return;

        var rows = new string[models.Count];
        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            if (model == null)
                throw new ArgumentNullException(nameof(models), "The list contains a null model.");
            if (model.GetType() != ModelType)
                throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

            rows[i] = BuildRow(model);
        }

        await Locked(() => DoAdd(rows)).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves all models stored in the database.
    /// </summary>
    public async Task<List<TModel>> All()
    {
        return await Locked(() =>
        {
            var objects = new List<TModel>();
            foreach (var line in EnumerateRows())
                objects.Add(ParseRow(line));
            return objects;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves count of models stored in the database.
    /// </summary>
    public async Task<int> Count()
    {
        return await Locked(() =>
        {
            int count = 0;
            foreach (var _ in EnumerateRows())
                count++;
            return count;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves first model stored in the database (an empty model if the database is empty).
    /// </summary>
    public async Task<TModel> First()
    {
        return await Locked(() =>
        {
            foreach (var line in EnumerateRows())
                return ParseRow(line);

            return NewInstance();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves a model from the database by its key (an empty model if there is no such key).
    /// </summary>
    public async Task<TModel> Find<T>(T id)
    {
        string key = KeyToText(id);

        return await Locked(() =>
        {
            foreach (var line in EnumerateRows())
                if (string.Equals(ExtractKey(line), key, StringComparison.Ordinal))
                    return ParseRow(line);

            return NewInstance();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the model with the specified key from the database.
    /// </summary>
    public async Task Remove<T>(T id)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { KeyToText(id) };
        await Locked(() => DoRemove(keys)).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the specified model from the database.
    /// </summary>
    public async Task Remove(TModel model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));

        var keys = new HashSet<string>(StringComparer.Ordinal) { GetKeyText(model) };
        await Locked(() => DoRemove(keys)).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the specified models from the database.
    /// </summary>
    public async Task RemoveRange(List<TModel> models)
    {
        if (models is null)
            throw new ArgumentNullException(nameof(models));
        if (models.Count == 0)
            return;

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
            keys.Add(GetKeyText(model));

        await Locked(() => DoRemove(keys)).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the specified model in the database (does nothing if the key doesn't exist).
    /// </summary>
    public async Task Update(TModel model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));
        if (model.GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        var rows = new Dictionary<string, string>(StringComparer.Ordinal) { [GetKeyText(model)] = BuildRow(model) };
        await Locked(() => DoUpdate(rows)).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the specified models in the database (keys that don't exist are ignored).
    /// </summary>
    public async Task UpdateRange(List<TModel> models)
    {
        if (models is null)
            throw new ArgumentNullException(nameof(models));
        if (models.Count == 0)
            return;

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(models), "The list contains a null model.");
            if (model.GetType() != ModelType)
                throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

            rows[GetKeyText(model)] = BuildRow(model);
        }

        await Locked(() => DoUpdate(rows)).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes every row (and the file) under the same lock the other operations use.
    /// Use this instead of File.Delete when other code might be using the database at the same time.
    /// </summary>
    public async Task Clear()
    {
        await Locked(() =>
        {
            // The main file goes last: if we die in the middle, the old data is still there (never a .bak that resurrects deleted data).
            DeleteQuietly(JournalPath);
            DeleteQuietly(JournalTmpPath);
            DeleteQuietly(TmpPath);
            DeleteQuietly(BakPath);
            if (File.Exists(_fullPath))
                File.Delete(_fullPath);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves all models that satisfy the specified condition.
    /// </summary>
    public async Task<List<TModel>> Where(Expression<Func<TModel, bool>> predicate)
    {
        var filter = predicate.Compile();

        return await Locked(() =>
        {
            var objects = new List<TModel>();
            foreach (var line in EnumerateRows())
            {
                var model = ParseRow(line);
                if (filter(model))
                    objects.Add(model);
            }
            return objects;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether any model exists that satisfies the specified condition.
    /// </summary>
    public async Task<bool> Any(Expression<Func<TModel, bool>> predicate)
    {
        var filter = predicate.Compile();

        return await Locked(() =>
        {
            foreach (var line in EnumerateRows())
                if (filter(ParseRow(line)))
                    return true;

            return false;
        }).ConfigureAwait(false);
    }


    // ------------------------------------------------------------------------------------------------ locking

    // The body is plain synchronous file work. When nobody else holds the lock WaitAsync completes synchronously,
    // so an uncontended call still finishes before it returns (code that fires these without await keeps working).
    private async Task<T> Locked<T>(Func<T> body)
    {
        await _state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureRecovered();
            return body();
        }
        finally { _state.Gate.Release(); }
    }

    private async Task Locked(Action body)
    {
        await _state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureRecovered();
            body();
        }
        finally { _state.Gate.Release(); }
    }

    private sealed class PathState
    {
        public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        public bool Recovered;   // only touched while holding Gate
    }


    // ------------------------------------------------------------------------------------------------ file access (call only while holding the lock)

    private void EnsureDirectory()
    {
        var dir = Path.GetDirectoryName(_fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    // Lines are split ONLY on '\n' (the old code used ReadLine, which also splits on '\r' and tore apart rows whose text had a '\r').
    // A last line without '\n' is a row that was being written when the app died, it is skipped.
    private IEnumerable<string> EnumerateRows()
    {
        if (!File.Exists(_fullPath))
            yield break;

        using var fs = new FileStream(_fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
        using var reader = new StreamReader(fs, Utf8NoBom, true, BufferSize);

        var sb = new StringBuilder();
        var buffer = new char[8192];
        int read;

        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != '\n')
                    continue;

                sb.Append(buffer, start, i - start);
                start = i + 1;

                if (sb.Length > 0 && sb[sb.Length - 1] == '\r')   // file with "\r\n" line ends
                    sb.Length--;

                if (sb.Length > 0)
                    yield return sb.ToString();

                sb.Clear();
            }

            if (start < read)
                sb.Append(buffer, start, read - start);
        }

        if (sb.Length > 0)
            DbLog.Log?.Invoke($"[{Path.GetFileName(_fullPath)}] ignored an incomplete last row ({sb.Length} chars).");
    }

    private string TmpPath => _fullPath + ".tmp";
    private string BakPath => _fullPath + ".bak";
    private string JournalPath => _fullPath + ".journal";
    private string JournalTmpPath => _fullPath + ".journal.tmp";

    [Conditional("DBTEST")]
    private static void Hook(string point)
    {
#if DBTEST
        DbFaultInjection.Hook?.Invoke(point);
#endif
    }

    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void SyncDirectory() => DirectorySync.TrySync(Path.GetDirectoryName(_fullPath));

    private void AppendRows(string[] rows)
    {
        EnsureDirectory();

        using var fs = new FileStream(_fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, BufferSize);
        CutTornTail(fs);
        fs.Seek(0, SeekOrigin.End);

        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append(row).Append('\n');

        var bytes = Utf8NoBom.GetBytes(sb.ToString());
#if DBTEST
        // lets a test die with half of the rows written
        fs.Write(bytes, 0, bytes.Length / 2);
        fs.Flush();
        Hook("add-half");
        fs.Write(bytes, bytes.Length / 2, bytes.Length - bytes.Length / 2);
#else
        fs.Write(bytes, 0, bytes.Length);
#endif
        fs.Flush(_durable);
        if (_durable)
            SyncDirectory();
    }

    // If the file doesn't end with '\n' the last row was torn by a crash: cut it off so the next row starts on a clean line.
    private void CutTornTail(FileStream fs)
    {
        long length = fs.Length;
        if (length == 0)
            return;

        fs.Seek(length - 1, SeekOrigin.Begin);
        if (fs.ReadByte() == '\n')
            return;

        long cut = 0;
        long position = length - 1;
        var buffer = new byte[4096];

        while (position > 0)
        {
            int size = (int)Math.Min(buffer.Length, position);
            position -= size;
            fs.Seek(position, SeekOrigin.Begin);
            fs.ReadExactly(buffer, 0, size);

            int index = Array.LastIndexOf(buffer, (byte)'\n', size - 1);
            if (index >= 0)
            {
                cut = position + index + 1;
                break;
            }
        }

        DbLog.Log?.Invoke($"[{Path.GetFileName(_fullPath)}] removed an incomplete last row ({length - cut} bytes).");
        fs.SetLength(cut);
    }

    // map(line): return the same string = keep, another string = replace, null = delete.
    // The new content is written completely to "file.tmp", checked, and only then swapped in with an atomic rename
    // (and only if something really changed). The real file is never edited in place.
    private int Rewrite(Func<string, string> map)
    {
        if (!File.Exists(_fullPath))
            return 0;

        string tmp = TmpPath;
        int changed = 0;
        long expectedBytes = 0;
        bool movedToBak = false;

        try
        {
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            using (var writer = new StreamWriter(dst, Utf8NoBom, BufferSize))
            {
                foreach (var line in EnumerateRows())
                {
                    var result = map(line);
                    if (!ReferenceEquals(result, line))
                        changed++;

                    if (result != null)
                    {
                        writer.Write(result);
                        writer.Write('\n');
                        expectedBytes += Utf8NoBom.GetByteCount(result) + 1;
                    }

                    Hook("tmp-writing");
                }

                writer.Flush();
                dst.Flush(_durable);

                // a short write (full disk...) must never replace a good file
                if (dst.Length != expectedBytes)
                    throw new IOException($"The new content of {Path.GetFileName(_fullPath)} is incomplete ({dst.Length} of {expectedBytes} bytes).");
            }

            Hook("tmp-written");

            if (changed == 0)
            {
                File.Delete(tmp);
                return 0;
            }

            if (_durable)
            {
                // keep the previous version; if we die between the two renames the next run restores it and finishes the job
                File.Move(_fullPath, BakPath, true);
                movedToBak = true;
                Hook("main-to-bak");
            }

            File.Move(tmp, _fullPath, true);
            movedToBak = false;
            Hook("tmp-to-main");

            if (_durable)
                SyncDirectory();

            return changed;
        }
        catch
        {
            // undo a half done swap
            if (movedToBak && !File.Exists(_fullPath) && File.Exists(BakPath))
            {
                try { File.Move(BakPath, _fullPath, false); } catch { }
            }

            DeleteQuietly(tmp);
            throw;
        }
    }


    // ------------------------------------------------------------------------------------------------ journal (finish interrupted work)

    private enum JournalOp { Add, Update, Remove }

    private sealed class JournalData
    {
        public JournalOp Op;
        public List<string> Lines;
    }

    private void DoAdd(string[] rows)
    {
        // A single row is all-or-nothing by itself (a torn row is dropped). Several rows (or any row of a durable dataset)
        // go through the journal, so an interrupted Add is completed by the next run: never half of a list.
        if (_durable || rows.Length > 1)
            Journaled(JournalOp.Add, rows, () => AppendRows(rows));
        else
            AppendRows(rows);
    }

    private void DoUpdate(Dictionary<string, string> rows)
    {
        if (!File.Exists(_fullPath))
            return;

        Journaled(JournalOp.Update, rows.Values.ToList(), () => Rewrite(MapUpdate(rows)));
    }

    private void DoRemove(HashSet<string> keys)
    {
        if (!File.Exists(_fullPath))
            return;

        Journaled(JournalOp.Remove, keys.ToList(), () => Rewrite(MapRemove(keys)));
    }

    private Func<string, string> MapUpdate(Dictionary<string, string> rows) => line =>
    {
        var key = ExtractKey(line);
        if (key == null || !rows.TryGetValue(key, out var newRow))
            return line;

        // Same content: keep the very same string so nothing is rewritten on disk.
        return string.Equals(line, newRow, StringComparison.Ordinal) ? line : newRow;
    };

    private Func<string, string> MapRemove(HashSet<string> keys) => line =>
    {
        var key = ExtractKey(line);
        return key != null && keys.Contains(key) ? null : line;
    };

    // 1. write the journal (complete or not at all)  2. do the work  3. delete the journal.
    private void Journaled(JournalOp op, IReadOnlyList<string> lines, Action apply)
    {
        WriteJournal(op, lines);
        Hook("journal-written");

        try
        {
            apply();
        }
        catch
        {
            // The caller gets this exception, so the operation must not be redone later.
            // Only exception: a swap that died between its two renames left no main file; keep the journal so recovery finishes it.
            if (File.Exists(_fullPath) || !File.Exists(BakPath))
                DeleteQuietly(JournalPath);
            else
                _state.Recovered = false;

            throw;
        }

        DeleteJournal();
        Hook("journal-deleted");
    }

    private void DeleteJournal()
    {
        if (File.Exists(JournalPath))
            File.Delete(JournalPath);

        if (_durable)
            SyncDirectory();
    }

    // Layout: SLVZJ1 / operation / number of lines / the lines / "#" + SHA-256 of everything before.
    // It is written to a temp file and renamed, and the checksum rejects a journal that was cut by a power loss.
    private void WriteJournal(JournalOp op, IReadOnlyList<string> lines)
    {
        EnsureDirectory();

        var sb = new StringBuilder();
        sb.Append("SLVZJ1\n").Append(op.ToString().ToLowerInvariant()).Append('\n').Append(lines.Count).Append('\n');
        foreach (var line in lines)
            sb.Append(line).Append('\n');

        string body = sb.ToString();
        string hash = Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(body)));
        var bytes = Utf8NoBom.GetBytes(body + "#" + hash + "\n");

        using (var fs = new FileStream(JournalTmpPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(_durable);
        }

        Hook("journal-tmp-written");
        File.Move(JournalTmpPath, JournalPath, true);

        if (_durable)
            SyncDirectory();
    }

    private JournalData ReadJournal()
    {
        try
        {
            string text = File.ReadAllText(JournalPath, Utf8NoBom);
            if (text.Length < 2 || text[text.Length - 1] != '\n')
                return null;

            int trailerStart = text.LastIndexOf('\n', text.Length - 2) + 1;
            string trailer = text.Substring(trailerStart).TrimEnd('\n');
            if (!trailer.StartsWith("#", StringComparison.Ordinal))
                return null;

            string body = text.Substring(0, trailerStart);
            string expected = Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(body)));
            if (!string.Equals(trailer.Substring(1), expected, StringComparison.Ordinal))
                return null;

            var parts = body.Split('\n');   // the last element is empty because the body ends with '\n'
            if (parts.Length < 4 || parts[0] != "SLVZJ1")
                return null;
            if (!Enum.TryParse(parts[1], true, out JournalOp op))
                return null;
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || parts.Length != 3 + count + 1)
                return null;

            return new JournalData { Op = op, Lines = parts.Skip(3).Take(count).ToList() };
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void EnsureRecovered()
    {
        if (_state.Recovered)
            return;

        Recover();
        _state.Recovered = true;
    }

    // Runs once per file and run of the app, before the first operation. If it throws, it is tried again by the next operation.
    private void Recover()
    {
        string name = Path.GetFileName(_fullPath);

        // unfinished temp files are useless (the journal is what matters)
        DeleteQuietly(TmpPath);
        DeleteQuietly(JournalTmpPath);

        // main file missing = we died between the two renames of a rewrite (or it was lost): the previous version is in .bak
        if (!File.Exists(_fullPath) && File.Exists(BakPath))
        {
            File.Copy(BakPath, TmpPath, true);
            File.Move(TmpPath, _fullPath, false);
            DbLog.Log?.Invoke($"[{name}] the file was missing, restored the previous version.");
        }

        if (!File.Exists(JournalPath))
            return;

        var journal = ReadJournal();
        if (journal == null)
        {
            // never completed = the operation never started (nothing was changed yet)
            DbLog.Log?.Invoke($"[{name}] ignored an incomplete journal.");
            DeleteQuietly(JournalPath);
            return;
        }

        DbLog.Log?.Invoke($"[{name}] finishing an interrupted {journal.Op.ToString().ToLowerInvariant()} ({journal.Lines.Count} rows).");
        Replay(journal);
        DeleteJournal();
    }

    // Every replay is idempotent: running it again after it already (partly) happened gives the same final state.
    private void Replay(JournalData journal)
    {
        switch (journal.Op)
        {
            case JournalOp.Update:
            {
                var rows = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var row in journal.Lines)
                {
                    var key = ExtractKey(row);
                    if (key != null)
                        rows[key] = row;
                }
                Rewrite(MapUpdate(rows));
                break;
            }

            case JournalOp.Remove:
                Rewrite(MapRemove(new HashSet<string>(journal.Lines, StringComparer.Ordinal)));
                break;

            case JournalOp.Add:
            {
                // rows that already reached the end of the file (before the crash) must not be added twice
                var existing = EnumerateRows().ToList();
                int skip = Math.Min(journal.Lines.Count, existing.Count);
                for (; skip > 0; skip--)
                {
                    bool same = true;
                    for (int i = 0; i < skip && same; i++)
                        same = string.Equals(existing[existing.Count - skip + i], journal.Lines[i], StringComparison.Ordinal);

                    if (same)
                        break;
                }

                if (skip < journal.Lines.Count)
                    AppendRows(journal.Lines.Skip(skip).ToArray());
                break;
            }
        }
    }


    // ------------------------------------------------------------------------------------------------ rows <-> models

    private TModel NewInstance() => (TModel)Activator.CreateInstance(ModelType);

    private string KeyPrefix(string key) => $"<db.{KeyName}>{key}</db.{KeyName}>";

    private string ExtractKey(string line)
    {
        string open = $"<db.{KeyName}>";
        if (!line.StartsWith(open, StringComparison.Ordinal))
            return null;

        int end = line.IndexOf($"</db.{KeyName}>", open.Length, StringComparison.Ordinal);
        return end < 0 ? null : line.Substring(open.Length, end - open.Length);
    }

    private static string KeyToText<T>(T id)
    {
        if (id == null)
            throw new ArgumentNullException(nameof(id));

        return ToText(id);
    }

    private string GetKeyText(TModel model)
    {
        var value = _info.Key.Property.GetValue(model);
        if (value == null)
            throw new InvalidOperationException($"The key '{KeyName}' of the model is null.");

        string key = ToText(value);
        if (key.Contains('\n') || key.Contains('\r'))
            throw new InvalidDataException("You cannot use Enter in key");

        return key;
    }

    private string BuildRow(TModel model)
    {
        var sb = new StringBuilder();
        sb.Append(KeyPrefix(GetKeyText(model)));

        foreach (var p in _info.WriteProps)
        {
            var value = p.Property.GetValue(model);
            if (value == null)
                continue;

            string text = ToText(value);
            if (string.IsNullOrEmpty(text))
                continue;

            sb.Append(RowSeparator)
              .Append("<db.").Append(p.Name).Append('>')
              .Append(Escape(text))
              .Append("</db.").Append(p.Name).Append('>');
        }

        sb.Append(RowSeparator).Append(FormatTag);
        return sb.ToString();
    }

    private TModel ParseRow(string line)
    {
        var instance = NewInstance();
        var found = new List<KeyValuePair<PropInfo, string>>();
        bool invariant = false;

        foreach (var segment in line.Split(RowSeparator))
        {
            if (!segment.StartsWith("<db.", StringComparison.Ordinal))
                continue;

            int gt = segment.IndexOf('>');
            if (gt < 0)
                continue;

            string name = segment.Substring(4, gt - 4);
            string close = "</db." + name + ">";
            int valueStart = gt + 1;

            string raw = segment.EndsWith(close, StringComparison.Ordinal) && segment.Length - close.Length >= valueStart
                ? segment.Substring(valueStart, segment.Length - close.Length - valueStart)
                : segment.Substring(valueStart);

            if (name == FormatTagName)
            {
                invariant = raw == "2";
                continue;
            }

            if (_info.ReadProps.TryGetValue(name, out var prop))
                found.Add(new KeyValuePair<PropInfo, string>(prop, raw));
        }

        // New rows are invariant. Rows written by the previous version used the device culture, so try that first
        // (exactly like before), then the invariant one.
        var cultures = invariant
            ? new[] { CultureInfo.InvariantCulture }
            : new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture };

        foreach (var pair in found)
        {
            var prop = pair.Key;
            string text = Unescape(pair.Value);

            try
            {
                if (prop.IsString)
                {
                    prop.Property.SetValue(instance, text);
                    continue;
                }

                object value = null;
                bool ok = false;
                foreach (var culture in cultures)
                {
                    if (TryConvert(text, prop.Underlying, culture, out value))
                    {
                        ok = true;
                        break;
                    }
                }

                if (ok)
                    prop.Property.SetValue(instance, value);
                else
                    DbLog.Log?.Invoke($"[{Path.GetFileName(_fullPath)}] could not read {typeof(TModel).Name}.{prop.Name} from \"{text}\", default value used.");
            }
            catch (Exception e)
            {
                DbLog.Log?.Invoke($"[{Path.GetFileName(_fullPath)}] could not set {typeof(TModel).Name}.{prop.Name}: {e.Message}");
            }
        }

        return instance;
    }

    private static string Escape(string text) =>
        text.Replace("\r", CarriageReturnToken).Replace("\n", LineBreakToken);

    private static string Unescape(string text) =>
        text.Replace(LineBreakToken, "\n").Replace(CarriageReturnToken, "\r");

    // Numbers, enums, Guids... always as invariant text. DateTime with "O" keeps the milliseconds and the Kind.
    private static string ToText(object value)
    {
        switch (value)
        {
            case string s: return s;
            case DateTime dt: return dt.ToString("O", CultureInfo.InvariantCulture);
            case DateTimeOffset dto: return dto.ToString("O", CultureInfo.InvariantCulture);
            case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
            default: return value.ToString();
        }
    }

    private static bool TryConvert(string text, Type type, CultureInfo culture, out object value)
    {
        value = null;
        try
        {
            if (type.IsEnum) value = Enum.Parse(type, text);
            else if (type == typeof(DateTime)) value = DateTime.Parse(text, culture, DateTimeStyles.RoundtripKind);
            else if (type == typeof(DateTimeOffset)) value = DateTimeOffset.Parse(text, culture, DateTimeStyles.RoundtripKind);
            else if (type == typeof(TimeSpan)) value = TimeSpan.Parse(text, culture);
            else if (type == typeof(Guid)) value = Guid.Parse(text);
            else value = Convert.ChangeType(text, type, culture);

            return true;
        }
        catch
        {
            return false;
        }
    }


    // ------------------------------------------------------------------------------------------------ model description (computed once per model type)

    private sealed class PropInfo
    {
        public PropertyInfo Property;
        public string Name;
        public Type Underlying;
        public bool IsString;
    }

    private sealed class ModelInfo
    {
        public PropInfo Key;
        public PropInfo[] WriteProps;
        public Dictionary<string, PropInfo> ReadProps;
    }

    private static ModelInfo BuildModelInfo(Type modelType)
    {
        var properties = modelType.GetProperties();

        var keyProperty = properties.FirstOrDefault(x => x.GetCustomAttribute<Key>() is not null);
        if (keyProperty == null)
            throw new Exception("There is no key in your model. Use [SLVZ.Db.Light.Key] attribute in your model");

        static bool IsStorable(PropertyInfo p) =>
            p.GetIndexParameters().Length == 0 && p.PropertyType != typeof(byte[]) && p.PropertyType != typeof(byte);

        PropInfo Describe(PropertyInfo p) => new PropInfo
        {
            Property = p,
            Name = p.Name,
            Underlying = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType,
            IsString = p.PropertyType == typeof(string)
        };

        var info = new ModelInfo { Key = Describe(keyProperty) };

        info.WriteProps = properties
            .Where(p => p.Name != keyProperty.Name && p.CanRead && IsStorable(p) && p.GetCustomAttribute<Ignore>() is null)
            .Select(Describe)
            .ToArray();

        info.ReadProps = properties
            .Where(p => p.CanWrite && IsStorable(p) && p.GetCustomAttribute<Ignore>() is null)
            .Select(Describe)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        return info;
    }
}
