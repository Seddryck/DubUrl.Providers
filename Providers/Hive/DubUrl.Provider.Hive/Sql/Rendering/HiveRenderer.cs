using DubUrl.Querying.Dialects.Formatters;
using DubUrl.Querying.Dialects.Renderers;

namespace DubUrl.Provider.Hive.Sql.Rendering;

internal sealed class HiveRenderer() : AnsiRenderer(new ValueFormatter(), new NullFormatter(), new HiveIdentifierFormatter())
{
    private sealed class HiveIdentifierFormatter : IdentifierQuotedFormatter
    {
        protected override string SurroundByQuotes(string value)
            => string.Join(".", value.Split('.').Select(part => "`" + part.Replace("`", "``", StringComparison.Ordinal) + "`"));
    }
}
