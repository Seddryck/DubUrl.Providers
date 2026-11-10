using DubUrl.Querying;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Dialects.Renderers;
using DubUrl.Querying.Dialects.Formatters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubUrl.Provider.MySql.Sql.Rendering;

internal class MySqlRenderer : AnsiRenderer
{
    public MySqlRenderer()
        : base(new ValueFormatter()
                    .With(new FunctionFormatter<TimeSpan>("TIME", new IntervalAsTimeFormatter()))
              , new NullFormatter()
              , new MySqlIdentifierFormatter()) { }

    protected MySqlRenderer(BaseValueFormatter value)
        : base(value, new NullFormatter(), new MySqlIdentifierFormatter()) { }
}

internal sealed class MySqlIdentifierFormatter : IIdentifierFormatter
{
    public string Format(string value) => "`" + value.Replace("`", "``", StringComparison.Ordinal) + "`";

    public string Format(object value) => Format((string)value);
}
