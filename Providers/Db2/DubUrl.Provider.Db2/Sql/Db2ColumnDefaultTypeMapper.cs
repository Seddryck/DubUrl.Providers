using System.Data;
using DubUrl.Querying.TypeMapping;

namespace DubUrl.Provider.Db2.Sql;

internal sealed class Db2ColumnDefaultTypeMapper : AnsiTypeMapper
{
    public static new Db2ColumnDefaultTypeMapper Instance { get; } = new();

    private Db2ColumnDefaultTypeMapper()
    {
        AddOrReplace(DbType.Time, "TIME");
        AddOrReplace(DbType.DateTime2, "TIMESTAMP");
    }
}
