using DubUrl.Querying.Dialects.Formatters;
using DubUrl.Querying.Dialects.Renderers;

namespace DubUrl.Provider.Spark.Sql;

internal sealed class SparkSqlRenderer() : AnsiRenderer(new ValueFormatter(), new NullFormatter(), new SparkIdentifierFormatter());

internal sealed class SparkIdentifierFormatter : IIdentifierFormatter
{
    public string Format(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return "`" + value.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    public string Format(object value) => Format((string)value);
}
