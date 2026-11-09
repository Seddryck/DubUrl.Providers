using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using DubUrl.Discovery;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;

namespace DubUrl.Provider.Oracle.Discovery;

/// <summary>Read-only discovery of Oracle objects visible to the connected user in the current PDB.</summary>
public sealed class OracleMetadataReader : CatalogSchemaMetadataReader, IUniqueConstraintMetadataReader
{
    protected override IReadOnlySet<SchemaFeature> UnsupportedFeatures { get; } = new HashSet<SchemaFeature>
        { SchemaFeature.Defaults, SchemaFeature.Generation, SchemaFeature.Checks };

    // Leave table selection and foreign-key dependency closure to SchemaInspector.
    protected override string TablesSql => """
        SELECT t.owner AS schema_name, t.table_name, c.comments AS "comment"
        FROM all_tables t
        LEFT JOIN all_tab_comments c ON c.owner = t.owner AND c.table_name = t.table_name
        WHERE t.nested = 'NO' AND t.secondary = 'N' AND (t.iot_type IS NULL OR t.iot_type = 'IOT')
        """;

    protected override string ColumnsSql => """
        SELECT c.owner AS schema_name, c.table_name, c.column_name, c.column_id AS ordinal,
          c.data_type AS type_name, c.data_type_owner AS type_owner, c.char_used,
          c.char_length, c.data_length, c.data_precision AS "precision", c.data_scale AS scale,
          CASE c.nullable WHEN 'Y' THEN 1 WHEN 'N' THEN 0 END AS nullable, m.comments AS "comment"
        FROM all_tab_columns c
        JOIN all_tables t ON t.owner = c.owner AND t.table_name = c.table_name
        LEFT JOIN all_col_comments m ON m.owner = c.owner AND m.table_name = c.table_name AND m.column_name = c.column_name
        """;

    protected override string KeysSql => """
        SELECT k.owner AS schema_name, k.table_name, k.constraint_name AS key_name,
          CASE k.constraint_type WHEN 'P' THEN 'PRIMARY KEY' WHEN 'U' THEN 'UNIQUE' ELSE 'FOREIGN KEY' END AS kind,
          c.position AS ordinal, c.column_name, k.r_owner AS target_schema, p.table_name AS target_table,
          pc.column_name AS target_column, k.r_constraint_name AS target_key,
          CASE WHEN k.status <> 'ENABLED' OR k.validated <> 'VALIDATED'
            OR k.deferrable <> 'NOT DEFERRABLE' OR k.delete_rule <> 'NO ACTION' THEN 1 ELSE 0 END AS unsupported
        FROM all_constraints k
        LEFT JOIN all_cons_columns c ON c.owner = k.owner AND c.constraint_name = k.constraint_name AND c.table_name = k.table_name
        LEFT JOIN all_constraints p ON p.owner = k.r_owner AND p.constraint_name = k.r_constraint_name
        LEFT JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name
          AND pc.table_name = p.table_name AND pc.position = c.position
        WHERE k.constraint_type IN ('P', 'U', 'R')
        """;

    protected override string IndexesSql => """
        SELECT i.table_owner AS schema_name, i.table_name, i.owner AS index_owner, i.index_name,
          CASE i.uniqueness WHEN 'UNIQUE' THEN 1 ELSE 0 END AS unique_index,
          c.column_position AS ordinal, c.column_name,
          CASE c.descend WHEN 'DESC' THEN 1 WHEN 'ASC' THEN 0 END AS descending,
          CASE WHEN i.index_type NOT IN ('NORMAL', 'FUNCTION-BASED NORMAL') OR i.visibility <> 'VISIBLE'
            OR i.status <> 'VALID' OR i.table_owner <> i.owner OR col.column_name IS NULL THEN 1 ELSE 0 END AS unsupported
        FROM all_indexes i
        LEFT JOIN all_ind_columns c ON c.index_owner = i.owner AND c.index_name = i.index_name
          AND c.table_owner = i.table_owner AND c.table_name = i.table_name
        LEFT JOIN all_tab_columns col ON col.owner = i.table_owner AND col.table_name = i.table_name
          AND col.column_name = c.column_name
        WHERE NOT EXISTS (
          SELECT 1 FROM all_constraints k WHERE k.owner = i.table_owner AND k.table_name = i.table_name
            AND k.index_owner = i.owner AND k.index_name = i.index_name AND k.constraint_type IN ('P', 'U'))
        """;

