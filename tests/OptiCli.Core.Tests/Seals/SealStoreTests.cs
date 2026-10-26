using OptiCli.Core.Errors;
using OptiCli.Core.Seals;

namespace OptiCli.Core.Tests.Seals;

/// <summary>Seals in the user config folder: a MAC with a per-user key, user-only files, and one use each.</summary>
public sealed class SealStoreTests : IDisposable
{
    private readonly TempDirectory _config = new();

    public void Dispose() => _config.Dispose();

    private SealStore Store() => new(_config.Combine("seals"), _config.Combine("seal.key"));

    [Fact]
    public void A_saved_seal_reads_back_as_it_was()
    {
        var seal = SealFixtures.Seal();

        var path = Store().Save(seal);
        var read = Store().Load(seal.Id);

        Assert.Equal(_config.Combine($"seals/{seal.Id}.json"), path);
        Assert.Equal(seal.PlanHash, read.PlanHash);
        Assert.Equal(seal.Target, read.Target);
        Assert.Equal(seal.Options, read.Options);
        Assert.Equal(seal.Build, read.Build);
        Assert.Equal(seal.Created, read.Created);
        Assert.Equal(seal.Production.Model, read.Production.Model);
        Assert.Equal(seal.Production.Items.Single().Branches, read.Production.Items.Single().Branches);
        Assert.Null(read.Leftovers);

        var withLeftovers = SealFixtures.Seal() with { Leftovers = ["FooterSettings", "StartPage.LegacyFooter"] };
        Store().Save(withLeftovers);
        Assert.Equal(["FooterSettings", "StartPage.LegacyFooter"], Store().Load(withLeftovers.Id).Leftovers);
        Assert.Empty(ProductionState.Changes(seal.Production, read.Production));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_config.Combine("seal.key")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_config.Combine("seals")));
        }
    }

    [Theory]
    [InlineData("\"updateExisting\": false", "\"updateExisting\": true")]
    [InlineData("\"database\": \"cms-prod\"", "\"database\": \"cms-other\"")]
    [InlineData("\"latest\": 456", "\"latest\": 457")]
    public void An_edited_seal_fails_its_mac(string from, string to)
    {
        var seal = SealFixtures.Seal();
        var path = Store().Save(seal);
        var text = File.ReadAllText(path);
        Assert.Contains(from, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));

        var refused = Assert.Throws<RefusedException>(() => Store().Load(seal.Id));

        Assert.Equal(SealStore.TamperedReason, Reason(refused));
    }

    [Fact]
    public void A_seal_written_by_hand_or_with_another_key_is_refused()
    {
        var seal = SealFixtures.Seal();
        var path = Store().Save(seal);
        var body = File.ReadAllText(path);

        File.WriteAllText(path, body.Replace(body[(body.IndexOf("\"mac\": \"", StringComparison.Ordinal) + 8)..][..64], new string('0', 64), StringComparison.Ordinal));
        Assert.Equal(SealStore.TamperedReason, Reason(Assert.Throws<RefusedException>(() => Store().Load(seal.Id))));

        File.WriteAllText(path, "{\"seal\": {\"id\": \"" + seal.Id + "\"}}");
        Assert.Equal(SealStore.TamperedReason, Reason(Assert.Throws<RefusedException>(() => Store().Load(seal.Id))));

        Store().Save(seal);
        File.Delete(_config.Combine("seal.key"));
        new SealStore(_config.Combine("other"), _config.Combine("seal.key")).Save(SealFixtures.Seal());
        Assert.Equal(SealStore.TamperedReason, Reason(Assert.Throws<RefusedException>(() => Store().Load(seal.Id))));
    }

    [Fact]
    public void A_seal_is_used_once()
    {
        var seal = SealFixtures.Seal();
        Store().Save(seal);

        Store().Consume(seal.Id);

        Assert.Equal(SealStore.UsedReason, Reason(Assert.Throws<RefusedException>(() => Store().Consume(seal.Id))));
        Assert.Equal(SealStore.UsedReason, Reason(Assert.Throws<RefusedException>(() => Store().Load(seal.Id))));
    }

    [Theory]
    [InlineData("../config")]
    [InlineData("0123456789ABCDEF")]
    [InlineData("0123456789abcde")]
    [InlineData("0123456789abcdef0")]
    [InlineData("")]
    public void Only_a_seal_id_names_a_file(string id)
    {
        Assert.False(SealStore.IsId(id));
        Assert.Throws<UsageException>(() => Store().Load(id));
    }

    [Fact]
    public void A_seal_that_isnt_there_is_not_found_and_ids_are_new_each_time()
    {
        Assert.Throws<NotFoundException>(() => Store().Load("0123456789abcdef"));
        Assert.True(SealStore.IsId(SealStore.NewId()));
        Assert.NotEqual(SealStore.NewId(), SealStore.NewId());
    }

    internal static string? Reason(OptiCliException ex) => ex.Details?.GetType().GetProperty("reason")?.GetValue(ex.Details) as string;
}
