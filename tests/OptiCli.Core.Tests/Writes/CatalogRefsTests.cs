using OptiCli.Core.Refs;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

/// <summary>Commerce's catalog content: its refs, as opticli prints them and takes them back.</summary>
public class CatalogRefsTests
{
    private static WriteResult Result(string reference, int id, int version, bool published = true) => new()
    {
        Content = new ContentSummary { Ref = reference, Id = id, Version = version, Guid = Guid.NewGuid(), Name = "Fashion", Language = "en", Status = published ? "published" : "checkedOut" },
        Saved = true,
        Published = published,
        BaseVersion = version - 1,
        PreviouslyPublished = version - 1,
    };

    [Theory]
    // A catalog (0x80000000 | id), a node (0x40000000 | id), an entry, and the catalog root.
    [InlineData("-2147483645_8_CatalogContent", -2147483645, 8, "-2147483645__CatalogContent", "-2147483645_8_CatalogContent")]
    [InlineData("1073741825_3_CatalogContent", 1073741825, 3, "1073741825__CatalogContent", "1073741825_3_CatalogContent")]
    [InlineData("1_4_CatalogContent", 1, 4, "1__CatalogContent", "1_4_CatalogContent")]
    public void A_write_to_catalog_content_prints_its_provider_ref_never_a_bare_id(string agentRef, int id, int version, string expected, string expectedVersion)
    {
        var output = WriteOutput.From(Result(agentRef, id, version));

        Assert.Equal(expected, output.Ref);
        Assert.Equal(expectedVersion, output.Version);
        Assert.Equal($"{id}_{version - 1}_CatalogContent", output.BaseVersion);
        Assert.Equal($"{id}_{version - 1}_CatalogContent", output.PreviouslyPublished);
        Assert.Equal("CatalogContent", WriteOutput.ProviderOf(output.Version));
    }

    [Fact]
    public void CMS_content_keeps_its_plain_refs()
    {
        var output = WriteOutput.From(Result("123_456", 123, 456));

        Assert.Equal("123", output.Ref);
        Assert.Equal("123_456", output.Version);
        Assert.Null(WriteOutput.ProviderOf(output.Version));
        Assert.Null(WriteOutput.ProviderOf(output.Ref));
    }

    /// <summary>Whatever opticli prints for catalog content, it takes back: the round trip the E2E suite relies on.</summary>
    [Theory]
    [InlineData("-2147483645_8_CatalogContent", -2147483645, 8)]
    [InlineData("-1073741823_1_CatalogContent", -1073741823, 1)]
    [InlineData("1073741825_3_CatalogContent", 1073741825, 3)]
    [InlineData("1_4_CatalogContent", 1, 4)]
    public void The_printed_ref_of_catalog_content_parses_back_to_the_same_content(string agentRef, int id, int version)
    {
        var printed = WriteOutput.From(Result(agentRef, id, version)).Ref!;

        var parsed = ContentRefParser.Parse(printed);

        Assert.Equal(ContentRefKind.Provider, parsed.Kind);
        Assert.Equal(id, parsed.Id);
        Assert.Equal("CatalogContent", parsed.Provider);
        Assert.Equal(printed, parsed.ToString());
        Assert.Equal(id, CatalogRefSyntax.ProviderId(printed));
        Assert.Equal(printed, CatalogRefSyntax.Format(id));
    }

    [Fact]
    public void A_version_of_catalog_content_isnt_a_ref_opticli_takes_and_says_which_is()
    {
        Assert.False(ContentRefParser.TryParse("-2147483645_8__CatalogContent", out _, out var error));
        Assert.Contains("-2147483645__CatalogContent", error);
    }

    [Theory]
    [InlineData("code:P-1001", "P-1001", null, false)]
    [InlineData(" CODE: men ", "men", null, false)]
    [InlineData("catalog:Fashion", null, "Fashion", false)]
    [InlineData("catalog-root", null, null, true)]
    [InlineData("Catalog-Root", null, null, true)]
    public void Catalog_refs_by_code_name_and_the_root(string reference, string? code, string? catalog, bool root)
    {
        Assert.Equal(code, CatalogRefSyntax.Code(reference));
        Assert.Equal(catalog, CatalogRefSyntax.CatalogName(reference));
        Assert.Equal(root, CatalogRefSyntax.IsRoot(reference));
        Assert.True(CatalogRefSyntax.IsCommerceOnly(reference));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("63__dam")]
    [InlineData("code:")]
    [InlineData("catalog:")]
    [InlineData("0__CatalogContent")]
    [InlineData("/en/products/")]
    public void Other_refs_arent_catalog_refs(string reference)
    {
        Assert.False(CatalogRefSyntax.IsCommerceOnly(reference));
        Assert.Null(CatalogRefSyntax.ProviderId(reference));
    }

    [Theory]
    [InlineData("Catalog", CatalogKinds.Catalog)]
    [InlineData("Node", CatalogKinds.Node)]
    [InlineData("Product", CatalogKinds.Product)]
    [InlineData("Variation", CatalogKinds.Variation)]
    [InlineData("Bundle", CatalogKinds.Bundle)]
    [InlineData("Package", CatalogKinds.Package)]
    [InlineData("Page", null)]
    [InlineData(null, null)]
    public void A_catalog_type_is_known_by_its_base(string? typeBase, string? kind) => Assert.Equal(kind, CatalogKinds.FromBase(typeBase));

    /// <summary>
    /// A plan's $id of catalog content binds to its ref, never to its id: entry 1 is not the CMS's content 1 (opticli 0.35
    /// bound the id, so a later step on $product wrote to CMS content of the same id).
    /// </summary>
    [Fact]
    public void A_plan_id_of_catalog_content_resolves_to_its_ref_in_refs_and_property_values()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "shirt", "type": "FashionProduct", "parent": "code:men", "name": "Shirt"},
              {"op": "set", "ref": "$shirt", "properties": {"Brand": "Acme"}},
              {"op": "create", "id": "start", "type": "StartPage", "parent": "1", "name": "Home", "properties": {"FeaturedProduct": "$shirt"}}
            ]}
            """);
        var refs = new Dictionary<string, string> { ["shirt"] = "1__CatalogContent" };
        var created = new Dictionary<string, int>();

        var set = Assert.IsType<SetOperation>(WritePlan.Resolve(plan.Steps[1], created, refs: refs));
        var start = Assert.IsType<CreateOperation>(WritePlan.Resolve(plan.Steps[2], created, refs: refs));

        Assert.Equal("1__CatalogContent", set.Ref);
        Assert.Equal("1__CatalogContent", start.Properties!["FeaturedProduct"]!.GetValue<string>());
    }

    [Fact]
    public void The_undo_of_catalog_writes_says_what_opticli_can_and_cant_do()
    {
        var created = WriteOutput.From(Result("1_4_CatalogContent", 1, 4));
        var create = UndoHints.For(new CreateOperation("code:men", "FashionProduct", "Shirt"), created);
        var set = UndoHints.For(new SetOperation("1__CatalogContent"), created);

        Assert.StartsWith("None in opticli: 1__CatalogContent is catalog content", create);
        Assert.DoesNotContain("opticli delete", create);
        Assert.StartsWith("1_4_CatalogContent was saved and published", set);
    }
}
