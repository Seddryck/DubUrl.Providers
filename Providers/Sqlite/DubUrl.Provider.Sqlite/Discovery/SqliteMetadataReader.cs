using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using DubUrl.Discovery;
using DubUrl.Provider.Sqlite.Schema.Generation;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;

namespace DubUrl.Provider.Sqlite.Discovery;

/// <summary>SQLite main and attached-database metadata via read-only table-valued PRAGMAs.</summary>
public sealed class SqliteMetadataReader : CatalogSchemaMetadataReader, IUniqueConstraintMetadataReader, IStructuredColumnMetadataReader
{
    private readonly ConditionalWeakTable<MetadataColumn, StructuredColumnMetadata> structured = new();

    public async Task<StructuredSchemaMetadata> ReadWithStructuredTypesAsync(DbConnection connection,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        var metadata = await ReadAsync(connection, options, cancellationToken).ConfigureAwait(false);
        var types = new Dictionary<StructuredColumnIdentity, StructuredColumnMetadata>();
        foreach (var table in metadata.Tables)
        foreach (var column in table.Columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (structured.TryGetValue(column, out var type))
                types.Add(new(table.Identity, column.Name), type);
        }
        return new(metadata, types);
    }

    public UniqueConstraintMetadata GetUniqueConstraintMetadata(MetadataTable table, MetadataKey key)
        => new(UniqueNullPolicy.NullsDistinct);

    protected override MetadataTable MapTable(CatalogRow row, List<CatalogRow> columnRows, List<CatalogRow> keyRows,
        List<CatalogRow> indexRows, List<CatalogRow> checkRows, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        var identity = Identity(row);
        var definition = SqliteTableDefinition.Parse(row.Text("definition"));
        var columns = columnRows.Where(column => Identity(column) == identity).ToList();
        if (definition.Generated.Count > 0 && options.Features.Contains(SchemaFeature.Generation))
            columns = columns.Select(column => column.Text("generation_expression") is not null
                && column.Text("column_name") is { } name && definition.Generated.TryGetValue(name, out var expression)
                    ? column.With(("generation_expression", expression)) : column).ToList();

        var table = base.MapTable(row, columns, keyRows, indexRows, checkRows, options, diagnose);
        var ambiguous = columns.Where(column => column.Text("type_name")?.Trim().ToUpperInvariant() is "JSON" or "JSONB")
            .Select(column => column.Text("column_name")).ToHashSet(StringComparer.Ordinal);
        foreach (var column in table.Columns)
        {
            if (definition.JsonColumns.TryGetValue(column.Name, out var storage))
                structured.Add(column, new(new(new JsonType(), storage), MetadataCoverage.Available));
            else if (ambiguous.Contains(column.Name))
                structured.Add(column, new(null, MetadataCoverage.Unrepresentable));
        }
        return table;
    }

    protected override MetadataColumn MapCatalogColumn(CatalogRow row, DiscoveryOptions options,
        Action<SchemaFeature, string> diagnose)
    {
        if (!options.Features.Contains(SchemaFeature.Defaults))
            return base.MapCatalogColumn(row, options, diagnose);

        var type = row.Text("type_name")?.ToUpperInvariant();
        var sql = row.Text("default_expression")?.Trim().ToUpperInvariant();
        (ColumnDefaultExpression? Expression, DbType? Type) recognized = (type, sql) switch
        {
            ("TEXT", "CURRENT_TIMESTAMP") => (ColumnDefaultExpression.CurrentTimestamp(), DbType.DateTime),
            ("TEXT", "DATETIME('NOW')" or "(DATETIME('NOW'))") => (ColumnDefaultExpression.CurrentUtcTimestamp(), DbType.DateTime),
            ("TEXT", "CURRENT_DATE") => (ColumnDefaultExpression.CurrentDate, DbType.Date),
            ("TIME" or "TEXT", "CURRENT_TIME") => (ColumnDefaultExpression.CurrentTime(), DbType.Time),
            (var declared, var value) when declared?.StartsWith("TEXT(", StringComparison.Ordinal) == true
                && value == SqliteSchemaSqlRenderer.UuidText.ToUpperInvariant()
                => (ColumnDefaultExpression.NewUuid(UuidRepresentation.CanonicalText), DbType.String),
            _ => (null, (DbType?)null)
        };
        var (expression, logicalType) = recognized;
        return expression is null ? base.MapCatalogColumn(row, options, diagnose)
            : base.MapCatalogColumn(row.With(("default_expression", null)), options, diagnose)
                with { Default = expression, Type = logicalType };
    }

