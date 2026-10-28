#if CMS13
using System.Text;
using EPiServer.Core;
using EPiServer.Core.Html.StringParsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiCli.Cms;
using OptiCli.Cms.Operations;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Cms;

/// <summary>
/// CMS 13's edit UI runs an upload through the CMS's media file validator, whose script parser refuses or removes script
/// in the file types the site lists: an editor's upload through the module is refused where it would refuse or change it.
/// </summary>
public sealed class CmsMediaSanitizerTests
{
    private static readonly byte[] File = Encoding.UTF8.GetBytes("<p onclick=\"alert(1)\">x</p>");

    private static CmsCall Call(IMediaFileValidator? validator, ScriptParserMode mode = ScriptParserMode.ThrowException)
    {
        var services = new ServiceCollection().AddSingleton(Options.Create(new ScriptParserOptions { MediaUploadMode = mode }));
        if (validator is not null)
        {
            services.AddSingleton(validator);
        }
        return new CmsCall(services.BuildServiceProvider(), CancellationToken.None, CmsCaller.Editor);
    }

    [Fact]
    public void What_the_CMS_refuses_is_refused_with_its_message()
    {
        var error = Assert.Throws<AgentException>(() => UploadOperation.RequireCmsSanitized(
            Call(new Validator(_ => throw new InvalidPropertyValueException("The element 'p' contains illegal attribute: 'onclick'."))), ".txt", File));

        Assert.Equal(AgentErrorCodes.Usage, error.Code);
        Assert.Equal("The CMS refuses this .txt file: The element 'p' contains illegal attribute: 'onclick'.", error.Message);
        Assert.Contains("Nothing was uploaded", error.Hint);
    }

    [Fact]
    public void What_the_CMS_would_strip_is_refused_and_what_it_leaves_as_it_is_uploads()
    {
        var stripped = new Validator(_ => new MemoryStream(Encoding.UTF8.GetBytes("<p>x</p>")));
        var error = Assert.Throws<AgentException>(() => UploadOperation.RequireCmsSanitized(Call(stripped, ScriptParserMode.Remove), ".txt", File));
        Assert.Contains("would remove some", error.Message);

        // The same text back (the parser drops a byte order mark), or a type it doesn't read: as stored.
        UploadOperation.RequireCmsSanitized(Call(new Validator(_ => new MemoryStream(File)), ScriptParserMode.Remove), ".txt", [0xEF, 0xBB, 0xBF, .. File]);
        UploadOperation.RequireCmsSanitized(Call(new Validator(input => input), ScriptParserMode.Remove), ".txt", File);
        // Reading to keep or refuse script, the parser may write it differently: only a refusal counts.
        UploadOperation.RequireCmsSanitized(Call(stripped, ScriptParserMode.ThrowException), ".txt", File);
        UploadOperation.RequireCmsSanitized(Call(stripped, ScriptParserMode.Keep), ".txt", File);
    }

    [Fact]
    public void Without_the_CMSs_validator_or_a_file_nothing_is_checked()
    {
        UploadOperation.RequireCmsSanitized(Call(null), ".txt", File);
        UploadOperation.RequireCmsSanitized(Call(new Validator(_ => throw new InvalidPropertyValueException("no"))), ".txt", null);
    }

    private sealed class Validator(Func<Stream, Stream> sanitize) : IMediaFileValidator
    {
        public Stream Sanitize(string extension, Stream stream) => sanitize(stream);

        public bool ValidateAllowedExtension(string extension) => true;

        public bool ValidateFileSize(long fileSize) => true;
    }
}
#endif