    protected override string ViewsSql => """
        SELECT v.owner AS schema_name, v.view_name AS table_name,
          CASE WHEN v.text_length < 4000 AND v.read_only = 'N' AND v.bequeath = 'DEFINER'
            AND v.view_type IS NULL THEN v.text_vc END AS definition,
          c.column_name, c.column_id AS ordinal, c.data_type AS type_name, c.data_type_owner AS type_owner,
          c.char_used, c.char_length, c.data_length, c.data_precision AS "precision", c.data_scale AS scale,
          CASE c.nullable WHEN 'Y' THEN 1 WHEN 'N' THEN 0 END AS nullable
        FROM all_views v
        LEFT JOIN all_tab_columns c ON c.owner = v.owner AND c.table_name = v.view_name
        """;

    protected override Task<List<CatalogRow>> ReadCatalogRowsAsync(DbConnection connection, string sql,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        // Unscoped inspection describes application schemas. Explicit selections can still inspect system objects.
        if (options.Schemas.Count == 0 && options.Tables.Count == 0)
            return QueryAsync(connection,
                $"SELECT * FROM ({sql}) WHERE schema_name IN (SELECT username FROM all_users WHERE oracle_maintained = 'N')",
                cancellationToken);

        // Dependency closure can include owners outside the requested scope.
        if (options.IncludeReferencedTables) return base.ReadCatalogRowsAsync(connection, sql, options, cancellationToken);
        var owners = options.Schemas;
        if (owners.Count == 0 && options.Tables.All(table => table.Schema is not null))
            owners = options.Tables.Select(table => table.Schema!).Distinct(StringComparer.Ordinal).ToArray();
        if (owners.Count == 0 || owners.Count > 1000)
            return base.ReadCatalogRowsAsync(connection, sql, options, cancellationToken);

        var parameters = owners.Select((owner, index) => (Name: $"owner{index}", Value: (object)owner)).ToArray();
        var binds = string.Join(", ", parameters.Select(parameter => ":" + parameter.Name));
        return QueryAsync(connection, $"SELECT * FROM ({sql}) WHERE schema_name IN ({binds})", cancellationToken, parameters);
    }

    public UniqueConstraintMetadata GetUniqueConstraintMetadata(MetadataTable table, MetadataKey key)
    {
        var nullableMembers = table.Columns.Count(column => key.Columns.Contains(column.Name) && column.Nullable == true);
        // Oracle permits repeated all-null tuples, but compares matching partially-null tuples.
        // Report an explicit policy only where the key shape identifies a portable equivalent.
        return new((key.Columns.Count, nullableMembers) switch
        {
            (1, 1) => UniqueNullPolicy.NullsDistinct,
            (var count, var nullable) when nullable > 0 && nullable < count => UniqueNullPolicy.NullsNotDistinct,
            _ => UniqueNullPolicy.Native
        });
    }

    protected override MetadataColumn MapColumn(CatalogRow row)
    {
        var column = base.MapColumn(row);
        if (row.Text("type_owner") is not null) return column with { Type = null };

        return row.Text("type_name") switch
        {
            "NUMBER" => MapNumber(row, column),
            "CHAR" => MapCharacter(row, column, "CHAR", DbType.AnsiStringFixedLength, 2000),
            "VARCHAR2" => MapCharacter(row, column, "VARCHAR2", DbType.AnsiString, 4000),
            "DATE" => Native(column, "DATE", DbType.DateTime),
            "TIMESTAMP(6)" => Native(column, "TIMESTAMP", DbType.DateTime),
            "BINARY_FLOAT" => Native(column, "BINARY_FLOAT", DbType.Single),
            "BINARY_DOUBLE" => Native(column, "BINARY_DOUBLE", DbType.Double),
            "RAW" => Native(column, "RAW", DbType.Binary) with { Length = row.Int("data_length") },
            "CLOB" => Native(column, "CLOB", DbType.AnsiString),
            "NCLOB" => Native(column, "NCLOB", DbType.String),
            "BLOB" => Native(column, "BLOB", DbType.Binary),
            _ => column with { Type = null }
        };
    }

