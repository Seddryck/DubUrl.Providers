using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using DubUrl.Discovery;
using DubUrl.Provider.Snowflake.Schema.Generation;
using DubUrl.Schema;

namespace DubUrl.Provider.Snowflake.Discovery;

/// <summary>Read-only column discovery for Snowflake standard tables.</summary>
public sealed class SnowflakeMetadataReader : CatalogSchemaMetadataReader, IStructuredColumnMetadataReader
{
    private readonly ConditionalWeakTable<MetadataColumn, StructuredColumnMetadata> structuredTypes = new();

    protected override IReadOnlySet<SchemaFeature> UnsupportedFeatures { get; } = Enum.GetValues<SchemaFeature>()
        .Where(feature => feature is not (SchemaFeature.Tables or SchemaFeature.Columns or SchemaFeature.Comments)).ToHashSet();

    private const string StandardTables = """
        t.table_type = 'BASE TABLE' AND t.table_schema <> 'INFORMATION_SCHEMA'
          AND t.is_iceberg = 'NO' AND t.is_hybrid = 'NO' AND t.is_dynamic = 'NO'
        """;

    protected override string TablesSql => $$"""
        SELECT t.table_catalog AS database_name,t.table_schema AS schema_name,t.table_name,t.comment AS "comment"
        FROM INFORMATION_SCHEMA.TABLES t WHERE {{StandardTables}}
        """;

    protected override string ColumnsSql => $$"""
        SELECT c.table_catalog AS database_name,c.table_schema AS schema_name,c.table_name,c.column_name,
          c.ordinal_position AS ordinal,c.data_type AS type_name,c.is_nullable AS nullable,
          c.character_maximum_length AS "length",c.numeric_precision AS "precision",c.numeric_scale AS "scale",c.comment AS "comment"
        FROM INFORMATION_SCHEMA.COLUMNS c JOIN INFORMATION_SCHEMA.TABLES t
          ON c.table_catalog=t.table_catalog AND c.table_schema=t.table_schema AND c.table_name=t.table_name
        WHERE {{StandardTables}}
        """;

    // Standard tables can declare unenforced keys. Discovery does not report them as portable constraints.
    protected override string KeysSql => "SELECT 1 WHERE FALSE";

    public async Task<StructuredSchemaMetadata> ReadWithStructuredTypesAsync(DbConnection connection,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        var metadata = await ReadAsync(connection, options, cancellationToken).ConfigureAwait(false);
        var types = new Dictionary<StructuredColumnIdentity, StructuredColumnMetadata>();
        foreach (var table in metadata.Tables)
        foreach (var column in table.Columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (structuredTypes.TryGetValue(column, out var type))
                types.Add(new(table.Identity, column.Name), type);
        }
        return new(metadata, types);
    }

