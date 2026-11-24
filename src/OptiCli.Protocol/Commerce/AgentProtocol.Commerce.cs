namespace OptiCli.Protocol;

/// <summary>The Commerce agent's switch. Not compiled into the MCP module, which has no Commerce.</summary>
public static partial class AgentProtocol
{
    /// <summary>
    /// Environment variable of the site process that turns the Commerce agent off when it is <see cref="CommerceOff"/>. The
    /// agent loads it into every site whose build has EPiServer.Commerce.Core otherwise.
    /// </summary>
    public const string CommerceVariable = "OPTICLI_COMMERCE";

    public const string CommerceOff = "off";
}
