using System.Data;
using System.Text.RegularExpressions;
using System.Data.Common;
using DubUrl.Discovery;
using System.Runtime.CompilerServices;
using DubUrl.Provider.PostgreSql.Schema.Generation;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;

namespace DubUrl.Provider.PostgreSql.Discovery;

/// <summary>PostgreSQL catalog metadata. PostgreSQL-compatible protocols are not automatically opted in.</summary>
public sealed class PostgreSqlMetadataReader : CatalogSchemaMetadataReader, IUniqueConstraintMetadataReader, IStructuredColumnMetadataReader
{
    private readonly ConditionalWeakTable<MetadataKey, UniqueConstraintMetadata> nullPolicies = new();
    private readonly ConditionalWeakTable<MetadataColumn, StructuredColumnMetadata> structuredTypes = new();
    private readonly ConditionalWeakTable<CatalogRow, StructuredColumnMetadata> composites = new();

    public async Task<StructuredSchemaMetadata> ReadWithStructuredTypesAsync(DbConnection connection,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        var metadata = await ReadAsync(connection, options, cancellationToken).ConfigureAwait(false);
        var types = new Dictionary<StructuredColumnIdentity, StructuredColumnMetadata>();
        foreach (var table in metadata.Tables)
        foreach (var column in table.Columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (structuredTypes.TryGetValue(column, out var structured))
                types.Add(new(table.Identity, column.Name), structured);
            else if (column.NativeType == "jsonb")
                types.Add(new(table.Identity, column.Name), new(new(new JsonType(), StructuredStorage.Json), MetadataCoverage.Available));
        }
        return new(metadata, types);
    }

    public UniqueConstraintMetadata GetUniqueConstraintMetadata(MetadataTable table, MetadataKey key)
        => nullPolicies.TryGetValue(key, out var policy) ? policy : new(UniqueNullPolicy.NullsDistinct);

    protected override MetadataTable MapTable(CatalogRow row, List<CatalogRow> columnRows, List<CatalogRow> keyRows,
        List<CatalogRow> indexRows, List<CatalogRow> checkRows, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        if (checkRows.Count > 0)
        {
            var identity = Identity(row);
            var rankChecks = columnRows.Where(column => Identity(column) == identity
                    && column.Text("array_rank_check")?.StartsWith(PostgreSqlArrayType.RankCheckPrefix, StringComparison.Ordinal) == true
                    && DecodeArray(column).Coverage == MetadataCoverage.Available)
                .Select(column => column.Text("array_rank_check")).ToHashSet(StringComparer.Ordinal);
            // The array renderer recreates its rank check. Independent catalog checks remain visible.
            if (rankChecks.Count > 0)
                checkRows = checkRows.Where(check => Identity(check) != identity || !rankChecks.Contains(check.Text("check_name"))).ToList();
        }
        var table = base.MapTable(row, columnRows, keyRows, indexRows, checkRows, options, diagnose);
        if (table.UniqueConstraints.Count == 0)
            return table;

        var notDistinct = keyRows.Where(key => key.Text("schema_name") == table.Identity.Schema
            && key.Text("table_name") == table.Identity.Name && key.Text("kind") == "UNIQUE"
            && key.Bool("nulls_not_distinct") == true)
            .Select(key => key.Text("key_name")).ToHashSet(StringComparer.Ordinal);
        foreach (var key in table.UniqueConstraints.Where(key => notDistinct.Contains(key.Name)))
            nullPolicies.Add(key, new(UniqueNullPolicy.NullsNotDistinct));
        return table;
    }

    private static string ExcludeTimescaleInternals(string schema) => $"""
        AND (NOT (pg_catalog.starts_with({schema}, '_timescaledb_')
          OR {schema} IN ('timescaledb_information','timescaledb_experimental'))
          OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_extension WHERE extname='timescaledb'))
        """;

