using System.Data;
using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Schema;

namespace DubUrl.Provider.PostgreSql.Discovery;

/// <summary>Reads QuestDB's native column declarations instead of the PostgreSQL compatibility type names.</summary>
public sealed class QuestDbMetadataReader : StructuredCatalogMetadataReader
{
    protected override string TablesSql => "SELECT table_name FROM tables() WHERE NOT matView";
    protected override string ColumnsSql => "SHOW COLUMNS FROM ";

    protected override async Task<List<CatalogRow>> ReadCatalogRowsAsync(DbConnection connection, string sql,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        if (sql != ColumnsSql)
            return await base.ReadCatalogRowsAsync(connection, sql, options, cancellationToken).ConfigureAwait(false);
        var rows = new List<CatalogRow>();
        var tables = await base.ReadCatalogRowsAsync(connection, TablesSql, options, cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            if (table.Text("table_name") is not { } name || options.Databases.Count > 0 || options.Catalogs.Count > 0 || options.Schemas.Count > 0
                || (options.Tables.Count > 0 && !options.Tables.Contains(new MetadataName(name))))
                continue;
            var quoted = "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            var columns = await QueryAsync(connection, ColumnsSql + quoted, cancellationToken).ConfigureAwait(false);
            rows.AddRange(columns.Select((column, ordinal) => column.With(("table_name", name), ("column_name", column.Text("column")),
                ("ordinal", ordinal + 1), ("type_name", column.Text("type")), ("nullable", true))));
        }
        return rows;
    }

    protected override MetadataColumn MapCatalogColumn(CatalogRow row, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        var column = base.MapCatalogColumn(row, options, diagnose);
        if (row.Text("type_name") is { } type && IsArray(type))
            Track(column, type.Equals("DOUBLE[]", StringComparison.OrdinalIgnoreCase)
                ? new(new(new ArrayType(new ScalarType(DbType.Double), true), StructuredStorage.Native), MetadataCoverage.Available)
                : new(null, MetadataCoverage.Unrepresentable));
        return column;
    }

    protected override MetadataColumn MapColumn(CatalogRow row)
    {
        var column = base.MapColumn(row);
        var type = row.Text("type_name") ?? "";
        if (IsArray(type))
            return StructuredColumn(column);
        return type switch
        {
            "LONG" => column with { Type = DbType.Int64 },
            "DOUBLE" => column with { Type = DbType.Double },
            "FLOAT" => column with { Type = DbType.Single },
            "STRING" or "VARCHAR" => column with { Type = DbType.String, Length = null },
            "TIMESTAMP" => column with { Type = DbType.DateTime2 },
            _ => column
        };
    }

    private static bool IsArray(string type) => type.Contains('[');
}