    private static MetadataColumn MapNumber(CatalogRow row, MetadataColumn column)
    {
        var scale = row.Int("scale");
        // Oracle records NUMBER(*, s), including the ANSI INTEGER alias, with a null precision.
        byte? precision = row.Byte("precision") ?? (scale.HasValue ? (byte)38 : null);
        if (scale is < 0 || scale > precision) return column with { Type = null };
        return Native(column, "NUMBER", DbType.Decimal) with { Precision = precision, Scale = (byte?)scale };
    }

    private static MetadataColumn MapCharacter(CatalogRow row, MetadataColumn column, string type, DbType logicalType, int maximum)
    {
        var length = row.Int("char_length");
        if (length is null or <= 0 || length > maximum) return column with { Type = null };
        var unit = row.Text("char_used") == "C" ? "CHAR" : "BYTE";
        return Native(column, type + " " + unit, logicalType) with { Length = length };
    }

    private static MetadataColumn Native(MetadataColumn column, string nativeType, DbType logicalType)
        => column with { NativeType = nativeType, NativeDialect = typeof(OracleDialect), Type = logicalType };

    protected override MetadataColumn MapCatalogColumn(CatalogRow row, DiscoveryOptions options,
        Action<SchemaFeature, string> diagnose)
        => base.MapCatalogColumn(row, options, (feature, message) =>
            diagnose(feature, $"Oracle column '{row.Text("column_name")}' ({row.Text("type_name")}): {message}"));

    protected override MetadataTable MapTable(CatalogRow row, List<CatalogRow> columns, List<CatalogRow> keys,
        List<CatalogRow> indexes, List<CatalogRow> checks, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        var identity = Identity(row);
        var invalid = keys.Where(key => Identity(key) == identity)
            .GroupBy(key => (Kind: key.Text("kind"), Name: key.Text("key_name")))
            .Where(key => key.Any(part => part.Bool("unsupported") == true || part.Text("column_name") is null
                || (key.Key.Kind == "FOREIGN KEY" && part.Text("target_column") is null))).ToArray();

        foreach (var key in invalid)
        {
            var feature = key.Key.Kind switch
            {
                "PRIMARY KEY" => SchemaFeature.PrimaryKeys,
                "UNIQUE" => SchemaFeature.UniqueConstraints,
                _ => SchemaFeature.ForeignKeys
            };
            if (!options.Features.Contains(feature)) continue;
            var target = key.FirstOrDefault(part => key.Key.Kind == "FOREIGN KEY" && part.Text("target_column") is null);
            if (key.Any(part => part.Text("column_name") is null))
                diagnose(feature, $"Oracle key '{key.Key.Name}' has columns unavailable to this connection.");
            else if (target is not null)
                diagnose(feature, $"Oracle foreign key '{key.Key.Name}' refers to '{target.Text("target_schema")}.{target.Text("target_key")}', whose columns are unavailable to this connection.");
            else
                diagnose(feature, $"Oracle key '{key.Key.Name}' has an enforcement state or delete action that cannot be represented.");
        }

        return base.MapTable(row, columns, invalid.Length == 0 ? keys : keys.Except(invalid.SelectMany(key => key)).ToList(),
            indexes, checks, options, diagnose);
    }

    protected override MetadataView MapView(MetadataName identity, MetadataColumn[] inventory, string? sql)
    {
        if (sql is null) return new(identity, inventory);
        // Oracle folds bare identifiers to uppercase and preserves quoted names.
        sql = Regex.Replace(sql, "\"(?:[^\"]|\"\")*\"|[a-zA-Z][a-zA-Z0-9_$]*",
            match => match.Value.StartsWith('"') ? match.Value : match.Value.ToUpperInvariant(),
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var view = base.MapView(identity, inventory, sql);
        if (view.Projections is null || view.Projections.Count != inventory.Length)
            return new(identity, inventory);

        // CREATE VIEW may declare output names outside its SELECT body.
        return view with
        {
            Projections = view.Projections.Select((projection, index) => projection with
            {
                Alias = projection.Expression.Name == inventory[index].Name ? null : inventory[index].Name
            }).ToArray()
        };
    }
}
