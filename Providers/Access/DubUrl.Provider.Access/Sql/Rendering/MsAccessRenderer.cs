using DubUrl.Querying;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Dialects.Renderers;
using DubUrl.Querying.Dialects.Formatters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubUrl.Provider.Access.Sql.Rendering;

internal sealed class MsAccessRenderer : AnsiRenderer
{
    public MsAccessRenderer()
        : base(new ValueFormatter()
                    .With(new DateCrossSurroundingFormatter())
              , new NullFormatter()
              , new AccessIdentifierFormatter()) { }

    private sealed class AccessIdentifierFormatter : IdentifierQuotedFormatter
    {
        protected override string SurroundByQuotes(string value)
        {
            if (value.Contains('[') || value.Contains(']'))
                throw new InvalidIdentifierException("Access identifiers cannot contain square brackets.");

            return $"[{value}]";
        }
    }
}
