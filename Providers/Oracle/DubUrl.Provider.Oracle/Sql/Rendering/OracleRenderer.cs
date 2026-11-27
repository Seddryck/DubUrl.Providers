using DubUrl.Querying.Dialects.Formatters;
using DubUrl.Querying.Dialects.Renderers;

namespace DubUrl.Provider.Oracle.Sql.Rendering;

internal sealed class OracleRenderer()
    : AnsiRenderer(new ValueFormatter(), new NullFormatter(), new OracleIdentifierFormatter())
{
    private sealed class OracleIdentifierFormatter : IdentifierQuotedFormatter
    {
        protected override string SurroundByQuotes(string value)
        {
            if (value.Contains('"') || value.Contains('\0'))
                throw new InvalidIdentifierException("Oracle identifiers cannot contain double quotes or NUL characters.");

            return base.SurroundByQuotes(value);
        }
    }
}
