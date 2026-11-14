using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DubUrl.Discovery;
using DubUrl.Provider.MySql.Schema.Generation;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Schema;

namespace DubUrl.Provider.MySql.Discovery;

/// <summary>MariaDB's catalog quotes string literals and stores generated defaults as bare expressions.</summary>
public sealed class MariaDbMetadataReader : MySqlMetadataReader
{
    private readonly ConditionalWeakTable<MetadataColumn, StructuredColumnMetadata> structuredTypes = new();

    protected override IReadOnlySet<SchemaFeature> UnsupportedFeatures { get; } = new HashSet<SchemaFeature>
        { SchemaFeature.Indexes };

    protected override string IndexKeyExpression => "NULL";

    protected override string ChecksSql => """
        SELECT c.TABLE_NAME AS table_name,c.CONSTRAINT_SCHEMA AS database_name,c.CONSTRAINT_NAME AS check_name,
          c.CHECK_CLAUSE AS expression
        FROM information_schema.check_constraints c
        WHERE c.CONSTRAINT_SCHEMA NOT IN ('mysql','sys','performance_schema','information_schema')
        """;

    private const string StructuredChecksSql = """
        SELECT c.TABLE_NAME AS table_name,c.CONSTRAINT_SCHEMA AS database_name,c.CONSTRAINT_NAME AS check_name,
          c.CHECK_CLAUSE AS expression,col.COLUMN_NAME AS json_candidate
        FROM information_schema.check_constraints c
        LEFT JOIN information_schema.columns col
          ON BINARY col.TABLE_SCHEMA=BINARY c.CONSTRAINT_SCHEMA AND BINARY col.TABLE_NAME=BINARY c.TABLE_NAME
          AND col.COLUMN_NAME=c.CONSTRAINT_NAME AND c.LEVEL='Column'
          AND col.DATA_TYPE='longtext' AND col.COLLATION_NAME='utf8mb4_bin'
        WHERE c.CONSTRAINT_SCHEMA NOT IN ('mysql','sys','performance_schema','information_schema')
        """;

    protected override async Task<List<CatalogRow>> ReadCatalogRowsAsync(DbConnection connection, string sql,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        if (sql != ColumnsSql && sql != ChecksSql || !HasColumnCheckMetadata(connection.ServerVersion))
            return await base.ReadCatalogRowsAsync(connection, sql, options, cancellationToken).ConfigureAwait(false);

        if (sql == ChecksSql)
        {
            var checks = await base.ReadCatalogRowsAsync(connection, StructuredChecksSql, options, cancellationToken).ConfigureAwait(false);
            // The JSON alias recreates this check itself. Preserve all other checks.
            checks.RemoveAll(IsJsonCheck);
            return checks;
        }

        var rows = await base.ReadCatalogRowsAsync(connection, sql, options, cancellationToken).ConfigureAwait(false);
        if (!rows.Any(IsJsonCandidate))
            return rows;

        try
        {
            var checks = await base.ReadCatalogRowsAsync(connection, StructuredChecksSql, options, cancellationToken).ConfigureAwait(false);
            var json = checks.Where(IsJsonCheck)
                .Select(row => (Identity(row), row.Text("json_candidate"))).ToHashSet();
            return rows.Select(row => IsJsonCandidate(row) && json.Contains((Identity(row), row.Text("column_name")))
                ? row.With(("json_validated", true)) : row).ToList();
        }
        catch (DbException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return rows.Select(row => IsJsonCandidate(row) ? row.With(("json_inaccessible", true)) : row).ToList();
        }
    }

    private static bool HasColumnCheckMetadata(string? serverVersion)
    {
        // Some clients surface the compatibility prefix from the MariaDB handshake.
        if (serverVersion?.StartsWith("5.5.5-", StringComparison.Ordinal) == true)
            serverVersion = serverVersion[6..];
        var version = serverVersion?.Split('-', 2)[0];
        return Version.TryParse(version, out var parsed) && parsed >= MariaDbStructuredTypes.JsonBaseline;
    }

    private static bool IsJsonCandidate(CatalogRow row)
        => row.Text("type_name") == "longtext" && row.Text("collation_name") == "utf8mb4_bin";

    private static bool IsJsonCheck(CatalogRow row)
    {
        if (row.Text("json_candidate") is not { } column || row.Text("expression") is not { } expression)
            return false;
        var quoted = "`" + column.Replace("`", "``", StringComparison.Ordinal) + "`";
        return expression.Trim().Equals($"json_valid({quoted})", StringComparison.OrdinalIgnoreCase);
    }

    protected override MetadataColumn MapCatalogColumn(CatalogRow row, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        var column = base.MapCatalogColumn(row, options, diagnose);
        StructuredColumnMetadata? type = row.Text("type_name") switch
        {
            "vector" => MariaDbStructuredTypes.DecodeVector(row.Text("native_type_name") ?? ""),
            "json" => new(new(new JsonType(), StructuredStorage.JsonText), MetadataCoverage.Available),
            "longtext" when row.Bool("json_validated") == true => new(new(new JsonType(), StructuredStorage.JsonText), MetadataCoverage.Available),
            "longtext" when row.Bool("json_inaccessible") == true => new(null, MetadataCoverage.Inaccessible),
            _ => null
        };
        if (type is not null)
            structuredTypes.Add(column, type);
        return column;
    }

    protected override MetadataColumn MapColumn(CatalogRow row)
    {
        var column = base.MapColumn(row);
        return row.Text("type_name") switch
        {
            "vector" => column with { Type = DbType.Object, Length = null, Precision = null, Scale = null },
            "longtext" => column with { Type = DbType.String, Length = null, NativeType = "LONGTEXT", NativeDialect = typeof(MySqlDialect) },
            _ => column
        };
    }

    protected override StructuredColumnMetadata? GetStructuredType(MetadataColumn column)
        => structuredTypes.TryGetValue(column, out var type) ? type : null;

    protected override CatalogRow NormalizeDefault(CatalogRow row) => row;

    private static readonly Regex NamedSequence = new(
        @"\Anextval\((?<name>`(?:[^`]|``)+`\.`(?:[^`]|``)+`)\)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    protected override ColumnDefaultExpression? ReadDefault(CatalogRow row)
    {
        if (row.Text("type_name") is "tinyint" or "smallint" or "int" or "bigint" or "decimal"
            && row.Text("default_expression") is { } sql)
        {
            var match = NamedSequence.Match(sql);
            if (match.Success)
            {
                var name = MetadataNames.Parse(match.Groups["name"].Value);
                return ColumnDefaultExpression.NextSequenceValue(new(name.Name, database: name.Schema));
            }
        }

        // MySQL-only binary UUID defaults cannot be inferred from MariaDB expression text.
        var expression = RecognizeDefault(row);
        return expression?.Representation == UuidRepresentation.Binary ? null : expression;
    }
}
