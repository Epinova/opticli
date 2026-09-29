namespace OptiCli.Tests.Shared;

/// <summary>
/// The local-only rule's test table, shared by every implementation of it (the CLI's
/// SqlClient-based check and the agent's own parser) so the two can't drift apart.
/// </summary>
public static class ConnectionStringCases
{
    /// <summary>Strings that must be accepted, with the server SqlClient would connect to. All use database <c>Cms</c>.</summary>
    public static readonly (string ConnectionString, string Server)[] Local =
    [
        ("Server=localhost;Database=Cms", "localhost"),
        ("Server=LOCALHOST;Database=Cms", "LOCALHOST"),
        ("Server=127.0.0.1;Database=Cms", "127.0.0.1"),
        ("Server=::1;Database=Cms", "::1"),
        ("Server=[::1],1433;Database=Cms", "[::1],1433"),
        ("Server=.;Database=Cms", "."),
        (@"Server=.\SQLEXPRESS;Database=Cms", @".\SQLEXPRESS"),
        ("Server=(local);Database=Cms", "(local)"),
        (@"Server=(localdb)\MSSQLLocalDB;Database=Cms", @"(localdb)\MSSQLLocalDB"),
        (@"Data Source=(LocalDb)\MSSQLLocalDB;Initial Catalog=Cms;Integrated Security=True", @"(LocalDb)\MSSQLLocalDB"),
        ("Server=tcp:localhost,1433;Database=Cms", "tcp:localhost,1433"),
        ("Server=localhost, 1433;Database=Cms", "localhost, 1433"),
        (@"Server=localhost\SQL2022,1433;Database=Cms", @"localhost\SQL2022,1433"),
        ("Data Source=localhost;Initial Catalog=Cms", "localhost"),
        ("Address=localhost;Database=Cms", "localhost"),
        ("Addr=127.0.0.1;Database=Cms", "127.0.0.1"),
        ("Network Address=localhost;Database=Cms", "localhost"),
        ("Server='localhost';Database=Cms", "localhost"),
        ("Server=\"localhost\";Database=Cms", "localhost"),
        ("  server = localhost ; database = Cms ", "localhost"),
        (";;Server=localhost;;Database=Cms;;", "localhost"),
        // Duplicates: SqlClient uses the last value, so the local one here is what it would connect to.
        ("Server=sql.example.com;Server=localhost;Database=Cms", "localhost"),
        ("Server=sql.example.com;Data Source=localhost;Database=Cms", "localhost"),
        ("Server=localhost;Failover Partner=(local);Database=Cms", "localhost"),
        // A quoted value may contain ';' and '=' without starting a new key.
        ("Password=\"p;Server=sql.example.com\";Server=localhost;Database=Cms", "localhost"),
        ("Server=localhost;Password='it''s;Server=sql.example.com';Database=Cms", "localhost"),
        ("Server=localhost;User Id=app;Password=\"a\"\"b\";Database=Cms;TrustServerCertificate=True", "localhost"),
    ];

    /// <summary>Strings that must be refused (remote, malformed, or ambiguous).</summary>
    public static readonly string[] Remote =
    [
        "Server=sql.example.invalid;Database=x;User Id=a;Password=b",
        "Server=localhost.example.com;Database=Cms",
        "Server=127.0.0.1.nip.io;Database=Cms",
        "Server=localhost.;Database=Cms",
        "Server=10.0.0.5;Database=Cms",
        "Server=127.0.0.2;Database=Cms",
        "Server=tcp:sql.example.com,1433;Database=Cms",
        "Server=tcp:db.database.windows.net,1433;Database=Cms;Authentication=Active Directory Default",
        // Last duplicate wins, including across synonyms.
        "Server=localhost;Server=sql.example.com;Database=Cms",
        "Data Source=localhost;Server=sql.example.com;Database=Cms",
        "Server=localhost;Address=sql.example.com;Database=Cms",
        "Server=localhost;Addr=sql.example.com;Database=Cms",
        "Server=localhost;Network Address=sql.example.com;Database=Cms",
        // A "Server=localhost" hidden inside a quoted value must not count.
        "Server=sql.example.com;Password=\"x;Server=localhost\";Database=Cms",
        "Server=sql.example.com;Password='x;Server=localhost';Database=Cms",
        "Server=\"local\"\"host\";Database=Cms",
        // A later empty value wins too.
        "Server=localhost;Server=;Database=Cms",
        // Other protocols and malformed ports are refused rather than interpreted.
        @"Server=np:\\sql.example.com\pipe\sql\query;Database=Cms",
        "Server=np:localhost;Database=Cms",
        "Server=admin:localhost;Database=Cms",
        "Server=lpc:.;Database=Cms",
        "Server=localhost,abc;Database=Cms",
        "Server=localhost,;Database=Cms",
        "Server=localhost:1433;Database=Cms",
        @"Server=localhost\;Database=Cms",
        @"Server=(localdb)\;Database=Cms",
        // Cyrillic 'о' look-alike.
        "Server=lоcalhost;Database=Cms",
        // SqlClient fails over to the partner silently.
        "Server=localhost;Failover Partner=sql.example.com;Database=Cms",
        "Server=localhost;FailoverPartner=sql.example.com;Database=Cms",
        "Server=;Database=Cms",
        "Database=Cms",
    ];

    /// <summary>Not valid connection strings at all.</summary>
    public static readonly string[] Malformed =
    [
        "Server=localhost;Database=\"Cms",
        "Server='localhost;Database=Cms",
        "Server=\"localhost\"x;Database=Cms",
        "Server=localhost\"",
        "Server",
        "=localhost",
    ];
}
