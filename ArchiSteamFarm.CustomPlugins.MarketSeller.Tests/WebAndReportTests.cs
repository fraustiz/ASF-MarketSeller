using System;
using System.Text.Json;
using ArchiSteamFarm.Steam.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller.Tests;

#pragma warning disable CA1812 // False positive, the class is used during MSTest
[TestClass]
internal sealed class WebAndReportTests {
	private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

	[TestMethod]
	public void ConfigRoundTripsThroughJson() {
		using JsonDocument document = JsonDocument.Parse("""{"Enabled": true, "Types": ["FoilTradingCard"], "Pricing": {"Source": "Fixed", "FixedCents": 12}, "Lock": {"Names": ["Gordon"]}}""");

		MarketSellerConfig? config = MarketSellerConfig.Parse(document.RootElement, out string? error);

		Assert.IsNotNull(config, error);

		JsonElement serialized = config.ToJsonElement();

		Assert.AreEqual("Fixed", serialized.GetProperty("Pricing").GetProperty("Source").GetString());
		Assert.AreEqual("FoilTradingCard", serialized.GetProperty("Types")[0].GetString());

		MarketSellerConfig? reparsed = MarketSellerConfig.Parse(serialized, out error);

		Assert.IsNotNull(reparsed, error);
		Assert.AreEqual(12U, reparsed.Pricing.FixedCents);
		Assert.Contains("Gordon", reparsed.Lock.Names);
	}

	[TestMethod]
	public void LoaderIsInjectedBeforeBodyEnd() {
		string? injected = WebAssets.InjectLoader("<html><body><div id=\"app\"></div></body></html>");

		Assert.IsNotNull(injected);
		Assert.Contains("market-seller/app.js", injected);
		Assert.IsLessThan(injected.LastIndexOf("</body>", StringComparison.Ordinal), injected.IndexOf("__MARKET_SELLER__", StringComparison.Ordinal));
		Assert.IsNull(WebAssets.InjectLoader("<html>no body here</html>"));
	}

	[TestMethod]
	public void LockReasonsAreReadable() {
		using JsonDocument document = JsonDocument.Parse("""{"Lock": {"Rarities": ["Rare"], "AppIDs": [440], "Names": ["gordon"]}}""");

		LockConfig locks = MarketSellerConfig.Parse(document.RootElement, out _)!.Lock;

		Assert.AreEqual("rareté rare", locks.GetItemLockReason("730-Background", "Background", EAssetRarity.Rare, 730));
		Assert.AreEqual("jeu 440", locks.GetItemLockReason("440-Scout", "Scout", EAssetRarity.Common, 440));
		Assert.AreEqual("nom contenant « gordon »", locks.GetItemLockReason("70-Gordon Freeman", "Gordon Freeman", EAssetRarity.Common, 70));
		Assert.IsNull(locks.GetItemLockReason("730-Card", "Card", EAssetRarity.Common, 730));
	}

	[TestMethod]
	public void SellSummaryCountsUnitsAndTotals() {
		SellRunResult result = new() {
			Currency = "EUR",
			FinishedAt = Now,
			Lines = [
				new SellLine { Amount = 2, BuyerPrice = 10, MarketHashName = "a", Name = "A", SellerPrice = 8, Status = ESellStatus.Listed },
				new SellLine { Amount = 1, MarketHashName = "b", Name = "B", Reason = "aucun ordre d'achat", Status = ESellStatus.Skipped },
				new SellLine { Amount = 3, MarketHashName = "c", Name = "C", Reason = "catégorie non vendue", Status = ESellStatus.Locked },
				new SellLine { Amount = 1, MarketHashName = "a", Name = "A", Reason = "1 gardé(s) par objet", Status = ESellStatus.Kept }
			],
			Operation = EOperation.SellPreview,
			StartedAt = Now
		};

		string summary = ReportFormatter.Summarize(result);

		Assert.StartsWith("2 objet(s) seraient mis en vente pour 0.20 EUR (tu reçois 0.16 EUR), 1 ignoré(s), 3 verrouillé(s), 1 gardé(s), 0 échec(s).", summary);

		// Details skip locked and kept items, which are expected and would flood chat
		string text = ReportFormatter.Format(result);

		Assert.Contains("A x2 : 0.10 EUR (tu reçois 0.08 EUR)", text);
		Assert.Contains("B x1 : ignoré (aucun ordre d'achat)", text);
		Assert.DoesNotContain("C x3", text);
	}

	[TestMethod]
	public void RepriceSummaryCountsEveryListing() {
		RepriceLine Line(ERepriceStatus status, bool createdByPlugin = false) => new() { CreatedByPlugin = createdByPlugin, CurrentBuyerPrice = 5, ListingID = "1", MarketHashName = "a", Name = "A", Status = status };

		RepriceRunResult result = new() {
			FinishedAt = Now,
			Lines = [Line(ERepriceStatus.Repriced), Line(ERepriceStatus.Unchanged, true), Line(ERepriceStatus.Unchanged), Line(ERepriceStatus.Ignored), Line(ERepriceStatus.Ignored)],
			Operation = EOperation.RepricePreview,
			StartedAt = Now
		};

		Assert.StartsWith("5 annonce(s) en vente, 3 gérée(s) dont 2 créée(s) à la main : 1 seraient réajustée(s), 0 seraient retirée(s), 2 inchangée(s), 0 sans prix de référence, 0 échec(s). 2 non gérée(s).", ReportFormatter.Summarize(result));
	}

	[TestMethod]
	public void SummaryReportsErrorsAsIs() {
		SellRunResult result = new() { Error = "le bot n'est pas connecté.", FinishedAt = Now, Operation = EOperation.Sell, StartedAt = Now };

		Assert.AreEqual("le bot n'est pas connecté.", ReportFormatter.Summarize(result));
	}
}
#pragma warning restore CA1812 // False positive, the class is used during MSTest
