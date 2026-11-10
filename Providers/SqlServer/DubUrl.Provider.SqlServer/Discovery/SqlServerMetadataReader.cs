using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DubUrl.Discovery;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;

namespace DubUrl.Provider.SqlServer.Discovery;

public sealed class SqlServerMetadataReader : CatalogSchemaMetadataReader, IUniqueConstraintMetadataReader
{
    private readonly ConditionalWeakTable<MetadataKey, UniqueConstraintMetadata> nullPolicies = new();

    public UniqueConstraintMetadata GetUniqueConstraintMetadata(MetadataTable table, MetadataKey key)
        => nullPolicies.TryGetValue(key, out var policy) ? policy : new(UniqueNullPolicy.NullsNotDistinct);

    private const string NotNullTerm = @"\(*\s*\[(?<column>(?:[^\]]|\]\])+)\]\s+IS\s+NOT\s+NULL\s*\)*";
    private static readonly Regex AllNotNullFilter = new(
        @"\A\s*" + NotNullTerm + @"(?:\s+AND\s+" + NotNullTerm + @")*\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    protected override MetadataTable MapTable(CatalogRow row, List<CatalogRow> columnRows, List<CatalogRow> keyRows,
        List<CatalogRow> indexRows, List<CatalogRow> checkRows, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        if (!options.Features.Contains(SchemaFeature.UniqueConstraints))
            return base.MapTable(row, columnRows, keyRows, indexRows, checkRows, options, diagnose);

        bool SameTable(CatalogRow part) => part.Text("schema_name") == row.Text("schema_name")
            && part.Text("table_name") == row.Text("table_name");

        // A filtered index is a portable UNIQUE declaration only if its filter covers exactly the all-nonnull tuples.
        var promoted = keyRows.Where(key => SameTable(key) && key.Text("kind") == "FILTERED UNIQUE")
            .GroupBy(key => key.Text("key_name"), StringComparer.Ordinal)
            .Where(MatchesAllNotNullFilter).ToArray();
        if (promoted.Length == 0)
            return base.MapTable(row, columnRows, keyRows, indexRows, checkRows, options, diagnose);

        var names = promoted.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var keys = keyRows.Concat(promoted.SelectMany(group => group.Select(key => key.With(("kind", "UNIQUE"))))).ToList();
        var indexes = indexRows.Where(index => !SameTable(index) || !names.Contains(index.Text("index_name"))).ToList();
        var table = base.MapTable(row, columnRows, keys, indexes, checkRows, options, diagnose);
        foreach (var key in table.UniqueConstraints.Where(key => names.Contains(key.Name)))
            nullPolicies.Add(key, new(UniqueNullPolicy.NullsDistinct));
        return table;
    }

