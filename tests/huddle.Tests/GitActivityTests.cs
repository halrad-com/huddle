using Huddle;
namespace Huddle.Tests;

/// <summary>
/// Pure-function coverage for the git activity monitor: how raw remote-tracking
/// reflog lines and credential-request drops turn into console lines, what the
/// injected per-session credential config looks like, and that the credential
/// logger writes a drop without ever printing to stdout (which would break git's
/// auth fall-through to GCM).
/// </summary>
public class GitActivityTests
{
    // Raw reflog file line: "<old> <new> <name> <email> <unixts> <tz>\t<message>".
    private static string Reflog(string oldSha, string newSha, string message)
        => $"{oldSha} {newSha} Dev User <s@x> 1690000000 -0700\t{message}";

    private const string ShaOld = "bb2ebfeaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaNew = "7e70026bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Push_reflog_line_reads_as_pushed_to()
    {
        var line = GitActivityMonitor.FormatMovementLine("myapp", "origin/master",
            Reflog(ShaOld, ShaNew, "update by push"));
        Assert.Equal("[git] myapp pushed to origin/master (7e70026)", line);
    }

    [Fact]
    public void Fetch_reflog_line_reads_as_fetched_from()
    {
        var line = GitActivityMonitor.FormatMovementLine("otherapp", "origin/main",
            Reflog(ShaOld, ShaNew, "fetch"));
        Assert.Equal("[git] otherapp fetched from origin/main (7e70026)", line);
    }

    [Fact]
    public void Pull_reflog_line_reads_as_pulled_into()
    {
        var line = GitActivityMonitor.FormatMovementLine("LIB", "origin/master",
            Reflog(ShaOld, ShaNew, "pull: Fast-forward"));
        Assert.Equal("[git] LIB pulled into origin/master (7e70026)", line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no tab in this line so it is not a reflog entry")]
    public void Malformed_reflog_lines_produce_nothing(string raw)
    {
        Assert.Null(GitActivityMonitor.FormatMovementLine("repo", "origin/master", raw));
    }

    [Fact]
    public void Auth_drop_names_the_session_host_and_short_id()
    {
        var line = GitActivityMonitor.FormatAuthLine(
            "myapp:architect\t1c803fca-4c72-47be-9da7-ed5e0d7d29ae\thttps\tgithub.com");
        Assert.NotNull(line);
        Assert.Contains("myapp:architect", line);
        Assert.Contains("github.com", line);
        Assert.Contains("[1c803fca]", line);          // short session id, so the agent is traceable
        Assert.DoesNotContain("4c72", line);          // only the short prefix, not the whole guid
    }

    [Fact]
    public void Auth_drop_without_a_session_id_still_names_the_repo()
    {
        var line = GitActivityMonitor.FormatAuthLine("myapp\t\thttps\tgithub.com");
        Assert.NotNull(line);
        // No id tag between the repo and "is requesting" when there's no session id.
        Assert.Contains("myapp is requesting", line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("only-instance")]                          // no host
    [InlineData("\tsess\thttps\tgithub.com")]              // no instance
    [InlineData("app:architect\tsess\thttps\t")]           // no host
    public void Malformed_auth_drops_produce_nothing(string drop)
    {
        Assert.Null(GitActivityMonitor.FormatAuthLine(drop));
    }

    [Fact]
    public void Credential_config_resets_then_puts_logger_before_manager()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-cred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sysConfig = Path.Combine(dir, "system.gitconfig");
            File.WriteAllText(sysConfig, "[credential]\n\thelper = manager\n");
            var outPath = Path.Combine(dir, "session.gitconfig");

            GitActivityMonitor.WriteCredentialLoggerConfig(
                outPath, "C:\\tools\\huddle.exe", sysConfig, "myapp:architect", "1c803fca-guid", "C:\\repo\\ipc\\gitauth");

            var text = File.ReadAllText(outPath);
            var idxReset = text.IndexOf("helper =" + Environment.NewLine, StringComparison.Ordinal);
            var idxLogger = text.IndexOf("--cred-log", StringComparison.Ordinal);
            var idxManager = text.IndexOf("helper = manager", StringComparison.Ordinal);

            Assert.Contains("[include]", text);                 // real system config preserved
            Assert.True(idxReset >= 0, "expected an empty-reset 'helper =' line");
            Assert.True(idxLogger > idxReset, "logger must come after the reset");
            Assert.True(idxManager > idxLogger, "manager (GCM) must come after the logger");
            // Paths are forward-slashed (backslashes in a config value are escapes);
            // the exe path must be converted, not left with Windows separators.
            Assert.Contains("C:/tools/huddle.exe", text);
            Assert.DoesNotContain("C:\\tools", text);
            Assert.Contains("myapp:architect", text);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Credential_config_without_a_system_path_omits_the_include()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-cred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var outPath = Path.Combine(dir, "session.gitconfig");
            GitActivityMonitor.WriteCredentialLoggerConfig(
                outPath, "C:/tools/huddle.exe", null, "app:architect", "sess-guid", "C:/ipc/gitauth");

            var text = File.ReadAllText(outPath);
            Assert.DoesNotContain("[include]", text);
            Assert.Contains("helper = manager", text);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Cred_log_get_writes_a_drop_and_prints_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-authdrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var savedIn = Console.In;
        var savedOut = Console.Out;
        try
        {
            Console.SetIn(new StringReader("protocol=https\nhost=github.com\n\n"));
            var captured = new StringWriter();
            Console.SetOut(captured);

            var rc = GitActivityMonitor.RunCredLog(
                new[] { "--cred-log", "myapp:architect", "1c803fca-guid", dir, "get" });

            Console.SetOut(savedOut);
            Assert.Equal(0, rc);
            Assert.Equal("", captured.ToString());   // must not answer git — GCM does that

            var drops = Directory.GetFiles(dir, "auth-*.txt");
            var body = File.ReadAllText(Assert.Single(drops));
            Assert.Equal("myapp:architect\t1c803fca-guid\thttps\tgithub.com", body);
        }
        finally
        {
            Console.SetIn(savedIn);
            Console.SetOut(savedOut);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Cred_log_ignores_store_and_erase_operations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-authdrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var savedIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("protocol=https\nhost=github.com\n\n"));
            var rc = GitActivityMonitor.RunCredLog(
                new[] { "--cred-log", "app:architect", "some-guid", dir, "store" });

            Assert.Equal(0, rc);
            Assert.Empty(Directory.GetFiles(dir, "auth-*.txt"));   // only 'get' is a request
        }
        finally
        {
            Console.SetIn(savedIn);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
