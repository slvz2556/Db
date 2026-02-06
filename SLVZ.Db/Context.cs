

namespace SLVZ.Db;


internal interface IDbContext
{
    Task<Index> InsertData(byte[] data);
    Task<byte[]> SelectData(Index index);
    Task<List<byte[]>> SelectRange(List<Index> indexs);
    Task RemoveData(Index index);
    Task RemoveRange(List<Index> indexs);
    public Task<Index> UpdateData(byte[] data, Index index);
    string _path { get; set; }
}

public abstract class DbContext
{
    internal string _path { get; set; }

    public abstract void Configuration();

    public DbContext Initialize(string path)
    {
        _path = path;

        if (!File.Exists(_path))
        {
            using var file = new FileStream(_path, FileMode.Create, FileAccess.Write);
        }
        if (!File.Exists(_path + "-spc"))
        {
            using var file = new FileStream(_path + "-spc", FileMode.Create, FileAccess.Write);
        }

        File.SetAttributes(_path + "-spc", FileAttributes.Hidden);

        return this;
    }

    protected DbContext()
    {
        Configuration();
    }

    public virtual DataSet<TModel> DataSet<TModel>() where TModel : class
    => new DataSet<TModel>(this);



    internal FileInfo dbFile { get => new FileInfo(_path); }


