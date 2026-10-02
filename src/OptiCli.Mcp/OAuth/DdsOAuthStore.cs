using EPiServer.Data;
using EPiServer.Data.Dynamic;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// The store in the CMS database, through the Dynamic Data Store: every instance of a load-balanced site sees the same
/// clients, codes and grants, and a site needs no table or migration of its own.
/// </summary>
/// <remarks>
/// <para>
/// The Dynamic Data Store is synchronous; the async signatures leave room for a store that isn't. It has no conditional
/// update or transaction of its own, so what must happen once is a read and a write under a lock, which serialises it
/// within the instance only:
/// </para>
/// <list type="bullet">
/// <item>Taking a code is a find then a delete. Two instances racing for the same code within milliseconds could both
/// get it, which PKCE still guards (only the client that started the sign-in has the verifier).</item>
/// <item>A refresh re-reads the grant and compares its refresh token before it saves. Two instances given the same
/// refresh token within milliseconds could both rotate it; the later save wins, so one of the two new refresh tokens
/// never works (the first refresh after that fails, and the client signs in again), while both access tokens last
/// their hour. A revocation on one instance in the same milliseconds as a refresh on another could be undone by the
/// refresh's save. On one instance, and so on a site that isn't load-balanced, neither can happen.</item>
/// </list>
/// </remarks>
internal sealed class DdsOAuthStore(DynamicDataStoreFactory stores) : IOAuthStore
{
    private static readonly object CodeLock = new();

    public Task<RegisteredClient?> FindClientAsync(string clientId, CancellationToken cancellationToken) =>
        Task.FromResult(Store<McpClientData>().Find<McpClientData>(nameof(McpClientData.ClientId), clientId).FirstOrDefault()?.ToRecord());

    public Task AddClientAsync(RegisteredClient client, CancellationToken cancellationToken)
    {
        Store<McpClientData>().Save(McpClientData.From(client));
        return Task.CompletedTask;
    }

    public Task AddCodeAsync(AuthorizationCode code, CancellationToken cancellationToken)
    {
        Store<McpCodeData>().Save(McpCodeData.From(code));
        return Task.CompletedTask;
    }

    public Task<AuthorizationCode?> TakeCodeAsync(string codeHash, CancellationToken cancellationToken)
    {
        lock (CodeLock)
        {
            var store = Store<McpCodeData>();
            var found = store.Find<McpCodeData>(nameof(McpCodeData.CodeHash), codeHash).FirstOrDefault();
            if (found is not null)
            {
                store.Delete(found.Id);
            }
            return Task.FromResult(found?.ToRecord());
        }
    }

    public Task<Grant?> FindGrantAsync(string grantId, CancellationToken cancellationToken) =>
        Task.FromResult(FindGrantData(grantId) is { } data ? data.ToRecord(LastUsed(grantId)) : null);

    public Task<Grant?> FindGrantByRefreshAsync(string refreshHash, CancellationToken cancellationToken) =>
        Task.FromResult(Store<McpGrantData>().Find<McpGrantData>(nameof(McpGrantData.RefreshHash), refreshHash).FirstOrDefault() is { } data
            ? data.ToRecord(LastUsed(data.GrantId))
            : null);

    public Task<Grant?> FindGrantByPreviousRefreshAsync(string refreshHash, CancellationToken cancellationToken) =>
        Task.FromResult(refreshHash.Length > 0
            && Store<McpGrantData>().Find<McpGrantData>(nameof(McpGrantData.PreviousRefreshHash), refreshHash).FirstOrDefault() is { } data
                ? data.ToRecord(LastUsed(data.GrantId))
                : null);

    public Task AddGrantAsync(Grant grant, CancellationToken cancellationToken)
    {
        Store<McpGrantData>().Save(McpGrantData.From(grant));
        return Task.CompletedTask;
    }

    public Task<bool> TryRotateRefreshAsync(string grantId, string currentRefreshHash, string newRefreshHash, DateTimeOffset expires,
        IReadOnlyList<string> roles, string scope, CancellationToken cancellationToken)
    {
        lock (GrantLocks.For(grantId))
        {
            // Read again inside the lock: what FindGrantByRefreshAsync found may have been rotated or deleted since.
            var data = FindGrantData(grantId);
            if (data is null || data.RefreshHash != currentRefreshHash)
            {
                return Task.FromResult(false);
            }
            data.PreviousRefreshHash = currentRefreshHash;
            data.RefreshHash = newRefreshHash;
            data.Expires = expires.UtcDateTime;
            data.Roles = Lines(roles);
            data.Scope = scope;
            Store<McpGrantData>().Save(data);
            return Task.FromResult(true);
        }
    }