    protected override string TablesSql => $$"""
        SELECT n.nspname AS schema_name, c.relname AS table_name, obj_description(c.oid) AS comment
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE c.relkind IN ('r','p') AND n.nspname NOT IN ('pg_catalog','information_schema')
          AND n.nspname NOT LIKE 'pg_toast%' AND n.nspname NOT LIKE 'pg_temp_%'
          {{ExcludeTimescaleInternals("n.nspname")}}
        """;
    protected override string ColumnsSql => $$"""
        SELECT c.table_schema AS schema_name,c.table_name,c.column_name,c.ordinal_position AS ordinal,
          c.data_type AS type_name,c.udt_name AS native_type_name,c.is_nullable AS nullable,c.character_maximum_length AS length,
          c.numeric_precision AS precision,c.numeric_scale AS scale,c.column_default AS default_expression,
          CASE WHEN c.is_identity='YES' THEN c.identity_start END AS identity_seed,
          CASE WHEN c.is_identity='YES' THEN c.identity_increment END AS identity_increment,
          CASE WHEN c.is_generated='ALWAYS' THEN c.generation_expression END AS generation_expression,
          TRUE AS stored, col_description(pc.oid,a.attnum) AS comment,
          a.attndims AS array_rank,a.atttypmod AS array_type_modifier,e.typname AS array_element,en.nspname AS array_element_schema,
          a.attcollation=at.typcollation AS array_default_collation,shape.conname AS array_rank_check,
          at.typtype='c' AS is_composite,at.oid AS composite_oid,at.typname AS composite_name,an.nspname AS composite_schema,
          seq.sequence_name,seq.sequence_schema,seq.sequence_count,
          EXISTS (SELECT 1 FROM pg_catalog.pg_depend fd
            WHERE fd.classid='pg_catalog.pg_attrdef'::regclass AND fd.objid=ad.oid
              AND fd.refclassid='pg_catalog.pg_proc'::regclass) AS custom_default_function
        FROM information_schema.columns c
        JOIN pg_catalog.pg_namespace n ON n.nspname=c.table_schema
        JOIN pg_catalog.pg_class pc ON pc.relnamespace=n.oid AND pc.relname=c.table_name
        JOIN pg_catalog.pg_attribute a ON a.attrelid=pc.oid AND a.attname=c.column_name
        JOIN pg_catalog.pg_type at ON at.oid=a.atttypid
        JOIN pg_catalog.pg_namespace an ON an.oid=at.typnamespace
        LEFT JOIN pg_catalog.pg_type e ON e.oid=at.typelem AND e.typarray=at.oid
        LEFT JOIN pg_catalog.pg_namespace en ON en.oid=e.typnamespace
        LEFT JOIN LATERAL (
          SELECT ac.conname
          FROM pg_catalog.pg_constraint ac
          WHERE e.oid IS NOT NULL AND ac.conrelid=pc.oid AND ac.contype='c'
            AND ac.conkey=ARRAY[a.attnum] AND ac.convalidated AND NOT ac.connoinherit
            AND COALESCE((pg_catalog.to_jsonb(ac)->>'conenforced')::boolean, true)
            AND pg_catalog.pg_get_expr(ac.conbin, ac.conrelid) IN (
              pg_catalog.format('(%I(%I) = 1)', 'array_ndims', a.attname),
              pg_catalog.format('(%I.%I(%I) = 1)', 'pg_catalog', 'array_ndims', a.attname),
              pg_catalog.format('(%I(%I) OPERATOR(%I.=) 1)', 'array_ndims', a.attname, 'pg_catalog'),
              pg_catalog.format('(%I.%I(%I) OPERATOR(%I.=) 1)', 'pg_catalog', 'array_ndims', a.attname, 'pg_catalog'))
            AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d
              WHERE d.classid='pg_catalog.pg_constraint'::regclass AND d.objid=ac.oid
                AND d.refclassid IN ('pg_catalog.pg_proc'::regclass,'pg_catalog.pg_operator'::regclass))
          ORDER BY pg_catalog.starts_with(ac.conname, '{{PostgreSqlArrayType.RankCheckPrefix}}') DESC, ac.oid
          LIMIT 1
        ) shape ON TRUE
        LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid=pc.oid AND ad.adnum=a.attnum
        LEFT JOIN LATERAL (
          SELECT MIN(s.relname::text) AS sequence_name,MIN(sn.nspname::text) AS sequence_schema,COUNT(*) AS sequence_count
          FROM pg_catalog.pg_depend dep
          JOIN pg_catalog.pg_class s ON s.oid=dep.refobjid AND s.relkind='S'
          JOIN pg_catalog.pg_namespace sn ON sn.oid=s.relnamespace
          WHERE dep.classid='pg_catalog.pg_attrdef'::regclass AND dep.objid=ad.oid
            AND dep.refclassid='pg_catalog.pg_class'::regclass
            AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend own
              WHERE own.classid='pg_catalog.pg_class'::regclass AND own.objid=s.oid
                AND own.refclassid='pg_catalog.pg_class'::regclass
                AND own.refobjid=pc.oid AND own.refobjsubid=a.attnum AND own.deptype IN ('a','i'))
        ) seq ON TRUE
        WHERE c.table_schema NOT IN ('pg_catalog','information_schema') AND pc.relkind IN ('r','p')
          {{ExcludeTimescaleInternals("n.nspname")}}
        """;
    protected override string KeysSql => $$"""
        SELECT n.nspname AS schema_name,t.relname AS table_name,c.conname AS key_name,
          CASE c.contype WHEN 'p' THEN 'PRIMARY KEY' WHEN 'u' THEN 'UNIQUE' ELSE 'FOREIGN KEY' END AS kind,
          k.ordinality AS ordinal,a.attname AS column_name,tn.nspname AS target_schema,
          tt.relname AS target_table,ta.attname AS target_column,
          COALESCE((to_jsonb(i)->>'indnullsnotdistinct')::boolean, false) AS nulls_not_distinct
        FROM pg_catalog.pg_constraint c
        LEFT JOIN pg_catalog.pg_index i ON i.indexrelid=c.conindid AND c.contype='u'
        JOIN pg_catalog.pg_class t ON t.oid=c.conrelid JOIN pg_catalog.pg_namespace n ON n.oid=t.relnamespace
        CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY k(attnum,ordinality)
        JOIN pg_catalog.pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum
        LEFT JOIN pg_catalog.pg_class tt ON tt.oid=c.confrelid
        LEFT JOIN pg_catalog.pg_namespace tn ON tn.oid=tt.relnamespace
        LEFT JOIN pg_catalog.pg_attribute ta ON ta.attrelid=tt.oid AND ta.attnum=c.confkey[k.ordinality::int]
        WHERE c.contype IN ('p','u','f') AND n.nspname NOT IN ('pg_catalog','information_schema')
          {{ExcludeTimescaleInternals("n.nspname")}}
        """;
    protected override string IndexesSql => $$"""
        SELECT n.nspname AS schema_name,t.relname AS table_name,ix.relname AS index_name,
          i.indisunique AS unique_index,k.ordinality AS ordinal,a.attname AS column_name,
          (i.indoption[(k.ordinality-1)::int] & 1)=1 AS descending,
          (i.indpred IS NOT NULL OR i.indexprs IS NOT NULL OR i.indnatts<>i.indnkeyatts OR am.amname<>'btree'
           OR COALESCE((to_jsonb(i)->>'indnullsnotdistinct')::boolean, false)) AS unsupported
        FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class t ON t.oid=i.indrelid
        JOIN pg_catalog.pg_namespace n ON n.oid=t.relnamespace JOIN pg_catalog.pg_class ix ON ix.oid=i.indexrelid
        JOIN pg_catalog.pg_am am ON am.oid=ix.relam
        CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum,ordinality)
        LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum
        WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c WHERE c.conindid=i.indexrelid AND c.contype IN ('p','u'))
          AND n.nspname NOT IN ('pg_catalog','information_schema')
          {{ExcludeTimescaleInternals("n.nspname")}}
        """;
    protected override string ChecksSql => $$"""
        SELECT n.nspname AS schema_name,t.relname AS table_name,c.conname AS check_name,
          pg_get_expr(c.conbin,c.conrelid) AS expression
        FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class t ON t.oid=c.conrelid
        JOIN pg_catalog.pg_namespace n ON n.oid=t.relnamespace
        WHERE c.contype='c' AND n.nspname NOT IN ('pg_catalog','information_schema')
          {{ExcludeTimescaleInternals("n.nspname")}}
        """;
    protected override string ViewsSql => $$"""
        SELECT v.schemaname AS schema_name,v.viewname AS table_name,v.definition,
          c.column_name,c.ordinal_position AS ordinal,c.data_type AS type_name,c.is_nullable AS nullable
        FROM pg_catalog.pg_views v LEFT JOIN information_schema.columns c
          ON c.table_schema=v.schemaname AND c.table_name=v.viewname
        WHERE v.schemaname NOT IN ('pg_catalog','information_schema')
          {{ExcludeTimescaleInternals("v.schemaname")}}
        """;
    private static readonly Regex TemporalDefault = new(
        @"\A(?:LOCAL(?<local>TIME|TIMESTAMP)(?:\((?<precision>[0-6])\))?|\(CURRENT_TIMESTAMP(?:\((?<precision>[0-6])\))? AT TIME ZONE 'UTC'::text\))\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex NamedSequenceDefault = new(
        @"\A(?:pg_catalog\.)?nextval\('(?:[^']|'')*'::(?:pg_catalog\.)?regclass\)\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    protected override async Task<List<CatalogRow>> ReadCatalogRowsAsync(DbConnection connection, string sql,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        var rows = await base.ReadCatalogRowsAsync(connection, sql, options, cancellationToken).ConfigureAwait(false);
        if (sql != ColumnsSql)
            return rows;
        var named = rows.Where(row => row.Bool("is_composite") == true).ToArray();
        if (named.Length == 0)
            return rows;

        List<CatalogRow> attributes;
        try
        {
            attributes = await QueryAsync(connection, PostgreSqlCompositeCatalog.Sql, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in named)
                composites.Add(row, new(null, MetadataCoverage.Inaccessible));
            return rows;
        }

        var catalog = new PostgreSqlCompositeCatalog(attributes);
        foreach (var row in named)
        {
            cancellationToken.ThrowIfCancellationRequested();
            composites.Add(row, catalog.Decode(row.Long("composite_oid")));
        }
        return rows;
    }