    public override void ConfigureOwnedConnection(System.Data.Common.DbConnection connection)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connection.ConnectionString };
        builder["Mode"] = "ReadOnly";
        connection.ConnectionString = builder.ConnectionString;
    }

    protected override MetadataName ResolveViewSource(MetadataName view, MetadataName source)
    {
        if (source.Database is not null || source.Catalog is not null)
            throw new ArgumentException("SQLite source names support at most two components.");
        return new(source.Name, Database: source.Schema ?? view.Database);
    }

    protected override IReadOnlySet<SchemaFeature> UnsupportedFeatures => new HashSet<SchemaFeature>
        { SchemaFeature.Comments };
    protected override string ChecksSql => "SELECT name AS table_name,sql AS definition FROM {database}.sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'";

    protected override async Task<List<CatalogRow>> ReadCatalogRowsAsync(System.Data.Common.DbConnection connection,
        string sql, DiscoveryOptions options, CancellationToken cancellationToken)
    {
        var databases = await QueryAsync(connection, "PRAGMA database_list", cancellationToken).ConfigureAwait(false);
        var rows = new List<CatalogRow>();
        foreach (var database in databases)
        {
            var name = database.Text("name")!;
            // Only native catalog identifiers enter SQL. PRAGMA arguments remain parameters.
            var quoted = "\"" + name.Replace("\"", "\"\"") + "\"";
            var query = sql.Replace("{database}", quoted);
            var found = await QueryAsync(connection, query, cancellationToken, ("@database", name)).ConfigureAwait(false);
            if (sql == ChecksSql)
                found = found.SelectMany(row => SqliteTableDefinition.Parse(row.Text("definition")).Checks.Select(check =>
                    row.With(("check_name", check.Name), ("expression", check.Expression)))).ToList();
            rows.AddRange(found.Select(row => row.With(("schema_name", null), ("database_name", name),
                ("target_schema", null), ("target_database", row.Text("target_table") is null ? null : name))));
        }
        return rows;
    }

    protected override string TablesSql => """
        SELECT 'main' AS schema_name,name AS table_name,sql AS definition FROM {database}.sqlite_schema
        WHERE type='table' AND name NOT LIKE 'sqlite_%'
        """;
    protected override string ColumnsSql => """
        SELECT 'main' AS schema_name,t.name AS table_name,p.name AS column_name,p.cid AS ordinal,p.type AS type_name,
          CASE WHEN p."notnull"=1 OR (p.pk>0 AND upper(p.type)='INTEGER' AND
            (SELECT count(*) FROM pragma_table_xinfo(t.name,@database) x WHERE x.pk>0)=1) THEN 0 ELSE 1 END AS nullable,
          p.dflt_value AS default_expression,
          CASE WHEN p.hidden IN (2,3) THEN '<unavailable generated expression>' END AS generation_expression,
          CASE WHEN p.hidden=3 THEN 1 ELSE 0 END AS stored
        FROM {database}.sqlite_schema t,pragma_table_xinfo(t.name,@database) p
        WHERE t.type='table' AND t.name NOT LIKE 'sqlite_%'
        """;
    protected override string KeysSql => """
        SELECT 'main' AS schema_name,t.name AS table_name,NULL AS key_name,'PRIMARY KEY' AS kind,p.pk AS ordinal,p.name AS column_name,
          NULL AS target_schema,NULL AS target_table,NULL AS target_column
        FROM {database}.sqlite_schema t,pragma_table_xinfo(t.name,@database) p WHERE t.type='table' AND t.name NOT LIKE 'sqlite_%' AND p.pk>0
        UNION ALL
        SELECT 'main',t.name,'fk_'||t.name||'_'||f.id,'FOREIGN KEY',f.seq,f."from",'main',f."table",
          COALESCE(f."to",(SELECT x.name FROM pragma_table_xinfo(f."table",@database) x WHERE x.pk=f.seq+1))
        FROM {database}.sqlite_schema t,pragma_foreign_key_list(t.name,@database) f WHERE t.type='table' AND t.name NOT LIKE 'sqlite_%'
        UNION ALL
        SELECT 'main',t.name,i.name,'UNIQUE',x.seqno,x.name,NULL,NULL,NULL
        FROM {database}.sqlite_schema t,pragma_index_list(t.name,@database) i,pragma_index_xinfo(i.name,@database) x
        WHERE t.type='table' AND t.name NOT LIKE 'sqlite_%' AND i.origin='u' AND x."key"=1
        """;
    protected override string IndexesSql => """
        SELECT 'main' AS schema_name,t.name AS table_name,i.name AS index_name,i."unique" AS unique_index,
          x.seqno AS ordinal,x.name AS column_name,x.desc AS descending,
          CASE WHEN i.partial=1 OR x.cid<0 THEN 1 ELSE 0 END AS unsupported
        FROM {database}.sqlite_schema t,pragma_index_list(t.name,@database) i,pragma_index_xinfo(i.name,@database) x
        WHERE t.type='table' AND t.name NOT LIKE 'sqlite_%' AND i.origin='c' AND x."key"=1
        """;
    protected override string ViewsSql => """
        SELECT 'main' AS schema_name,v.name AS table_name,v.sql AS definition,p.name AS column_name,
          p.cid AS ordinal,p.type AS type_name,CASE WHEN p."notnull"=1 THEN 0 ELSE 1 END AS nullable
        FROM {database}.sqlite_schema v LEFT JOIN pragma_table_xinfo(v.name,@database) p WHERE v.type='view'
        """;
}
