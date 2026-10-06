using OptiCli.Core.Serve;
using OptiCli.Core.Users;

namespace OptiCli.Core.Tests.Users;

public class PasswordFilesTests
{
    [Fact]
    public void A_generated_password_has_every_kind_of_character_and_is_new_each_time()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => PasswordFiles.Generate()).ToList();

        Assert.All(passwords, p =>
        {
            Assert.Equal(24, p.Length);
            Assert.Contains(p, char.IsAsciiLetterLower);
            Assert.Contains(p, char.IsAsciiLetterUpper);
            Assert.Contains(p, char.IsAsciiDigit);
            Assert.Contains(p, c => !char.IsAsciiLetterOrDigit(c));
            Assert.DoesNotContain(p, c => c is 'l' or 'I' or 'O' or '0' or '1' || char.IsWhiteSpace(c));
        });
        Assert.Equal(passwords.Count, passwords.Distinct().Count());
    }

    [Fact]
    public void The_file_is_per_project_in_the_state_directory()
    {
        var project = Path.GetFullPath("/src/Site");

        var path = PasswordFiles.PathFor("/state", project + Path.DirectorySeparatorChar, "dev");

        Assert.Equal(Path.Combine("/state", "users", StateStore.Key(project), "dev.txt"), path);
    }

    [Theory]
    [InlineData("dev", "dev")]
    [InlineData("../dev", "_dev")]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("...", "user")]
    [InlineData("dev@example.com", "dev@example.com")]
    public void A_user_name_becomes_a_file_name_that_stays_in_its_folder(string name, string file)
    {
        Assert.Equal(file, PasswordFiles.FileName(name));
    }

    [Fact]
    public void Only_the_user_can_read_the_file_and_its_folders()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "users", "Site-1", "dev.txt");

        PasswordFiles.Write(path, "Secret-123");

        Assert.Equal("Secret-123" + Environment.NewLine, File.ReadAllText(path));
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(folder, File.GetUnixFileMode(Path.Combine(temp.Path, "users", "Site-1")));
            Assert.Equal(folder, File.GetUnixFileMode(Path.Combine(temp.Path, "users")));
        }
        Assert.True(PasswordFiles.Delete(path));
        Assert.False(PasswordFiles.Delete(path));
    }

    [Fact]
    public void A_file_with_a_looser_mode_is_replaced_not_reused()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var temp = new TempDirectory();
        var path = temp.Write("users/Site-1/dev.txt", "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        PasswordFiles.Write(path, "Secret-123");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal("Secret-123" + Environment.NewLine, File.ReadAllText(path));
    }

    [Fact]
    public void Pending_files_are_new_each_time_and_found_beside_the_file()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "users", "Site-1", "dev.txt");
        Assert.Empty(PasswordFiles.Pending(path));

        var first = PasswordFiles.NewPending(path);
        var second = PasswordFiles.NewPending(path);
        PasswordFiles.Write(first, "a");
        PasswordFiles.Write(second, "b");
        PasswordFiles.Write(Path.Combine(temp.Path, "users", "Site-1", "dev2.txt.0.pending"), "c");

        Assert.NotEqual(first, second);
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), PasswordFiles.Pending(path));
    }
}
