using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql";
}
