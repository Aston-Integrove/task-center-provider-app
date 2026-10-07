using Tcp.TestSupport;

namespace Tcp.ContractTests;

[CollectionDefinition(Name)]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql";
}
