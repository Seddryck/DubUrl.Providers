using System.Text.RegularExpressions;
using DubUrl.Discovery;
using System.Runtime.CompilerServices;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;

namespace DubUrl.Provider.PostgreSql.Discovery;

/// <summary>PostgreSQL catalog metadata. PostgreSQL-compatible protocols are not automatically opted in.</summary>
public sealed class PostgreSqlMetadataReader : CatalogSchemaMetadataReader, IUniqueConstraintMetadataReader
{
    private readonly ConditionalWeakTable<MetadataKey, UniqueConstraintMetadata> nullPolicies = new();

    public UniqueConstraintMetadata GetUniqueConstraintMetadata(MetadataTable table, MetadataKey key)
        => nullPolicies.TryGetValue(key, out var policy) ? policy : new(UniqueNullPolicy.NullsDistinct);

    protected override MetadataTable MapTable(CatalogRow row, List<CatalogRow> columnRows, List<CatalogRow> keyRows,
        List<CatalogRow> indexRows, List<CatalogRow> checkRows, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
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

    protected override string TablesSql => """
        SELECT n.nspname AS schema_name, c.relname AS table_name, obj_description(c.oid) AS comment
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE c.relkind IN ('r','p') AND n.nspname NOT IN ('pg_catalog','information_schema')
          AND n.nspname NOT LIKE 'pg_toast%' AND n.nspname NOT LIKE 'pg_temp_%'
        """;
    protected override string ColumnsSql => """
        SELECT c.table_schema AS schema_name,c.table_name,c.column_name,c.ordinal_position AS ordinal,
          c.data_type AS type_name,c.udt_name AS native_type_name,c.is_nullable AS nullable,c.character_maximum_length AS length,
          c.numeric_precision AS precision,c.numeric_scale AS scale,c.column_default AS default_expression,
          CASE WHEN c.is_identity='YES' THEN c.identity_start END AS identity_seed,
          CASE WHEN c.is_identity='YES' THEN c.identity_increment END AS identity_increment,
          CASE WHEN c.is_generated='ALWAYS' THEN c.generation_expression END AS generation_expression,
          TRUE AS stored, col_description(pc.oid,a.attnum) AS comment,
          seq.sequence_name,seq.sequence_schema,seq.sequence_count,
          EXISTS (SELECT 1 FROM pg_catalog.pg_depend fd
            WHERE fd.classid='pg_catalog.pg_attrdef'::regclass AND fd.objid=ad.oid
              AND fd.refclassid='pg_catalog.pg_proc'::regclass) AS custom_default_function
        FROM information_schema.columns c
        JOIN pg_catalog.pg_namespace n ON n.nspname=c.table_schema
        JOIN pg_catalog.pg_class pc ON pc.relnamespace=n.oid AND pc.relname=c.table_name
        JOIN pg_catalog.pg_attribute a ON a.attrelid=pc.oid AND a.attname=c.column_name
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
        """;
    protected override string KeysSql => """
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
        """;
    protected override string IndexesSql => """
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
        """;
    protected override string ChecksSql => """
        SELECT n.nspname AS schema_name,t.relname AS table_name,c.conname AS check_name,
          pg_get_expr(c.conbin,c.conrelid) AS expression
        FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class t ON t.oid=c.conrelid
        JOIN pg_catalog.pg_namespace n ON n.oid=t.relnamespace
        WHERE c.contype='c' AND n.nspname NOT IN ('pg_catalog','information_schema')
        """;
    protected override string ViewsSql => """
        SELECT v.schemaname AS schema_name,v.viewname AS table_name,v.definition,
          c.column_name,c.ordinal_position AS ordinal,c.data_type AS type_name,c.is_nullable AS nullable
        FROM pg_catalog.pg_views v LEFT JOIN information_schema.columns c
          ON c.table_schema=v.schemaname AND c.table_name=v.viewname
        WHERE v.schemaname NOT IN ('pg_catalog','information_schema')
        """;
    private static readonly Regex TemporalDefault = new(
        @"\A(?:LOCAL(?<local>TIME|TIMESTAMP)(?:\((?<precision>[0-6])\))?|\(CURRENT_TIMESTAMP(?:\((?<precision>[0-6])\))? AT TIME ZONE 'UTC'::text\))\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex NamedSequenceDefault = new(
        @"\A(?:pg_catalog\.)?nextval\('(?:[^']|'')*'::(?:pg_catalog\.)?regclass\)\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    protected override MetadataColumn MapCatalogColumn(CatalogRow row, DiscoveryOptions options,
        Action<SchemaFeature, string> diagnose)
    {
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

    protected override MetadataColumn MapColumn(CatalogRow row)
    {
        var column = base.MapColumn(row);
        if (row.Text("native_type_name") is "json" or "jsonb" or "citext")
            return column with { Type = System.Data.DbType.String, NativeType = row.Text("native_type_name"), NativeDialect = typeof(DubUrl.Provider.PostgreSql.Sql.PgsqlDialect) };
        // Preserve VARCHAR for bounded columns: String maps to PostgreSQL TEXT,
        // and TEXT(length) is invalid when a discovered schema is rendered again.
        return column.Type is System.Data.DbType.AnsiString && column.Length is null
            ? column with { Type = System.Data.DbType.String } : column;
    }
}
