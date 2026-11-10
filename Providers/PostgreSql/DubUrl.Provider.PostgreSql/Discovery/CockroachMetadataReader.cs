using DubUrl.Discovery;
using DubUrl.Provider.PostgreSql.Schema.Generation;
using DubUrl.Schema;

namespace DubUrl.Provider.PostgreSql.Discovery;

/// <summary>Reads tables and column declarations in the active CockroachDB database.</summary>
public sealed class CockroachMetadataReader : StructuredCatalogMetadataReader
{
    protected override string TablesSql => """
        SELECT table_schema AS schema_name,table_name
        FROM information_schema.tables
        WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema','crdb_internal')
        """;

    protected override string ColumnsSql => """
        SELECT table_schema AS schema_name,table_name,column_name,ordinal_position AS ordinal,is_nullable AS nullable,
          data_type AS type_name,crdb_sql_type AS declaration,character_maximum_length AS length,
          numeric_precision AS "precision",numeric_scale AS scale
        FROM information_schema.columns
        WHERE is_hidden='NO' AND table_schema NOT IN ('pg_catalog','information_schema','crdb_internal')
        """;

    protected override MetadataColumn MapCatalogColumn(CatalogRow row, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        var column = base.MapCatalogColumn(row, options, diagnose);
        if (Decode(row) is { } type)
            Track(column, type);
        return column;
    }

    protected override MetadataColumn MapColumn(CatalogRow row)
    {
        var column = base.MapColumn(row);
        if (Decode(row) is not null)
            return StructuredColumn(column);
        // CockroachDB reports STRING as text. These declarations distinguish the three integer widths.
        return row.Text("declaration") switch
        {
            "INT2" => column with { Type = System.Data.DbType.Int16, Precision = null, Scale = null },
            "INT4" => column with { Type = System.Data.DbType.Int32, Precision = null, Scale = null },
            "INT8" => column with { Type = System.Data.DbType.Int64, Precision = null, Scale = null },
            _ => column
        };
    }

    private static StructuredColumnMetadata? Decode(CatalogRow row)
        => CockroachStructuredTypes.Decode(row.Text("type_name") ?? "", row.Text("declaration"));
}
