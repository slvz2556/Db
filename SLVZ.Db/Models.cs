
namespace SLVZ.Db;

public class Index
{
    public Int64 SelfPosition { get; set; } = 0;
    public Int64 PrimaryKey { get; set; } = 0;
    public Point Position1 { get; set; } = new Point();
    public Point Position2 { get; set; } = new Point();
    public byte IsFree { get; set; } = 0; // 0 = Taken, 1 = Free
}

public class Point
{
    public Int64 Position { get; set; } = 0;
    public Int32 Length { get; set; } = 0;
}
