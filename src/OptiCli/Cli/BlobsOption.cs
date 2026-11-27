using OptiCli.Core.Errors;

namespace OptiCli.Cli;

/// <summary>
/// <c>--blobs</c> of <c>export</c> and <c>sync pull</c>: a folder, or an Azure container's URL with a SAS token. <c>-</c>
/// reads the value from standard input (one line), or asks for it at a terminal without showing it, so a token never
/// stands on the command line, where other processes (<c>ps</c>) and the shell's history see it.
/// </summary>
internal static class BlobsOption
{
    public const string FromInput = "-";

    public const string Description =
        " A URL's SAS token is visible on the command line: give - to paste it at a prompt, or to pipe it in, instead.";

    public static string Resolve(string value)
    {
        if (value != FromInput)
        {
            return value;
        }
        if (!Console.IsInputRedirected)
        {
            return Present(HiddenInput.Read("The blob storage (folder, or the container's https:// URL with its SAS token): "));
        }
        return Present(Console.In.ReadLine()?.Trim());
    }

    private static string Present(string? value) =>
        string.IsNullOrWhiteSpace(value) ? throw new UsageException("--blobs - got no folder or URL.") : value.Trim();
}
