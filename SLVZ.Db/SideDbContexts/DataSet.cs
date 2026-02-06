using System.Linq.Expressions;
using System.Reflection;
namespace SLVZ.Db.Light;

public partial class DataSet<TModel>
{
    private string KeyName { get; set; }
    private string FilePath { get; set; }
    private Type ModelType { get; set; }


    public DataSet(string filepath)
    {
        ModelType = typeof(TModel);
        FilePath = filepath;

        if (ModelType == null)
            throw new InvalidOperationException("Model type must be set in Configuration().");


        var properties = ModelType.GetProperties();

        if (properties.Where(x => x.GetCustomAttribute<Key>() is not null).Count() == 0)
            throw new Exception("There is no key in your model. Use [SLVZ.Db.Light.Key] attribute in your model");

        var primaryKey = properties.Where(x => x.GetCustomAttribute<Key>() is not null).First();

        this.KeyName = primaryKey.Name;
     
        if (string.IsNullOrEmpty(KeyName) || string.IsNullOrEmpty(FilePath))
            throw new InvalidOperationException("Both Key and FilePath must be set in Configuration().");
    }


    /// <summary>
    /// Adds a new model to the database and returns its generated primary key.
    /// </summary>
    public async Task Add(TModel model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));
        if (model.GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");


        try
        {
            FileStream file = null;

            while (file is null)
            {
                try
                {
                    file = new FileStream(FilePath, File.Exists(FilePath) ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 60 * 1024, true);
                }
                catch { await Task.Delay(150); }
            }

            using StreamWriter writer = new StreamWriter(file);
            string str = "";
            var properties = ModelType.GetProperties();

            string key = ModelType.GetProperty(KeyName).GetValue(model).ToString();
            if (key.Contains("\n"))
                throw new InvalidDataException("You cannot use Enter in key");

            str += $"<db.{KeyName}>{key}</db.{KeyName}><db.br/>";

            foreach (var prop in properties)
            {
                if (prop.Name != KeyName && prop.CanRead && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte[])
                    && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte))
                {
                    var value = ModelType.GetProperty(prop.Name)?.GetValue(model)?.ToString();
                    var type = prop.PropertyType;

                    if (type == typeof(string) && !string.IsNullOrEmpty(value))
                        value = value.Replace("\n", "<db.break/>");

                    if(!string.IsNullOrEmpty(value))
                        str += $"<db.{prop.Name}>{value}</db.{prop.Name}><db.br/>";
                }
            }
            if (str.EndsWith("<db.br/>"))
                str = str.Remove(str.Length - 9, 8);

            writer.WriteLine(str);

        }
        catch (Exception e)
        {
            throw new Exception(e.Message);
        }
    }

    /// <summary>
    /// Adds a list of new models to the database
    /// </summary>
    public async Task Add(List<TModel> models)
    {
        if (models is null)
            throw new ArgumentNullException(nameof(models));
        if (models[^1]?.GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");


        try
        {
            FileStream file = null;

            while (file is null)
            {
                try
                {
                    file = new FileStream(FilePath, File.Exists(FilePath) ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 60 * 1024, true);
                }
                catch { await Task.Delay(150); }
            }

            using StreamWriter writer = new StreamWriter(file);
            foreach (var model in models)
            {
                string str = "";
                var properties = ModelType.GetProperties();

                string key = ModelType.GetProperty(KeyName).GetValue(model).ToString();
                if (key.Contains("\n"))
                    throw new InvalidDataException("You cannot use Enter in key");

                str += $"<db.{KeyName}>{key}</db.{KeyName}><db.br/>";

                foreach (var prop in properties)
                {
                    if (prop.Name != KeyName && prop.CanRead && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte[])
                    && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte))
                    {
                        var value = ModelType.GetProperty(prop.Name)?.GetValue(model)?.ToString();
                        var type = prop.PropertyType;

                        if (type == typeof(string) && !string.IsNullOrEmpty(value))
                            value = value.Replace("\n", "<db.break/>");

                        if (!string.IsNullOrEmpty(value))
                            str += $"<db.{prop.Name}>{value}</db.{prop.Name}><db.br/>";
                    }
                }
                if (str.EndsWith("<db.br/>"))
                    str = str.Remove(str.Length - 9, 8);

                writer.WriteLine(str);
            }

        }
        catch (Exception e)
        {
            throw new Exception(e.Message);
        }
    }



    /// <summary>
    /// Retrieves all models stored in the database.
    /// </summary>
    public async Task<List<TModel>> All()
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        List<TModel> objects = new List<TModel>();


        if (!File.Exists(FilePath))
            return objects;

        FileStream file = null;

        while (file is null)
        {
            try
            {
                file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 60 * 1024, true);
            }
            catch { await Task.Delay(150); }
        }

        using var reader = new StreamReader(file);
        string line;
        while (reader.Peek() != -1)
        {
            line = reader.ReadLine();
            var instance = Activator.CreateInstance(ModelType);

            foreach (var parameter in line.Split("<db.br/>"))
            {
                foreach (var prop in ModelType.GetProperties())
                {
                    if (prop.CanWrite && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte[])
                    && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte))
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
        }

        return objects;

    }



    /// <summary>
    /// Retrieves a model from the database by its key.
    /// </summary>
    public async Task<TModel> Find<T>(T id)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        var instance = Activator.CreateInstance(ModelType);

        if (!File.Exists(FilePath))
            return (TModel)Convert.ChangeType(instance, ModelType);

        FileStream file = null;

        while (file is null)
        {
            try
            {
                file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 60 * 1024, true);
            }
            catch { await Task.Delay(150); }
        }

        using var reader = new StreamReader(file);
        string line;
        while (reader.Peek() != -1)
        {
            line = reader.ReadLine();

            if (line.StartsWith($"<db.{KeyName}>{id}</db.{KeyName}>"))
            {
                foreach (var parameter in line.Split("<db.br/>"))
                {
                    foreach (var prop in ModelType.GetProperties())
                    {
                        if (prop.CanWrite && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte[])
                    && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte))
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

                return (TModel)Convert.ChangeType(instance, typeof(TModel));
            }
        }

        return (TModel)Convert.ChangeType(instance, ModelType);
    }



    /// <summary>
    /// Deletes the model with the specified key from the database.
    /// </summary>
    public async Task Remove<T>(T id)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (File.Exists(FilePath))
        {
            FileStream MainFile = null;

            while (MainFile is null)
            {
                try
                {
                    MainFile = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None, 60 * 1024, true);
                }
                catch { await Task.Delay(150); }
            }

            using FileStream TmpFile = new FileStream(FilePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.Read, 60 * 1024, true);

            using StreamWriter writer = new StreamWriter(TmpFile);
            using StreamReader reader = new StreamReader(MainFile);

            while (reader.Peek() != -1)
            {
                string line = reader.ReadLine();
                if (!line.StartsWith($"<db.{KeyName}>{id}</db.{KeyName}>"))
                    writer.WriteLine(line);
            }

            reader.Close();
            writer.Close();

            if (MainFile.CanSeek)
                MainFile.Close();

            if (TmpFile.CanSeek)
                TmpFile.Close();

            File.Copy(FilePath + ".tmp", FilePath, true);
            File.Delete(FilePath + ".tmp");
        }

    }

    /// <summary>
    /// Deletes the specified model from the database.
    /// </summary>
    public async Task Remove(TModel model)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (File.Exists(FilePath))
        {
            FileStream MainFile = null;

            while (MainFile is null)
            {
                try
                {
                    MainFile = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None, 60 * 1024, true);
                }
                catch { await Task.Delay(150); }
            }

            using FileStream TmpFile = new FileStream(FilePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 60 * 1024, true);

            using StreamWriter writer = new StreamWriter(TmpFile);
            using StreamReader reader = new StreamReader(MainFile);

            string key = ModelType.GetProperty(KeyName).GetValue(model).ToString();

            while (reader.Peek() != -1)
            {
                string line = reader.ReadLine();
                if (!line.StartsWith($"<db.{KeyName}>{key}</db.{KeyName}>"))
                    writer.WriteLine(line);
            }

            reader.Close();
            writer.Close();

            if (MainFile.CanSeek)
                MainFile.Close();

            if (TmpFile.CanSeek)
                TmpFile.Close();

            File.Copy(FilePath + ".tmp", FilePath, true);
            File.Delete(FilePath + ".tmp");
        }

    }

    /// <summary>
    /// Deletes the specified models from the database.
    /// </summary>
    public async Task RemoveRange(List<TModel> models)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (File.Exists(FilePath))
        {
            FileStream MainFile = null;

            while (MainFile is null)
            {
                try
                {
                    MainFile = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None, 60 * 1024, true);
                }
                catch { await Task.Delay(150); }
            }

            using FileStream TmpFile = new FileStream(FilePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 60 * 1024, true);

            using StreamWriter writer = new StreamWriter(TmpFile);
            using StreamReader reader = new StreamReader(MainFile);

            List<string> keys = new List<string>();
            foreach (var model in models)
                keys.Add(ModelType.GetProperty(KeyName).GetValue(model).ToString());

            var prefixes = keys.Select(x => $"<db.{KeyName}>{x}</db.{KeyName}>");

            while (reader.Peek() != -1)
            {
                string line = reader.ReadLine();

                if (!prefixes.Any(line.StartsWith))
                    writer.WriteLine(line);
            }

            reader.Close();
            writer.Close();

            if (MainFile.CanSeek)
                MainFile.Close();

            if (TmpFile.CanSeek)
                TmpFile.Close();

            File.Copy(FilePath + ".tmp", FilePath, true);
            File.Delete(FilePath + ".tmp");
        }

    }



    /// <summary>
    /// Updates the specified model in the database.
    /// </summary>
    public async Task Update(TModel model)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (model.GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");


        if (File.Exists(FilePath))
        {
            FileStream MainFile = null;

            while (MainFile is null)
            {
                try
                {
                    MainFile = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None, 60 * 1024, true);
                }
                catch { await Task.Delay(150); }
            }

            using FileStream TmpFile = new FileStream(FilePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 60 * 1024, true);

            string key = ModelType.GetProperty(KeyName).GetValue(model).ToString();


            using StreamWriter writer = new StreamWriter(TmpFile);
            using StreamReader reader = new StreamReader(MainFile);

            while (reader.Peek() != -1)
            {
                string line = reader.ReadLine();
                if (!line.StartsWith($"<db.{KeyName}>{key}</db.{KeyName}>"))
                    writer.WriteLine(line);
                else
                {
                    string str = "";
                    var properties = ModelType.GetProperties();

                    str += $"<db.{KeyName}>{key}</db.{KeyName}><db.br/>";

                    foreach (var prop in properties)
                    {
                        if (prop.Name != KeyName && prop.CanRead && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte[])
                    && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte))
                        {
                            var value = ModelType.GetProperty(prop.Name)?.GetValue(model)?.ToString();
                            var type = prop.PropertyType;

                            if (type == typeof(string) && !string.IsNullOrEmpty(value))
                                value = value.Replace("\n", "<db.break/>");

                            if (!string.IsNullOrEmpty(value))
                                str += $"<db.{prop.Name}>{value}</db.{prop.Name}><db.br/>";
                        }
                    }
                    if (str.EndsWith("<db.br/>"))
                        str = str.Remove(str.Length - 9, 8);

                    writer.WriteLine(str);
                }
            }

            reader.Close();
            writer.Close();

            if (MainFile.CanSeek)
                MainFile.Close();

            if (TmpFile.CanSeek)
                TmpFile.Close();

            File.Copy(FilePath + ".tmp", FilePath, true);
            File.Delete(FilePath + ".tmp");
        }

    }

    /// <summary>
    /// Updates the specified models in the database.
    /// </summary>
    public async Task UpdateRange(List<TModel> models)
    {
        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (models[0].GetType() != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");


        if (File.Exists(FilePath))
        {
            FileStream MainFile = null;

            while (MainFile is null)
            {
                try
                {
                    MainFile = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None, 60 * 1024, true);
                }
                catch { await Task.Delay(150); }
            }

            using FileStream TmpFile = new FileStream(FilePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 60 * 1024, true);

            List<string> keys = new List<string>();
            foreach (var model in models)
                keys.Add(ModelType.GetProperty(KeyName).GetValue(model).ToString());

            var prefixes = keys.Select(x => $"<db.{KeyName}>{x}</db.{KeyName}>");


            using StreamWriter writer = new StreamWriter(TmpFile);
            using StreamReader reader = new StreamReader(MainFile);

            while (reader.Peek() != -1)
            {
                string line = reader.ReadLine();
                if (!prefixes.Any(line.StartsWith))
                    writer.WriteLine(line);
                else
                {
                    foreach (var model in models)
                    {
                        string key = ModelType.GetProperty(KeyName).GetValue(model).ToString();
                        if (line.StartsWith($"<db.{KeyName}>{key}</db.{KeyName}>"))
                        {
                            string str = "";
                            var properties = ModelType.GetProperties();

                            str += $"<db.{KeyName}>{key}</db.{KeyName}><db.br/>";

                            foreach (var prop in properties)
                            {
                                if (prop.Name != KeyName && prop.CanRead && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte[])
                                    && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte))
                                {
                                    var value = ModelType.GetProperty(prop.Name)?.GetValue(model)?.ToString();
                                    var type = prop.PropertyType;

                                    if (type == typeof(string) && !string.IsNullOrEmpty(value))
                                        value = value.Replace("\n", "<db.break/>");

                                    if (!string.IsNullOrEmpty(value))
                                        str += $"<db.{prop.Name}>{value}</db.{prop.Name}><db.br/>";
                                }
                            }
                            if (str.EndsWith("<db.br/>"))
                                str = str.Remove(str.Length - 9, 8);

                            writer.WriteLine(str);
                        }
                    }
                }
            }

            reader.Close();
            writer.Close();

            if (MainFile.CanSeek)
                MainFile.Close();

            if (TmpFile.CanSeek)
                TmpFile.Close();

            File.Copy(FilePath + ".tmp", FilePath, true);
            File.Delete(FilePath + ".tmp");
        }

    }
}