    private static bool MatchesAllNotNullFilter(IGrouping<string?, CatalogRow> key)
    {
        var filter = key.First().Text("filter_definition");
        if (filter is null)
            return false;
        var match = AllNotNullFilter.Match(filter);
        return match.Success && match.Groups["column"].Captures
            .Select(capture => capture.Value.Replace("]]", "]", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal).SetEquals(key.Select(part => part.Text("column_name")!));
    }

    protected override MetadataColumn MapCatalogColumn(CatalogRow row, DiscoveryOptions options,
        Action<SchemaFeature, string> diagnose)
    {
        if (!options.Features.Contains(SchemaFeature.Defaults))
            return base.MapCatalogColumn(row, options, diagnose);

        var type = row.Text("type_name");
        var sql = row.Text("default_expression")?.Trim().ToUpperInvariant();
        var expression = (type, sql) switch
        {
            ("nvarchar" or "nchar", "(USER_NAME())" or "(CURRENT_USER)") => ColumnDefaultExpression.CurrentUser,
            ("nvarchar" or "nchar", "(ORIGINAL_LOGIN())") => ColumnDefaultExpression.SessionUser,
            ("nvarchar" or "nchar", "(SCHEMA_NAME())") => ColumnDefaultExpression.CurrentSchema,
            ("nvarchar" or "nchar", "(DB_NAME())") => ColumnDefaultExpression.CurrentDatabase,
            ("uniqueidentifier", "(NEWID())") => ColumnDefaultExpression.NewUuid(UuidRepresentation.Native),
            ("datetime2", "(SYSDATETIME())") when row.Int("scale") == 7 => ColumnDefaultExpression.CurrentTimestamp(),
            ("datetimeoffset", "(SYSDATETIMEOFFSET())") when row.Int("scale") == 7 => ColumnDefaultExpression.CurrentTimestamp(),
            ("datetime2", "(SYSUTCDATETIME())") when row.Int("scale") == 7 => ColumnDefaultExpression.CurrentUtcTimestamp(),
            ("datetimeoffset", "(TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00'))") when row.Int("scale") == 7
                => ColumnDefaultExpression.CurrentUtcTimestamp(),
            ("date", "(CONVERT([DATE],SYSDATETIME()))") => ColumnDefaultExpression.CurrentDate,
            ("time", "(CONVERT([TIME],SYSDATETIME()))") when row.Int("scale") == 7 => ColumnDefaultExpression.CurrentTime(),
            _ => null
        };
        if (expression is null && type is "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric"
            && row.Int("sequence_count") == 1 && row.Text("sequence_name") is { } name && row.Text("sequence_schema") is { } schema)
        {
            static string Quote(string part) => "[" + part.Replace("]", "]]", StringComparison.Ordinal) + "]";
            var qualified = $"{Quote(schema)}.{Quote(name)}";
            var database = row.Text("sequence_database");
            if (string.Equals(sql, $"(NEXT VALUE FOR {qualified})", StringComparison.OrdinalIgnoreCase))
                expression = ColumnDefaultExpression.NextSequenceValue(new(name, schema));
            else if (database is not null && string.Equals(sql, $"(NEXT VALUE FOR {Quote(database)}.{qualified})", StringComparison.OrdinalIgnoreCase))
                expression = ColumnDefaultExpression.NextSequenceValue(new(name, schema, database));
        }
        return expression is null ? base.MapCatalogColumn(row, options, diagnose)
            : base.MapCatalogColumn(row.With(("default_expression", null)), options, diagnose) with { Default = expression };
    }

    protected override MetadataColumn MapColumn(CatalogRow row)
    {
        var column = base.MapColumn(row);
        return row.Text("type_name") is "timestamp" or "rowversion" ? column with { Type = System.Data.DbType.Binary, Length = 8 } : column;
    }

    protected override string TablesSql => """
        SELECT s.name AS schema_name,t.name AS table_name,CONVERT(nvarchar(max),ep.value) AS comment
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
        LEFT JOIN sys.extended_properties ep ON ep.major_id=t.object_id AND ep.minor_id=0 AND ep.name='MS_Description' AND ep.class=1
        WHERE t.is_ms_shipped=0
        """;
    protected override string ColumnsSql => """
        SELECT s.name AS schema_name,t.name AS table_name,c.name AS column_name,c.column_id AS ordinal,
          ty.name AS type_name,c.is_nullable AS nullable,
          CASE WHEN ty.name IN ('nvarchar','nchar') AND c.max_length>0 THEN c.max_length/2 ELSE c.max_length END AS length,
          c.precision,c.scale,dc.definition AS default_expression,
          CONVERT(bigint,ic.seed_value) AS identity_seed,CONVERT(bigint,ic.increment_value) AS identity_increment,
          cc.definition AS generation_expression,cc.is_persisted AS stored,CONVERT(nvarchar(max),ep.value) AS comment,
          seq.sequence_name,seq.sequence_schema,seq.sequence_count,DB_NAME() AS sequence_database
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
        JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id=t.object_id AND ic.column_id=c.column_id
        OUTER APPLY (
          SELECT MIN(q.name) AS sequence_name,MIN(SCHEMA_NAME(q.schema_id)) AS sequence_schema,COUNT(*) AS sequence_count
          FROM sys.sql_expression_dependencies d JOIN sys.objects q ON q.object_id=d.referenced_id AND q.type='SO'
          WHERE d.referencing_id=dc.object_id
        ) seq
        LEFT JOIN sys.computed_columns cc ON cc.object_id=t.object_id AND cc.column_id=c.column_id
        LEFT JOIN sys.extended_properties ep ON ep.major_id=t.object_id AND ep.minor_id=c.column_id AND ep.name='MS_Description' AND ep.class=1
        WHERE t.is_ms_shipped=0
        """;
    protected override string KeysSql => """
        SELECT s.name AS schema_name,t.name AS table_name,k.name AS key_name,
          CASE k.type WHEN 'PK' THEN 'PRIMARY KEY' ELSE 'UNIQUE' END AS kind,
          ic.key_ordinal AS ordinal,c.name AS column_name,
          CAST(NULL AS nvarchar(128)) AS target_schema,CAST(NULL AS nvarchar(128)) AS target_table,CAST(NULL AS nvarchar(128)) AS target_column,
          CAST(NULL AS nvarchar(max)) AS filter_definition
        FROM sys.key_constraints k JOIN sys.tables t ON t.object_id=k.parent_object_id
        JOIN sys.schemas s ON s.schema_id=t.schema_id
        JOIN sys.index_columns ic ON ic.object_id=t.object_id AND ic.index_id=k.unique_index_id
        JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=ic.column_id
        WHERE t.is_ms_shipped=0 AND ic.key_ordinal>0
        UNION ALL
        SELECT s.name,t.name,f.name,'FOREIGN KEY',fc.constraint_column_id,c.name,ts.name,tt.name,tc.name,NULL
        FROM sys.foreign_keys f JOIN sys.tables t ON t.object_id=f.parent_object_id
        JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id
        JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=fc.parent_column_id
        JOIN sys.tables tt ON tt.object_id=f.referenced_object_id JOIN sys.schemas ts ON ts.schema_id=tt.schema_id
        JOIN sys.columns tc ON tc.object_id=tt.object_id AND tc.column_id=fc.referenced_column_id
        WHERE t.is_ms_shipped=0
        UNION ALL
        SELECT s.name,t.name,i.name,'FILTERED UNIQUE',ic.key_ordinal,c.name,NULL,NULL,NULL,i.filter_definition
        FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id
        JOIN sys.schemas s ON s.schema_id=t.schema_id
        JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
        JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=ic.column_id
        WHERE t.is_ms_shipped=0 AND i.type=2 AND i.is_unique=1 AND i.has_filter=1
          AND i.is_disabled=0 AND i.is_hypothetical=0 AND ic.key_ordinal>0
          AND NOT EXISTS (SELECT 1 FROM sys.index_columns x WHERE x.object_id=i.object_id AND x.index_id=i.index_id
            AND (x.is_included_column=1 OR x.is_descending_key=1 OR x.partition_ordinal>0))
        """;
    protected override string IndexesSql => """
        SELECT s.name AS schema_name,t.name AS table_name,i.name AS index_name,i.is_unique AS unique_index,
          ic.key_ordinal AS ordinal,c.name AS column_name,ic.is_descending_key AS descending,
          CASE WHEN i.has_filter=1 OR i.type NOT IN (1,2) OR EXISTS
            (SELECT 1 FROM sys.index_columns x WHERE x.object_id=i.object_id AND x.index_id=i.index_id AND x.is_included_column=1)
            THEN 1 ELSE 0 END AS unsupported
        FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
        JOIN sys.index_columns ic ON ic.object_id=t.object_id AND ic.index_id=i.index_id
        JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=ic.column_id
        WHERE t.is_ms_shipped=0 AND i.is_primary_key=0 AND i.is_unique_constraint=0 AND i.index_id>0
        """;
    protected override string ChecksSql => """
        SELECT s.name AS schema_name,t.name AS table_name,c.name AS check_name,c.definition AS expression
        FROM sys.check_constraints c JOIN sys.tables t ON t.object_id=c.parent_object_id
        JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0
        """;
    protected override string ViewsSql => """
        SELECT s.name AS schema_name,v.name AS table_name,m.definition,c.name AS column_name,
          c.column_id AS ordinal,ty.name AS type_name,c.is_nullable AS nullable
        FROM sys.views v JOIN sys.schemas s ON s.schema_id=v.schema_id
        LEFT JOIN sys.sql_modules m ON m.object_id=v.object_id
        LEFT JOIN sys.columns c ON c.object_id=v.object_id LEFT JOIN sys.types ty ON ty.user_type_id=c.user_type_id
        WHERE v.is_ms_shipped=0
        """;
}
