using System.Security.Principal;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Framework.Blobs;
using EPiServer.ImageLibrary;
using EPiServer.Security;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Tests.Content;
using OptiCli.Cms;
using OptiCli.Cms.Compat;
using OptiCli.Cms.Operations;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Cms;

/// <summary>
/// Showing media through stand-ins for the CMS's loader and image service: what is shown as it is, what is scaled down
/// and how, and what can't be shown.
/// </summary>
public sealed class ViewMediaOperationTests : IDisposable
{
    private readonly string _blobs = Directory.CreateTempSubdirectory("opticli-view-media-").FullName;

    private const int Media = 30;

    private readonly Dictionary<int, IContent> _content = [];

    private readonly Images _images = new();

    private bool _hasImageService = true;

    private MediaView View(string reference = "30", ViewMediaRequest? body = null)
    {
        var (repository, recorder) = Recorder<IContentRepository>.Create();
        recorder.Answer = (method, args) =>
        {
            var id = args.Length > 0 && args[0] is ContentReference link ? link.ID : 0;
            switch (method.Name)
            {
                case nameof(IContentLoader.TryGet):
                    var found = _content.GetValueOrDefault(id);
                    args[^1] = found;
                    return found is not null;
                case nameof(IContentLoader.Get):
                    return _content.TryGetValue(id, out var content) ? content : throw new ContentNotFoundException(new ContentReference(id));
                default:
                    return null;
            }
        };
        var services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton<IContentLoader>(repository)
            .AddSingleton(Recorder<IContentVersionRepository>.Create().Proxy)
            .AddSingleton(Recorder<ILanguageBranchRepository>.Create().Proxy)
            .AddSingleton<IPrincipalAccessor>(new CmsCallTests.PrincipalAccessor(new GenericPrincipal(new GenericIdentity("developer"), [])));
        if (_hasImageService)
        {
            services.AddSingleton<IImageService>(_images);
        }
        return ViewMediaOperation.Run(new CmsCall(services.BuildServiceProvider(), CancellationToken.None, CmsCaller.Developer), reference, body ?? new ViewMediaRequest());
    }

    public void Dispose() => Directory.Delete(_blobs, recursive: true);

    /// <summary>Media whose file is in a file blob, as the CMS's default blob provider stores it.</summary>
    private void AddMedia(string fileName, byte[] file)
    {
        var path = Path.Combine(_blobs, fileName);
        File.WriteAllBytes(path, file);
        _content[Media] = new ImageData
        {
            ContentLink = new ContentReference(Media),
            Name = "Team photo",
            BinaryData = new FileBlob(new Uri($"epi.fx.blob://default/{Media}/{fileName}"), path),
        };
    }

    [Fact]
    public void An_image_that_fits_is_shown_as_it_is_stored()
    {
        var png = ImageHeadersTests.Png(800, 600);
        AddMedia("team.png", png);

        var view = View();

        Assert.Equal(("30", "Team photo", "image/png", 800, 600, (long)png.Length), (view.Ref, view.Name, view.MimeType, view.Width, view.Height, view.Bytes));
        Assert.Equal(png, view.Image);
        Assert.Null(view.Original);
        Assert.Empty(_images.Requests);
    }

    [Fact]
    public void A_larger_jpeg_is_scaled_to_the_largest_size_as_a_jpeg()
    {
        var jpeg = ImageHeadersTests.Jpeg(4000, 3000);
        AddMedia("team.jpg", jpeg);

        var view = View();

        var request = Assert.Single(_images.Requests);
        Assert.Equal(("image/jpeg", 1568f / 4000, 80), (request.MimeType, request.ZoomFactor, request.Quality));
        Assert.NotNull(request.Operations);
        Assert.Equal(("image/jpeg", 1568, 1176, Images.Small.Length), (view.MimeType, view.Width, view.Height, (int)view.Bytes));
        Assert.Equal(new MediaOriginal("image/jpeg", 4000, 3000, jpeg.Length), view.Original);
        Assert.Equal(Images.Small, view.Image);
    }

    [Fact]
    public void A_smaller_size_can_be_asked_for()
    {
        AddMedia("team.png", ImageHeadersTests.Png(800, 400));

        var view = View(body: new ViewMediaRequest(MaxSize: 200));

        Assert.Equal(("image/png", 0.25f), (Assert.Single(_images.Requests).MimeType, _images.Requests[0].ZoomFactor));
        Assert.Equal((200, 100), (view.Width, view.Height));
    }

    [Fact]
    public void The_size_shown_is_the_one_the_image_service_made()
    {
        // EPiServer.ImageLibrary.ImageSharp 1.0.6 and newer (and CMS 13's) floor a zoomed side, and 2400 at 1568/2400 is
        // just below 1568 in float arithmetic: the image is 1567 by 783, and so is what is said about it.
        AddMedia("gradient.png", ImageHeadersTests.Png(2400, 1200));
        _images.Made = r => ImageHeadersTests.Png((uint)Math.Floor(2400f * r.ZoomFactor), (uint)Math.Floor(1200f * r.ZoomFactor));

        var view = View();

        Assert.Equal((1567, 783), (view.Width, view.Height));
        Assert.Equal(new MediaOriginal("image/png", 2400, 1200, ImageHeadersTests.Png(2400, 1200).Length), view.Original);
    }

