

using System.Linq.Expressions;
using System.Text;

namespace SLVZ.Db;

internal abstract partial class HeavyDb<TModel>
{
    private int DATACOUNT = 30000;

    private string KeyName { get; set; }
    private FileInfo Info { get; set; }
    private string BaseFileName { get => Info.Name.Replace(Info.Extension, ""); }

    private Type ModelType { get; set; }

    private List<DataStructure<TModel>> Data = new List<DataStructure<TModel>>();


    public abstract void Configuration();
    public HeavyDb<TModel> SetConfig(string filepath, string keyname)
    {
        ModelType = typeof(TModel);
        Info = new FileInfo(filepath);
        this.KeyName = keyname;

        if (!ModelType.GetProperty(keyname).ToString().Contains("Int64"))
            throw new InvalidDataException($"The type of primary key must be Int64. It is necessary for indexing and speed");

        fs_Index = new FileStream($"{Info.FullName}-inx", FileMode.OpenOrCreate, FileAccess.ReadWrite);
        fs_Last = new FileStream($"{Info.FullName}-lst", FileMode.OpenOrCreate, FileAccess.ReadWrite);

        File.SetAttributes(fs_Index.Name, FileAttributes.Hidden);
        File.SetAttributes(fs_Last.Name, FileAttributes.Hidden);

        //Get all files
        string[] files = Directory.GetFiles(Info.FullName.Replace(Info.Name, ""));
        foreach (var item in files)
        {
            FileInfo info = new FileInfo(item);
            if (info.Name.StartsWith(BaseFileName) && info.Name.EndsWith(Info.Extension)
                && !info.Name.EndsWith($"_Index{Info.Extension}") && !info.Name.EndsWith($"_LastIndex{Info.Extension}"))
            {
                string str = info.Name.Replace(BaseFileName + "[", "").Replace("]" + Info.Extension, "");
                Int64 start = Int64.Parse(str.Split('-')[0]);
                Int64 end = Int64.Parse(str.Split('-')[1]);

                Data.Add(new DataStructure<TModel>(start, end, info.FullName, KeyName));
            }
        }

        if (ModelType.GetProperties().Where(x => x.PropertyType.Name.Contains("string")).Count() == 0)
            DATACOUNT = 25000;
        else if (ModelType.GetProperties().Where(x => x.PropertyType.Name.Contains("string")).Count() < 3)
            DATACOUNT = 20000;
        else if (ModelType.GetProperties().Where(x => x.PropertyType.Name.Contains("string")).Count() == 3)
            DATACOUNT = 15000;
        else if (ModelType.GetProperties().Where(x => x.PropertyType.Name.Contains("string")).Count() > 3)
            DATACOUNT = 10000;

        if (Data.Count() < 1)
            CreateNewDataFile(); //Create new datafile



        return this;
    }

    protected HeavyDb()
    {
        Configuration();

        if (ModelType == null)
            throw new InvalidOperationException("Model type must be set in Configuration().");

        if (string.IsNullOrEmpty(KeyName) || string.IsNullOrEmpty(Info.FullName))
            throw new InvalidOperationException("Both KeyName and FilePath must be set in Configuration().");
    }



    private bool CreateNewDataFile()
    {
        if (Data.Count() > 0)
        {
            var data = Data[^1];

            Data.Add(new DataStructure<TModel>(data.EndIndex + 1, data.EndIndex + DATACOUNT,
                Info.FullName.Replace(Info.Name, "") + $"{BaseFileName}[{data.EndIndex + 1}-{data.EndIndex + DATACOUNT}]{Info.Extension}",
                KeyName));
        }
        else
            Data.Add(new DataStructure<TModel>(0, DATACOUNT,
                Info.FullName.Replace(Info.Name, "") + $"{BaseFileName}[0-{DATACOUNT}]{Info.Extension}",
                KeyName));

        return true;
    }

