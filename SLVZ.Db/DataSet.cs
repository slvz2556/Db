using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using static System.Net.Mime.MediaTypeNames;

namespace SLVZ.Db;


public class DataSet<TModel>
{

    private string path = "";

    private readonly byte[] key = { 0x13, 0x37, 0xAA, 0x5c };
    private Type ModelType { get; set; }

    private readonly PropertyInfo[] _props;
    private readonly Dictionary<string, PropertyInfo> _propDict;

    PropertyInfo primaryKey;

    //Actions
    internal readonly DbContext Context;

    internal DataSet(DbContext _context)
    {
        ModelType = typeof(TModel);
        var properties = ModelType.GetProperties();

        if (properties.Where(x => x.GetCustomAttribute<PrimaryKey>() is not null).Count() == 0)
            throw new Exception("There is no primary key");

        primaryKey = properties.Where(x => x.GetCustomAttribute<PrimaryKey>() is not null).First();

        var primaryKeyType = primaryKey.ToString().Remove(primaryKey.ToString().Length - primaryKey.Name.Length - 1, primaryKey.Name.Length + 1);

        if (primaryKeyType != "Int64")
            throw new Exception("Primary key type must be Int64");

        path = $"{_context._path}-{ModelType.Name.ToLower()}";
        if (!File.Exists(path))
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        }

        File.SetAttributes(path, FileAttributes.Hidden);

        _props = ModelType.GetProperties().Where(p => p.CanRead || p.CanWrite).ToArray();
        _propDict = _props.Where(p => p.CanWrite).ToDictionary(p => p.Name);

