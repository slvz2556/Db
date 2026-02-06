
namespace SLVZ.Db.Light;

/// <summary>
/// Defines the unique key used by the database to identify the record
/// It can be any type
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class Key : Attribute
{
}