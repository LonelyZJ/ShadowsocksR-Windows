using Shadowsocks.Model;
using System;
using System.Reflection;
using System.Threading.Tasks;
using UpdateChecker;

namespace Shadowsocks.Controller.HttpRequest
{
    public class UpdateChecker : HttpRequest
    {
        internal const string Owner = @"LonelyZJ";
        internal const string Repo = @"ShadowsocksR-Windows";
        private const string UpdateProbeVersion = @"0.0.0";

        public string LatestVersionNumber;
        public string LatestVersionUrl;

        public bool Found;

        public event EventHandler NewVersionFound;
        public event EventHandler NewVersionFoundFailed;
        public event EventHandler NewVersionNotFound;

        public const string Name = @"ShadowsocksR";
        public const string Copyright = @"Copyright © 2019 - 2022 HMBSbige. Forked from ShadowsocksR by BreakWa11";
        public const string AssemblyVersion = @"6.1.0.0";
        public const string ReleaseVersion = @"6.1.0-net10";
        public const string Version = ReleaseVersion;
        public const string DocumentationUrl = @"https://github.com/HMBSbige/ShadowsocksR-Windows/wiki";
        public const string FeedbackUrl = @"https://github.com/LonelyZJ/ShadowsocksR-Windows/issues/new/choose";

        private const string BuildFlavor =
#if SelfContained
#if Is64Bit
            @" x64" +
#else
            @" x86" +
#endif
#endif
#if DEBUG
        @" Debug";
#else
        @"";
#endif

        public static string FullVersion => $@"{InformationalVersion}{BuildFlavor}";

        private static string InformationalVersion
        {
            get
            {
                var value = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                return string.IsNullOrWhiteSpace(value) ? ReleaseVersion : value;
            }
        }

        public async Task CheckAsync(Configuration config, bool notifyNoFound)
        {
            try
            {
                var updater = new GitHubReleasesUpdateChecker(
                    Owner,
                    Repo,
                    config.IsPreRelease,
                    UpdateProbeVersion);

                var userAgent = config.ProxyUserAgent;
                var proxy = CreateProxy(config);
                using var client = CreateClient(true, proxy, userAgent, config.ConnectTimeout * 1000);

                await updater.CheckAsync(client, default);
                LatestVersionNumber = updater.LatestVersion;
                Found = IsReleaseNewer(ReleaseVersion, LatestVersionNumber);
                if (Found)
                {
                    LatestVersionUrl = updater.LatestVersionUrl;
                    NewVersionFound?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    if (notifyNoFound)
                    {
                        NewVersionNotFound?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch (Exception e)
            {
                Logging.LogUsefulException(e);
                if (notifyNoFound)
                {
                    NewVersionFoundFailed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        internal static bool IsSameRelease(string left, string right)
        {
            return TryParseReleaseTag(left, out var leftTag)
                && TryParseReleaseTag(right, out var rightTag)
                && string.Equals(leftTag.Normalized, rightTag.Normalized, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsReleaseNewer(string currentTag, string latestTag)
        {
            if (!TryParseReleaseTag(currentTag, out var current)
                || !TryParseReleaseTag(latestTag, out var latest))
            {
                return false;
            }

            var versionCompare = latest.Version.CompareTo(current.Version);
            return versionCompare > 0;
        }

        private static bool TryParseReleaseTag(string tag, out ReleaseTag releaseTag)
        {
            releaseTag = default;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            var normalized = tag.Trim();
            const string refsTagsPrefix = @"refs/tags/";
            if (normalized.StartsWith(refsTagsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[refsTagsPrefix.Length..];
            }

            if (normalized.StartsWith(@"v", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[1..];
            }

            var numericPart = normalized.Split('-', 2)[0];
            var numericSegments = numericPart.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (numericSegments.Length is < 2 or > 4)
            {
                return false;
            }

            var versionParts = new int[4];
            for (var i = 0; i < numericSegments.Length; i++)
            {
                if (!int.TryParse(numericSegments[i], out versionParts[i]))
                {
                    return false;
                }
            }

            releaseTag = new ReleaseTag(
                normalized,
                new Version(versionParts[0], versionParts[1], versionParts[2], versionParts[3]));
            return true;
        }

        private readonly record struct ReleaseTag(string Normalized, Version Version);
    }
}
