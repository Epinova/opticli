using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class StableGuidsTests
{
    [Fact]
    public void Matches_the_rfc_example()
    {
        // RFC 9562, appendix A.4: the DNS namespace and "www.example.com".
        var dns = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        Assert.Equal(Guid.Parse("2ed6657d-e927-568b-95e1-2665a8aea6a2"), StableGuids.Create(dns, "www.example.com"));
    }

    [Fact]
    public void Same_input_same_guid_and_the_name_matters()
    {
        var ns = Guid.Parse("6f1c2b3a-9d4e-4f5a-8b7c-1d2e3f4a5b6c");

        Assert.Equal(StableGuids.Create(ns, "root"), StableGuids.Create(ns, "root"));
        Assert.NotEqual(StableGuids.Create(ns, "root"), StableGuids.Create(ns, "Root"));
        Assert.NotEqual(StableGuids.Create(ns, "root"), StableGuids.Create(Guid.Parse("00000000-0000-0000-0000-000000000001"), "root"));
        Assert.Equal('5', StableGuids.Create(ns, "sidebar-ø").ToString()[14]);
    }
}
