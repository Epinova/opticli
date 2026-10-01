using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OptiCli.Core.Configuration;

namespace OptiCli.Core.Tests.Configuration;

/// <summary>JsonConfigFile against ASP.NET Core's own JSON configuration provider, which is what the site reads.</summary>
public class JsonConfigFileTests : IDisposable
{
    private const string AppSettings = """
        /* The kind of file sites have: comments, trailing commas, nested sections and arrays. */
        {
          // Line comment before a section.
          "ConnectionStrings": {
            "EPiServerDB": "Server=localhost;Database=ExampleDb;Integrated Security=true", // after a value
          },
          "EPiServer": {
            "Cms": {
              "MappedRoles": {
                "Items": {
                  "CmsAdmins": { "MappedRoles": [ "WebAdmins", "Administrators", ], "ShouldMatchAll": "false" },
                },
              },
            },
          },
          "Kestrel": { "Endpoints": { "Http": { "Url": "http://localhost:5000" /* not a comment inside the string: // */ } } },
          "Numbers": { "Port": 5199, "Ratio": 1.5, "Negative": -3, "Exponent": 1e3 },
          "Section:With:Colons": "kept as one key",
          "Objects": [ { "Name": "first" }, { "Name": "second", "Tags": [ "a", "b" ] } ],
          "Text": "Ærlig talt, \"quoted\" é",
        }
        """;

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Flattens_like_the_aspnet_core_json_provider(bool withBom)
    {
        var file = _root.Write("appsettings.json", AppSettings, withBom);

        var expected = AspNetCore(file);
        using var document = JsonConfigFile.Parse(file);
        var actual = JsonConfigFile.Flatten(document.RootElement);

        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase), actual.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase));
        Assert.Equal("Administrators", actual["EPiServer:Cms:MappedRoles:Items:CmsAdmins:MappedRoles:1"]);
        Assert.Equal("b", actual["Objects:1:Tags:1"]);
        Assert.Equal("5199", actual["Numbers:Port"]);
        Assert.Equal("http://localhost:5000", actual["Kestrel:Endpoints:Http:Url"]);
    }

    [Fact]
    public void Keys_are_case_insensitive_like_configuration()
    {
        var file = _root.Write("appsettings.json", AppSettings);

        using var document = JsonConfigFile.Parse(file);
        var values = JsonConfigFile.Flatten(document.RootElement);

        Assert.Equal(
            new ConfigurationBuilder().AddJsonFile(file).Build().GetConnectionString("episerverdb"),
            values["connectionstrings:episerverdb"]);
        Assert.Equal("kept as one key", values["SECTION:WITH:COLONS"]);
    }

    [Fact]
    public void Keys_containing_colons_join_the_sections_they_name()
    {
        // secrets.json is usually flat ("ConnectionStrings:EPiServerDB"); it overrides the nested form in appsettings.
        var file = _root.Write("secrets.json", """{ "ConnectionStrings:EPiServerDB": "flat", "Kestrel:Endpoints": { "Https:Url": "https://localhost:5001" } }""");

        using var document = JsonConfigFile.Parse(file);
        var values = JsonConfigFile.Flatten(document.RootElement);
        var configuration = new ConfigurationBuilder().AddJsonFile(file).Build();

        Assert.Equal(configuration.GetConnectionString("EPiServerDB"), values["ConnectionStrings:EPiServerDB"]);
        Assert.Equal(configuration["Kestrel:Endpoints:Https:Url"], values["Kestrel:Endpoints:Https:Url"]);
    }

    [Theory]
    [InlineData("""{ "ConnectionStrings": { "EPiServerDB": "x" """)]
    [InlineData("""{ "A": 'single quotes' }""")]
    [InlineData("""{ "A": "1" } trailing""")]
    public void Invalid_json_is_a_json_exception(string json)
    {
        var file = _root.Write("appsettings.json", json);

        Assert.ThrowsAny<JsonException>(() => JsonConfigFile.Parse(file).Dispose());
        Assert.ThrowsAny<Exception>(() => new ConfigurationBuilder().AddJsonFile(file).Build());
    }

    /// <summary>The leaf values ASP.NET Core's JSON provider reads from <paramref name="file"/>.</summary>
    private static Dictionary<string, string?> AspNetCore(string file) =>
        new ConfigurationBuilder().AddJsonFile(file).Build().AsEnumerable()
            .Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
}
