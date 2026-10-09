using System.Reflection;
using EPiServer.Core;

namespace OptiCli.Cms.Compat;

/// <summary>
/// A ContentArea item's inline block (<c>ContentAreaItem.InlineBlock</c>): a block stored in the area itself rather than as
/// content of its own. The property exists from CMS 12.20 (and on CMS 13); the agent compiles against CMS 12.0 and the MCP
/// module against an older 12 as well, so it is reached by reflection.
/// </summary>
internal static class InlineBlocks
{
    /// <summary>The CMS version that brought inline blocks in ContentAreas.</summary>
    public const string Since = "12.20";

    /// <summary>The render setting the CMS edit UI keeps an inline block's name in (<c>data-inlineblockname</c>).</summary>
    public const string NameKey = "data-inlineblockname";

    private static readonly PropertyInfo? Property = typeof(ContentAreaItem).GetProperty("InlineBlock", BindingFlags.Public | BindingFlags.Instance);

    /// <summary><c>ContentData.ContentTypeID</c>, which an inline block must have set; it came with inline blocks too.</summary>
    private static readonly PropertyInfo? TypeIdProperty = typeof(ContentData).GetProperty("ContentTypeID", BindingFlags.Public | BindingFlags.Instance);

    /// <summary>Whether the site's CMS has inline blocks in ContentAreas that can be set.</summary>
    public static bool Supported => Property is { CanRead: true, CanWrite: true };

    /// <summary>The item's inline block; null for an item that shows content, and on a CMS without inline blocks.</summary>
    public static BlockData? Of(ContentAreaItem item) => Property?.GetValue(item) as BlockData;

    /// <summary>The content type id of a block that isn't content (an inline block, a local block); 0 when it has none set.</summary>
    public static int TypeId(BlockData block) => TypeIdProperty?.GetValue(block) is int id ? id : 0;

    /// <summary>Makes <paramref name="item"/> (a writable one) show <paramref name="block"/>, a writable block of <paramref name="typeId"/>.</summary>
    /// <exception cref="AgentException"><c>usage</c> on a CMS before 12.20.</exception>
    public static void Set(ContentAreaItem item, BlockData block, int typeId)
    {
        Require();
        try
        {
            // The CMS refuses an inline block that doesn't name its type; the factory sets it, this makes sure.
            if (TypeId(block) == 0 && TypeIdProperty is { CanWrite: true })
            {
                TypeIdProperty.SetValue(block, typeId);
            }
            Property!.SetValue(item, block);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
        }
    }

    /// <exception cref="AgentException"><c>usage</c> on a CMS before 12.20, which has no inline blocks in ContentAreas.</exception>
    public static void Require()
    {
        if (!Supported)
        {
            throw AgentException.Usage(
                $"Inline blocks in a ContentArea need CMS {Since} or later; this site runs EPiServer.CMS.Core {CmsVersion()}.",
                "Add a shared block instead (a ref: `opticli block create --for <page>` makes one in the page's \"For this page\" folder), or upgrade the CMS.");
        }
    }

    /// <summary>The item's name in the area (<see cref="NameKey"/>); null when it has none.</summary>
    public static string? Name(ContentAreaItem item) =>
        CmsApi.RenderSettings(item).FirstOrDefault(s => s.Key == NameKey).Value?.ToString() is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name) ? name : null;

    /// <summary>
    /// Whether the site's CMS edit UI makes new blocks in a ContentArea inline (its <c>UIOptions.InlineBlocksInContentAreaEnabled</c>,
    /// off by default on CMS 12): with it off, "Create a new block" makes a shared block in the page's "For this page"
    /// folder instead. Inline blocks already in an area are shown and edited either way. Null when it can't be told (no
    /// CMS UI, or a version without the option).
    /// </summary>
    public static bool? EditUiCreatesThem(IServiceProvider services)
    {
        try
        {
            var type = Type.GetType("EPiServer.Web.UIOptions, EPiServer.Shell", throwOnError: false);
            var option = type?.GetProperty("InlineBlocksInContentAreaEnabled", BindingFlags.Public | BindingFlags.Instance);
            if (type is null || option is null)
            {
                return null;
            }
            // The CMS registers its options classes as services; IOptions<T> as well, where a site configures them so.
            var wrapper = Type.GetType("Microsoft.Extensions.Options.IOptions`1, Microsoft.Extensions.Options", throwOnError: false);
            var options = services.GetService(type)
                ?? (wrapper is not null && services.GetService(wrapper.MakeGenericType(type)) is { } wrapped
                    ? wrapped.GetType().GetProperty("Value")?.GetValue(wrapped)
                    : null);
            return options is null ? null : option.GetValue(options) as bool?;
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or TypeLoadException or System.IO.FileLoadException)
        {
            return null;
        }
    }

    private static string CmsVersion()
    {
        var assembly = typeof(ContentAreaItem).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString() ?? "(unknown)";
    }
}
