#if CMS13
using System.Text;
using EPiServer.Core;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Cms;
using OptiCli.Cms.Operations;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Cms;

/// <summary>
/// CMS 13's edit UI runs an upload through the CMS's media file validator, whose script parser refuses, removes or keeps
/// script in the file types the site lists: an editor's upload through the module is refused where it refuses it, and
/// stored as it would store it.
/// </summary>
public sealed class CmsMediaSanitizerTests
{
    private static readonly byte[] File = Encoding.UTF8.GetBytes("<p onclick=\"alert(1)\">x</p>");

    private static CmsCall Call(IMediaFileValidator? validator)
    {
        var services = new ServiceCollection();
        if (validator is not null)
        {
            services.AddSingleton(validator);
        }
        return new CmsCall(services.BuildServiceProvider(), CancellationToken.None, CmsCaller.Editor);
    }

    [Fact]
    public void What_the_CMS_refuses_is_refused_with_its_message()
    {
        var error = Assert.Throws<AgentException>(() => UploadOperation.CmsSanitized(
            Call(new Validator(_ => throw new InvalidPropertyValueException("The element 'p' contains illegal attribute: 'onclick'."))), ".txt", File));

        Assert.Equal(AgentErrorCodes.Usage, error.Code);
        Assert.Equal("The CMS refuses this .txt file: The element 'p' contains illegal attribute: 'onclick'.", error.Message);
        Assert.Contains("Nothing was uploaded", error.Hint);
    }

    [Fact]
    public void The_file_is_stored_as_the_CMS_writes_it_back()
    {
        var stripped = Encoding.UTF8.GetBytes("<p>x</p>");
        Assert.Equal(stripped, UploadOperation.CmsSanitized(Call(new Validator(_ => new MemoryStream(stripped))), ".txt", File));
        // A type it doesn't read: as it is.
        Assert.Same(File, UploadOperation.CmsSanitized(Call(new Validator(input => input)), ".png", File));
    }

    [Fact]
    public void What_the_CMS_would_store_is_checked_again()
    {
        // Inert as uploaded (the attribute's value is text), live as CMS 13.3's parser writes it back.
        var uploaded = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg"><rect width="1" height="1" class='" onload="alert(1)'/></svg>""");
        var rewritten = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg"><rect width="1" height="1" class="" onload="alert(1)" /></svg>""");
        UploadOperation.RequireNoScript(".svg", uploaded);

        var error = Assert.Throws<AgentException>(() => UploadOperation.CmsSanitized(Call(new Validator(_ => new MemoryStream(rewritten))), ".svg", uploaded));

        Assert.Equal(AgentErrorCodes.Usage, error.Code);
        Assert.StartsWith("The CMS's script parser rewrites this file, and as it would store it: The SVG file has ", error.Message);
        Assert.Contains("onload", error.Message);
        Assert.Contains("Nothing was uploaded", error.Hint);
    }

    [Fact]
    public void Without_the_CMSs_validator_or_a_file_nothing_is_checked()
    {
        Assert.Same(File, UploadOperation.CmsSanitized(Call(null), ".txt", File));
        Assert.Null(UploadOperation.CmsSanitized(Call(new Validator(_ => throw new InvalidPropertyValueException("no"))), ".txt", null));
    }

    private sealed class Validator(Func<Stream, Stream> sanitize) : IMediaFileValidator
    {
        public Stream Sanitize(string extension, Stream stream) => sanitize(stream);

        public bool ValidateAllowedExtension(string extension) => true;

        public bool ValidateFileSize(long fileSize) => true;
    }
}
#endif
