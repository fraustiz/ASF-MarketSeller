using System;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Web.GitHub;
using ArchiSteamFarm.Web.GitHub.Data;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

// Compares the running version with the latest GitHub release, for the page's update button.
// The update itself is done by ASF (IGitHubPluginUpdates + /Api/Plugins/Update), which downloads the release zip, replaces the DLL and restarts.
internal static class UpdateChecker {
	internal const string RepositoryName = "fraustiz/ASF-MarketSeller";

	// GitHub allows 60 anonymous API requests per hour and IP
	private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
	private static readonly SemaphoreSlim CheckSemaphore = new(1, 1);

	private static UpdateInfo? Cached;

	internal static async Task<UpdateInfo> GetAsync(bool refresh) {
		await CheckSemaphore.WaitAsync().ConfigureAwait(false);

		try {
			if (!refresh && (Cached != null) && (DateTime.UtcNow - Cached.CheckedAt < CacheDuration)) {
				return Cached;
			}

			return Cached = await CheckAsync().ConfigureAwait(false);
		} finally {
			CheckSemaphore.Release();
		}
	}

	private static async Task<UpdateInfo> CheckAsync() {
		Version currentVersion = Normalize(MarketSellerPlugin.PluginVersion);

		UpdateInfo result = new() {
			AssemblyName = typeof(UpdateChecker).Assembly.GetName().Name ?? throw new InvalidOperationException(nameof(UpdateInfo.AssemblyName)),
			CheckedAt = DateTime.UtcNow,
			CurrentVersion = Format(currentVersion),
			ReleasePage = new Uri($"https://github.com/{RepositoryName}/releases")
		};

		ReleaseResponse? release;

		try {
			release = await GitHubService.GetLatestRelease(RepositoryName).ConfigureAwait(false);
		} catch (Exception e) {
			return result with { Error = e.Message };
		}

		if (release == null) {
			return result with { Error = "GitHub ne répond pas, ou aucune version n'est publiée" };
		}

		Version latestVersion;

		try {
			// Same parsing as ASF's own plugin updater, which reads the tag name as the version
			latestVersion = Normalize(new Version(release.Tag));
		} catch (Exception e) when (e is ArgumentException or FormatException or OverflowException) {
			return result with { Error = $"numéro de version invalide sur GitHub : {release.Tag}" };
		}

		return result with {
			LatestVersion = Format(latestVersion),
			PublishedAt = release.PublishedAt,
			ReleaseNotes = string.IsNullOrWhiteSpace(release.MarkdownBody) ? null : release.MarkdownBody,
			ReleasePage = new Uri($"https://github.com/{RepositoryName}/releases/tag/{Uri.EscapeDataString(release.Tag)}"),
			UpdateAvailable = latestVersion > currentVersion
		};
	}

	private static string Format(Version version) => version.Revision > 0 ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}" : $"{version.Major}.{version.Minor}.{version.Build}";

	// "1.2.0" and "1.2.0.0" are the same version for us, System.Version disagrees
	private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
}
