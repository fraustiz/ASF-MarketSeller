using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ArchiSteamFarm.Core;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

// ASF-ui has no extension point for plugins, so we serve its own index.html with one extra script that adds our page to it.
// Everything is rebuilt from the embedded resources and ASF's current www folder each time ASF starts its web server,
// which keeps the page working after ASF updates and makes the DLL the only file to deploy.
internal static class WebAssets {
	internal const string AssetsWebDirectory = "market-seller";

	private static readonly string[] AssetNames = ["app.css", "app.js", "index.html"];
	private static readonly Lock PrepareLock = new();

	internal static string PrepareDirectory() {
		string directory = Path.Combine(Path.GetTempPath(), $"ASF-MarketSeller-{GetInstanceID()}");

		lock (PrepareLock) {
			try {
				string assetsDirectory = Path.Combine(directory, AssetsWebDirectory);

				Directory.CreateDirectory(assetsDirectory);

				foreach (string assetName in AssetNames) {
					WriteResource(assetName, Path.Combine(assetsDirectory, assetName));
				}

				string indexPath = Path.Combine(directory, "index.html");
				string? asfIndexPath = FindAsfIndex();
				string? injectedIndex = asfIndexPath != null ? InjectLoader(File.ReadAllText(asfIndexPath)) : null;

				if (injectedIndex != null) {
					// File.WriteAllText() is trimmed away from ASF's Docker image
					File.WriteAllBytes(indexPath, Encoding.UTF8.GetBytes(injectedIndex));
				} else {
					// Without ASF-ui the page is still reachable on its own at /market-seller/index.html
					File.Delete(indexPath);

					ASF.ArchiLogger.LogGenericWarning($"{nameof(MarketSeller)} : interface web d'ASF introuvable, la page est disponible seule sur /{AssetsWebDirectory}/index.html");
				}
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		return directory;
	}

	internal static string? InjectLoader(string asfIndex) {
		ArgumentNullException.ThrowIfNull(asfIndex);

		int bodyEnd = asfIndex.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

		if (bodyEnd < 0) {
			return null;
		}

		string version = MarketSellerPlugin.PluginVersion.ToString();

		// Waits for ASF-ui to boot, then loads our script from the same base path ASF-ui ended up using (it supports reverse proxy prefixes)
		string loader = $$"""
			<script>
			  window.__MARKET_SELLER__ = { initialPath: window.location.pathname, version: '{{version}}' };
			  (function () {
			    var deadline = Date.now() + 60000;
			    (function waitForAsfUi() {
			      if (window.__ASF_UI_LOADED__ && document.querySelector('.app')) {
			        var script = document.createElement('script');
			        script.src = (window.__BASE_PATH__ || '/') + '{{AssetsWebDirectory}}/app.js?v={{version}}';
			        document.head.appendChild(script);
			      } else if (Date.now() < deadline) {
			        setTimeout(waitForAsfUi, 100);
			      }
			    })();
			  })();
			</script>

			""";

		return asfIndex.Insert(bodyEnd, loader);
	}

	// Same lookup order as ASF itself: custom www in the working directory first, bundled one next
	private static string? FindAsfIndex() {
		foreach (string root in (string[]) [Directory.GetCurrentDirectory(), AppContext.BaseDirectory]) {
			string candidate = Path.Combine(root, "www", "index.html");

			if (File.Exists(candidate)) {
				return candidate;
			}
		}

		return null;
	}

	// Separates several ASF instances running on the same machine
	private static string GetInstanceID() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory)))[..12];

	private static void WriteResource(string assetName, string targetPath) {
		string resourceName = $"{nameof(MarketSeller)}.www.{assetName}";

		using Stream resource = typeof(WebAssets).Assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException(resourceName);
		using FileStream target = File.Create(targetPath);

		resource.CopyTo(target);
	}
}
