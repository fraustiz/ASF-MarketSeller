using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Interaction;
using SteamKit2;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

internal sealed record BotState(bool ConfigPresent, MarketSellerConfig? Config, string? ConfigError, BotMarketSeller? Seller);

[Export(typeof(IPlugin))]
internal sealed class MarketSellerPlugin : IBot, IBotCardsFarmerInfo, IBotCommand2, IBotModules, IGitHubPluginUpdates, IWebInterface {
	// Set from the release tag by GitHub Actions, see Directory.Build.props
	internal static Version PluginVersion => typeof(MarketSellerPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(PluginVersion));

	private static readonly ConcurrentDictionary<Bot, BotState> States = new();

	[JsonInclude]
	public string Name => nameof(MarketSeller);

	// Read by ASF while it builds its web server, outside of any try/catch: a failure here would take down the whole web interface.
	// Any problem, including a framework method missing from a trimmed ASF build, only disables our page.
	public string PhysicalPath {
		get {
			try {
				return WebAssets.PrepareDirectory();
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericError($"{nameof(MarketSeller)} : page web désactivée, le reste du plugin continue de fonctionner. Erreur : {e.Message}");

				// ASF skips plugin web folders that don't exist
				return Path.Combine(Path.GetTempPath(), $"{nameof(MarketSeller)}-disabled");
			}
		}
	}

	// Lets ASF update the plugin from the GitHub releases: the page's update button, the updateplugins command, or PluginsUpdateList in ASF.json
	public string RepositoryName => UpdateChecker.RepositoryName;

	[JsonInclude]
	public Version Version => PluginVersion;

	[JsonInclude]
	public string WebPath => "/";

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(args);

		if (args.Length == 0) {
			return null;
		}

		EOperation? operation = args[0].ToUpperInvariant() switch {
			"MSELL" => EOperation.Sell,
			"MPREVIEW" => EOperation.SellPreview,
			"MREPRICE" => EOperation.Reprice,
			"MREPRICEPREVIEW" => EOperation.RepricePreview,
			_ => null
		};

		bool status = args[0].Equals("MSTATUS", StringComparison.OrdinalIgnoreCase);

		if (!operation.HasValue && !status) {
			return null;
		}

		if (args.Length == 1) {
			return access >= EAccess.Master ? Commands.FormatBotResponse(await Execute(bot, operation).ConfigureAwait(false), bot.BotName) : null;
		}

		string botNames = Utilities.GetArgsAsText(args, 1, ",");
		HashSet<Bot>? bots = Bot.GetBots(botNames);

		if ((bots == null) || (bots.Count == 0)) {
			return access >= EAccess.Owner ? Commands.FormatStaticResponse($"Aucun bot trouvé : {botNames}") : null;
		}

		IList<string?> results = await Utilities.InParallel(bots.Select(target => ExecuteWithAccess(target, Commands.GetProxyAccess(target, access, steamID), operation))).ConfigureAwait(false);

		List<string> responses = [.. results.Where(static result => !string.IsNullOrEmpty(result)).Select(static result => result!)];

		return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : null;
	}

	public Task OnBotDestroy(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		RemoveState(bot);

		return Task.CompletedTask;
	}

	public Task OnBotFarmingFinished(Bot bot, bool farmedSomething) {
		ArgumentNullException.ThrowIfNull(bot);

		if (farmedSomething && TryGetSeller(bot, out BotMarketSeller? seller) && seller.Config.SellOnFarmingFinished) {
			seller.TryStart(EOperation.Sell);
		}

		return Task.CompletedTask;
	}

	public Task OnBotFarmingStarted(Bot bot) => Task.CompletedTask;

	public Task OnBotFarmingStopped(Bot bot) => Task.CompletedTask;

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		ArgumentNullException.ThrowIfNull(bot);

		RemoveState(bot);

		if ((additionalConfigProperties == null) || !additionalConfigProperties.TryGetValue(MarketSellerConfig.PropertyName, out JsonElement json)) {
			States[bot] = new BotState(false, null, null, null);

			return Task.CompletedTask;
		}

		MarketSellerConfig? config = MarketSellerConfig.Parse(json, out string? error);

		if (config == null) {
			States[bot] = new BotState(true, null, error, null);

			bot.ArchiLogger.LogGenericError($"Configuration {MarketSellerConfig.PropertyName} invalide, plugin désactivé pour ce bot : {error}");

			return Task.CompletedTask;
		}

		if (!config.Enabled) {
			States[bot] = new BotState(true, config, null, null);

			return Task.CompletedTask;
		}

