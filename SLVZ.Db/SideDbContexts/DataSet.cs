#nullable disable
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace SLVZ.Db.Light;

/// <summary>
/// Optional diagnostics hook. The database never throws for a single bad field or a torn last row,
/// it reports them here instead (wire it to your logger if you want to see them).
/// </summary>
public static class DbLog
{
    public static Action<string> Log { get; set; }
}

/// <summary>
/// A tiny text database: one row per line, rows look like
///   &lt;db.ID&gt;1&lt;/db.ID&gt;&lt;db.br/&gt;&lt;db.Text&gt;hello&lt;/db.Text&gt;&lt;db.br/&gt;&lt;db.__v&gt;2&lt;/db.__v&gt;
/// The on-disk format is backward compatible with the previous version of this class.
///
/// What makes it safe:
///  - Every operation on a file takes one in-process lock that is shared by ALL DataSet instances of that path
///    (so several "new AppDb()" instances and several threads are fine). Nothing polls or retries on exceptions.
///  - Rewrites (Update/Remove) go to "file.tmp" first and replace the real file with an atomic rename,
///    so a crash never leaves a half-written database.
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
    private static ModelInfo _modelInfo;

    private string KeyName { get; set; }
    private string FilePath { get; set; }
    private Type ModelType { get; set; }

    private readonly string _fullPath;
    private readonly SemaphoreSlim _gate;
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
        _gate = Gates.GetOrAdd(_fullPath, _ => new SemaphoreSlim(1, 1));
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
        await Locked(() => AppendRows(new[] { row })).ConfigureAwait(false);
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

        await Locked(() => AppendRows(rows)).ConfigureAwait(false);
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
        string key = KeyToText(id);
        await Locked(() => Rewrite(line => string.Equals(ExtractKey(line), key, StringComparison.Ordinal) ? null : line))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the specified model from the database.
    /// </summary>
    public async Task Remove(TModel model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));

        string key = GetKeyText(model);
        await Locked(() => Rewrite(line => string.Equals(ExtractKey(line), key, StringComparison.Ordinal) ? null : line))
            .ConfigureAwait(false);
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

        await Locked(() => Rewrite(line =>
        {
            var key = ExtractKey(line);
            return key != null && keys.Contains(key) ? null : line;
        })).ConfigureAwait(false);
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

        string key = GetKeyText(model);
        string newRow = BuildRow(model);

        await Locked(() => Rewrite(line =>
        {
            if (!string.Equals(ExtractKey(line), key, StringComparison.Ordinal))
                return line;

            // Same content: keep the very same string so nothing is rewritten on disk.
            return string.Equals(line, newRow, StringComparison.Ordinal) ? line : newRow;
        })).ConfigureAwait(false);
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

        await Locked(() => Rewrite(line =>
        {
            var key = ExtractKey(line);
            if (key == null || !rows.TryGetValue(key, out var newRow))
                return line;

            return string.Equals(line, newRow, StringComparison.Ordinal) ? line : newRow;
        })).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes every row (and the file) under the same lock the other operations use.
    /// Use this instead of File.Delete when other code might be using the database at the same time.
    /// </summary>
    public async Task Clear()
    {
        await Locked(() =>
        {
            if (File.Exists(_fullPath)) File.Delete(_fullPath);
            if (File.Exists(_fullPath + ".tmp")) File.Delete(_fullPath + ".tmp");
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
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return body(); }
        finally { _gate.Release(); }
    }

    private async Task Locked(Action body)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { body(); }
        finally { _gate.Release(); }
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
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(_durable);
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
    // The new content is written to "file.tmp" and swapped in with an atomic rename, only if something really changed.
    private int Rewrite(Func<string, string> map)
    {
        if (!File.Exists(_fullPath))
            return 0;

        string tmp = _fullPath + ".tmp";
        int changed = 0;

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
                    }
                }

                writer.Flush();
                dst.Flush(_durable);
            }

            if (changed == 0)
            {
                File.Delete(tmp);
                return 0;
            }

            File.Move(tmp, _fullPath, true);
            return changed;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
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
