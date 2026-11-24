using System.Diagnostics;
using Xunit;

namespace RouteSyncWeb.Tests;

// Every dashboard script parses. A syntax error, such as a name declared twice with let,
// stops the whole file from running in the browser with nothing shown on the page.
// Runs node --check on each one; passes without checking where node is not installed.
public class ScriptSyntaxTests
{
    [Fact]
    public void EveryDashboardScriptParses()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "RouteSyncWeb", "wwwroot", "js")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var js = Path.Combine(dir!.FullName, "RouteSyncWeb", "wwwroot", "js");

        var failures = new List<string>();
        foreach (var file in Directory.GetFiles(js, "*.js"))
        {
            Process p;
            try
            {
                p = Process.Start(new ProcessStartInfo("node", $"--check \"{file}\"")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                })!;
            }
            catch (System.ComponentModel.Win32Exception) { return; } // node not installed
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) failures.Add($"{Path.GetFileName(file)}: {err}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