    [Fact]
    public void A_png_still_too_large_after_scaling_becomes_a_jpeg_and_then_smaller()
    {
        AddMedia("diagram.png", ImageHeadersTests.Png(3000, 3000));
        _images.TooLarge = r => r.MimeType == "image/png" || r.ZoomFactor > 0.4f;

        var view = View();

        Assert.Equal(["image/png", "image/jpeg", "image/jpeg"], _images.Requests.Select(r => r.MimeType));
        Assert.Equal(1176f / 3000, _images.Requests[^1].ZoomFactor);
        Assert.Equal(("image/jpeg", 1176), (view.MimeType, view.Width));
    }

    [Fact]
    public void An_image_that_never_gets_small_enough_is_refused()
    {
        AddMedia("noise.png", ImageHeadersTests.Png(3000, 3000));
        _images.TooLarge = _ => true;

        var error = Assert.Throws<AgentException>(() => View());

        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Contains("still larger", error.Message);
    }

    [Fact]
    public void Without_an_image_service_a_large_image_is_refused_and_a_small_one_still_shown()
    {
        _hasImageService = false;
        AddMedia("team.jpg", ImageHeadersTests.Jpeg(4000, 3000));

        var error = Assert.Throws<AgentException>(() => View());

        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Contains("no image service", error.Message);
        Assert.Contains("open the media in the CMS", error.Hint, StringComparison.OrdinalIgnoreCase);

        AddMedia("icon.png", ImageHeadersTests.Png(64, 64));
        Assert.Equal(64, View().Width);
    }

    [Fact]
    public void An_image_with_too_many_pixels_to_decode_is_refused_before_the_image_service_sees_it()
    {
        AddMedia("bomb.png", ImageHeadersTests.Png(20000, 20000));

        var error = Assert.Throws<AgentException>(() => View());

        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Contains("20000 by 20000", error.Message);
        Assert.Empty(_images.Requests);
    }

    [Fact]
    public void The_image_services_failure_is_a_refusal_that_names_it()
    {
        AddMedia("photo.webp", ImageHeadersTests.Jpeg(4000, 3000));
        _images.Failure = new NotSupportedException("Image cannot be loaded.");

        var error = Assert.Throws<AgentException>(() => View());

        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Contains("Image cannot be loaded.", error.Message);
    }

    [Fact]
    public void An_svg_is_shown_as_its_markup()
    {
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><title>Logo</title></svg>";
        AddMedia("logo.svg", System.Text.Encoding.UTF8.GetBytes(svg));

        var view = View();

        Assert.Equal(("image/svg+xml", svg, (int?)null, (byte[]?)null), (view.MimeType, view.Svg, view.Width, view.Image));
    }

    [Fact]
    public void Other_files_and_content_that_is_not_media_can_not_be_shown()
    {
        AddMedia("report.pdf", "%PDF-1.7\n"u8.ToArray());
        var pdf = Assert.Throws<AgentException>(() => View());
        Assert.Equal(AgentErrorCodes.Usage, pdf.Code);
        Assert.Contains("a pdf file", pdf.Message);

        _content[31] = new CmsCallTests.Unsecured(new ContentReference(31)) { Name = "About us" };
        var page = Assert.Throws<AgentException>(() => View("31"));
        Assert.Equal(AgentErrorCodes.Usage, page.Code);
        Assert.Contains("is not media", page.Message);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(1569)]
    public void The_size_asked_for_must_be_in_range(int maxSize)
    {
        AddMedia("team.png", ImageHeadersTests.Png(800, 600));

        Assert.Equal(AgentErrorCodes.Usage, Assert.Throws<AgentException>(() => View(body: new ViewMediaRequest(MaxSize: maxSize))).Code);
    }

    [Fact]
    public void The_resizer_is_null_without_an_image_service() =>
        Assert.Null(CmsImages.Resizer(new ServiceCollection().BuildServiceProvider()));

    /// <summary>The image service: records each request, and writes <see cref="Small"/> or, when <see cref="TooLarge"/> says so, more than can be shown.</summary>
    private sealed class Images : IImageService
    {
        public static readonly byte[] Small = [1, 2, 3];

        public List<RenderRequest> Requests { get; } = [];

        public Func<RenderRequest, bool> TooLarge { get; set; } = _ => false;

        public Exception? Failure { get; set; }

        /// <summary>The image made for a request; <see cref="Small"/>, which has no header, unless set.</summary>
        public Func<RenderRequest, byte[]> Made { get; set; } = _ => Small;

        public void RenderImage(Stream input, Stream output, RenderRequest request)
        {
            Requests.Add(request);
            if (Failure is not null)
            {
                throw Failure;
            }
            output.Write(TooLarge(request) ? new byte[ViewMediaOperation.LargestBytes + 1] : Made(request));
        }
    }
}
