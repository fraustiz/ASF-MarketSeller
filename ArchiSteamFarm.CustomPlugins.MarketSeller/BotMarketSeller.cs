using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Data;
using SteamKit2;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

internal readonly record struct SellCandidate(Asset Asset, uint Amount);

internal readonly record struct QuoteResult(PriceQuote? Quote, string? Reason, uint? HighestBuyOrder = null, uint? LowestSellOrder = null);

internal sealed class BotMarketSeller : IDisposable {
	private const byte ConfirmationBatchSize = 10;

	private static readonly TimeSpan InitialSellDelay = TimeSpan.FromMinutes(5);
	private static readonly TimeSpan RelistDelay = TimeSpan.FromSeconds(5);

	internal MarketSellerConfig Config { get; }
	internal RepriceRunResult? LastReprice { get; private set; }
	internal SellRunResult? LastSell { get; private set; }
	internal OperationProgress? Progress => ProgressTracker.Snapshot;

	private readonly Bot Bot;
	private readonly ListingTracker Listings;
	private readonly ProgressTracker ProgressTracker = new();
	private readonly Timer? RepriceTimer;

	// Not disposed on purpose, a run may still be in progress when the bot gets destroyed and it holds no unmanaged resources
	[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "See comment above")]
	private readonly SemaphoreSlim RunSemaphore = new(1, 1);

	private readonly Timer? SellTimer;

	internal BotMarketSeller(Bot bot, MarketSellerConfig config) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(config);

		Bot = bot;
		Config = config;
		Listings = new ListingTracker(bot);

		if (config.SellIntervalMinutes > 0) {
			SellTimer = new Timer(OnSellTimer, null, InitialSellDelay, TimeSpan.FromMinutes(config.SellIntervalMinutes));
		}

		if (config.RepriceIntervalMinutes > 0) {
			TimeSpan period = TimeSpan.FromMinutes(config.RepriceIntervalMinutes);

			RepriceTimer = new Timer(OnRepriceTimer, null, period, period);
		}
	}

	public void Dispose() {
		SellTimer?.Dispose();
		RepriceTimer?.Dispose();
	}

	// Waits for the end of the run, used by chat commands
	internal async Task<string> ExecuteAsync(EOperation operation) {
		if (!Enum.IsDefined(operation)) {
			throw new ArgumentOutOfRangeException(nameof(operation));
		}

		if (!await RunSemaphore.WaitAsync(0).ConfigureAwait(false)) {
			return $"{ReportFormatter.GetTitle(operation)} : une opération MarketSeller est déjà en cours pour ce bot.";
		}

		return await RunAcquiredAsync(operation).ConfigureAwait(false);
	}

	internal string GetStatus() {
		PricingConfig pricing = Config.Pricing;

		List<string> lines = [
			$"Prix : {pricing.Source}{(pricing.Source == EPriceSource.Fixed ? $" {pricing.FixedCents} cts" : "")}, x{pricing.Multiplier.ToString(CultureInfo.InvariantCulture)} {pricing.OffsetCents:+0;-0;+0} cts, min {pricing.MinCents} cts{(pricing.MaxCents > 0 ? $", max {pricing.MaxCents} cts" : "")}",
			$"Types : {string.Join(", ", Config.Types)}, garder {Config.KeepPerItem} par objet{(Config.DryRun ? ", mode simulation" : "")}",
			$"Vente toutes les {FormatInterval(Config.SellIntervalMinutes)}, réajustement toutes les {FormatInterval(Config.RepriceIntervalMinutes)} (écart min {Config.RepriceThresholdCents} cts)"
		];

		if (DateTime.UtcNow < SteamMarket.RateLimitedUntil) {
			lines.Add($"Limite de requêtes Steam active jusqu'à {SteamMarket.RateLimitedUntil.ToLocalTime():HH:mm}");
		}

		if (Progress is { } progress) {
			lines.Add($"En cours : {ReportFormatter.GetTitle(progress.Operation)}, {progress.Stage}{(progress.Total > 0 ? $" ({progress.Done}/{progress.Total})" : "")}");
		}

		if (LastSell != null) {
			lines.Add($"Dernière vente ({LastSell.FinishedAt.ToLocalTime():yyyy-MM-dd HH:mm}) : {ReportFormatter.Summarize(LastSell)}");
		}

		if (LastReprice != null) {
			lines.Add($"Dernier réajustement ({LastReprice.FinishedAt.ToLocalTime():yyyy-MM-dd HH:mm}) : {ReportFormatter.Summarize(LastReprice)}");
		}

		return string.Join(Environment.NewLine, lines);
	}

	// Starts the run in the background, used by the web page, timers and farming events
	internal bool TryStart(EOperation operation) {
		if (!Enum.IsDefined(operation)) {
			throw new ArgumentOutOfRangeException(nameof(operation));
		}

		if (!RunSemaphore.Wait(0)) {
			return false;
		}

		Utilities.InBackground(() => RunAcquiredInBackgroundAsync(operation));

		return true;
	}

	private static string FormatInterval(ushort minutes) => minutes > 0 ? $"{minutes} min" : "jamais";

	private static Dictionary<string, Dictionary<uint, uint>> GroupOwnPrices(IEnumerable<OwnListing> listings) {
		Dictionary<string, Dictionary<uint, uint>> result = new(StringComparer.Ordinal);

		foreach (OwnListing listing in listings) {
			if (string.IsNullOrEmpty(listing.MarketHashName)) {
				continue;
			}

			if (!result.TryGetValue(listing.MarketHashName, out Dictionary<uint, uint>? prices)) {
				prices = [];
				result[listing.MarketHashName] = prices;
			}

			prices[listing.BuyerPrice] = prices.GetValueOrDefault(listing.BuyerPrice) + 1;
		}

		return result;
	}

	// Enumerable.Min<T>() is trimmed away from ASF's Docker image
	private static uint GetLowestPrice(IEnumerable<uint> prices) {
		uint lowest = uint.MaxValue;

		foreach (uint price in prices) {
			lowest = Math.Min(lowest, price);
		}

		return lowest;
	}

	private static uint SumAmounts(IEnumerable<SellCandidate> candidates) => (uint) Math.Min(candidates.Sum(static candidate => (long) candidate.Amount), uint.MaxValue);

	private string? CheckReady() {
		if (!Bot.IsConnectedAndLoggedOn) {
			return "le bot n'est pas connecté.";
		}

		if (Bot.WalletCurrency == ECurrencyCode.Invalid) {
			return "devise du portefeuille Steam inconnue, le compte doit avoir un portefeuille pour utiliser le marché.";
		}

		return null;
	}

	// Returns the number of confirmed listings, or null when they have to be confirmed manually
	private async Task<uint?> ConfirmListingsAsync() {
		if (!Config.AutoConfirm || !Bot.HasMobileAuthenticator) {
			return null;
		}

		ProgressTracker.Update("Confirmation des annonces");

		(bool success, IReadOnlyCollection<Confirmation>? handledConfirmations, string message) = await Bot.Actions.HandleTwoFactorAuthenticationConfirmations(true, EMobileConfirmationType.MarketListing, waitIfNeeded: true).ConfigureAwait(false);

		if (!success) {
			Bot.ArchiLogger.LogGenericWarning($"Confirmation des annonces : {message}");
		}

		// The creator of a market listing confirmation is the listing itself
		if (handledConfirmations != null) {
			Listings.Add(handledConfirmations.Where(static confirmation => confirmation.ConfirmationType == EMobileConfirmationType.MarketListing).Select(static confirmation => confirmation.CreatorID));
		}

		return (uint) (handledConfirmations?.Count ?? 0);
	}

	private EOperation GetEffectiveOperation(EOperation operation) {
		if (!Config.DryRun) {
			return operation;
		}

		return operation switch {
			EOperation.Sell => EOperation.SellPreview,
			EOperation.Reprice => EOperation.RepricePreview,
			_ => operation
		};
	}

	private async Task<ulong?> GetItemNameIDAsync(string marketHashName) {
		if (ItemCache.Get(marketHashName) is { NameID: > 0 } cachedItem) {
			return cachedItem.NameID;
		}

		ulong? nameID = await SteamMarket.GetItemNameIDAsync(Bot, marketHashName, Config.RequestDelayMilliseconds).ConfigureAwait(false);

		if (nameID.HasValue) {
			ItemCache.SetNameID(marketHashName, nameID.Value);
		}

		return nameID;
	}

	private async Task<QuoteResult> GetQuoteAsync(string marketHashName, IReadOnlyDictionary<uint, uint>? ownPrices) {
		PricingConfig pricing = Config.Pricing;
		ushort delay = Config.RequestDelayMilliseconds;

		switch (pricing.Source) {
			case EPriceSource.Fixed:
				return new QuoteResult(MarketPricing.CreateQuote(pricing.FixedCents, pricing), null);
			case EPriceSource.AverageSold:
				List<PriceHistoryEntry>? history = await SteamMarket.GetPriceHistoryAsync(Bot, marketHashName, delay).ConfigureAwait(false);

				if (history == null) {
					return new QuoteResult(null, "historique des ventes indisponible");
				}

				uint? averagePrice = MarketPricing.GetAverageSoldPrice(history, DateTime.UtcNow.AddDays(-pricing.AverageDays));

				return averagePrice.HasValue ? new QuoteResult(MarketPricing.CreateQuote(averagePrice.Value, pricing), null) : new QuoteResult(null, $"aucune vente sur les {pricing.AverageDays} derniers jours");
			case EPriceSource.HighestBuyOrder:
			case EPriceSource.LowestSellOrder:
				ulong? nameID = await GetItemNameIDAsync(marketHashName).ConfigureAwait(false);

				if (!nameID.HasValue) {
					return new QuoteResult(null, "identifiant de l'objet sur le marché introuvable, voir le log d'ASF");
				}

				OrderHistogram? histogram = await SteamMarket.GetOrderHistogramAsync(Bot, nameID.Value, Config.Country, Bot.WalletCurrency, delay).ConfigureAwait(false);

				if (histogram == null) {
					return new QuoteResult(null, "carnet d'ordres indisponible");
				}

				uint? competitorPrice = MarketPricing.GetLowestCompetitorPrice(histogram.SellOrderGraph, ownPrices);
				uint? highestBuyOrder = histogram.HighestBuyOrder is > 0 ? histogram.HighestBuyOrder : null;
				uint? lowestSellOrder = competitorPrice ?? (ownPrices is not { Count: > 0 } && histogram.LowestSellOrder is > 0 ? histogram.LowestSellOrder : null);

				if (pricing.Source == EPriceSource.HighestBuyOrder) {
					return highestBuyOrder.HasValue ? new QuoteResult(MarketPricing.CreateQuote(highestBuyOrder.Value, pricing), null, highestBuyOrder, lowestSellOrder) : new QuoteResult(null, "aucun ordre d'achat", null, lowestSellOrder);
				}

				if (lowestSellOrder.HasValue) {
					return new QuoteResult(MarketPricing.CreateQuote(lowestSellOrder.Value, pricing), null, highestBuyOrder, lowestSellOrder);
				}

				// Nobody else sells it, keep our own price as-is: adjusting it would make us undercut ourselves at every run
				if (ownPrices is { Count: > 0 }) {
					return new QuoteResult(MarketPricing.CreateQuote(GetLowestPrice(ownPrices.Keys), pricing, false), null, highestBuyOrder);
				}

				return new QuoteResult(null, "aucune annonce en vente pour comparer", highestBuyOrder);
			default:
				throw new InvalidOperationException(nameof(pricing.Source));
		}
	}

	// Whether the listing passes the same filters as inventory items, whoever created it
	private bool IsManaged(OwnListing listing) {
		if ((listing.AppID != Asset.SteamAppID) || (listing.ContextID != Asset.SteamCommunityContextID) || string.IsNullOrEmpty(listing.MarketHashName)) {
			return false;
		}

		CachedItem? item = ItemCache.Get(listing.MarketHashName);

		// Items seen in the inventory are classified by ASF from Steam's tags, the others (e.g. listed by hand) from the listing's type line
		if (item is not { Type: not EAssetType.Unknown }) {
			if (listing.Type == EAssetType.Unknown) {
				return false;
			}

			ItemCache.Remember(listing.MarketHashName, listing.Type, listing.Rarity, listing.RealAppID, listing.Name ?? listing.MarketHashName, listing.IconHash);

			item = ItemCache.Get(listing.MarketHashName);
		}

		return (item != null) && Config.Types.Contains(item.Type) && !Config.Lock.IsItemLocked(listing.MarketHashName, item.Name, item.Rarity, item.RealAppID);
	}

	private async Task<List<Asset>?> LoadInventoryAsync() {
		List<Asset> result = [];

		try {
			await foreach (Asset asset in Bot.ArchiHandler.GetMyInventoryAsync(Asset.SteamAppID, Asset.SteamCommunityContextID, marketableOnly: true).ConfigureAwait(false)) {
				result.Add(asset);
			}
		} catch (Exception e) {
			Bot.ArchiLogger.LogGenericWarningException(e);

			return null;
		}

		return result;
	}

	private void OnRepriceTimer(object? state) => StartFromSchedule(EOperation.Reprice);

	private void OnSellTimer(object? state) => StartFromSchedule(EOperation.Sell);

	private async Task<RepriceRunResult> RepriceCoreAsync(EOperation operation) {
		bool dryRun = operation == EOperation.RepricePreview;
		DateTime startedAt = DateTime.UtcNow;
		string? currency = Bot.WalletCurrency != ECurrencyCode.Invalid ? Bot.WalletCurrency.ToString() : null;
		List<RepriceLine> lines = [];
		DateTime? rateLimitedUntil = null;

		RepriceRunResult Finish(string? error = null, SellRunResult? relist = null) => new() {
			Currency = currency,
			Error = error,
			FinishedAt = DateTime.UtcNow,
			Lines = lines,
			Operation = operation,
			RateLimitedUntil = rateLimitedUntil,
			Relist = relist,
			StartedAt = startedAt
		};

		string? problem = CheckReady();

		if (problem != null) {
			return Finish(problem);
		}

		if (!dryRun && (!Config.AutoConfirm || !Bot.HasMobileAuthenticator)) {
			return Finish("le réajustement nécessite l'authentificateur mobile ASF et AutoConfirm, sinon les objets remis en vente resteraient en attente de confirmation.");
		}

		Dictionary<string, PriceQuote> quotes = new(StringComparer.Ordinal);
		uint removed = 0;

		try {
			ProgressTracker.Update("Lecture de tes annonces");

			List<OwnListing>? listings = await SteamMarket.GetMyListingsAsync(Bot, Config.RequestDelayMilliseconds).ConfigureAwait(false);

			if (listings == null) {
				return Finish("impossible de lire tes annonces en cours.");
			}

			Dictionary<string, Dictionary<uint, uint>> ownPrices = GroupOwnPrices(listings);
			List<IGrouping<string, OwnListing>> managedListings = listings.Where(IsManaged).GroupBy(static listing => listing.MarketHashName!, StringComparer.Ordinal).ToList();
			uint done = 0;

			foreach (IGrouping<string, OwnListing> group in managedListings) {
				CachedItem? item = ItemCache.Get(group.Key);
				string name = item?.Name is { Length: > 0 } cachedName ? cachedName : group.Key;

				ProgressTracker.Update("Vérification des prix", done++, (uint) managedListings.Count, name);

				QuoteResult quoteResult = await GetQuoteAsync(group.Key, ownPrices.GetValueOrDefault(group.Key)).ConfigureAwait(false);

				RepriceLine CreateLine(OwnListing listing, ERepriceStatus status, uint? targetPrice = null, string? reason = null, uint? adjustedPrice = null) => new() {
					AdjustedPrice = adjustedPrice,
					CreatedByPlugin = Listings.IsCreatedByPlugin(listing.ListingID),
					CurrentBuyerPrice = listing.BuyerPrice,
					IconHash = item?.IconHash,
					ListingID = listing.ListingID.ToString(CultureInfo.InvariantCulture),
					MarketHashName = group.Key,
					Name = name,
					Reason = reason,
					Status = status,
					TargetBuyerPrice = targetPrice
				};

				if (quoteResult.Quote is not { } quote) {
					lines.AddRange(group.Select(listing => CreateLine(listing, ERepriceStatus.Skipped, reason: quoteResult.Reason)));

					continue;
				}

				bool priceLocked = Config.Lock.IsPriceLocked(quote.AdjustedPrice);

				if (!priceLocked) {
					quotes[group.Key] = quote;
				}

				foreach (OwnListing listing in group) {
					// The user listed it by hand, so they want it sold: a price lock doesn't take it off the market
					if (priceLocked && !Listings.IsCreatedByPlugin(listing.ListingID)) {
						lines.Add(CreateLine(listing, ERepriceStatus.Kept, reason: $"verrouillé par prix ({ReportFormatter.FormatPrice(quote.AdjustedPrice, currency)}), annonce créée à la main", adjustedPrice: quote.AdjustedPrice));

						continue;
					}

					if (!priceLocked && (Math.Abs((long) listing.BuyerPrice - quote.BuyerPrice) < Config.RepriceThresholdCents)) {
						lines.Add(CreateLine(listing, ERepriceStatus.Unchanged, quote.BuyerPrice));

						continue;
					}

					if (!dryRun) {
						ProgressTracker.Update("Retrait des annonces", done, (uint) managedListings.Count, name);

						if (!await SteamMarket.RemoveListingAsync(Bot, listing.ListingID, Config.RequestDelayMilliseconds).ConfigureAwait(false)) {
							lines.Add(CreateLine(listing, ERepriceStatus.Failed, quote.BuyerPrice, "Steam a refusé le retrait de l'annonce"));

							continue;
						}

						removed++;
					}

					lines.Add(priceLocked ? CreateLine(listing, ERepriceStatus.Withdrawn, reason: $"verrouillé par prix ({ReportFormatter.FormatPrice(quote.AdjustedPrice, currency)})", adjustedPrice: quote.AdjustedPrice) : CreateLine(listing, ERepriceStatus.Repriced, quote.BuyerPrice));
				}
			}
		} catch (MarketRateLimitedException) {
			rateLimitedUntil = SteamMarket.RateLimitedUntil;
		} finally {
			// Items learned from hand-made listings
			ItemCache.Save();
		}

		if (dryRun || (removed == 0)) {
			return Finish();
		}

		if (rateLimitedUntil.HasValue) {
			return Finish("les objets retirés seront remis en vente à la prochaine vente, Steam limite les requêtes du marché.");
		}

		// Give Steam a moment to put the items back into the inventory
		ProgressTracker.Update("Remise en vente");

		await Task.Delay(RelistDelay).ConfigureAwait(false);

		SellRunResult relist = await SellCoreAsync(EOperation.Sell, quotes).ConfigureAwait(false);

		return Finish(relist: relist);
	}

	private async Task<string> RunAcquiredAsync(EOperation operation) {
		EOperation effectiveOperation = GetEffectiveOperation(operation);
		string text;

		try {
			ProgressTracker.Start(effectiveOperation);

			switch (effectiveOperation) {
				case EOperation.SellPreview:
				case EOperation.Sell:
					SellRunResult sellResult = await SellCoreAsync(effectiveOperation).ConfigureAwait(false);

					LastSell = sellResult;
					text = ReportFormatter.Format(sellResult);

					break;
				case EOperation.RepricePreview:
				case EOperation.Reprice:
					RepriceRunResult repriceResult = await RepriceCoreAsync(effectiveOperation).ConfigureAwait(false);

					LastReprice = repriceResult;
					text = ReportFormatter.Format(repriceResult);

					break;
				default:
					throw new InvalidOperationException(nameof(operation));
			}
		} catch (Exception e) {
			Bot.ArchiLogger.LogGenericException(e);

			text = $"erreur inattendue ({e.Message}).";
		} finally {
			ProgressTracker.Stop();
			RunSemaphore.Release();
		}

		text = $"{ReportFormatter.GetTitle(effectiveOperation)} : {text}";

		Bot.ArchiLogger.LogGenericInfo(text);

		return text;
	}

	private async Task RunAcquiredInBackgroundAsync(EOperation operation) {
		try {
			await RunAcquiredAsync(operation).ConfigureAwait(false);
		} catch (Exception e) {
			Bot.ArchiLogger.LogGenericException(e);
		}
	}

	private List<SellCandidate> SelectCandidates(IEnumerable<Asset> inventory, SellLineCollector lines) {
		List<SellCandidate> result = [];
		Dictionary<string, uint> keptPerItem = new(StringComparer.Ordinal);

		foreach (Asset asset in inventory.OrderBy(static asset => asset.AssetID)) {
			if (!asset.Marketable || (asset.Amount == 0) || asset.Description is not { MarketHashName.Length: > 0 } description) {
				continue;
			}

			ItemCache.Remember(description.MarketHashName, asset.Type, asset.Rarity, asset.RealAppID, description.Name, description.IconURL);

			if (!Config.Types.Contains(asset.Type)) {
				lines.Add(asset, asset.Amount, ESellStatus.Locked, "catégorie non vendue");

				continue;
			}

			string? lockReason = Config.Lock.GetItemLockReason(description.MarketHashName, description.Name, asset.Rarity, asset.RealAppID);

			if (lockReason != null) {
				lines.Add(asset, asset.Amount, ESellStatus.Locked, lockReason);

				continue;
			}

			uint amount = asset.Amount;

			if (Config.KeepPerItem > 0) {
				uint alreadyKept = keptPerItem.GetValueOrDefault(description.MarketHashName);
				uint toKeep = Math.Min(amount, Config.KeepPerItem - Math.Min(alreadyKept, Config.KeepPerItem));

				if (toKeep > 0) {
					keptPerItem[description.MarketHashName] = alreadyKept + toKeep;
					amount -= toKeep;

					lines.Add(asset, toKeep, ESellStatus.Kept, $"{Config.KeepPerItem} gardé(s) par objet");
				}
			}

			if (amount > 0) {
				result.Add(new SellCandidate(asset, amount));
			}
		}

		return result;
	}

	private async Task<SellRunResult> SellCoreAsync(EOperation operation, Dictionary<string, PriceQuote>? knownQuotes = null) {
		bool dryRun = operation == EOperation.SellPreview;
		DateTime startedAt = DateTime.UtcNow;
		string? currency = Bot.WalletCurrency != ECurrencyCode.Invalid ? Bot.WalletCurrency.ToString() : null;
		SellLineCollector lines = new();
		uint confirmed = 0;
		bool needsManualConfirmation = false;
		DateTime? rateLimitedUntil = null;

		SellRunResult Finish(string? error = null) => new() {
			Confirmed = confirmed,
			Currency = currency,
			Error = error,
			FinishedAt = DateTime.UtcNow,
			Lines = lines.ToList(),
			NeedsManualConfirmation = needsManualConfirmation,
			Operation = operation,
			RateLimitedUntil = rateLimitedUntil,
			StartedAt = startedAt
		};

		string? problem = CheckReady();

		if (problem != null) {
			return Finish(problem);
		}

		ProgressTracker.Update("Lecture de l'inventaire");

		List<Asset>? inventory = await LoadInventoryAsync().ConfigureAwait(false);

		if (inventory == null) {
			return Finish("impossible de lire l'inventaire Steam.");
		}

		List<SellCandidate> candidates = SelectCandidates(inventory, lines);

		ItemCache.Save();

		if (candidates.Count == 0) {
			return Finish();
		}

		uint pendingConfirmations = 0;

		try {
			Dictionary<string, Dictionary<uint, uint>>? ownPrices = null;

			if (Config.Pricing.Source == EPriceSource.LowestSellOrder) {
				ProgressTracker.Update("Lecture de tes annonces");

				List<OwnListing>? listings = await SteamMarket.GetMyListingsAsync(Bot, Config.RequestDelayMilliseconds).ConfigureAwait(false);

				if (listings == null) {
					return Finish("impossible de lire tes annonces en cours, nécessaire avec la source LowestSellOrder.");
				}

				ownPrices = GroupOwnPrices(listings);
			}

			List<IGrouping<string, SellCandidate>> groups = candidates.GroupBy(static candidate => candidate.Asset.Description!.MarketHashName, StringComparer.Ordinal).ToList();
			uint done = 0;

			foreach (IGrouping<string, SellCandidate> group in groups) {
				Asset firstAsset = group.First().Asset;
				string name = firstAsset.Description!.Name is { Length: > 0 } displayName ? displayName : group.Key;

				ProgressTracker.Update("Calcul des prix", done++, (uint) groups.Count, name);

				QuoteResult quoteResult = knownQuotes?.TryGetValue(group.Key, out PriceQuote knownQuote) == true ? new QuoteResult(knownQuote, null) : await GetQuoteAsync(group.Key, ownPrices?.GetValueOrDefault(group.Key)).ConfigureAwait(false);

				if (quoteResult.Quote is not { } quote) {
					lines.Add(firstAsset, SumAmounts(group), ESellStatus.Skipped, quoteResult.Reason, market: quoteResult);

					continue;
				}

				if (Config.Lock.IsPriceLocked(quote.AdjustedPrice)) {
					lines.Add(firstAsset, SumAmounts(group), ESellStatus.PriceLocked, $"verrouillé par prix ({ReportFormatter.FormatPrice(quote.AdjustedPrice, currency)})", quote, quoteResult);

					continue;
				}

				if (dryRun) {
					lines.Add(firstAsset, SumAmounts(group), ESellStatus.Listed, null, quote, quoteResult);

					continue;
				}

				ProgressTracker.Update("Mise en vente", done, (uint) groups.Count, name);

				uint listed = 0;

				foreach (SellCandidate candidate in group) {
					SellResult result = await SteamMarket.SellItemAsync(Bot, candidate.Asset.AssetID, candidate.Amount, quote.SellerPrice, Config.RequestDelayMilliseconds).ConfigureAwait(false);

					if (!result.Success) {
						lines.Add(firstAsset, candidate.Amount, ESellStatus.Failed, result.Message ?? "raison inconnue", quote, quoteResult);
						Bot.ArchiLogger.LogGenericWarning($"Échec de la mise en vente de {name} : {result.Message ?? "raison inconnue"}");

						continue;
					}

					listed += candidate.Amount;

					if (result.NeedsConfirmation && (++pendingConfirmations >= ConfirmationBatchSize)) {
						uint? handled = await ConfirmListingsAsync().ConfigureAwait(false);

						confirmed += handled ?? 0;
						needsManualConfirmation |= !handled.HasValue;
						pendingConfirmations = 0;
					}
				}

				if (listed > 0) {
					lines.Add(firstAsset, listed, ESellStatus.Listed, null, quote, quoteResult);
				}
			}
		} catch (MarketRateLimitedException) {
			rateLimitedUntil = SteamMarket.RateLimitedUntil;
		} finally {
			if (pendingConfirmations > 0) {
				uint? handled = await ConfirmListingsAsync().ConfigureAwait(false);

				confirmed += handled ?? 0;
				needsManualConfirmation |= !handled.HasValue;
			}

			ItemCache.Save();
		}

		return Finish();
	}

	private void StartFromSchedule(EOperation operation) {
		if (!TryStart(operation)) {
			Bot.ArchiLogger.LogGenericDebug($"{ReportFormatter.GetTitle(operation)} planifiée ignorée, une opération est déjà en cours");
		}
	}
}