    protected override MetadataColumn MapCatalogColumn(CatalogRow row, DiscoveryOptions options,
        Action<SchemaFeature, string> diagnose)
    {
        if (row.Text("type_name") == "ARRAY")
        {
            var array = base.MapCatalogColumn(row, options, diagnose);
            structuredTypes.Add(array, DecodeArray(row));
            return array;
        }
        if (composites.TryGetValue(row, out var composite))
        {
            var column = base.MapCatalogColumn(row, options, diagnose);
            structuredTypes.Add(column, composite);
            return column;
        }

        if (!options.Features.Contains(SchemaFeature.Defaults) || row.Text("default_expression") is not { } sql
            || row.Bool("custom_default_function") == true)
            return base.MapCatalogColumn(row, options, diagnose);

        var type = row.Text("type_name");
        var context = (type, sql.ToUpperInvariant()) switch
        {
            ("character varying" or "character", "CURRENT_USER") => ColumnDefaultExpression.CurrentUser,
            ("character varying" or "character", "SESSION_USER") => ColumnDefaultExpression.SessionUser,
            ("character varying" or "character", "CURRENT_SCHEMA") => ColumnDefaultExpression.CurrentSchema,
            ("character varying" or "character", "CURRENT_DATABASE()" or "PG_CATALOG.CURRENT_DATABASE()")
                => ColumnDefaultExpression.CurrentDatabase,
            ("uuid", "GEN_RANDOM_UUID()" or "PG_CATALOG.GEN_RANDOM_UUID()")
                => ColumnDefaultExpression.NewUuid(UuidRepresentation.Native),
            _ => null
        };
        var match = TemporalDefault.Match(sql);
        int? precision = match.Groups["precision"].Success ? match.Groups["precision"].Value[0] - '0' : null;
        if (context is null && type is "smallint" or "integer" or "bigint" or "numeric"
            && row.Int("sequence_count") == 1 && NamedSequenceDefault.IsMatch(sql))
        {
            context = ColumnDefaultExpression.NextSequenceValue(new(row.Text("sequence_name")!, row.Text("sequence_schema")));
        }
        var expression = context ?? ((type, match.Groups["local"].Value.ToUpperInvariant()) switch
        {
            ("timestamp without time zone", "TIMESTAMP") => ColumnDefaultExpression.CurrentTimestamp(precision),
            ("timestamp without time zone", "") when match.Success => ColumnDefaultExpression.CurrentUtcTimestamp(precision),
            ("time without time zone", "TIME") => ColumnDefaultExpression.CurrentTime(precision),
            ("date", "") when sql.Equals("CURRENT_DATE", StringComparison.OrdinalIgnoreCase) => ColumnDefaultExpression.CurrentDate,
            _ => null
        });
        return expression is null ? base.MapCatalogColumn(row, options, diagnose)
            : base.MapCatalogColumn(row.With(("default_expression", null)), options, diagnose) with { Default = expression };
    }

