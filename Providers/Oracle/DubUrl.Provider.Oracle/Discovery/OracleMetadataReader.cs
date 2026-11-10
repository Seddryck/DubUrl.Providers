using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DubUrl.Discovery;
using DubUrl.Provider.Oracle.Schema.Generation;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;

namespace DubUrl.Provider.Oracle.Discovery;

/// <summary>Read-only discovery of Oracle objects visible to the connected user in the current PDB.</summary>
public sealed class OracleMetadataReader : CatalogSchemaMetadataReader, IUniqueConstraintMetadataReader, IStructuredColumnMetadataReader
{
    private readonly ConditionalWeakTable<MetadataColumn, StructuredColumnMetadata> structuredTypes = new();
    private readonly ConditionalWeakTable<CatalogRow, OracleNamedTypeMetadata> namedTypes = new();

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

    public OracleMetadataReader()
        => UnsupportedFeatures = new HashSet<SchemaFeature> { SchemaFeature.Defaults, SchemaFeature.Generation, SchemaFeature.Checks };

    /// <summary>Enables default discovery when the Oracle catalog exposes DATA_DEFAULT_VC.</summary>
    public OracleMetadataReader(Version serverVersion) : this()
    {
        ArgumentNullException.ThrowIfNull(serverVersion);
        if (serverVersion.Major >= 23)
            UnsupportedFeatures = new HashSet<SchemaFeature> { SchemaFeature.Generation, SchemaFeature.Checks };
    }

    protected override IReadOnlySet<SchemaFeature> UnsupportedFeatures { get; }

    // Leave table selection and foreign-key dependency closure to SchemaInspector.
    protected override string TablesSql => """
        SELECT t.owner AS schema_name, t.table_name, c.comments AS "comment"
        FROM all_tables t
        LEFT JOIN all_tab_comments c ON c.owner = t.owner AND c.table_name = t.table_name
        WHERE t.nested = 'NO' AND t.secondary = 'N' AND (t.iot_type IS NULL OR t.iot_type = 'IOT')
        """;

