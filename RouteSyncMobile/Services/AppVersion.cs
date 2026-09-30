using System.Reflection;

namespace FleetWiseMobile.Services
{
    /// <summary>The RouteSync release this build was made from, as the driver app shows it.</summary>
    /// <remarks>
    /// The number comes from the git tag at build time (see Directory.Build.props). A release
    /// reads as its number alone. A build between releases reads as a preview of the next one
    /// with the commit it was made from, so a screenshot says exactly which code it shows.
    /// </remarks>
    public static class AppVersion
    {
        /// <summary>"1.2.0" for a release, "1.3.0 preview · 3f14144" for a build between releases.</summary>
        public static string Text { get; } = TextFor(
            typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        /// <summary>The shown version for an informational version such as 1.3.0-alpha.0.4+3f14144…</summary>
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
