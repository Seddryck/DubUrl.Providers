using System.Data.Common;
using System.Globalization;
using DubUrl.Discovery;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using DbType = System.Data.DbType;

namespace DubUrl.Providers.QA.Contracts.Schema;

public static class TemporalDefaultChecks
{
    public static async Task VerifyAsync(DbConnection connection, IDialect dialect, ISchemaMetadataReader reader,
        DatabaseObjectName table, TimeSpan sessionOffset, DbType timestampType = DbType.DateTime)
    {
        Column WithDefault(string name, DbType type, ColumnDefaultExpression expression)
            => new(name, type, constraints: [new NullableConstraint()], defaultExpression: expression);

        var columns = new[]
        {
            new Column("id", DbType.Int32),
            WithDefault("local_stamp", timestampType, ColumnDefaultExpression.CurrentTimestamp()),
            WithDefault("local_date", DbType.Date, ColumnDefaultExpression.CurrentDate),
            WithDefault("local_time", DbType.Time, ColumnDefaultExpression.CurrentTime()),
            WithDefault("utc_stamp", timestampType, ColumnDefaultExpression.CurrentUtcTimestamp()),
            new Column("literal_value", DbType.Int32, defaultValue: 23)
        };
        var schema = new DubUrl.Schema.Schema([new Table(table, columns, [])]);
        var sqlName = UniqueNullPolicyChecks.SqlName(dialect, table);
        var renderer = new SchemaScriptRenderer(dialect);
        foreach (var command in renderer.Plan(schema))
            QaSql.Execute(connection, command.CommandText);

        try
        {
            // Compare INSERT-time clocks across separate statements and, on PostgreSQL, transactions.
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            var before = DateTime.UtcNow;
            QaSql.Execute(connection, $"INSERT INTO {sqlName} (id) VALUES (1)");
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            QaSql.Execute(connection, $"INSERT INTO {sqlName} (id) VALUES (2)");
            QaSql.Execute(connection, $"INSERT INTO {sqlName} VALUES (3, '2001-02-03 04:05:06', '2001-02-03', '04:05:06', '2001-02-03 04:05:06', 42)");
            QaSql.Execute(connection, $"INSERT INTO {sqlName} VALUES (4, NULL, NULL, NULL, NULL, 0)");

            using (var select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT * FROM {sqlName} ORDER BY id";
                using var rows = select.ExecuteReader();
                Assert.That(rows.Read(), Is.True);
                var first = Timestamp(rows.GetValue(4));
                Assert.That(first, Is.InRange(before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1)));
                Assert.That(Timestamp(rows.GetValue(1)) - first, Is.EqualTo(sessionOffset).Within(TimeSpan.FromSeconds(1)));
                Assert.That(Timestamp(rows.GetValue(2)).Date, Is.EqualTo(first.Add(sessionOffset).Date));
                var difference = (TimeOfDay(rows.GetValue(3)) - first.Add(sessionOffset).TimeOfDay).Duration();
                var tolerance = TimeSpan.FromSeconds(1);
                Assert.That(difference <= tolerance || difference >= TimeSpan.FromDays(1) - tolerance, Is.True);
                Assert.That(rows.GetInt32(5), Is.EqualTo(23));
                Assert.That(rows.Read(), Is.True);
                Assert.That(Timestamp(rows.GetValue(4)), Is.GreaterThan(first));
                Assert.That(rows.Read(), Is.True);
                var supplied = new DateTime(2001, 2, 3, 4, 5, 6);
                Assert.That(Timestamp(rows.GetValue(1)), Is.EqualTo(supplied));
                Assert.That(Timestamp(rows.GetValue(2)), Is.EqualTo(supplied.Date));
                Assert.That(TimeOfDay(rows.GetValue(3)), Is.EqualTo(supplied.TimeOfDay));
                Assert.That(Timestamp(rows.GetValue(4)), Is.EqualTo(supplied));
                Assert.That(rows.GetInt32(5), Is.EqualTo(42));
                Assert.That(rows.Read(), Is.True);
                Assert.That(Enumerable.Range(1, 4).All(rows.IsDBNull), Is.True);
                Assert.That(rows.Read(), Is.False);
            }

            var inspected = await new SchemaInspector().InspectAsync(connection, reader, new()
            {
                Tables = [new(table.Name, table.Schema, table.Database)],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Defaults]
            });
            Assert.That(inspected.IsComplete, Is.True, string.Join(", ", inspected.Diagnostics.Select(item => item.Message)));
            var discovered = inspected.Schema!.Tables.Values.Single();
            foreach (var expected in columns.Where(column => column.DefaultExpression is not null))
                Assert.That(discovered.Columns[expected.Name].DefaultExpression?.Kind, Is.EqualTo(expected.DefaultExpression!.Kind), expected.Name);
            Assert.That(discovered.Columns["literal_value"].DefaultValue, Is.EqualTo(23));
        }
        finally
        {
            QaSql.Execute(connection, $"DROP TABLE {sqlName}");
        }
    }

    private static TimeSpan TimeOfDay(object value) => value switch
    {
        TimeOnly time => time.ToTimeSpan(),
        TimeSpan time => time,
        string text => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("Expected a time-of-day representation.")
    };

    private static DateTime Timestamp(object value) => value switch
    {
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        DateTime timestamp => timestamp,
        _ => DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)
    };
}
