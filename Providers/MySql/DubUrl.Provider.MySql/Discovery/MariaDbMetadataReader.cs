using System.Text.RegularExpressions;
using DubUrl.Discovery;
using DubUrl.Schema;

namespace DubUrl.Provider.MySql.Discovery;

/// <summary>MariaDB's catalog quotes string literals and stores generated defaults as bare expressions.</summary>
public sealed class MariaDbMetadataReader : MySqlMetadataReader
{
    protected override IReadOnlySet<SchemaFeature> UnsupportedFeatures { get; } = new HashSet<SchemaFeature>
        { SchemaFeature.Indexes };

    protected override string ChecksSql => """
        SELECT c.TABLE_NAME AS table_name,c.CONSTRAINT_SCHEMA AS database_name,c.CONSTRAINT_NAME AS check_name,
          c.CHECK_CLAUSE AS expression
        FROM information_schema.check_constraints c
        WHERE c.CONSTRAINT_SCHEMA NOT IN ('mysql','sys','performance_schema','information_schema')
        """;

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
