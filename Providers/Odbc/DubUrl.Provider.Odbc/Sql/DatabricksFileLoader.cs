using DubUrl.BulkCopy;

namespace DubUrl.Provider.Odbc.Sql;

internal sealed class DatabricksFileLoader : IFileLoader
{
    public void Load(ConnectionUrl connectionUrl, string tableName, string sourcePath, string fileFormat)
    {
        var format = fileFormat.ToUpperInvariant();
        if (format is not ("AVRO" or "BINARYFILE" or "CSV" or "JSON" or "ORC" or "PARQUET" or "TEXT"))
            throw new ArgumentException("The file format is unsupported by Databricks COPY INTO.", nameof(fileFormat));

        if (!IsVolumePath(sourcePath))
            throw new ArgumentException("Use a Unity Catalog path starting with /Volumes/<catalog>/<schema>/<volume>.", nameof(sourcePath));

        var names = tableName.Split('.');
        if (names.Length > 3 || names.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Use table, schema.table, or catalog.schema.table.", nameof(tableName));

        var dialect = connectionUrl.Dialect;
        var target = dialect.SchemaSqlRenderer.RenderObjectName(dialect,
            catalog: names.Length == 3 ? names[0] : null,
            database: null,
            schema: names.Length >= 2 ? names[^2] : null,
            name: names[^1]);
        var path = sourcePath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

        using var connection = connectionUrl.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"COPY INTO {target} FROM '{path}' FILEFORMAT = {format}";
        command.ExecuteNonQuery();
    }

    private static bool IsVolumePath(string path)
    {
        var segments = path.Split('/');
        return path.StartsWith("/Volumes/", StringComparison.Ordinal)
            && segments.Length >= 5
            && !string.IsNullOrWhiteSpace(segments[2])
            && !string.IsNullOrWhiteSpace(segments[3])
            && !string.IsNullOrWhiteSpace(segments[4])
            && !segments.Any(segment => segment is "." or "..")
            && !path.Any(char.IsControl);
    }
}
