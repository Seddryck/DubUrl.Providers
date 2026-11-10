using System.Data;
using DubUrl.Querying.TypeMapping;

namespace DubUrl.Provider.Teradata.Sql;

internal sealed class TeradataColumnDefaultTypeMapper : AnsiTypeMapper
{
    public static new TeradataColumnDefaultTypeMapper Instance { get; } = new();

    private TeradataColumnDefaultTypeMapper()
    {
        AddOrReplace(DbType.Time, "TIME");
        AddOrReplace(DbType.DateTime2, "TIMESTAMP");
    }
}