    private static StructuredColumnMetadata DecodeArray(CatalogRow row)
        => PostgreSqlArrayType.Decode(row.Int("array_rank"), row.Int("array_type_modifier"), row.Text("array_element_schema"),
            row.Text("array_element"), row.Bool("array_default_collation"), row.Text("array_rank_check") is not null);

    protected override MetadataColumn MapColumn(CatalogRow row)
    {
        var column = base.MapColumn(row);
        if (row.Text("type_name") == "ARRAY")
            return column with { Type = DbType.Object, Length = null, Precision = null, Scale = null };
        if (row.Bool("is_composite") == true && row.Text("composite_schema") is { } schema && row.Text("composite_name") is { } name)
            return column with { Type = DbType.Object, NativeType = PostgreSqlCompositeType.QualifiedName(schema, name),
                NativeDialect = typeof(DubUrl.Provider.PostgreSql.Sql.PgsqlDialect) };
        if (row.Text("native_type_name") is "json" or "jsonb" or "citext")
            return column with { Type = System.Data.DbType.String, NativeType = row.Text("native_type_name"), NativeDialect = typeof(DubUrl.Provider.PostgreSql.Sql.PgsqlDialect) };
        // Preserve VARCHAR for bounded columns: String maps to PostgreSQL TEXT,
        // and TEXT(length) is invalid when a discovered schema is rendered again.
        return column.Type is System.Data.DbType.AnsiString && column.Length is null
            ? column with { Type = System.Data.DbType.String } : column;
    }
}
