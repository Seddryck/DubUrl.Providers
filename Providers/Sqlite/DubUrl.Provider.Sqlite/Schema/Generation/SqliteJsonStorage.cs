using DubUrl.Schema;

namespace DubUrl.Provider.Sqlite.Schema.Generation;

internal static class SqliteJsonStorage
{
    private static readonly Version TextBaseline = new(3, 38);
    private static readonly Version BinaryBaseline = new(3, 45);

    internal static string? Render(Version? version, StructuredStorage storage, string column)
        => storage switch
        {
            StructuredStorage.JsonText when version >= TextBaseline => $"TEXT CHECK ({Check(column, storage)})",
            StructuredStorage.Binary when version >= BinaryBaseline => $"BLOB CHECK ({Check(column, storage)})",
            _ => null
        };

    internal static string Check(string column, StructuredStorage storage)
    {
        var binary = storage == StructuredStorage.Binary;
        var kind = binary ? "blob" : "text";
        var flags = binary ? ", 8" : "";
        return $"{column} IS NULL OR (typeof({column}) = '{kind}' AND json_valid({column}{flags}))";
    }
}