    protected override string ColumnsSql => """
        SELECT c.owner AS schema_name, c.table_name, c.column_name, c.column_id AS ordinal,
          c.data_type AS type_name, c.data_type_owner AS type_owner, c.data_type_mod AS type_modifier, dt.typecode AS named_kind, c.char_used,
          c.char_length, c.data_length, c.data_precision AS "precision", c.data_scale AS scale,
          CASE c.nullable WHEN 'Y' THEN 1 WHEN 'N' THEN 0 END AS nullable, m.comments AS "comment"
        FROM all_tab_columns c
        JOIN all_tables t ON t.owner = c.owner AND t.table_name = c.table_name
        LEFT JOIN all_types dt ON dt.owner = c.data_type_owner AND dt.type_name = c.data_type
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

    protected override async Task<List<CatalogRow>> ReadCatalogRowsAsync(DbConnection connection, string sql,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        var columns = sql == ColumnsSql;
        if (columns && options.Features.Contains(SchemaFeature.Defaults)
            && !UnsupportedFeatures.Contains(SchemaFeature.Defaults))
        {
            sql = sql.Replace("m.comments AS \"comment\"", """
                m.comments AS "comment",
                CASE WHEN c.default_length > 4000 OR c.default_on_null = 'YES' OR c.identity_column = 'YES'
                     THEN '<unrepresentable Oracle default>' ELSE c.data_default_vc END AS default_expression
                """, StringComparison.Ordinal);
        }

        var rows = await ReadScopedRowsAsync(connection, sql, options, cancellationToken).ConfigureAwait(false);
        if (columns && rows.Any(IsStructuredColumn))
            await ReadStructuredModifiersAsync(connection, options, rows, cancellationToken).ConfigureAwait(false);
        if (columns)
            await ReadNamedTypesAsync(connection, rows, cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private async Task ReadNamedTypesAsync(DbConnection connection, List<CatalogRow> rows, CancellationToken cancellationToken)
    {
        var named = rows.Where(row => row.Text("type_owner") is not null && row.Text("named_kind") is "OBJECT" or "COLLECTION").ToArray();
        if (named.Length == 0)
            return;

        List<CatalogRow> members;
        try
        {
            members = await QueryAsync(connection, OracleNamedTypeCatalog.Sql, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in named)
                namedTypes.Add(row, new(new(null, MetadataCoverage.Inaccessible)));
            return;
        }

        var catalog = new OracleNamedTypeCatalog(members);
        foreach (var row in named)
        {
            cancellationToken.ThrowIfCancellationRequested();
            namedTypes.Add(row, catalog.Decode(row.Text("type_owner")!, row.Text("type_name")!, row.Text("type_modifier")));
        }
    }

    private Task<List<CatalogRow>> ReadScopedRowsAsync(DbConnection connection, string sql,
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

    private async Task ReadStructuredModifiersAsync(DbConnection connection, DiscoveryOptions options,
        List<CatalogRow> rows, CancellationToken cancellationToken)
    {
        // Dictionary columns differ across releases. Older catalogs still identify the base types.
        try
        {
            var fields = await QueryAsync(connection, """
                SELECT column_name FROM all_tab_columns WHERE owner = 'SYS' AND table_name = 'ALL_TAB_COLUMNS'
                    AND column_name IN ('VECTOR_INFO', 'JSON_MODIFIER')
                """, cancellationToken).ConfigureAwait(false);
            var vector = fields.Any(field => field.Text("column_name") == "VECTOR_INFO") ? "vector_info" : "NULL AS vector_info";
            var json = fields.Any(field => field.Text("column_name") == "JSON_MODIFIER") ? "json_modifier" : "NULL AS json_modifier";
            if (fields.Count == 0)
                return;

            var modifiers = await ReadScopedRowsAsync(connection, $"""
                SELECT owner AS schema_name, table_name, column_name, {vector}, {json}
                FROM all_tab_columns WHERE data_type IN ('JSON', 'VECTOR') AND data_type_owner IS NULL
                """, options, cancellationToken).ConfigureAwait(false);
            var byColumn = modifiers.ToDictionary(row => (Identity(row), row.Text("column_name")));
            for (var i = 0; i < rows.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = rows[i];
                if (IsStructuredColumn(row) && byColumn.TryGetValue((Identity(row), row.Text("column_name")), out var evidence))
                    rows[i] = row.With(("vector_info", evidence.Text("vector_info")), ("json_modifier", evidence.Text("json_modifier")));
            }
        }
        catch (DbException)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStructuredColumn(rows[i]))
                    rows[i] = rows[i].With(("structured_inaccessible", true));
            }
        }
    }

    private static bool IsStructuredColumn(CatalogRow row)
        => row.Text("type_owner") is null && row.Text("type_name") is "JSON" or "VECTOR";

    private static readonly Regex VectorInfo = new(
        @"\AVECTOR\(\s*(?<size>[0-9]+)\s*,\s*(?<format>INT8|FLOAT32|FLOAT64)\s*(?:,\s*DENSE\s*)?\)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    internal static StructuredColumnMetadata? DecodeStructuredType(CatalogRow row)
    {
        if (!IsStructuredColumn(row))
            return null;
        if (row.Bool("structured_inaccessible") == true)
            return new(null, MetadataCoverage.Inaccessible);

        if (row.Text("type_name") == "JSON")
        {
            var modifier = row.Text("json_modifier");
            return modifier is null || modifier.Replace(" ", "", StringComparison.Ordinal) == "JSON()"
                ? new(new(new JsonType(), StructuredStorage.Json), MetadataCoverage.Available)
                : new(null, MetadataCoverage.Unrepresentable);
        }

        var match = VectorInfo.Match(row.Text("vector_info") ?? "");
        if (match.Success && int.TryParse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            && size is > 0 and <= OracleStructuredTypes.MaxVectorDimensions)
        {
            var element = OracleStructuredTypes.VectorFormats.Single(item =>
                item.Value.Equals(match.Groups["format"].Value, StringComparison.OrdinalIgnoreCase)).Key;
            return new(new(new VectorType(element, size), StructuredStorage.Native), MetadataCoverage.Available);
        }
        return new(null, MetadataCoverage.Unrepresentable);
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
        if (IsStructuredColumn(row))
            return column with { Type = DbType.Object, Length = null, Precision = null, Scale = null };
        if (namedTypes.TryGetValue(row, out var named) && row.Text("type_owner") is { } owner && row.Text("type_name") is { } name)
            return Native(column, OracleNamedType.QualifiedName(owner, name), DbType.Object)
                with { Length = named.Capacity, Precision = null, Scale = null };
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
    {
        void Diagnose(SchemaFeature feature, string message)
            => diagnose(feature, $"Oracle column '{row.Text("column_name")}' ({row.Text("type_name")}): {message}");

        if ((namedTypes.TryGetValue(row, out var named) ? named.Structured : DecodeStructuredType(row)) is { } structured)
        {
            var mapped = base.MapCatalogColumn(row, options, Diagnose);
            structuredTypes.Add(mapped, structured);
            return mapped;
        }

        if (!options.Features.Contains(SchemaFeature.Defaults) || row.Text("default_expression") is not { } raw)
            return base.MapCatalogColumn(row, options, Diagnose);

        var type = row.Text("type_name");
        var sql = raw.Trim();
        var expression = (type, sql.ToUpperInvariant()) switch
        {
            ("TIMESTAMP(6)", "CURRENT_TIMESTAMP") => ColumnDefaultExpression.CurrentTimestamp(),
            ("TIMESTAMP(6)", "SYS_EXTRACT_UTC(SYSTIMESTAMP)") => ColumnDefaultExpression.CurrentUtcTimestamp(),
            ("DATE", "TRUNC(CURRENT_DATE)") => ColumnDefaultExpression.CurrentDate,
            ("CHAR" or "VARCHAR2", "SYS_CONTEXT('USERENV','CURRENT_USER')") => ColumnDefaultExpression.CurrentUser,
            ("CHAR" or "VARCHAR2", "SYS_CONTEXT('USERENV','SESSION_USER')") => ColumnDefaultExpression.SessionUser,
            ("CHAR" or "VARCHAR2", "SYS_CONTEXT('USERENV','CURRENT_SCHEMA')") => ColumnDefaultExpression.CurrentSchema,
            ("CHAR" or "VARCHAR2", "SYS_CONTEXT('USERENV','DB_NAME')") => ColumnDefaultExpression.CurrentDatabase,
            ("RAW", "SYS_GUID()") when row.Int("data_length") == 16
                => ColumnDefaultExpression.NewUuid(UuidRepresentation.Binary, UuidByteOrder.Network),
            ("NUMBER", _) => ReadSequenceDefault(sql),
            _ => null
        };
        if (expression is null)
            return base.MapCatalogColumn(row, options, Diagnose);

        var column = base.MapCatalogColumn(row.With(("default_expression", null)), options, Diagnose);
        return column with
        {
            Default = expression,
            Type = expression.Kind == ColumnDefaultKind.CurrentDate ? DbType.Date : column.Type
        };
    }

    private static readonly Regex SequenceDefault = new(
        "\\A\"(?<schema>[^\"]+)\"\\.\"(?<name>[^\"]+)\"\\.NEXTVAL\\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static ColumnDefaultExpression? ReadSequenceDefault(string sql)
    {
        var match = SequenceDefault.Match(sql);
        return match.Success
            ? ColumnDefaultExpression.NextSequenceValue(new(match.Groups["name"].Value, match.Groups["schema"].Value))
            : null;
    }

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