public partial class DataSet<TModel>
{
    /// <summary>
    /// Retrieves all models that satisfy the specified condition.
    /// </summary>
    public async Task<List<TModel>> Where(Expression<Func<TModel, bool>> predicate)
    {
        var filter = predicate.Compile();

        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        List<TModel> objects = new List<TModel>();


        if (!File.Exists(FilePath))
            return objects;

        FileStream file = null;

        while (file is null)
        {
            try
            {
                file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 60 * 1024, true);
            }
            catch { await Task.Delay(150); }
        }

        using var reader = new StreamReader(file);
        string line;
        while (reader.Peek() != -1)
        {
            line = reader.ReadLine();
            var instance = Activator.CreateInstance(ModelType);

            foreach (var parameter in line.Split("<db.br/>"))
            {
                foreach (var prop in ModelType.GetProperties())
                {
                    if (prop.CanWrite && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte[])
                    && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte))
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
        }

        return objects;
    }

    /// <summary>
    /// Determines whether any model exists that satisfies the specified condition.
    /// </summary>
    public async Task<bool> Any(Expression<Func<TModel, bool>> predicate)
    {
        var filter = predicate.Compile();

        if (ModelType == null)
            throw new InvalidOperationException("Model type is not set.");

        if (typeof(TModel) != ModelType)
            throw new InvalidOperationException($"This configuration only works with model type {ModelType.Name}.");

        List<TModel> objects = new List<TModel>();


        if (!File.Exists(FilePath))
            return false;

        FileStream file = null;

        while (file is null)
        {
            try
            {
                file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 60 * 1024, true);
            }
            catch { await Task.Delay(150); }
        }

        using var reader = new StreamReader(file);
        string line;
        while (reader.Peek() != -1)
        {
            line = reader.ReadLine();
            var instance = Activator.CreateInstance(ModelType);

            foreach (var parameter in line.Split("<db.br/>"))
            {
                foreach (var prop in ModelType.GetProperties())
                {
                    if (prop.CanWrite && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte[])
                    && ModelType.GetProperty(prop.Name).PropertyType != typeof(Byte))
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
                return true;
            }
        }

        return false;
    }
}