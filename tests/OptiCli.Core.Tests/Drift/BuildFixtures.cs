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

namespace EPiServer.Data.SchemaUpdates.Internal
{
    /// <summary>Stands in for the CMS's own type in EPiServer.Data.dll, which holds the schema version its packages need.</summary>
    internal static class DatabaseVersionValidator
    {
        internal const int RequiredDatabaseVersion = 8023;
    }
}
