using DubUrl.Schema;
using SchemaModel = DubUrl.Schema.Schema;
using System.Data.Common;
using System.Text;
using DubUrl.Conversion;
using NUnit.Framework;

namespace DubUrl.Providers.QA.Contracts.Schema;

public enum ExpectedOutcome
{
    Supported,
    RendererRejects,
    DatabaseRejects,
    NoDiscovery,
    NullDefaultNotPreserved,
    NoEngineComments,
    NotEnforced,
    Informational,
    Inapplicable
}
public sealed record FeatureExpectation(ExpectedOutcome Outcome, string Evidence, params string[] ErrorCodes)
{
    public string? ErrorFragment { get; init; }
    public static FeatureExpectation Supported { get; } = new(ExpectedOutcome.Supported, "Persisted and verified against the live database.");
}
public sealed record ColumnFact(string Name, string Type, int? Length, int? Precision, int? Scale, bool Nullable);
public sealed record IndexFact(string Name, string Column, int Ordinal, bool Unique, bool Descending);
public sealed record CommentFact(string? Table, IReadOnlyDictionary<string, string?> Columns);

/// <summary>One isolated public-API target; never includes its URL in diagnostics.</summary>
public sealed class PublicDatabase(ConnectionUrl url, Action cleanup) : IDisposable
{
    public ConnectionUrl Url { get; } = url;
    public void Dispose() => cleanup();
}

public static class QaSql
{
    public static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static List<object?[]> Rows(DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++) row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(row);
        }
        return rows;
    }

    public static IReadOnlyList<ColumnFact> ReaderColumns(DbConnection connection, DubUrl.Discovery.ISchemaMetadataReader reader, string? schema, string table, string? database = null)
    {
        var result = reader.ReadAsync(connection, new DubUrl.Discovery.DiscoveryOptions
        { Features = [DubUrl.Discovery.SchemaFeature.Columns] }).GetAwaiter().GetResult();
        return result.Tables.Single(t => t.Identity.Name == table && t.Identity.Schema == schema && (database is null || t.Identity.Database == database)).Columns
            .Select(c => new ColumnFact(c.Name, c.NativeType ?? c.Type.ToString()!, c.Length, c.Precision, c.Scale, c.Nullable ?? true)).ToArray();
    }

    public static CommentFact ReaderComments(DbConnection connection, DubUrl.Discovery.ISchemaMetadataReader reader, string? schema, string table, string? database = null)
    {
        var result = reader.ReadAsync(connection, new DubUrl.Discovery.DiscoveryOptions
        { Features = [DubUrl.Discovery.SchemaFeature.Columns, DubUrl.Discovery.SchemaFeature.Comments] }).GetAwaiter().GetResult();
        var actual = result.Tables.Single(t => t.Identity.Name == table && t.Identity.Schema == schema && (database is null || t.Identity.Database == database));
        return new(actual.Comment, actual.Columns.ToDictionary(c => c.Name, c => c.Comment));
    }

    public static IReadOnlyList<IndexFact> ReaderIndexes(DbConnection connection, DubUrl.Discovery.ISchemaMetadataReader reader, string? schema, string table, string? database = null)
    {
        var result = reader.ReadAsync(connection, new DubUrl.Discovery.DiscoveryOptions
        { Features = [DubUrl.Discovery.SchemaFeature.Indexes] }).GetAwaiter().GetResult();
        return result.Tables.Single(t => t.Identity.Name == table && t.Identity.Schema == schema && (database is null || t.Identity.Database == database)).Indexes
            .SelectMany(i => i.Columns.Select((c, n) => new IndexFact(i.Identity.Name, c.Name, n, i.Unique, c.Descending ?? false))).ToArray();
    }

    public static int? Int(object? value) => value is null ? null : Convert.ToInt32(value);
    public static string Text(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static PublicDatabase Public(string scheme, DbConnection connection)
    {
        using (connection)
        {
            // ConnectionUri.ToString() redacts by default. The unredacted value is
            // used only in memory to open the disposable database, never logged.
            var uri = new ConnectionStringConverter().Convert(scheme, connection.ConnectionString);
            return new(new ConnectionUrl(uri.ToString(redactPassword: false)), () => { });
        }
    }

    public static PublicDatabase FileDatabase(string scheme, DbProviderFactory factory, string invariant, Action? beforeCleanup = null)
    {
        DbProviderFactories.RegisterFactory(invariant, factory);
        var path = Path.Combine(Path.GetTempPath(), "schema-contract-" + Guid.NewGuid().ToString("N") + ".db");
        return new(new ConnectionUrl(scheme + ":///" + path.Replace((char)92, '/')), () =>
        {
            beforeCleanup?.Invoke();
            // All public-API connections have already been disposed.
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".wal")) File.Delete(path + ".wal");
        });
    }

    /// <summary>Split simple DDL for drivers accepting one SQL statement per command.
    /// Quotes, escaped quotes and comment literals remain intact; SQL Server batches
    /// deliberately bypass this helper.</summary>
    public static IEnumerable<string> Statements(string script)
    {
        var statement = new StringBuilder();
        char quote = '\0';
        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            if (quote != '\0')
            {
                statement.Append(c);
                if (c == quote)
                {
                    if (i + 1 < script.Length && script[i + 1] == quote) statement.Append(script[++i]);
                    else quote = '\0';
                }
            }
            else if (c == (char)39 || c == (char)34) { quote = c; statement.Append(c); }
            else if (c == ';')
            {
                if (!string.IsNullOrWhiteSpace(statement.ToString())) yield return statement.ToString().Trim();
                statement.Clear();
            }
            else
            {
                statement.Append(c);
            }
        }
        if (!string.IsNullOrWhiteSpace(statement.ToString())) yield return statement.ToString().Trim();
    }
}