    /// <summary>In a store of its own, so recording a use can never overwrite a refresh token another instance just rotated.</summary>
    public Task TouchGrantAsync(string grantId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var store = Store<McpGrantUseData>();
        var use = store.Find<McpGrantUseData>(nameof(McpGrantUseData.GrantId), grantId).FirstOrDefault() ?? new McpGrantUseData { GrantId = grantId };
        use.LastUsed = at.UtcDateTime;
        store.Save(use);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Grant>> ListGrantsAsync(string? userName, CancellationToken cancellationToken)
    {
        var grants = (userName is null
                ? Store<McpGrantData>().LoadAll<McpGrantData>()
                : Store<McpGrantData>().Find<McpGrantData>(nameof(McpGrantData.UserName), userName))
            .ToList();
        var used = Store<McpGrantUseData>().LoadAll<McpGrantUseData>()
            .GroupBy(u => u.GrantId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(u => u.LastUsed), StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<Grant>>(grants
            .Select(g => g.ToRecord(used.TryGetValue(g.GrantId, out var at) ? Utc(at) : null))
            .OrderBy(g => g.Created)
            .ToList());
    }

    public Task DeleteGrantAsync(string grantId, CancellationToken cancellationToken)
    {
        lock (GrantLocks.For(grantId))
        {
            var grants = Store<McpGrantData>();
            foreach (var grant in grants.Find<McpGrantData>(nameof(McpGrantData.GrantId), grantId).ToList())
            {
                grants.Delete(grant.Id);
            }
            DeleteUses(grantId);
        }
        return Task.CompletedTask;
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, DateTimeOffset unusedClientsBefore, CancellationToken cancellationToken)
    {
        var cutoff = now.UtcDateTime;
        var deleted = 0;
        var codes = Store<McpCodeData>();
        foreach (var code in codes.Items<McpCodeData>().Where(c => c.Expires < cutoff).ToList())
        {
            codes.Delete(code.Id);
            deleted++;
        }
        var grants = Store<McpGrantData>();
        foreach (var grant in grants.Items<McpGrantData>().Where(g => g.Expires < cutoff).ToList())
        {
            lock (GrantLocks.For(grant.GrantId))
            {
                grants.Delete(grant.Id);
                DeleteUses(grant.GrantId);
            }
            deleted++;
        }
        var inUse = grants.LoadAll<McpGrantData>().Select(g => g.ClientId)
            .Concat(codes.LoadAll<McpCodeData>().Select(c => c.ClientId))
            .ToHashSet(StringComparer.Ordinal);
        var clients = Store<McpClientData>();
        var clientCutoff = unusedClientsBefore.UtcDateTime;
        foreach (var client in clients.Items<McpClientData>().Where(c => c.Created < clientCutoff).ToList().Where(c => !inUse.Contains(c.ClientId)))
        {
            clients.Delete(client.Id);
            deleted++;
        }
        return Task.FromResult(deleted);
    }

    private McpGrantData? FindGrantData(string grantId) =>
        Store<McpGrantData>().Find<McpGrantData>(nameof(McpGrantData.GrantId), grantId).FirstOrDefault();

    private DateTimeOffset? LastUsed(string grantId) =>
        Store<McpGrantUseData>().Find<McpGrantUseData>(nameof(McpGrantUseData.GrantId), grantId).Select(u => (DateTime?)u.LastUsed).Max() is { } at
            ? Utc(at)
            : null;

    private void DeleteUses(string grantId)
    {
        var uses = Store<McpGrantUseData>();
        foreach (var use in uses.Find<McpGrantUseData>(nameof(McpGrantUseData.GrantId), grantId).ToList())
        {
            uses.Delete(use.Id);
        }
    }

    private DynamicDataStore Store<T>() => stores.GetStore(typeof(T)) ?? stores.CreateStore(typeof(T));

    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    internal static string Lines(IEnumerable<string> values) => string.Join('\n', values);

    internal static IReadOnlyList<string> Lines(string? value) => (value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>A <see cref="RegisteredClient"/> as the Dynamic Data Store keeps it.</summary>
[EPiServerDataStore(StoreName = "OptiCliMcpClients", AutomaticallyCreateStore = true, AutomaticallyRemapStore = true)]
public sealed class McpClientData : IDynamicData
{
    /// <summary>The Dynamic Data Store's own id.</summary>
    public Identity Id { get; set; } = Identity.NewIdentity();

    /// <summary>See <see cref="RegisteredClient.ClientId"/>.</summary>
    [EPiServerDataIndex]
    public string ClientId { get; set; } = "";

    /// <summary>See <see cref="RegisteredClient.ClientName"/>.</summary>
    public string ClientName { get; set; } = "";

    /// <summary>See <see cref="RegisteredClient.RedirectUris"/>; one per line.</summary>
    public string RedirectUris { get; set; } = "";

    /// <summary>See <see cref="RegisteredClient.AuthMethod"/>.</summary>
    public string AuthMethod { get; set; } = ClientAuthMethods.None;

    /// <summary>See <see cref="RegisteredClient.SecretHash"/>.</summary>
    public string SecretHash { get; set; } = "";

    /// <summary>See <see cref="RegisteredClient.Created"/>; UTC.</summary>
    public DateTime Created { get; set; }

    internal static McpClientData From(RegisteredClient client) => new()
    {
        ClientId = client.ClientId,
        ClientName = client.ClientName,
        RedirectUris = DdsOAuthStore.Lines(client.RedirectUris),
        AuthMethod = client.AuthMethod,
        SecretHash = client.SecretHash,
        Created = client.Created.UtcDateTime,
    };

    internal RegisteredClient ToRecord() => new()
    {
        ClientId = ClientId,
        ClientName = ClientName,
        RedirectUris = DdsOAuthStore.Lines(RedirectUris),
        AuthMethod = AuthMethod,
        SecretHash = SecretHash,
        Created = DdsOAuthStore.Utc(Created),
    };
}

/// <summary>An <see cref="AuthorizationCode"/> as the Dynamic Data Store keeps it.</summary>
[EPiServerDataStore(StoreName = "OptiCliMcpCodes", AutomaticallyCreateStore = true, AutomaticallyRemapStore = true)]
public sealed class McpCodeData : IDynamicData
{
    /// <summary>The Dynamic Data Store's own id.</summary>
    public Identity Id { get; set; } = Identity.NewIdentity();

    /// <summary>See <see cref="AuthorizationCode.CodeHash"/>.</summary>
    [EPiServerDataIndex]
    public string CodeHash { get; set; } = "";

    /// <summary>See <see cref="AuthorizationCode.ClientId"/>.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>See <see cref="AuthorizationCode.ClientName"/>.</summary>
    public string ClientName { get; set; } = "";

    /// <summary>See <see cref="AuthorizationCode.RedirectUri"/>.</summary>
    public string RedirectUri { get; set; } = "";

    /// <summary>See <see cref="AuthorizationCode.CodeChallenge"/>.</summary>
    public string CodeChallenge { get; set; } = "";

    /// <summary>See <see cref="AuthorizationCode.UserName"/>.</summary>
    public string UserName { get; set; } = "";

    /// <summary>See <see cref="AuthorizationCode.Roles"/>; one per line.</summary>
    public string Roles { get; set; } = "";

    /// <summary>See <see cref="AuthorizationCode.Scope"/>.</summary>
    public string Scope { get; set; } = "";

    /// <summary>See <see cref="AuthorizationCode.Resource"/>.</summary>
    public string Resource { get; set; } = "";

    /// <summary>See <see cref="AuthorizationCode.Expires"/>; UTC.</summary>
    public DateTime Expires { get; set; }

    internal static McpCodeData From(AuthorizationCode code) => new()
    {
        CodeHash = code.CodeHash,
        ClientId = code.ClientId,
        ClientName = code.ClientName,
        RedirectUri = code.RedirectUri,
        CodeChallenge = code.CodeChallenge,
        UserName = code.UserName,
        Roles = DdsOAuthStore.Lines(code.Roles),
        Scope = code.Scope,
        Resource = code.Resource,
        Expires = code.Expires.UtcDateTime,
    };

    internal AuthorizationCode ToRecord() => new()
    {
        CodeHash = CodeHash,
        ClientId = ClientId,
        ClientName = ClientName,
        RedirectUri = RedirectUri,
        CodeChallenge = CodeChallenge,
        UserName = UserName,
        Roles = DdsOAuthStore.Lines(Roles),
        Scope = Scope,
        Resource = Resource,
        Expires = DdsOAuthStore.Utc(Expires),
    };
}

/// <summary>A <see cref="Grant"/> as the Dynamic Data Store keeps it (without <see cref="Grant.LastUsed"/>).</summary>
[EPiServerDataStore(StoreName = "OptiCliMcpGrants", AutomaticallyCreateStore = true, AutomaticallyRemapStore = true)]
public sealed class McpGrantData : IDynamicData
{
    /// <summary>The Dynamic Data Store's own id.</summary>
    public Identity Id { get; set; } = Identity.NewIdentity();

    /// <summary>See <see cref="Grant.GrantId"/>.</summary>
    [EPiServerDataIndex]
    public string GrantId { get; set; } = "";

    /// <summary>See <see cref="Grant.ClientId"/>.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>See <see cref="Grant.ClientName"/>.</summary>
    public string ClientName { get; set; } = "";

    /// <summary>See <see cref="Grant.UserName"/>.</summary>
    [EPiServerDataIndex]
    public string UserName { get; set; } = "";

    /// <summary>See <see cref="Grant.Roles"/>; one per line.</summary>
    public string Roles { get; set; } = "";

    /// <summary>See <see cref="Grant.Scope"/>.</summary>
    public string Scope { get; set; } = "";

    /// <summary>See <see cref="Grant.Resource"/>.</summary>
    public string Resource { get; set; } = "";

    /// <summary>See <see cref="Grant.RefreshHash"/>.</summary>
    [EPiServerDataIndex]
    public string RefreshHash { get; set; } = "";

    /// <summary>See <see cref="Grant.PreviousRefreshHash"/>.</summary>
    [EPiServerDataIndex]
    public string PreviousRefreshHash { get; set; } = "";

    /// <summary>See <see cref="Grant.Created"/>; UTC.</summary>
    public DateTime Created { get; set; }

    /// <summary>See <see cref="Grant.Expires"/>; UTC.</summary>
    public DateTime Expires { get; set; }

    internal static McpGrantData From(Grant grant) => new()
    {
        GrantId = grant.GrantId,
        ClientId = grant.ClientId,
        ClientName = grant.ClientName,
        UserName = grant.UserName,
        Roles = DdsOAuthStore.Lines(grant.Roles),
        Scope = grant.Scope,
        Resource = grant.Resource,
        RefreshHash = grant.RefreshHash,
        PreviousRefreshHash = grant.PreviousRefreshHash,
        Created = grant.Created.UtcDateTime,
        Expires = grant.Expires.UtcDateTime,
    };

    internal Grant ToRecord(DateTimeOffset? lastUsed) => new()
    {
        GrantId = GrantId,
        ClientId = ClientId,
        ClientName = ClientName,
        UserName = UserName,
        Roles = DdsOAuthStore.Lines(Roles),
        Scope = Scope,
        Resource = Resource,
        RefreshHash = RefreshHash,
        PreviousRefreshHash = PreviousRefreshHash ?? "",
        Created = DdsOAuthStore.Utc(Created),
        Expires = DdsOAuthStore.Utc(Expires),
        LastUsed = lastUsed,
    };
}

/// <summary>When a grant was last used, kept apart from the grant itself (see <see cref="IOAuthStore.TouchGrantAsync"/>).</summary>
[EPiServerDataStore(StoreName = "OptiCliMcpGrantUses", AutomaticallyCreateStore = true, AutomaticallyRemapStore = true)]
public sealed class McpGrantUseData : IDynamicData
{
    /// <summary>The Dynamic Data Store's own id.</summary>
    public Identity Id { get; set; } = Identity.NewIdentity();

    /// <summary>The grant it belongs to.</summary>
    [EPiServerDataIndex]
    public string GrantId { get; set; } = "";

    /// <summary>When a token for the grant was last used; UTC.</summary>
    public DateTime LastUsed { get; set; }
}