    public async void Add(TModel model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));
        if (model.GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");


        var key = (Int64)ModelType.GetProperty(KeyName).GetValue(model);

        while (!Data.Any(x => x.EndIndex >= key && x.EndIndex <= key))
            CreateNewDataFile();

        var data = Data.Find(x => x.EndIndex >= key && x.EndIndex <= key);
        await data.Add(model);

        SetLastIndex(key);
        RemoveAvailableIndex(key);
    }

    public async void Add(List<TModel> models)
    {
        if (models is null)
            throw new ArgumentNullException(nameof(models));
        if (models[^1]?.GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        models = models.OrderBy(x => (Int64)ModelType.GetProperty(KeyName).GetValue(x)).ToList();

        List<Int64> keys = new List<Int64>();
        foreach (var model in models)
            keys.Add((Int64)ModelType.GetProperty(KeyName).GetValue(model));

        while (models.Count() > 0)
        {
            if (Data.Any(x => x.EndIndex >= (Int64)ModelType.GetProperty(KeyName).GetValue(models[0])))
            {
                var data = Data.Find(x => x.EndIndex >= (Int64)ModelType.GetProperty(KeyName).GetValue(models[0]));


                await data.Add(models.FindAll(x => (Int64)ModelType.GetProperty(KeyName).GetValue(x) <= data.EndIndex));
                models.RemoveAll(x => (Int64)ModelType.GetProperty(KeyName).GetValue(x) <= data.EndIndex);
            }
            else CreateNewDataFile();
        }

        SetLastIndex(keys[^1]);
        RemoveAvailableIndex(keys);
    }

    public async Task<List<TModel>> All()
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        List<TModel> objects = new List<TModel>();

        foreach (var data in Data)
            objects.AddRange(await data.All());

        return objects;
    }

    public async Task<TModel> Find<T>(T id)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        var instance = Activator.CreateInstance(ModelType);

        Int64 key = Int64.Parse(id.ToString());


        if (!Data.Any(x => x.EndIndex >= key && x.EndIndex <= key))
            return (TModel)Convert.ChangeType(instance, ModelType);

        var data = await Data.Find(x => x.EndIndex <= key && x.EndIndex >= key).Find(key);
        return data;
    }


    public async void Remove<T>(T id)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        Int64 key = Int64.Parse(id.ToString());

        if (Data.Any(x => x.EndIndex >= key && x.EndIndex <= key))
        {
            await Data.Find(x => x.EndIndex >= key && x.EndIndex <= key).Remove(id);
            AddAvailableIndex(key);
        }
    }

    public async void Remove(TModel model)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");


        Int64 key = (Int64)ModelType.GetProperty(KeyName).GetValue(model);

        if (Data.Any(x => x.EndIndex >= key && x.EndIndex <= key))
        {
            await Data.Find(x => x.EndIndex >= key && x.EndIndex <= key).Remove(model);

            AddAvailableIndex(key);
        }
    }

    public async void RemoveRange(List<TModel> models)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        models = models.OrderBy(x => (Int64)ModelType.GetProperty(KeyName).GetValue(x)).ToList();

        List<Int64> keys = new List<Int64>();
        foreach (var model in models)
            keys.Add((Int64)ModelType.GetProperty(KeyName).GetValue(model));

        while (models.Count() > 0)
        {
            if (Data.Any(x => x.EndIndex >= (Int64)ModelType.GetProperty(KeyName).GetValue(models[0])))
            {
                var data = Data.Find(x => x.EndIndex >= (Int64)ModelType.GetProperty(KeyName).GetValue(models[0]));


                await data.RemoveRange(models.FindAll(x => (Int64)ModelType.GetProperty(KeyName).GetValue(x) <= data.EndIndex));
                models.RemoveAll(x => (Int64)ModelType.GetProperty(KeyName).GetValue(x) <= data.EndIndex);
            }
        }
        AddAvailableIndex(keys);
    }

    public async void Update(TModel model)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (model.GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        Int64 key = (Int64)ModelType.GetProperty(KeyName).GetValue(model);

        if (Data.Any(x => x.EndIndex >= key && x.EndIndex <= key))
            await Data.Find(x => x.EndIndex >= key && x.EndIndex <= key).Update(model);
    }

    public async void UpdateRange(List<TModel> models)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (models[0].GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");


        models = models.OrderBy(x => (Int64)ModelType.GetProperty(KeyName).GetValue(x)).ToList();

        while (models.Count() > 0)
        {
            if (Data.Any(x => x.EndIndex >= (Int64)ModelType.GetProperty(KeyName).GetValue(models[0])))
            {
                var data = Data.Find(x => x.EndIndex >= (Int64)ModelType.GetProperty(KeyName).GetValue(models[0]));


                await data.UpdateRange(models.FindAll(x => (Int64)ModelType.GetProperty(KeyName).GetValue(x) <= data.EndIndex));
                models.RemoveAll(x => (Int64)ModelType.GetProperty(KeyName).GetValue(x) <= data.EndIndex);
            }
        }

    }
}


