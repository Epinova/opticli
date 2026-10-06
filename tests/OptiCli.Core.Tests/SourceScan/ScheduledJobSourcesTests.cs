using OptiCli.Core.SourceScan;

namespace OptiCli.Core.Tests.SourceScan;

public class ScheduledJobSourcesTests : IDisposable
{
    private static readonly Guid ImportGuid = Guid.Parse("0b1c2d3e-0000-4000-8000-000000000001");

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Classes_with_the_attribute_are_found_with_what_it_says_and_where()
    {
        _root.Write("Web/Jobs/JobNames.cs", """
            namespace Example.Web.Jobs;

            public static class JobNames
            {
                public const string Export = "Content export";
            }
            """);
        _root.Write("Web/Jobs/ImportJob.cs", """
            using EPiServer.PlugIn;
            using EPiServer.Scheduler;

            namespace Example.Web.Jobs;

            [ScheduledPlugIn(
                DisplayName = "Content import",
                Description = "Imports content",
                GUID = "0b1c2d3e-0000-4000-8000-000000000001",
                IntervalType = ScheduledIntervalType.Hours,
                IntervalLength = 6,
                Restartable = true)]
            public class ImportJob : ScheduledJobBase
            {
                public override string Execute() => "done";
            }
            """);
        _root.Write("Web/Jobs/ExportJob.cs", """
            namespace Example.Web.Jobs
            {
                [EPiServer.PlugIn.ScheduledPlugInAttribute(DisplayName = JobNames.Export)]
                [System.Obsolete]
                internal sealed class ExportJob : ScheduledJobBase
                {
                    public override string Execute() => "done";
                }

                public class NotAJob
                {
                }
            }
            """);

        var sources = ScheduledJobSources.Find(CSharpSourceIndex.Build(_root.Path)).OrderBy(s => s.TypeName).ToList();

        Assert.Equal(2, sources.Count);
        var export = sources[0];
        Assert.Equal(("Example.Web.Jobs.ExportJob", "Content export", (Guid?)null, 0, 0), (export.TypeName, export.DisplayName, export.Guid, export.IntervalType, export.IntervalLength));
        var import = sources[1];
        Assert.Equal(("Example.Web.Jobs.ImportJob", "Content import", "Imports content", (Guid?)ImportGuid), (import.TypeName, import.DisplayName, import.Description, import.Guid));
        Assert.Equal((5, 6, true), (import.IntervalType, import.IntervalLength, import.Restartable));
        Assert.Equal((Path.Combine("Web", "Jobs", "ImportJob.cs"), 13), (import.File, import.Line));
    }

    [Fact]
    public void A_row_matches_its_source_by_guid_first_then_by_class()
    {
        var byGuid = new ScheduledJobSource("Example.Renamed.ImportJob", ImportGuid, "Content import", null, 0, 0, false, "ImportJob.cs", 1);
        var byClass = new ScheduledJobSource("Example.Web.Jobs.ExportJob", null, "Content export", null, 0, 0, false, "ExportJob.cs", 1);
        var sources = new[] { byGuid, byClass };

        Assert.Same(byGuid, ScheduledJobSources.Match(sources, ImportGuid, "Example.Web.Jobs.ImportJob"));
        Assert.Same(byClass, ScheduledJobSources.Match(sources, Guid.NewGuid(), "Example.Web.Jobs.ExportJob"));
        Assert.Null(ScheduledJobSources.Match(sources, Guid.NewGuid(), "EPiServer.Util.BlobCleanupJob"));
    }
}
