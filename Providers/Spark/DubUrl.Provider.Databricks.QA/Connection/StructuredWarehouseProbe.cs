using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Provider.Spark.QA;
using NUnit.Framework;

namespace DubUrl.Provider.Databricks.QA.Connection;

public sealed class StructuredWarehouseProbe
{
    [Test]
    public void Catalog()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        var url = new ConnectionUrl(DatabricksWarehouse.Url).WithAuthentication(DatabricksWarehouse.Authentication);
        SparkSchemaQa.WithScratch(url, (connection, catalog, schema) =>
        {
            var table = $"`{catalog}`.`{schema}`.`structured_probe`";
            void Run(string sql)
            {
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    using var rows = command.ExecuteReader();
                    while (rows.Read())
                        TestContext.Out.WriteLine(string.Join(" | ", Enumerable.Range(0, rows.FieldCount).Select(i => rows.GetValue(i))));
                    TestContext.Out.WriteLine("PASS " + sql);
                }
                catch (DbException e) { TestContext.Out.WriteLine("REJECT " + sql + " " + e.Message); }
            }

            Run($"CREATE TABLE {table} (id INT, v ARRAY<FLOAT>, m MAP<STRING,ARRAY<INT>>, s STRUCT<`x y`: INT NOT NULL, `b``c`: STRING> NOT NULL, j VARIANT) USING DELTA TBLPROPERTIES ('delta.columnMapping.mode'='name')");
            Run($"DESCRIBE TABLE EXTENDED {table} AS JSON");
            Run($"SHOW CREATE TABLE {table}");
            Run($"SELECT column_name, full_data_type, is_nullable FROM `{catalog}`.information_schema.columns WHERE table_schema='{schema}' AND table_name='structured_probe' ORDER BY ordinal_position");
            Run($"ALTER TABLE {table} ADD CONSTRAINT vector_length CHECK (v IS NULL OR size(v) = 3)");
            Run($"ALTER TABLE {table} ADD CONSTRAINT vector_elements CHECK (v IS NULL OR size(array_compact(v)) = size(v))");
            Run($"SHOW TBLPROPERTIES {table}");
            Run($"INSERT INTO {table} VALUES (1, array(1F, 2F, 3F), NULL, named_struct('x y', 1, 'b`c', 'ok'), parse_json('null'))");
            Run($"INSERT INTO {table} VALUES (2, array(1F, 2F), NULL, named_struct('x y', 1, 'b`c', 'ok'), NULL)");
            Run($"INSERT INTO {table} VALUES (3, array(1F, NULL, 3F), NULL, named_struct('x y', 1, 'b`c', 'ok'), NULL)");
            Run($"INSERT INTO {table} VALUES (4, NULL, NULL, named_struct('x y', CAST(NULL AS INT), 'b`c', 'ok'), NULL)");
            Run($"SELECT id, size(v), to_json(j), is_variant_null(j) FROM {table}");
        });
    }
}
