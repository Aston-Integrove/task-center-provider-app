using Tcp.Domain.Identity;

namespace Tcp.TestSupport;

/// <summary>Configurable <see cref="IUserDirectory"/> for tests that should not depend on the SCIM store.</summary>
public sealed class InMemoryUserDirectory : IUserDirectory
{
    private readonly List<DirectoryUser> _users = [];
    private readonly Dictionary<string, HashSet<string>> _groups = new(StringComparer.Ordinal);

    public InMemoryUserDirectory Add(DirectoryUser user, params string[] groups)
    {
        _users.Add(user);
        _groups[user.GlobalUserId] = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
        return this;
    }

    public Task<DirectoryUser?> FindByGlobalUserIdAsync(string globalUserId, CancellationToken ct = default) =>
        Task.FromResult(_users.FirstOrDefault(u => u.GlobalUserId == globalUserId));

    public Task<DirectoryUser?> FindUniqueByEmailAsync(string email, CancellationToken ct = default)
    {
        var matches = _users.Where(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(matches.Count == 1 ? matches[0] : null);
    }

    public Task<bool> IsMemberOfAnyGroupAsync(string globalUserId, IReadOnlyCollection<string> groupNames, CancellationToken ct = default) =>
        Task.FromResult(_groups.TryGetValue(globalUserId, out var g) && groupNames.Any(g.Contains));

    public Task<IReadOnlyList<DirectoryUser>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DirectoryUser>>(_users.Where(u => u.Active).ToList());
}
