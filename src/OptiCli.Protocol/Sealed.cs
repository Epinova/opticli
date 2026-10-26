namespace OptiCli.Protocol;

/// <summary>Response of <see cref="AgentRoutes.Sealed"/>: what a sealed apply's site runs with. Connection strings never leave the site.</summary>
/// <param name="Events">The event provider in effect; null when the run has no event bus.</param>
/// <param name="Blobs">The default blob provider in effect; null when the run has no blob storage.</param>
public sealed record SealedStatus(SealedEvents? Events, SealedBlobs? Blobs);

/// <param name="Provider">The event provider's type (<c>EPiServer.Azure.Events.AzureEventProvider</c> when it is the CMS's Azure one).</param>
/// <param name="Topic">The topic it uses.</param>
/// <param name="Subscription">The subscription it made for this site on the topic (the machine's name and 8 random characters).</param>
/// <param name="Connected">It has connected to the bus and sends what is raised (before that it only buffers).</param>
/// <param name="ConnectionMatches">The connection string in effect is the one typed for the run, and no <c>Endpoint</c> takes its place.</param>
/// <param name="Problem">Why something couldn't be read from the provider (another provider, a CMS version whose internals differ).</param>
public sealed record SealedEvents(string? Provider, string? Topic, string? Subscription, bool Connected, bool ConnectionMatches, string? Problem = null);

/// <param name="DefaultProvider">The name of the default blob provider (<c>azure</c> for the CMS's Azure one).</param>
/// <param name="ProviderType">Its type (<c>EPiServer.Azure.Blobs.AzureBlobProvider</c>).</param>
/// <param name="Container">The container the Azure provider uses.</param>
/// <param name="ConnectionMatches">The connection string in effect is the one typed for the run, and no <c>ServiceUri</c> takes its place.</param>
public sealed record SealedBlobs(string? DefaultProvider, string? ProviderType, string? Container, bool ConnectionMatches, string? Problem = null);

/// <param name="Flushed">Everything the provider had queued was sent (or failed and was logged by the CMS) before this answered.</param>
/// <param name="Problem">Why not.</param>
public sealed record EventsFlushResult(bool Flushed, string? Problem = null);
