using DubUrl.Providers.QA.Contracts.Query;

namespace DubUrl.Provider.Spark.QA;

public abstract class SparkOdbcQueryContract : ProviderContract
{
    protected override string SelectFirstCustomerSql => SparkOdbcCustomerQueries.First;
    protected override string SelectCustomerByIdSql => string.Empty;
    protected override string SelectCustomerByPositionSql => SparkOdbcCustomerQueries.ById;
    protected override string SelectAllCustomersSql => SparkOdbcCustomerQueries.All;
    protected override string SelectYoungestCustomersSql => SparkOdbcCustomerQueries.All + " ORDER BY BirthDate DESC LIMIT $count$";
    protected override string SelectWhereCustomersTemplate => SparkOdbcCustomerQueries.Customers + """
        select $fields:{field | $field; format="identity"$}; separator=", "$ from $table; format="identity"$
        where $clauses:{clause | $clause.Field; format="identity"$ $clause.Operator$ $clause.Value; format="value"$}; separator=" and "$
        """;

    // System.Data.Odbc binds parameter markers by position.
    protected override bool SupportsNamedParameters => false;
}

internal static class SparkOdbcCustomerQueries
{
    // A statement-local relation exercises each independently opened connection without requiring a persistent QA table.
    internal const string Customers = """
        WITH Customer AS (
          SELECT * FROM VALUES
            (1, 'Nikola Tesla', DATE '1856-07-10'),
            (2, 'Albert Einstein', DATE '1879-03-14'),
            (3, 'John von Neumann', DATE '1903-12-28'),
            (4, 'Alan Turing', DATE '1912-06-23'),
            (5, 'Linus Torvalds', DATE '1969-12-28')
          AS fixture(CustomerId, FullName, BirthDate)
        )
        """ + "\n";
    internal const string First = Customers + "SELECT FullName FROM Customer WHERE CustomerId = 1";
    internal const string ById = Customers + "SELECT FullName FROM Customer WHERE CustomerId = ?";
    internal const string All = Customers + "SELECT CustomerId, FullName, BirthDate FROM Customer";
}