// Merges the lines of identical items with the same outcome, so the page shows "Card x3" instead of three rows
internal sealed class SellLineCollector {
	private readonly Dictionary<(string MarketHashName, ESellStatus Status, string? Reason), SellLine> Lines = [];
	private readonly List<(string MarketHashName, ESellStatus Status, string? Reason)> Order = [];

	internal void Add(Asset asset, uint amount, ESellStatus status, string? reason, PriceQuote? quote = null, QuoteResult? market = null) {
		ArgumentNullException.ThrowIfNull(asset);

		InventoryDescription description = asset.Description ?? throw new InvalidOperationException(nameof(asset.Description));
		(string MarketHashName, ESellStatus Status, string? Reason) key = (description.MarketHashName, status, reason);

		if (Lines.TryGetValue(key, out SellLine? existing)) {
			Lines[key] = existing with { Amount = existing.Amount + amount };

			return;
		}

		Order.Add(key);

		Lines[key] = new SellLine {
			AdjustedPrice = quote?.AdjustedPrice,
			Amount = amount,
			BasePrice = quote?.BasePrice,
			BuyerPrice = quote?.BuyerPrice,
			HighestBuyOrder = market?.HighestBuyOrder,
			IconHash = string.IsNullOrEmpty(description.IconURL) ? null : description.IconURL,
			LowestSellOrder = market?.LowestSellOrder,
			MarketHashName = description.MarketHashName,
			Name = description.Name is { Length: > 0 } name ? name : description.MarketHashName,
			Rarity = asset.Rarity,
			RealAppID = asset.RealAppID,
			Reason = reason,
			SellerPrice = quote?.SellerPrice,
			Status = status,
			Type = asset.Type
		};
	}

	internal List<SellLine> ToList() => Order.Select(key => Lines[key]).ToList();
}

internal sealed class ProgressTracker {
	private readonly Lock StateLock = new();

	internal OperationProgress? Snapshot {
		get {
			lock (StateLock) {
				return Current;
			}
		}
	}

	private OperationProgress? Current;

	internal void Start(EOperation operation) {
		lock (StateLock) {
			Current = new OperationProgress { Operation = operation, Stage = "Démarrage", StartedAt = DateTime.UtcNow };
		}
	}

	internal void Stop() {
		lock (StateLock) {
			Current = null;
		}
	}

	internal void Update(string stage, uint done = 0, uint total = 0, string? currentItem = null) {
		ArgumentException.ThrowIfNullOrEmpty(stage);

		lock (StateLock) {
			if (Current != null) {
				Current = Current with { CurrentItem = currentItem, Done = done, Stage = stage, Total = total };
			}
		}
	}
}