#pragma warning disable CA2000 // Owned by States from now on, disposed in RemoveState()
		States[bot] = new BotState(true, config, null, new BotMarketSeller(bot, config));
#pragma warning restore CA2000 // Owned by States from now on, disposed in RemoveState()

		bot.ArchiLogger.LogGenericInfo($"{nameof(MarketSeller)} activé : prix {config.Pricing.Source}, types {string.Join(", ", config.Types)}{(config.DryRun ? ", mode simulation" : "")}");

		if (config.AutoConfirm && !bot.HasMobileAuthenticator) {
			bot.ArchiLogger.LogGenericWarning($"{nameof(MarketSeller)} : pas d'authentificateur mobile ASF, les annonces devront être confirmées dans l'application Steam et le réajustement des prix est désactivé");
		}

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{nameof(MarketSeller)} v{Version} chargé");

		return Task.CompletedTask;
	}

	internal static BotDetails GetBotDetails(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		BotState? state = States.GetValueOrDefault(bot);

		return new BotDetails {
			Config = (state?.Config ?? new MarketSellerConfig()).ToJsonElement(),
			LastReprice = state?.Seller?.LastReprice,
			LastSell = state?.Seller?.LastSell,
			Overview = GetBotOverview(bot)
		};
	}

	internal static MarketSellerOverview GetOverview() {
		IEnumerable<Bot> bots = Bot.BotsReadOnly?.Values ?? [];

		return new MarketSellerOverview {
			Bots = bots.OrderBy(static bot => bot.BotName, StringComparer.OrdinalIgnoreCase).Select(GetBotOverview).ToList(),
			RateLimitedUntil = DateTime.UtcNow < SteamMarket.RateLimitedUntil ? SteamMarket.RateLimitedUntil : null,
			Version = PluginVersion.ToString()
		};
	}

	internal static bool TryGetSeller(Bot bot, [NotNullWhen(true)] out BotMarketSeller? seller) {
		ArgumentNullException.ThrowIfNull(bot);

		seller = States.GetValueOrDefault(bot)?.Seller;

		return seller != null;
	}

	private static async Task<string> Execute(Bot bot, EOperation? operation) {
		if (!TryGetSeller(bot, out BotMarketSeller? seller)) {
			return $"{nameof(MarketSeller)} n'est pas activé pour ce bot (section \"{MarketSellerConfig.PropertyName}\" avec \"Enabled\": true dans sa config).";
		}

		return operation.HasValue ? await seller.ExecuteAsync(operation.Value).ConfigureAwait(false) : seller.GetStatus();
	}

	private static async Task<string?> ExecuteWithAccess(Bot bot, EAccess access, EOperation? operation) => access >= EAccess.Master ? Commands.FormatBotResponse(await Execute(bot, operation).ConfigureAwait(false), bot.BotName) : null;

	private static BotOverview GetBotOverview(Bot bot) {
		BotState? state = States.GetValueOrDefault(bot);
		BotMarketSeller? seller = state?.Seller;

		return new BotOverview {
			BotName = bot.BotName,
			ConfigError = state?.ConfigError,
			ConfigPresent = state?.ConfigPresent ?? false,
			Connected = bot.IsConnectedAndLoggedOn,
			Currency = bot.WalletCurrency != ECurrencyCode.Invalid ? bot.WalletCurrency.ToString() : null,
			DryRun = state?.Config?.DryRun ?? false,
			Enabled = seller != null,
			HasMobileAuthenticator = bot.HasMobileAuthenticator,
			LastReprice = seller?.LastReprice is { } lastReprice ? new RunSummary { FinishedAt = lastReprice.FinishedAt, Operation = lastReprice.Operation, Text = ReportFormatter.Summarize(lastReprice) } : null,
			LastSell = seller?.LastSell is { } lastSell ? new RunSummary { FinishedAt = lastSell.FinishedAt, Operation = lastSell.Operation, Text = ReportFormatter.Summarize(lastSell) } : null,
			Progress = seller?.Progress
		};
	}

	private static void RemoveState(Bot bot) {
		if (States.TryRemove(bot, out BotState? state)) {
			state.Seller?.Dispose();
		}
	}
}
