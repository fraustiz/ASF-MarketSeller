using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Web;
using ArchiSteamFarm.Web.Responses;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

internal readonly record struct LatestRelease(Version? Version, string? Tag, string? Error);

// Finds the latest GitHub release for the page's update button and for ASF's plugin updater (see MarketSellerPlugin.GetTargetReleaseURL).
// Uses github.com/<repo>/releases/latest, which redirects to /releases/tag/<tag>, rather than api.github.com:
// the API only allows 60 anonymous requests per hour and IP, shared by everything on the same network.
internal static class UpdateChecker {
	internal const string ReleaseAssetName = "MarketSeller.zip";
	internal const string RepositoryName = "fraustiz/ASF-MarketSeller";

	private const string TagPathMarker = "/releases/tag/";

	private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
	private static readonly SemaphoreSlim CheckSemaphore = new(1, 1);
	private static readonly Uri LatestReleasePage = new($"https://github.com/{RepositoryName}/releases/latest");

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

	internal static Uri GetDownloadURL(string tag) {
		ArgumentException.ThrowIfNullOrEmpty(tag);

		return new Uri($"https://github.com/{RepositoryName}/releases/download/{Uri.EscapeDataString(tag)}/{ReleaseAssetName}");
	}

	internal static async Task<LatestRelease> GetLatestReleaseAsync() {
		WebBrowser? webBrowser = ASF.WebBrowser;

		if (webBrowser == null) {
			return new LatestRelease(null, null, "ASF n'a pas fini de démarrer");
		}

		BasicResponse? response = await webBrowser.UrlHead(LatestReleasePage, requestOptions: WebBrowser.ERequestOptions.ReturnClientErrors | WebBrowser.ERequestOptions.ReturnServerErrors, maxTries: 2).ConfigureAwait(false);

		if (response == null) {
			return new LatestRelease(null, null, "GitHub ne répond pas, vérifie la connexion d'ASF à Internet");
		}

		if (response.StatusCode == HttpStatusCode.TooManyRequests) {
			return new LatestRelease(null, null, "GitHub limite les requêtes, réessaie dans quelques minutes");
		}

		// ASF follows the redirection itself, the final address carries the tag
		string path = response.FinalUri.AbsolutePath;
		int markerIndex = path.IndexOf(TagPathMarker, StringComparison.Ordinal);

		if (markerIndex < 0) {
			return new LatestRelease(null, null, (int) response.StatusCode is >= 200 and < 300 ? "aucune version publiée sur GitHub" : $"GitHub a répondu HTTP {(int) response.StatusCode}");
		}

		string tag = Uri.UnescapeDataString(path[(markerIndex + TagPathMarker.Length)..].TrimEnd('/'));

		try {
			// Same rule as ASF's own plugin updater: the tag name is the version
			return new LatestRelease(new Version(tag), tag, null);
		} catch (Exception e) when (e is ArgumentException or FormatException or OverflowException) {
			return new LatestRelease(null, tag, $"numéro de version invalide sur GitHub : {tag}");
		}
	}

	// "1.2.0" and "1.2.0.0" are the same version for us, System.Version disagrees
	internal static Version Normalize(Version version) {
		ArgumentNullException.ThrowIfNull(version);

		return new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
	}

	private static async Task<UpdateInfo> CheckAsync() {
		Version currentVersion = Normalize(MarketSellerPlugin.PluginVersion);

		UpdateInfo result = new() {
			AssemblyName = typeof(UpdateChecker).Assembly.GetName().Name ?? throw new InvalidOperationException(nameof(UpdateInfo.AssemblyName)),
			CheckedAt = DateTime.UtcNow,
			CurrentVersion = Format(currentVersion),
			ReleasePage = new Uri($"https://github.com/{RepositoryName}/releases")
		};

		LatestRelease latest = await GetLatestReleaseAsync().ConfigureAwait(false);

		if (latest is not { Version: { } latestVersion, Tag: { } tag }) {
			return result with { Error = latest.Error };
		}

		latestVersion = Normalize(latestVersion);

		return result with {
			LatestVersion = Format(latestVersion),
			ReleasePage = new Uri($"https://github.com/{RepositoryName}/releases/tag/{Uri.EscapeDataString(tag)}"),
			UpdateAvailable = latestVersion > currentVersion
		};
	}

	private static string Format(Version version) => version.Revision > 0 ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}" : $"{version.Major}.{version.Minor}.{version.Build}";
}
