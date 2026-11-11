using System.Data;
using DubUrl.Querying.TypeMapping;

namespace DubUrl.Provider.Spark.Sql;

internal sealed class SparkTypeMapper : BaseTypeMapper
{
    public static SparkTypeMapper Instance { get; } = new();

    private SparkTypeMapper()
    {
        AddOrReplace(DbType.Boolean, "BOOLEAN");
        AddOrReplace(DbType.SByte, "TINYINT");
        AddOrReplace(DbType.Int16, "SMALLINT");
        AddOrReplace(DbType.Int32, "INT");
        AddOrReplace(DbType.Int64, "BIGINT");
        AddOrReplace(DbType.Single, "FLOAT");
        AddOrReplace(DbType.Double, "DOUBLE");
        AddOrReplace(DbType.Decimal, "DECIMAL");
        AddOrReplace(DbType.String, "VARCHAR");
        AddOrReplace(DbType.AnsiString, "VARCHAR");
        AddOrReplace(DbType.StringFixedLength, "CHAR");
        AddOrReplace(DbType.AnsiStringFixedLength, "CHAR");
        AddOrReplace(DbType.Binary, "BINARY");
        AddOrReplace(DbType.Date, "DATE");
        AddOrReplace(DbType.DateTime, "TIMESTAMP");
        AddOrReplace(DbType.DateTime2, "TIMESTAMP_NTZ");
        AddOrReplace(DbType.DateTimeOffset, "TIMESTAMP_LTZ");
    }
}
