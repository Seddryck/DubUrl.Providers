using System.Data;
using DubUrl.Querying.TypeMapping;

namespace DubUrl.Provider.Snowflake.Sql;

internal sealed class SnowflakeColumnDefaultTypeMapper : AnsiTypeMapper
{
    public static new SnowflakeColumnDefaultTypeMapper Instance { get; } = new();

    private SnowflakeColumnDefaultTypeMapper()
    {
        AddOrReplace(DbType.Time, "TIME");
        AddOrReplace(DbType.DateTime2, "TIMESTAMP_NTZ");
    }
}