    internal async Task<Index> InsertData(byte[] data)
    {
        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 4 * 4096, true); }
            catch { await Task.Delay(150); }
        }

        var points = await GetFreeSpaces();

        using MemoryStream ms = new MemoryStream(data);

        using var writer = new BinaryWriter(file);

        if (points.Count() > 0)
        {

            if (points.Find(x => x.Length >= ms.Length) is not null)
            {
                var point = points.Find(x => x.Length >= ms.Length);

                while (points.Find(x => x.Length < point.Length && x.Length >= ms.Length) is not null)
                    point = points.Find(x => x.Length < point.Length && x.Length >= ms.Length);

                file.Position = point.Position;
                writer.Write(ms.ToArray(), 0, (Int32)ms.Length);

                var myIndex = new Index
                {
                    Position1 = new Point
                    {
                        Length = (Int32)ms.Length,
                        Position = file.Position - ms.Length
                    }
                };

                if (point.Length - ms.Length == 0)
                    points.Remove(point);
                else
                {
                    point.Length -= (Int32)ms.Length;
                    point.Position += (Int32)ms.Length;
                }

                writer.Close();

                await UpdateFreeSpaces(points);

                return myIndex;
            }

            else
            {
                var point = points.Where(x => x.Length <= ms.Length).First();

                while (points.Find(x => x.Length > point.Length && x.Length <= ms.Length) is not null)
                    point = points.Find(x => x.Length > point.Length && x.Length <= ms.Length);

                var myIndex = new Index
                {
                    Position1 = point
                };

                var part1 = new byte[point.Length];
                ms.Read(part1, 0, part1.Length);

                file.Position = point.Position;
                writer.Write(part1, 0, part1.Length);


                //Remove point1
                points.Remove(point);

                var point2 = points.Find(x => x.Length >= (ms.Length - point.Length));

                if (point2 == null)
                {
                    point2 = new Point
                    {
                        Position = (Int32)file.Length,
                        Length = (Int32)(ms.Length - point.Length)
                    };

                    var part2 = new byte[ms.Length - point.Length];

                    ms.Position = point.Length;
                    ms.Read(part2, 0, part2.Length);

                    file.Position = point2.Position;
                    writer.Write(part2, 0, part2.Length);

                    myIndex.Position2 = new Point
                    {
                        Length = part2.Length,
                        Position = point2.Position
                    };

                    writer.Close();

                    await UpdateFreeSpaces(points);

                    return myIndex;
                }
                else
                {
                    while (points.Find(x => x.Length < point2.Length && x.Length >= (ms.Length - point.Length)) is not null)
                        point = points.Find(x => x.Length < point2.Length && x.Length >= (ms.Length - point.Length));
                    var part2 = new byte[ms.Length - point.Length];

                    ms.Position = point.Length;
                    ms.Read(part2, 0, part2.Length);

                    file.Position = point2.Position;
                    writer.Write(part2, 0, part2.Length);

                    myIndex.Position2 = new Point
                    {
                        Length = part2.Length,
                        Position = point2.Position
                    };

                    if (point2.Length - part2.Length == 0)
                        points.Remove(point2);
                    else
                    {
                        point2.Length -= point2.Length;
                        point2.Position += point2.Length;
                    }

                    writer.Close();

                    await UpdateFreeSpaces(points);

                    return myIndex;
                }


            }

        }
        else
        {
            var point = new Point();
            file.Position = file.Length;
            point.Position = (Int32)file.Length;
            writer.Write(ms.ToArray());
            point.Length = (Int32)ms.Length;


            return new Index
            {
                Position1 = point
            };
        }

    }


    internal async Task<byte[]> SelectData(Index index)
    {
        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 60 * 1024, true); }
            catch { await Task.Delay(150); }
        }

        if (file.Length == 0)
        {
            file.Close();
            return new byte[0];
        }


        using var reader = new BinaryReader(file);

        using MemoryStream ms = new MemoryStream();

        try
        {
            file.Position = index.Position1.Position;
            var by = reader.ReadBytes(index.Position1.Length);
            ms.Write(by, 0, by.Length);

            if (index.Position2.Position > 0)
            {
                file.Position = index.Position2.Position;
                var by2 = reader.ReadBytes(index.Position2.Length);
                ms.Write(by2, 0, by2.Length);
            }


            return ms.ToArray();
        }
        catch (Exception e)
        {
            throw new Exception(e.Message);
        }

    }


    internal async Task<List<byte[]>> SelectRange(List<Index> indexs)
    {
        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 60 * 1024, true); }
            catch { await Task.Delay(150); }
        }

        if (file.Length == 0)
        {
            file.Close();
            return new List<byte[]>();
        }

        using var reader = new BinaryReader(file);

        List<byte[]> recordes = new List<byte[]>();



        try
        {
            foreach (var index in indexs)
            {
                using MemoryStream ms = new MemoryStream();

                file.Position = index.Position1.Position;
                var by = reader.ReadBytes(index.Position1.Length);
                ms.Write(by, 0, by.Length);

                if (index.Position2.Position > 0)
                {
                    file.Position = index.Position2.Position;
                    var by2 = reader.ReadBytes(index.Position2.Length);
                    ms.Write(by2, 0, by2.Length);
                }

                recordes.Add(ms.ToArray());
            }

            return recordes;
        }
        catch (Exception e)
        {
            throw new Exception(e.Message);
        }

    }


    internal async Task RemoveData(Index index)
    {
        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 60 * 1024, true); }
            catch { await Task.Delay(150); }
        }

        if (file.Length == 0)
        {
            file.Close();
            await ClearFreeSpaces();
            return;
        }


        var points = await GetFreeSpaces();


        points.Add(index.Position1);
        if (index.Position2.Position > 0)
            points.Add(index.Position2);

        points = points.OrderBy(x => x.Length).ToList();

        var positions = points.Select(x => x.Position);
        while (points.Find(x => positions.Contains(x.Length + x.Position)) is not null)
        {
            var result = points.FindAll(x => positions.Contains(x.Length + x.Position));

            foreach (var point in result)
            {
                var tmpPoint = points.Find(x => x.Position == (point.Position + point.Length));
                point.Length += tmpPoint.Length;
                points.Remove(tmpPoint);
            }

            positions = points.Select(x => x.Position);
        }

        var orderedByPosition = points.OrderBy(x => x.Position).ToList();
        if (orderedByPosition.Count() > 0)
        {
            if ((orderedByPosition[^1].Position + orderedByPosition[^1].Length) == file.Length)
            {
                file.SetLength(orderedByPosition[^1].Position);
                points.Remove(orderedByPosition[^1]);
            }
        }

        if (points.Sum(x => x.Length) == file.Length)
        {
            points.Clear();
            file.SetLength(0);
        }

        file.Close();

        await UpdateFreeSpaces(points);
    }


    internal async Task RemoveRange(List<Index> indexs)
    {
        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 60 * 1024, true); }
            catch { await Task.Delay(150); }
        }

        if (file.Length == 0)
        {
            file.Close();
            await ClearFreeSpaces();
            return;
        }


        var points = await GetFreeSpaces();

        foreach (var item in indexs)
        {
            points.Add(item.Position1);
            if (item.Position2.Position > 0)
                points.Add(item.Position2);
        }
        points = points.OrderBy(x => x.Length).ToList();

        var positions = points.Select(x => x.Position);
        while (points.Find(x => positions.Contains(x.Length + x.Position)) is not null)
        {
            var result = points.FindAll(x => positions.Contains(x.Length + x.Position));

            foreach (var point in result)
            {
                var tmpPoint = points.Find(x => x.Position == (point.Position + point.Length));
                point.Length += tmpPoint.Length;
                points.Remove(tmpPoint);
            }

            positions = points.Select(x => x.Position);
        }

        var orderedByPosition = points.OrderBy(x => x.Position).ToList();
        if (orderedByPosition.Count() > 0)
        {
            if ((orderedByPosition[^1].Position + orderedByPosition[^1].Length) == file.Length)
            {
                file.SetLength(orderedByPosition[^1].Position);
                points.Remove(orderedByPosition[^1]);
            }
        }

        if (points.Sum(x => x.Length) == file.Length)
        {
            points.Clear();
            file.SetLength(0);
        }

        file.Close();

        await UpdateFreeSpaces(points);
    }


    internal async Task<Index> UpdateData(byte[] data, Index index)
    {
        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 60 * 1024, true); }
            catch { await Task.Delay(150); }
        }

        var points = await GetFreeSpaces();

        points.Add(index.Position1);
        if (index.Position2.Position > 0)
            points.Add(index.Position2);

        points = points.OrderBy(x => x.Length).ToList();

        var positions = points.Select(x => x.Position);
        while (points.Find(x => positions.Contains(x.Length + x.Position)) is not null)
        {
            var result = points.FindAll(x => positions.Contains(x.Length + x.Position));

            foreach (var point in result)
            {
                var tmpPoint = points.Find(x => x.Position == (point.Position + point.Length));
                point.Length += tmpPoint.Length;
                points.Remove(tmpPoint);
            }

            positions = points.Select(x => x.Position);
        }


        using MemoryStream ms = new MemoryStream(data);
        using var writer = new BinaryWriter(file);


        if (points.Count() > 0)
        {

            if (points.Find(x => x.Length >= ms.Length) is not null)
            {
                var point = points.Find(x => x.Length >= ms.Length);

                while (points.Find(x => x.Length < point.Length && x.Length >= ms.Length) is not null)
                    point = points.Find(x => x.Length < point.Length && x.Length >= ms.Length);

                file.Position = point.Position;
                writer.Write(ms.ToArray(), 0, (Int32)ms.Length);

                var myIndex = new Index
                {
                    Position1 = new Point
                    {
                        Length = (Int32)ms.Length,
                        Position = file.Position - ms.Length
                    }
                };

                if (point.Length - ms.Length == 0)
                    points.Remove(point);
                else
                {
                    point.Length -= (Int32)ms.Length;
                    point.Position += (Int32)ms.Length;
                }

                var orderedByPosition = points.OrderBy(x => x.Position).ToList();
                if (orderedByPosition.Count() > 0)
                {
                    if ((orderedByPosition[^1].Position + orderedByPosition[^1].Length) == file.Length)
                    {
                        file.SetLength(orderedByPosition[^1].Position);
                        points.Remove(orderedByPosition[^1]);
                    }
                }

                writer.Close();
                await UpdateFreeSpaces(points);

                return myIndex;
            }

            else
            {
                var point = points.Where(x => x.Length <= ms.Length).First();

                while (points.Find(x => x.Length > point.Length && x.Length <= ms.Length) is not null)
                    point = points.Find(x => x.Length > point.Length && x.Length <= ms.Length);

                var myIndex = new Index
                {
                    Position1 = point
                };

                var part1 = new byte[point.Length];
                ms.Read(part1, 0, part1.Length);

                file.Position = point.Position;
                writer.Write(part1, 0, part1.Length);


                //Remove point1
                points.Remove(point);

                var point2 = points.Find(x => x.Length >= (ms.Length - point.Length));

                if (point2 == null)
                {
                    point2 = new Point
                    {
                        Position = (Int32)file.Length,
                        Length = (Int32)(ms.Length - point.Length)
                    };

                    var part2 = new byte[ms.Length - point.Length];

                    ms.Position = point.Length;
                    ms.Read(part2, 0, part2.Length);

                    file.Position = point2.Position;
                    writer.Write(part2, 0, part2.Length);

                    myIndex.Position2 = new Point
                    {
                        Length = part2.Length,
                        Position = point2.Position
                    };

                    writer.Close();


                    var orderedByPosition = points.OrderBy(x => x.Position).ToList();
                    if (orderedByPosition.Count() > 0)
                    {
                        if ((orderedByPosition[^1].Position + orderedByPosition[^1].Length) == file.Length)
                        {
                            file.SetLength(orderedByPosition[^1].Position);
                            points.Remove(orderedByPosition[^1]);
                        }
                    }

                    await UpdateFreeSpaces(points);

                    return myIndex;
                }
                else
                {
                    while (points.Find(x => x.Length < point2.Length && x.Length >= (ms.Length - point.Length)) is not null)
                        point = points.Find(x => x.Length < point2.Length && x.Length >= (ms.Length - point.Length));
                    var part2 = new byte[ms.Length - point.Length];

                    ms.Position = point.Length;
                    ms.Read(part2, 0, part2.Length);

                    file.Position = point2.Position;
                    writer.Write(part2, 0, part2.Length);

                    myIndex.Position2 = new Point
                    {
                        Length = part2.Length,
                        Position = point2.Position
                    };

                    if (point2.Length - part2.Length == 0)
                        points.Remove(point2);
                    else
                    {
                        point2.Length -= point2.Length;
                        point2.Position += point2.Length;
                    }

                    writer.Close();


                    var orderedByPosition = points.OrderBy(x => x.Position).ToList();
                    if (orderedByPosition.Count() > 0)
                    {
                        if ((orderedByPosition[^1].Position + orderedByPosition[^1].Length) == file.Length)
                        {
                            file.SetLength(orderedByPosition[^1].Position);
                            points.Remove(orderedByPosition[^1]);
                        }
                    }

                    await UpdateFreeSpaces(points);

                    return myIndex;
                }


            }

        }
        else
        {
            var point = new Point();
            file.Position = file.Length;
            point.Position = (Int32)file.Length;
            writer.Write(ms.ToArray());
            point.Length = (Int32)ms.Length;



            return new Index
            {
                Position1 = point
            };
        }
    }




    private async Task<List<Point>> GetFreeSpaces()
    {
        if (File.Exists(_path + "-spc"))
        {
            FileStream file = null;

            while (file == null)
            {
                try { file = new FileStream(_path + "-spc", FileMode.Open, FileAccess.Read, FileShare.Read, 60 * 1024, true); }
                catch { await Task.Delay(150); }
            }


            if (file.Length == 0)
            {
                file.Close();
                return new List<Point>();
            }

            file.Position = 0;
            var list = new List<Point>();
            using var reader = new BinaryReader(file);

            while (file.Position < file.Length)
            {
                list.Add(new Point
                {
                    Position = reader.ReadInt64(),
                    Length = reader.ReadInt32()
                });
            }


            return list.OrderBy(x => x.Length).ToList();
        }
        else
            return new List<Point>();
    }

    private async Task UpdateFreeSpaces(List<Point> points)
    {
        FileStream file = null;

        while (file == null)
        {
            try { file = new FileStream(_path + "-spc", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 60 * 1024, true); }
            catch { await Task.Delay(150); }
        }


        file.SetLength(0);

        if (points.Count() > 0)
        {
            using var writer = new BinaryWriter(file);

            foreach (var point in points.OrderBy(x => x.Length).ToList())
            {
                writer.Write(point.Position);
                writer.Write(point.Length);
            }
        }
        else file.Close();

    }

    private async Task ClearFreeSpaces()
    {
        if (File.Exists(_path + "-spc"))
        {
            FileStream file = null;

            while (file == null)
            {
                try { file = new FileStream(_path + "-spc", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 60 * 1024, true); }
                catch { await Task.Delay(150); }
            }

            file.SetLength(0);
            file.Close();
        }
    }

}


