using System.Reflection;

namespace FleetWiseMobile.Services
{
    // The RouteSync release this build was made from, as the driver app shows it.
    //
    // The number comes from the git tag at build time, read by MinVer (see the project file). A release
    // reads as its number alone. A build between releases reads as a preview of the next one
    // with the commit it was made from, so a screenshot says exactly which code it shows.
    public static class AppVersion
    {
        // "1.2.0" for a release, "1.3.0 preview · 3f14144" for a build between releases.
        public static string Text { get; } = TextFor(
            typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        // The shown version for an informational version such as 1.3.0-alpha.0.4+3f14144…
        public static string TextFor(string? informational)
        {
            if (string.IsNullOrWhiteSpace(informational)) return "";

            var plus = informational.IndexOf('+');
            var version = plus < 0 ? informational : informational[..plus];
            var commit = plus < 0 ? null : informational[(plus + 1)..];
            if (commit is { Length: > 7 }) commit = commit[..7];

            var dash = version.IndexOf('-');
            if (dash < 0) return version;

            return string.IsNullOrEmpty(commit)
                ? $"{version[..dash]} preview"
                : $"{version[..dash]} preview · {commit}";
        }
    }
}
