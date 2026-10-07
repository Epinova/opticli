using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

// Fixtures that BuildScanner finds by reading this test assembly's metadata. Nothing here runs.

namespace OptiCli.Core.Tests.Drift.Fixtures
{
    public sealed class EventsContext : DbContext;

    [DbContext(typeof(EventsContext))]
    [Migration("20260101000000_AddEvents")]
    public sealed class AddEvents : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }
    }

    [DbContext(typeof(EventsContext))]
    [Migration("20260201000000_AddVenues")]
    public sealed class AddVenues : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }
    }

    /// <summary>A model snapshot has [DbContext] but no [Migration]: not a migration.</summary>
    [DbContext(typeof(EventsContext))]
    public sealed class EventsContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
        }
    }
}

namespace OptiCli.Core.Tests.Drift.Fixtures.Cms
{
    /// <summary>Stands in for the CMS's <c>[ContentType]</c>: BuildScanner goes by the attribute's name and its GUID argument.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
    public sealed class ContentTypeAttribute : Attribute
    {
        public string? GUID { get; set; }

        public string? DisplayName { get; set; }

        public string[]? CompositionBehaviors { get; set; }

        public int Order { get; set; }

        public AttributeTargets Targets { get; set; }
    }

    [ContentType(GUID = "0b2b9c1e-3f4a-4b5c-8d6e-7f8091a2b301", DisplayName = "Fixture element", CompositionBehaviors = ["ElementEnabled"], Order = 3)]
    public sealed class FixtureElement;

    /// <summary>An enum argument: decoded too.</summary>
    [ContentType(Targets = AttributeTargets.Class, GUID = "0b2b9c1e-3f4a-4b5c-8d6e-7f8091a2b302")]
    public interface IFixtureContract;

    /// <summary>No GUID: nothing to find.</summary>
    [ContentType(DisplayName = "No GUID")]
    public sealed class FixtureWithoutGuid;
}

namespace EPiServer.Data.SchemaUpdates.Internal
{
    /// <summary>Stands in for the CMS's own type in EPiServer.Data.dll, which holds the schema version its packages need.</summary>
    internal static class DatabaseVersionValidator
    {
        internal const int RequiredDatabaseVersion = 8023;
    }
}