        Context = _context;
    }




    /// <summary>
    /// Adds a new model to the database.
    /// </summary>
    /// <param name="model">The model instance to add.</param>
    /// <returns>The primary key assigned to the newly added model.</returns>
    public async Task<Int64> Add(TModel model)
    {
        var key = await FindFirstAvailablePrimaryKey();
        ModelType.GetProperty(primaryKey.Name).SetValue(model, key);

        var byt = Serialize(model);


        var index = await Context.InsertData(byt);
        index.PrimaryKey = key;
        index.IsFree = 0;
        await SetIndex(index);
        return key;
    }




    /// <summary>
    /// Retrieves a model from the database by its primary key.
    /// </summary>
    /// <param name="primaryKey">The primary key of the model to retrieve.</param>
    /// <returns>The model that matches the specified primary key, or null if not found.</returns>
    public async Task<TModel> Find(Int64 primaryKey)
    {
        var index = await FindIndex(primaryKey);

        if (index.PrimaryKey < 0) return (TModel)Convert.ChangeType(null, typeof(TModel));

        var bytes = await Context.SelectData(index);

        return Deserialize(bytes);
    }

    /// <summary>
    /// Retrieves all models whose primary keys satisfy the specified condition.
    /// </summary>
    /// <param name="predicate">A condition to filter models based on their primary key.</param>
    /// <returns>A list of models matching the specified primary key condition.</returns>
    public async Task<List<TModel>> Find(Expression<Func<Int64, bool>> predicate)
    {
        var filter = predicate.Compile();

        var maxID = await FindFirstAvailablePrimaryKey();
        if (maxID == 0)
            return new List<TModel>();

        maxID -= 1;
        Int64 skip = 0, count = maxID > 1000 ? 1000 : maxID;

        List<TModel> models = new List<TModel>();

        do
        {
            var indexs = await RangeIndexs(skip, count);
            indexs.RemoveAll(x => x.IsFree == 1);

            indexs.RemoveAll(x => !filter(x.PrimaryKey));

            if (indexs.Count > 0)
            {
                var byts = await Context.SelectRange(indexs);

                foreach (var item in byts)
                    models.Add(Deserialize(item));
            }

            skip += 1000;
        }
        while (skip <= maxID);


        return models;
    }





    /// <summary>
    /// Retrieves all models stored in the database.
    /// </summary>
    /// <returns>A list containing all models.</returns>
    public async Task<List<TModel>> All()
    {
        var maxID = await LastIndex();
        if (maxID == 0)
            return new List<TModel>();

        maxID -= 1;
        Int64 skip = 0, count = maxID > 2000 ? 2000 : maxID;

        List<TModel> models = new List<TModel>();

        do
        {
            var indexs = await RangeIndexs(skip, count);
            indexs.RemoveAll(x => x.IsFree == 1);

            var byts = await Context.SelectRange(indexs);

            foreach (var item in byts)
                models.Add(Deserialize(item));

            skip += 2000;
            if (skip + count > maxID)
                count = maxID - skip + 1;
        }
        while (skip <= maxID);

        return models;
    }





    /// <summary>
    /// Deletes the model with the specified primary key from the database.
    /// </summary>
    /// <param name="primaryKey">The primary key of the model to delete.</param>
    public async Task Remove(Int64 primaryKey)
    {
        var index = await FindIndex(primaryKey);

        if (index.PrimaryKey >= 0)
        {
            await Context.RemoveData(index);
            index.IsFree = 1;
            await SetIndex(index);
        }
    }

    /// <summary>
    /// Deletes the specified models from the database.
    /// </summary>
    /// <param name="models">A list of models to remove.</param>
    public async Task Remove(List<TModel> models)
    {
        List<Index> indexs = new List<Index>();

        foreach (var model in models)
        {
            var index = await FindIndex(Int64.Parse(ModelType.GetProperty(primaryKey.Name)?.GetValue(model)?.ToString()));
            indexs.Add(index);
        }

        indexs.RemoveAll(x => x.PrimaryKey < 0);

        if (indexs.Count() > 0)
        {
            await Context.RemoveRange(indexs);

            foreach (var item in indexs)
            {
                item.IsFree = 1;
                await SetIndex(item);
            }
        }
    }

    /// <summary>
    /// Deletes all models whose primary keys satisfy the specified condition.
    /// </summary>
    /// <param name="predicate">A condition to filter models for deletion based on their primary key.</param>
    public async Task Remove(Expression<Func<Int64, bool>> predicate)
    {
        var filter = predicate.Compile();

        var maxID = await LastIndex();
        if (maxID == 0)
            return;

        maxID -= 1;
        Int64 skip = 0, count = maxID > 1000 ? 1000 : maxID;

        do
        {
            var indexs = await RangeIndexs(skip, count);
            indexs.RemoveAll(x => x.IsFree == 1);

            indexs.RemoveAll(x => !filter(x.PrimaryKey));

            if (indexs.Count > 0)
            {
                await Context.RemoveRange(indexs);

                foreach (var item in indexs)
                {
                    item.IsFree = 1;
                    await SetIndex(item);
                }
            }

            skip += 1000;
        }
        while (skip <= maxID);


        return;
    }




    /// <summary>
    /// Updates the specified model in the database.
    /// </summary>
    /// <param name="model">The model instance containing updated data.</param>
    public async Task Update(TModel model)
    {
        var key = Int64.Parse(ModelType.GetProperty(primaryKey.Name).GetValue(model).ToString());

        var index = await FindIndex(key);

        if (index.PrimaryKey >= 0)
        {
            var byt = Serialize(model);

            var index1 = await Context.UpdateData(byt, index);
            index1.PrimaryKey = key;
            index1.IsFree = 0;
            await SetIndex(index1);
        }
    }

    /// <summary>
    /// Updates the specified models in the database.
    /// </summary>
    /// <param name="models">A collection of models containing updated data.</param>
    public async Task Update(IEnumerable<TModel> models)
    {
        foreach (var model in models)
        {

            var key = Int64.Parse(ModelType.GetProperty(primaryKey.Name).GetValue(model).ToString());

            var index = await FindIndex(key);

            if (index.PrimaryKey >= 0)
            {
                var byt = Serialize(model);

                var index1 = await Context.UpdateData(byt, index);
                index1.PrimaryKey = key;
                index1.IsFree = 0;
                await SetIndex(index1);
            }
        }
    }






    /// <summary>
    /// Retrieves all models that satisfy the specified condition.
    /// </summary>
    /// <param name="predicate">A condition to filter the models.</param>
    /// <returns>A list of models that match the specified condition.</returns>
    public async Task<List<TModel>> Where(Expression<Func<TModel, bool>> predicate)
    {
        var filter = predicate.Compile();

        var maxID = await LastIndex();
        if (maxID == 0)
            return new List<TModel>();

        maxID -= 1;
        Int64 skip = 0, count = maxID > 1000 ? 1000 : maxID;

        List<TModel> models = new List<TModel>();

        do
        {
            var indexs = await RangeIndexs(skip, count);
            indexs.RemoveAll(x => x.IsFree == 1);

            var byts = await Context.SelectRange(indexs);

            foreach (var item in byts)
            {
                var model = Deserialize(item);
                if (filter(model))
                    models.Add(model);
            }

            skip += 1000;
        }
        while (skip <= maxID);


        return models;
    }

    /// <summary>
    /// Determines whether any model exists that satisfies the specified condition.
    /// </summary>
    /// <param name="predicate">A condition to test against the models.</param>
    /// <returns>True if any model satisfies the condition; otherwise, false.</returns>
    public async Task<bool> Any(Expression<Func<TModel, bool>> predicate)
    {
        var filter = predicate.Compile();

        var maxID = await LastIndex();
        if (maxID == 0)
            return false;

        maxID -= 1;
        Int64 skip = 0, count = maxID > 1000 ? 1000 : maxID;


        do
        {
            var indexs = await RangeIndexs(skip, count);
            indexs.RemoveAll(x => x.IsFree == 1);

            var byts = await Context.SelectRange(indexs);

            foreach (var item in byts)
            {
                var model = Deserialize(item);
                if (filter(model))
                    return true;
            }

            skip += 1000;
        }
        while (skip <= maxID);


        return false;
    }






    //Find primary ket
    private async Task<Int64> FindFirstAvailablePrimaryKey()
    {

        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: false);
            }
            catch { await Task.Delay(150); }
        }


        using var reader = new BinaryReader(file);

        if (file.Length == 0)
        {
            file.Close();
            return 0;
        }
        else
        {

            file.Position = 0;
            byte hasFreePrimaryKey = reader.ReadByte();

            if (hasFreePrimaryKey == 1)
            {
                file.Position = 25;

                while (file.Position < file.Length)
                {
                    byte tmp = reader.ReadByte();

                    if (tmp == 1)
                        return ((file.Position / 25) - 1) < 0 ? 0 : (file.Position / 25) - 1;

                    else
                        file.Position += 24;
                }
                
                //file.Close();
                //file = new FileStream(file.Name, FileMode.Open, FileAccess.Write); //Change file mode from Read to Write
                //file.Position = 0; //Set position to 0
                using var writer = new StreamWriter(file); //Create a streamwriter to write on file
                writer.Write((byte)0); //Write a 0 as NO AVAILABLE PRIMARY KEY

                return file.Length / 25;
            }
            else
                return file.Length / 25;
        }
    }

    //Last index
    private async Task<Int64> LastIndex()
    {
        var fileInfo = new FileInfo(path);

        var position = fileInfo.Length;

        if (position == 0)
            return 0;
        else
            return position / 25;
    }

    //Set index
    private async Task SetIndex(Index index)
    {
        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, true); }
            catch { await Task.Delay(150); }
        }


        using var writer = new BinaryWriter(file);

        if (file.Length == 0)
        {
            file.Position = 0;
            writer.Write((byte)0);
        }

        file.Position = (index.PrimaryKey * 25) + 1;

        if (file.Length < file.Position)
            throw new Exception("The entred primary key is not valid");

        writer.Write(index.Position1.Position);
        writer.Write(index.Position1.Length);
        writer.Write(index.Position2.Position);
        writer.Write(index.Position2.Length);
        writer.Write(index.IsFree); // 0 = Taken, 1 = Free

        if (index.IsFree == 1)
        {
            file.Position = 0;
            writer.Write((byte)1);
        }
    }


    //Find index
    private async Task<Index> FindIndex(Int64 primaryKey)
    {
        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true); }
            catch { await Task.Delay(150); }
        }

        using var reader = new BinaryReader(file);

        Int64 position = (primaryKey * 25) + 1;


        if (file.Length <= position)
            return new Index { PrimaryKey = -1 };
        else
        {
            file.Position = position;
            return new Index
            {
                Position1 = new Point { Position = reader.ReadInt64(), Length = reader.ReadInt32() },
                Position2 = new Point { Position = reader.ReadInt64(), Length = reader.ReadInt32() },
                IsFree = reader.ReadByte(),
                PrimaryKey = primaryKey
            };
        }
    }

    //Get all indexes
    private async Task<List<Index>> AllIndexs()
    {
        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(path, FileMode.Open, FileAccess.Read); }
            catch { await Task.Delay(150); }
        }

        using var reader = new BinaryReader(file);

        if (file.Length <= 1)
            return new List<Index>();
        else
        {
            file.Position = 1;
            var indexs = new List<Index>();
            while (file.Position < file.Length)
            {
                indexs.Add(new Index
                {
                    Position1 = new Point { Position = reader.ReadInt64(), Length = reader.ReadInt32() },
                    Position2 = new Point { Position = reader.ReadInt64(), Length = reader.ReadInt32() },
                    IsFree = reader.ReadByte(),
                    PrimaryKey = (file.Position / 25) - 1
                });
            }

            return indexs;
        }
    }

    //Get range indexes
    private async Task<List<Index>> RangeIndexs(Int64 skip, Int64 count)
    {
        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true); }
            catch { await Task.Delay(150); }
        }

        using var reader = new BinaryReader(file);

        Int64 position = (skip * 25) + 1;
        Int64 dectinationPosition = ((count + skip) * 25) + 1;

        if (file.Length <= 1)
            return new List<Index>();
        else
        {
            file.Position = position;
            var indexs = new List<Index>();
            while (file.Position <= dectinationPosition && file.Position < file.Length)
            {
                indexs.Add(new Index
                {
                    Position1 = new Point { Position = reader.ReadInt64(), Length = reader.ReadInt32() },
                    Position2 = new Point { Position = reader.ReadInt64(), Length = reader.ReadInt32() },
                    IsFree = reader.ReadByte(),
                    PrimaryKey = (file.Position / 25) - 1
                });
            }

            return indexs;
        }
    }









    /*private TModel Deserilize(byte[] data)
    {
        string str = UTF8Encoding.UTF8.GetString(Xor(data));

        var instance = Activator.CreateInstance(ModelType);

        foreach (var parameter in str.Split("\t"))
        {
            foreach (var prop in ModelType.GetProperties())
            {
                if (prop.CanWrite)
                {
                    string value = parameter.Replace($"<db.{prop.Name}>", "").Replace("<db.break/>", "\t");

                    if (parameter.StartsWith($"<db.{prop.Name}>") && !prop.PropertyType.IsEnum)
                    {
                        if (prop.PropertyType == typeof(string))
                            prop.SetValue(instance, value);
                        else if (ModelType.GetProperty(prop.Name).PropertyType == typeof(Byte[]) || ModelType.GetProperty(prop.Name).PropertyType == typeof(Byte))
                        {
                            var final = Convert.ChangeType(Convert.FromBase64String(value), prop.PropertyType);
                            prop.SetValue(instance, final);
                        }
                        else
                        {
                            var final = Convert.ChangeType(value, prop.PropertyType);
                            prop.SetValue(instance, final);

                        }
                        break;
                    }
                    else if (parameter.StartsWith($"<db.{prop.Name}>") && prop.PropertyType.IsEnum)
                    {
                        object convertedValue = value;
                        if (convertedValue is string strVal)
                            convertedValue = Enum.Parse(prop.PropertyType, strVal);
                        else
                            convertedValue = Enum.ToObject(prop.PropertyType, convertedValue);

                        prop.SetValue(instance, convertedValue);

                        break;
                    }
                }
            }
        }

        return (TModel)Convert.ChangeType(instance, typeof(TModel));

    }

    private byte[] Serilize(TModel model)
    {
        var properties = ModelType.GetProperties();

        string str = "";

        foreach (var prop in properties)
        {
            if (prop.CanRead)
            {
                if (ModelType.GetProperty(prop.Name).PropertyType == typeof(Byte[]) || ModelType.GetProperty(prop.Name).PropertyType == typeof(Byte))
                {
                    var bt = Convert.ChangeType(ModelType.GetProperty(prop.Name)?.GetValue(model), typeof(Byte[]));

                    if (bt != null)
                        str += $"<db.{prop.Name}>{Convert.ToBase64String((byte[])bt)}\t";
                }
                else
                {
                    var value = ModelType.GetProperty(prop.Name)?.GetValue(model)?.ToString();
                    var type = prop.PropertyType;

                    if (type == typeof(string) && !string.IsNullOrEmpty(value))
                        value = value.Replace("\t", "<db.break/>");

                    if (!string.IsNullOrEmpty(value))
                        str += $"<db.{prop.Name}>{value}\t";
                }
            }
        }
        if (str.EndsWith("\t"))
            str = str.Remove(str.Length - 1, 1);

        return Xor(UTF8Encoding.UTF8.GetBytes(str));
    }
    */

    private TModel Deserialize(byte[] data)
    {
        var str = Encoding.UTF8.GetString(Xor(data));

        var instance = Activator.CreateInstance(ModelType);

        foreach (var param in str.Split('\t'))
        {
            int start = param.IndexOf('>');
            if (start < 0) continue;

            string name = param.Substring(4, start - 4); // remove <db. ... >
            if (!_propDict.TryGetValue(name, out var prop)) continue;

            string value = param.Substring(start + 1).Replace("<db.break/>", "\t");

            if (prop.PropertyType.IsEnum)
                prop.SetValue(instance, Enum.Parse(prop.PropertyType, value));
            else if (prop.PropertyType == typeof(byte[]))
                prop.SetValue(instance, Convert.FromBase64String(value));
            else
                prop.SetValue(instance, Convert.ChangeType(value, prop.PropertyType));
        }

        return (TModel)instance;
    }

    private byte[] Serialize(TModel model)
    {
        var sb = new StringBuilder();
        foreach (var prop in _propDict)
        {
            var val = prop.Value.GetValue(model);
            if (val == null) continue;

            string strVal;
            if (prop.Value.PropertyType == typeof(byte[]))
                strVal = Convert.ToBase64String((byte[])val);
            else
            {
                strVal = val.ToString();
                if (prop.Value.PropertyType == typeof(string))
                    strVal = strVal.Replace("\t", "<db.break/>");
            }

            sb.Append($"<db.{prop.Key}>{strVal}\t");
        }

        if (sb.Length > 0) sb.Length--; // remove last \t
        return Xor(Encoding.UTF8.GetBytes(sb.ToString()));
    }



    private byte[] Xor(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] ^= key[i % key.Length];

        return data;
    }

}