    protected override async Task<List<CatalogRow>> ReadCatalogRowsAsync(DbConnection connection, string sql,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        if (sql == KeysSql)
            return [];

        var databases = options.Databases.Count > 0 ? options.Databases
            : options.Tables.Count > 0 && options.Tables.All(table => table.Database is not null)
                ? options.Tables.Select(table => table.Database!).ToArray() : [];
        var schemas = options.Schemas.Count > 0 ? options.Schemas
            : options.Tables.Count > 0 && options.Tables.All(table => table.Schema is not null)
                ? options.Tables.Select(table => table.Schema!).ToArray() : [];
        var bindings = new List<(string Name, object Value)>();
        var filters = BindFilter("t.table_schema", schemas, bindings)
            + BindFilter("t.table_name", options.Tables.Select(table => table.Name), bindings);
        var rows = new List<CatalogRow>();
        IEnumerable<string?> scopes = databases.Distinct(StringComparer.Ordinal);
        foreach (var database in scopes.DefaultIfEmpty())
        {
            var query = database is null ? sql : sql.Replace("INFORMATION_SCHEMA.",
                $"{SnowflakeStructuredTypes.QuoteIdentifier(database)}.INFORMATION_SCHEMA.", StringComparison.Ordinal);
            rows.AddRange(await QueryAsync(connection, query + filters, cancellationToken, bindings.ToArray()).ConfigureAwait(false));
        }

        rows.RemoveAll(row => !IsSelected(Identity(row), options));
        if (sql == ColumnsSql)
            await ReadTypeDeclarationsAsync(connection, rows, cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private static string BindFilter(string column, IEnumerable<string> values, List<(string Name, object Value)> bindings)
    {
        var names = values.Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0)
            return "";
        foreach (var name in names)
            bindings.Add(((bindings.Count + 1).ToString(CultureInfo.InvariantCulture), name));
        return $" AND {column} IN ({string.Join(", ", Enumerable.Repeat("?", names.Length))})";
    }

    private async Task ReadTypeDeclarationsAsync(DbConnection connection, List<CatalogRow> rows,
        CancellationToken cancellationToken)
    {
        var tables = Enumerable.Range(0, rows.Count)
            .Where(i => IsStructured(rows[i]) ? NeedsDeclaration(rows[i]) : MapColumn(rows[i]).Type is null)
            .GroupBy(i => Identity(rows[i])).ToArray();
        foreach (var table in tables)
        {
            var name = table.Key;
            if (name.Database is null || name.Schema is null)
                continue;
            var qualified = string.Join('.', new[] { name.Database, name.Schema, name.Name }
                .Select(SnowflakeStructuredTypes.QuoteIdentifier));
            try
            {
                var description = await QueryAsync(connection, $"DESCRIBE TABLE {qualified}", cancellationToken).ConfigureAwait(false);
                var declarations = description.Where(row => row.Text("name") is not null)
                    .ToLookup(row => row.Text("name"), StringComparer.Ordinal);
                foreach (var i in table)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var matches = declarations[rows[i].Text("column_name")];
                    if (matches.Count() == 1 && matches.First().Text("type") is { } declaration
                        && Family(rows[i]) == SnowflakeTypeDeclaration.Family(declaration))
                        rows[i] = rows[i].With(("native_type_declaration", declaration));
                }
            }
            catch (DbException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var i in table)
                    rows[i] = rows[i].With(("structured_inaccessible", true));
            }
        }
    }

    private static bool IsSelected(MetadataName name, DiscoveryOptions options)
        => (options.Databases.Count == 0 || options.Databases.Contains(name.Database, StringComparer.Ordinal))
            && (options.Schemas.Count == 0 || options.Schemas.Contains(name.Schema, StringComparer.Ordinal))
            && options.Catalogs.Count == 0
            && (options.Tables.Count == 0 || options.Tables.Any(table => table.Name == name.Name && table.Catalog is null
                && (table.Database is null || table.Database == name.Database) && (table.Schema is null || table.Schema == name.Schema)));

    private static string Family(CatalogRow row) => SnowflakeTypeDeclaration.Family(row.Text("type_name") ?? "");
    private static bool IsStructured(CatalogRow row) => Family(row) is "VARIANT" or "ARRAY" or "MAP" or "OBJECT" or "VECTOR";
    private static bool NeedsDeclaration(CatalogRow row) => Family(row) != "VARIANT"
        && !row.Text("type_name")!.Contains('(');

    protected override MetadataColumn MapCatalogColumn(CatalogRow row, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        var column = base.MapCatalogColumn(row, options, diagnose);
        if (!IsStructured(row))
            return column;

        var declaration = row.Text("native_type_declaration") ?? row.Text("type_name")!;
        var shape = SnowflakeTypeDeclaration.Parse(declaration);
        var evidence = shape switch
        {
            JsonType => new StructuredColumnMetadata(new(shape, StructuredStorage.Json), MetadataCoverage.Available),
            ArrayType or MapType or StructType or VectorType => new(new(shape, StructuredStorage.Native), MetadataCoverage.Available),
            _ => new(null, row.Bool("structured_inaccessible") == true ? MetadataCoverage.Inaccessible : MetadataCoverage.Unrepresentable)
        };
        structuredTypes.Add(column, evidence);
        return column;
    }

    protected override MetadataColumn MapColumn(CatalogRow row)
    {
        if (IsStructured(row))
            return base.MapColumn(row) with { Type = DbType.Object, Length = null, Precision = null, Scale = null };

        return row.Text("type_name") switch
        {
            "NUMBER" => base.MapColumn(row.With(("type_name", "DECIMAL"))),
            "TEXT" or "VARCHAR" => base.MapColumn(row.With(("type_name", "NVARCHAR"))),
            "TIMESTAMP_NTZ" => base.MapColumn(row.With(("type_name", "DATETIME2"))),
            _ => base.MapColumn(row)
        };
    }
}
