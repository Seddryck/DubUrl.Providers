using DubUrl.Querying.Dialects.Formatters;
using DubUrl.Querying.Dialects.Renderers;

namespace DubUrl.Provider.Spark.Sql;

internal sealed class SparkSqlRenderer() : AnsiRenderer(
    new ValueFormatter().With<string>(new SparkStringFormatter()).With<char>(new SparkStringFormatter())
        .With(new SparkBinaryFormatter()).With(new PrefixFormatter<DateTimeOffset>("TIMESTAMP_LTZ", new TimestampTimeZoneFormatter())),
    new NullFormatter(), new SparkIdentifierFormatter());

internal sealed class SparkStringFormatter : IValueFormatter<string>, IValueFormatter<char>
{
    internal static string Quote(string value) => "'" + value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal)
        .Replace("\0", "\\0", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal) + "'";

    public string Format(string value) => Quote(value);
    public string Format(char value) => Quote(value.ToString());
    public string Format(object value) => value is char character ? Format(character) : Format((string)value);
}

internal sealed class SparkBinaryFormatter : IValueFormatter<byte[]>
{
    public string Format(byte[] value) => $"X'{Convert.ToHexString(value)}'";
    public string Format(object value) => Format((byte[])value);
}

internal sealed class SparkIdentifierFormatter : IIdentifierFormatter
{
    public string Format(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return "`" + value.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    public string Format(object value) => Format((string)value);
}