internal abstract partial class HeavyDb<TModel>
{
    public async Task<List<TModel>> Where(Expression<Func<TModel, bool>> predicate)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        List<TModel> objects = new List<TModel>();

        foreach (var data in Data)
            objects.AddRange(data.Where(predicate).Result);

        return objects;
    }


    public async Task<bool> Any(Expression<Func<TModel, bool>> predicate)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        foreach (var data in Data)
        {
            bool result = await data.Any(predicate);
            if (result)
                return true;
        }

        return false;
    }
}


//ID managment
internal abstract partial class HeavyDb<TModel>
{

    private FileStream fs_Index;
    private FileStream fs_Last;

    public Int64 GeneratePrimaryKey()
    {
        try
        {

            if (!fs_Index.CanSeek)
                fs_Index = new FileStream(fs_Index.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            using var reader = new BinaryReader(fs_Index);

            if (fs_Index.Length > 0)
            {
                fs_Index.Position = fs_Index.Length - 8;
                return reader.ReadInt64();
            }
            else
                return GetLastIndex();

        }
        catch (Exception e)
        {
            return GetLastIndex();
        }
    }

    private void AddAvailableIndex(List<Int64> keys)
    {
        if (!fs_Index.CanSeek)
            fs_Index = new FileStream(fs_Index.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        fs_Index.Position = fs_Index.Length;
        using var writer = new BinaryWriter(fs_Index);
        foreach (Int64 key in keys)
            writer.Write(key);
    }

    private void AddAvailableIndex(Int64 key)
    {
        if (!fs_Index.CanSeek)
            fs_Index = new FileStream(fs_Index.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        fs_Index.Position = fs_Index.Length;
        using var writer = new BinaryWriter(fs_Index);
        writer.Write(key);
    }

    private void RemoveAvailableIndex(List<Int64> keys)
    {
        if (!fs_Index.CanSeek)
            fs_Index = new FileStream(fs_Index.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        fs_Index.Position = 0;

        using var reader = new BinaryReader(fs_Index);
        List<Int64> myKeys = new List<Int64>();
        while (fs_Index.Position < fs_Index.Length)
            myKeys.Add(reader.ReadInt64());

        keys = keys.OrderBy(x => x).ToList();
        foreach (Int64 key in keys)
            if (myKeys.Any(x => x == key))
                myKeys.RemoveAll(x => x == key);

        fs_Index.SetLength(0);

        using var writer = new BinaryWriter(fs_Index);
        foreach (Int64 key in myKeys)
            writer.Write(key);
    }

    private void RemoveAvailableIndex(Int64 key)
    {
        if (!fs_Index.CanSeek)
            fs_Index = new FileStream(fs_Index.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        fs_Index.Position = 0;

        using var reader = new BinaryReader(fs_Index);
        List<Int64> myKeys = new List<Int64>();
        bool startReading = false;
        Int64 length = 0;

        while (fs_Index.Position < fs_Index.Length)
        {
            var value = reader.ReadInt64();
            if (value == key && !startReading)
            {
                startReading = true;
                length = fs_Index.Position - 8;
            }
            else if (startReading)
                myKeys.Add(value);
        }

        fs_Index.SetLength(length);

        if (myKeys.Count() < 1)
            return;

        myKeys.RemoveAll(x => x == key);

        using var writer = new BinaryWriter(fs_Index);
        foreach (Int64 index in myKeys)
            writer.Write(index);
    }




    private void SetLastIndex(Int64 index)
    {
        if (!fs_Last.CanSeek)
            fs_Last = new FileStream(fs_Last.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        using var reader = new BinaryReader(fs_Last);
        using var writer = new BinaryWriter(fs_Last);

        if (fs_Last.Length > 0)
        {
            fs_Last.Position = 0;
            var value = reader.ReadInt64();
            fs_Last.Position = 0;

            if (value < index)
                writer.Write(index);
        }
        else
            writer.Write(index);
    }

    private Int64 GetLastIndex()
    {
        if (!fs_Last.CanSeek)
            fs_Last = new FileStream(fs_Last.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        using var reader = new BinaryReader(fs_Last);

        if (fs_Last.Length > 0)
            return reader.ReadInt64() + 1;
        else
            return 0;
    }
}








//Data structure
internal class DataStructure<TModel>
{
    private bool IsBusy { get; set; } = false;
    private Type ModelType { get; set; }
    private FileStream file;
    private string KeyName { get; set; }


    public Int64 StartIndex { get; set; } = 0;
    public Int64 EndIndex { get; set; } = 0;


    public DataStructure(Int64 start, Int64 end, string path, string keyname)
    {
        file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        EndIndex = start; EndIndex = end;
        ModelType = typeof(TModel);
        this.KeyName = keyname;
    }


    public async Task Add(TModel model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));
        if (model.GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");


        try
        {
            while (IsBusy)
                await Task.Delay(150);

            IsBusy = true;

            if (!file.CanSeek)
                file = new FileStream(file.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            file.Position = file.Length;

            using BinaryWriter writer = new BinaryWriter(file);

            string str = "";
            var properties = ModelType.GetProperties();

            string key = ModelType.GetProperty(KeyName).GetValue(model).ToString();
            if (key.Contains("\n"))
                throw new InvalidDataException("You cannot use Enter in key");

            str += $"<db.{KeyName}>{key}</db.{KeyName}><db.br/>";

            foreach (var prop in properties)
            {
                if (prop.Name != KeyName && prop.CanRead)
                {
                    var value = ModelType.GetProperty(prop.Name)?.GetValue(model)?.ToString();
                    var type = prop.PropertyType;

                    if (type == typeof(string) && !string.IsNullOrEmpty(value))
                        value = value.Replace("\n", "<db.break/>");

                    str += $"<db.{prop.Name}>{value}</db.{prop.Name}><db.br/>";
                }
            }
            if (str.EndsWith("<db.br/>"))
                str = str.Remove(str.Length - 9, 8);

            writer.Write(str);

            writer.Close();



            IsBusy = false;
        }
        catch (Exception e)
        {
            IsBusy = false;
            throw new Exception(e.Message);
        }
    }


    public async Task Add(List<TModel> models)
    {
        if (models is null)
            throw new ArgumentNullException(nameof(models));
        if (models[^1]?.GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");


        try
        {
            while (IsBusy)
                await Task.Delay(150);

            IsBusy = true;

            if (!file.CanSeek)
                file = new FileStream(file.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            file.Position = file.Length;

            using BinaryWriter writer = new BinaryWriter(file);

            foreach (var model in models)
            {
                string str = "";
                var properties = ModelType.GetProperties();

                string key = ModelType.GetProperty(KeyName).GetValue(model).ToString();

                str += $"<db.{KeyName}>{key}</db.{KeyName}><db.br/>";

                foreach (var prop in properties)
                {
                    if (prop.Name != KeyName && prop.CanRead)
                    {
                        var value = ModelType.GetProperty(prop.Name)?.GetValue(model)?.ToString();
                        var type = prop.PropertyType;

                        if (type == typeof(string) && !string.IsNullOrEmpty(value))
                            value = value.Replace("\n", "<db.break/>");

                        str += $"<db.{prop.Name}>{value}</db.{prop.Name}><db.br/>";
                    }
                }
                if (str.EndsWith("<db.br/>"))
                    str = str.Remove(str.Length - 9, 8);

                writer.Write(str);
            }
            writer.Close();

            IsBusy = false;
        }
        catch (Exception e)
        {
            IsBusy = false;
            throw new Exception(e.Message);
        }
    }


    public async Task<List<TModel>> All()
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        List<TModel> objects = new List<TModel>();

        if (!file.CanSeek)
            file = new FileStream(file.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);


        if (file.Length == 0)
            return objects;

        while (IsBusy)
            await Task.Delay(150);

        IsBusy = true;

        file.Position = 0;

        using var reader = new BinaryReader(file);
        string line;
        while (file.Position < file.Length)
        {
            line = reader.ReadString();
            var instance = Activator.CreateInstance(ModelType);

            foreach (var parameter in line.Split("<db.br/>"))
            {
                foreach (var prop in ModelType.GetProperties())
                {
                    if (prop.CanWrite)
                    {
                        string value = parameter.Replace($"<db.{prop.Name}>", "").Replace($"</db.{prop.Name}>", "").Replace("<db.break/>", "\n");

                        if (parameter.StartsWith($"<db.{prop.Name}>") && !prop.PropertyType.IsEnum)
                        {
                            if (prop.PropertyType == typeof(string))
                            {
                                prop.SetValue(instance, value);
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

            //Add model to the list
            objects.Add((TModel)Convert.ChangeType(instance, ModelType));

            if (file.Position > file.Length)
                file.Position = file.Length;
        }
        reader.Close();

        IsBusy = false;

        return objects;

    }


    public async Task<TModel> Find<T>(T id)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        var instance = Activator.CreateInstance(ModelType);

        if (!file.CanSeek)
            file = new FileStream(file.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        if (file.Length == 0)
            return (TModel)Convert.ChangeType(instance, ModelType);

        while (IsBusy)
            await Task.Delay(150);

        IsBusy = true; ;
        file.Position = 0;

        using var reader = new BinaryReader(file);
        string line;
        while (file.Position < file.Length)
        {
            line = reader.ReadString();

            if (line.StartsWith($"<db.{KeyName}>{id}</db.{KeyName}>"))
            {
                foreach (var parameter in line.Split("<db.br/>"))
                {
                    foreach (var prop in ModelType.GetProperties())
                    {
                        if (prop.CanWrite)
                        {
                            string value = parameter.Replace($"<db.{prop.Name}>", "").Replace($"</db.{prop.Name}>", "").Replace("<db.break/>", "\n");

                            if (parameter.StartsWith($"<db.{prop.Name}>") && !prop.PropertyType.IsEnum)
                            {
                                if (prop.PropertyType == typeof(string))
                                {

                                    prop.SetValue(instance, value);
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

                IsBusy = false;
                return (TModel)Convert.ChangeType(instance, typeof(TModel));
            }
        }
        reader.Close();

        IsBusy = false;

        return (TModel)Convert.ChangeType(instance, ModelType);
    }


    public async Task Remove<T>(T id)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (!file.CanSeek)
            file = new FileStream(file.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        if (file.Length == 0)
            return;


        while (IsBusy)
            await Task.Delay(150);
        IsBusy = true;

        file.Position = 0;

        using BinaryWriter writer = new BinaryWriter(file);
        using BinaryReader reader = new BinaryReader(file);

        bool startReading = false;
        Int64 length = 0;
        List<string> items = new List<string>();

        while (file.Position < file.Length)
        {
            var position = file.Position;

            string recorde = reader.ReadString();

            if (startReading)
                items.Add(recorde);

            if (recorde.StartsWith($"<db.{KeyName}>{id}</db.{KeyName}>"))
            {
                startReading = true;
                length = position;
            }

            if (file.Position > file.Length)
                file.Position = file.Length;

        }

        file.SetLength(length);

        foreach (var item in items)
            writer.Write(item);

        reader.Close();
        writer.Close();

        IsBusy = false;

    }


    public async Task Remove(TModel model)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (!file.CanSeek)
            file = new FileStream(file.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        if (file.Length == 0)
            return;


        while (IsBusy)
            await Task.Delay(150);
        IsBusy = true;

        file.Position = 0;

        using BinaryWriter writer = new BinaryWriter(file);
        using BinaryReader reader = new BinaryReader(file);

        bool startReading = false;
        Int64 length = 0;
        List<string> items = new List<string>();
        string key = ModelType.GetProperty(KeyName).GetValue(model).ToString();


        while (file.Position < file.Length)
        {
            var position = file.Position;
            string recorde = reader.ReadString();

            if (startReading)
                items.Add(recorde);

            if (recorde.StartsWith($"<db.{KeyName}>{key}</db.{KeyName}>"))
            {
                startReading = true;
                length = position;
            }
        }

        file.SetLength(length);

        foreach (var item in items)
            writer.Write(item);

        reader.Close();
        writer.Close();

        IsBusy = false;

    }


    public async Task RemoveRange(List<TModel> models)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (!file.CanSeek)
            file = new FileStream(file.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        if (file.Length == 0)
            return;

        while (IsBusy)
            await Task.Delay(150);
        IsBusy = true;

        file.Position = 0;

        using BinaryWriter writer = new BinaryWriter(file);
        using BinaryReader reader = new BinaryReader(file);

        List<string> keys = new List<string>();
        foreach (var model in models)
            keys.Add(ModelType.GetProperty(KeyName).GetValue(model).ToString());

        var prefixes = keys.Select(x => $"<db.{KeyName}>{x}</db.{KeyName}>");

        bool startReading = false;
        Int64 length = 0;
        List<string> items = new List<string>();

        while (file.Position < file.Length)
        {
            string recorde = reader.ReadString();

            if (startReading && !prefixes.Any(recorde.StartsWith))
                items.Add(recorde);
            else if (prefixes.Any(recorde.StartsWith) && !startReading)
            {
                startReading = true;
                length = file.Position - UTF8Encoding.ASCII.GetBytes(recorde).Length - 1;
            }

            if (file.Position > file.Length)
                file.Position = file.Length;
        }

        file.SetLength(length);

        foreach (var item in items)
            writer.Write(item);

        reader.Close();
        writer.Close();

        IsBusy = false;


    }


    public async Task Update(TModel model)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");


        if (model.GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        if (!file.CanSeek)
            file = new FileStream(file.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);


        if (file.Length == 0)
            return;

        while (IsBusy)
            await Task.Delay(150);
        IsBusy = true;

        string key = ModelType.GetProperty(KeyName).GetValue(model).ToString();

        file.Position = 0;

        using BinaryWriter writer = new BinaryWriter(file);
        using BinaryReader reader = new BinaryReader(file);

        bool startReading = false;
        Int64 length = 0;
        List<string> items = new List<string>();

        while (file.Position < file.Length)
        {
            var position = file.Position;
            string recorde = reader.ReadString();

            if (startReading)
                items.Add(recorde);
            else if (recorde.StartsWith($"<db.{KeyName}>{key}</db.{KeyName}>"))
            {
                startReading = true;
                length = position;

                string str = "";
                var properties = ModelType.GetProperties();

                str += $"<db.{KeyName}>{key}</db.{KeyName}><db.br/>";

                foreach (var prop in properties)
                {
                    if (prop.Name != KeyName && prop.CanRead)
                    {
                        var value = ModelType.GetProperty(prop.Name)?.GetValue(model)?.ToString();
                        var type = prop.PropertyType;

                        if (type == typeof(string) && !string.IsNullOrEmpty(value))
                            value = value.Replace("\n", "<db.break/>");

                        str += $"<db.{prop.Name}>{value}</db.{prop.Name}><db.br/>";
                    }
                }
                if (str.EndsWith("<db.br/>"))
                    str = str.Remove(str.Length - 9, 8);

                items.Add(str);
            }

        }

        file.SetLength(length);

        foreach (var item in items)
            writer.Write(item);

        reader.Close();
        writer.Close();

        IsBusy = false;

    }


    public async Task UpdateRange(List<TModel> models)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (models[0].GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        if (!file.CanSeek)
            file = new FileStream(file.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);


        if (file.Length == 0)
            return;

        while (IsBusy)
            await Task.Delay(150);

        IsBusy = true;

        List<string> keys = new List<string>();
        foreach (var model in models)
            keys.Add(ModelType.GetProperty(KeyName).GetValue(model).ToString());

        var prefixes = keys.Select(x => $"<db.{KeyName}>{x}</db.{KeyName}>");

        file.Position = 0;

        using BinaryWriter writer = new BinaryWriter(file);
        using BinaryReader reader = new BinaryReader(file);

        bool startReading = false;
        Int64 length = 0;
        List<string> items = new List<string>();

        while (file.Position < file.Length)
        {
            var position = file.Position;

            string recorde = reader.ReadString();

            if (!startReading && prefixes.Any(recorde.StartsWith))
            {
                startReading = true;
                length = position;

                foreach (var model in models)
                {
                    string key = ModelType.GetProperty(KeyName).GetValue(model).ToString();
                    if (recorde.StartsWith($"<db.{KeyName}>{key}</db.{KeyName}>"))
                    {
                        string str = "";
                        var properties = ModelType.GetProperties();

                        str += $"<db.{KeyName}>{key}</db.{KeyName}><db.br/>";

                        foreach (var prop in properties)
                        {
                            if (prop.Name != KeyName && prop.CanRead)
                            {
                                var value = ModelType.GetProperty(prop.Name)?.GetValue(model)?.ToString();
                                var type = prop.PropertyType;

                                if (type == typeof(string) && !string.IsNullOrEmpty(value))
                                    value = value.Replace("\n", "<db.break/>");

                                str += $"<db.{prop.Name}>{value}</db.{prop.Name}><db.br/>";
                            }
                        }
                        if (str.EndsWith("<db.br/>"))
                            str = str.Remove(str.Length - 9, 8);

                        items.Add(str);
                    }
                }
            }
            else if (startReading && prefixes.Any(recorde.StartsWith))
            {
                foreach (var model in models)
                {
                    string key = ModelType.GetProperty(KeyName).GetValue(model).ToString();
                    if (recorde.StartsWith($"<db.{KeyName}>{key}</db.{KeyName}>"))
                    {
                        string str = "";
                        var properties = ModelType.GetProperties();

                        str += $"<db.{KeyName}>{key}</db.{KeyName}><db.br/>";

                        foreach (var prop in properties)
                        {
                            if (prop.Name != KeyName && prop.CanRead)
                            {
                                var value = ModelType.GetProperty(prop.Name)?.GetValue(model)?.ToString();
                                var type = prop.PropertyType;

                                if (type == typeof(string) && !string.IsNullOrEmpty(value))
                                    value = value.Replace("\n", "<db.break/>");

                                str += $"<db.{prop.Name}>{value}</db.{prop.Name}><db.br/>";
                            }
                        }
                        if (str.EndsWith("<db.br/>"))
                            str = str.Remove(str.Length - 9, 8);

                        items.Add(str);
                    }
                }
            }
            else if (startReading)
                items.Add(recorde);

            if (file.Position > file.Length)
                file.Position = file.Length;
        }

        file.SetLength(length);

        foreach (var item in items)
            writer.Write(item);

        reader.Close();
        writer.Close();


        IsBusy = false;


    }


    public async Task<List<TModel>> Where(Expression<Func<TModel, bool>> predicate)
    {
        var filter = predicate.Compile();

        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        List<TModel> objects = new List<TModel>();

        if (!file.CanSeek)
            file = new FileStream(file.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        if (file.Length == 0)
            return objects;

        while (IsBusy)
            await Task.Delay(150);

        IsBusy = true;

        file.Position = 0;

        using var reader = new BinaryReader(file);
        string line;
        while (file.Position < file.Length)
        {
            line = reader.ReadString();
            var instance = Activator.CreateInstance(ModelType);

            foreach (var parameter in line.Split("<db.br/>"))
            {
                foreach (var prop in ModelType.GetProperties())
                {
                    if (prop.CanWrite)
                    {
                        string value = parameter.Replace($"<db.{prop.Name}>", "").Replace($"</db.{prop.Name}>", "").Replace("<db.break/>", "\n");

                        if (parameter.StartsWith($"<db.{prop.Name}>") && !prop.PropertyType.IsEnum)
                        {
                            if (prop.PropertyType == typeof(string))
                            {
                                prop.SetValue(instance, value);
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

            var tmpModel = (TModel)Convert.ChangeType(instance, ModelType);
            if (filter(tmpModel))
                objects.Add((TModel)Convert.ChangeType(instance, ModelType));

            if (file.Position > file.Length)
                file.Position = file.Length;
        }
        reader.Close();

        IsBusy = false;

        return objects;
    }


    public async Task<bool> Any(Expression<Func<TModel, bool>> predicate)
    {
        var filter = predicate.Compile();

        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        List<TModel> objects = new List<TModel>();

        if (!file.CanSeek)
            file = new FileStream(file.Name, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        if (file.Length == 0)
            return false;

        while (IsBusy)
            await Task.Delay(150);

        IsBusy = true;

        file.Position = 0;

        using var reader = new BinaryReader(file);
        string line;
        while (file.Position < file.Length)
        {
            line = reader.ReadString();
            var instance = Activator.CreateInstance(ModelType);

            foreach (var parameter in line.Split("<db.br/>"))
            {
                foreach (var prop in ModelType.GetProperties())
                {
                    if (prop.CanWrite)
                    {
                        string value = parameter.Replace($"<db.{prop.Name}>", "").Replace($"</db.{prop.Name}>", "").Replace("<db.break/>", "\n");

                        if (parameter.StartsWith($"<db.{prop.Name}>") && !prop.PropertyType.IsEnum)
                        {
                            if (prop.PropertyType == typeof(string))
                            {
                                prop.SetValue(instance, value);
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

            var tmpModel = (TModel)Convert.ChangeType(instance, ModelType);
            if (filter(tmpModel))
            {
                IsBusy = false;
                return true;
            }

            if (file.Position > file.Length)
                file.Position = file.Length;
        }
        reader.Close();

        IsBusy = false;

        return false;
    }

}