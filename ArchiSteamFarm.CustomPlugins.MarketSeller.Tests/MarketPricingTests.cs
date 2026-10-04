using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller.Tests;

#pragma warning disable CA1812 // False positive, the class is used during MSTest
[TestClass]
internal sealed class MarketPricingTests {
	[TestMethod]
	public void AverageSoldPriceIgnoresSalesOutsideWindow() {
		DateTime now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

		List<PriceHistoryEntry> history = [
			new(now.AddDays(-10), 5.00m, 100),
			new(now.AddDays(-2), 0.20m, 30),
			new(now.AddDays(-1), 0.10m, 10)
		];

		// (0.20 * 30 + 0.10 * 10) / 40 = 0.175
		Assert.AreEqual(18U, MarketPricing.GetAverageSoldPrice(history, now.AddDays(-7)));
	}

	[TestMethod]
	public void AverageSoldPriceIsNullWithoutSales() {
		DateTime now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

		Assert.IsNull(MarketPricing.GetAverageSoldPrice([], now));
		Assert.IsNull(MarketPricing.GetAverageSoldPrice([new PriceHistoryEntry(now.AddDays(-30), 0.10m, 5)], now.AddDays(-7)));
	}

	[TestMethod]
	[DataRow(1U, 3U)]
	[DataRow(2U, 4U)]
	[DataRow(3U, 5U)]
	[DataRow(8U, 10U)]
	[DataRow(20U, 23U)]
	[DataRow(87U, 99U)]
	[DataRow(88U, 100U)]
	public void BuyerPriceMatchesSteamFees(uint sellerPrice, uint expectedBuyerPrice) => Assert.AreEqual(expectedBuyerPrice, MarketPricing.GetBuyerPrice(sellerPrice));

	[TestMethod]
	public void CompetitorPriceIgnoresOwnListings() {
		List<SellOrderLevel> graph = [new(5, 3), new(6, 10)];

		Assert.AreEqual(5U, MarketPricing.GetLowestCompetitorPrice(graph, null));
		Assert.AreEqual(5U, MarketPricing.GetLowestCompetitorPrice(graph, new Dictionary<uint, uint> { [5] = 2 }));
		Assert.AreEqual(6U, MarketPricing.GetLowestCompetitorPrice(graph, new Dictionary<uint, uint> { [5] = 3 }));
		Assert.IsNull(MarketPricing.GetLowestCompetitorPrice(graph, new Dictionary<uint, uint> { [5] = 3, [6] = 7 }));
		Assert.IsNull(MarketPricing.GetLowestCompetitorPrice([], null));
	}

	[TestMethod]
	public void QuoteAppliesMultiplierAndOffset() {
		PriceQuote quote = MarketPricing.CreateQuote(20, ParsePricing("""{"Multiplier": 0.9, "OffsetCents": -1}"""));

		Assert.AreEqual(17U, quote.AdjustedPrice);
		Assert.AreEqual(17U, quote.BuyerPrice);
		Assert.AreEqual(15U, quote.SellerPrice);
	}

	[TestMethod]
	public void QuoteClampsToMinAndMax() {
		Assert.AreEqual(5U, MarketPricing.CreateQuote(3, ParsePricing("""{"MinCents": 5}""")).BuyerPrice);

		PriceQuote capped = MarketPricing.CreateQuote(80, ParsePricing("""{"MaxCents": 50}"""));

		Assert.AreEqual(80U, capped.AdjustedPrice);
		Assert.AreEqual(50U, capped.BuyerPrice);
		Assert.AreEqual(44U, capped.SellerPrice);
	}

	[TestMethod]
	public void QuoteNeverGoesBelowMarketMinimum() {
		PriceQuote quote = MarketPricing.CreateQuote(3, ParsePricing("""{"OffsetCents": -10, "MinCents": 0}"""));

		Assert.AreEqual(1U, quote.AdjustedPrice);
		Assert.AreEqual(MarketPricing.MinimumBuyerPrice, quote.BuyerPrice);
		Assert.AreEqual(1U, quote.SellerPrice);
	}

	[TestMethod]
	public void QuoteWithoutAdjustmentsKeepsBasePrice() {
		PriceQuote quote = MarketPricing.CreateQuote(10, ParsePricing("""{"Multiplier": 0.5, "OffsetCents": -3}"""), false);

		Assert.AreEqual(10U, quote.AdjustedPrice);
		Assert.AreEqual(10U, quote.BuyerPrice);
	}

	[TestMethod]
	[DataRow(2U, 0U)]
	[DataRow(3U, 1U)]
	[DataRow(4U, 2U)]
	[DataRow(5U, 3U)]
	[DataRow(10U, 8U)]
	[DataRow(100U, 88U)]
	public void SellerPriceMatchesSteamFees(uint buyerPrice, uint expectedSellerPrice) => Assert.AreEqual(expectedSellerPrice, MarketPricing.GetSellerPrice(buyerPrice));

	[TestMethod]
	public void SellerPriceIsHighestAmountFittingBuyerPrice() {
		for (uint buyerPrice = MarketPricing.MinimumBuyerPrice; buyerPrice <= 20_000; buyerPrice++) {
			uint sellerPrice = MarketPricing.GetSellerPrice(buyerPrice);

			Assert.IsLessThanOrEqualTo(buyerPrice, MarketPricing.GetBuyerPrice(sellerPrice));
			Assert.IsGreaterThan(buyerPrice, MarketPricing.GetBuyerPrice(sellerPrice + 1));
		}
	}

	private static PricingConfig ParsePricing(string pricingJson) {
		using JsonDocument document = JsonDocument.Parse($$"""{"Pricing": {{pricingJson}}}""");

		MarketSellerConfig? config = MarketSellerConfig.Parse(document.RootElement, out string? error);

		Assert.IsNotNull(config, error);

		return config.Pricing;
	}
}
#pragma warning restore CA1812 // False positive, the class is used during MSTest
