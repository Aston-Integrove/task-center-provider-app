namespace Tcp.Api.Endpoints;

/// <summary>Extension point: modules registered in DI are mapped at startup (used by tests to add probe endpoints).</summary>
public interface IEndpointModule
{
    void Map(IEndpointRouteBuilder app);
}
