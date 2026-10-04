using System.Text.Json;
using ArchiSteamFarm.Steam.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller.Tests;

#pragma warning disable CA1812 // False positive, the class is used during MSTest
[TestClass]
internal sealed class ConfigTests {
	[TestMethod]
	public void DefaultsAreSafe() {
		MarketSellerConfig config = Parse("""{"Enabled": true}""");

		Assert.IsTrue(config.Enabled);
		Assert.IsFalse(config.DryRun);
		Assert.AreEqual(EPriceSource.LowestSellOrder, config.Pricing.Source);
		Assert.HasCount(1, config.Types);
		Assert.Contains(EAssetType.TradingCard, config.Types);
		Assert.AreEqual(MarketPricing.MinimumBuyerPrice, config.Pricing.MinCents);
	}

	[TestMethod]
	public void EnumsAndPropertiesAreCaseInsensitive() {
		MarketSellerConfig config = Parse("""
			{
				"enabled": true,
				"types": ["tradingcard", "FoilTradingCard"],
				"pricing": { "source": "averagesold", "averageDays": 3 },
				"lock": { "rarities": ["rare"], "appIDs": [440], "names": ["gordon"], "priceAboveCents": 100 }
			}
			""");

		Assert.AreEqual(EPriceSource.AverageSold, config.Pricing.Source);
		Assert.AreEqual(3, config.Pricing.AverageDays);
		Assert.Contains(EAssetType.FoilTradingCard, config.Types);
		Assert.Contains(EAssetRarity.Rare, config.Lock.Rarities);
		Assert.Contains(440U, config.Lock.AppIDs);
		Assert.AreEqual(100U, config.Lock.PriceAboveCents);
	}

	[TestMethod]
	public void FixedSourceRequiresPrice() => AssertInvalid("""{"Pricing": {"Source": "Fixed"}}""", "FixedCents");

	[TestMethod]
	public void InconsistentPriceLocksAreRejected() => AssertInvalid("""{"Lock": {"PriceAboveCents": 10, "PriceBelowCents": 20}}""", "PriceAboveCents");

	[TestMethod]
	public void ItemLocksMatchRarityGameAndName() {
		LockConfig locks = Parse("""{"Lock": {"Rarities": ["Rare"], "AppIDs": [440], "Names": ["gordon"]}}""").Lock;

		Assert.IsTrue(locks.IsItemLocked("70-Gordon Freeman", "Gordon Freeman", EAssetRarity.Common, 70));
		Assert.IsTrue(locks.IsItemLocked("440-Scout", "Scout", EAssetRarity.Common, 440));
		Assert.IsTrue(locks.IsItemLocked("730-Background", "Background", EAssetRarity.Rare, 730));
		Assert.IsFalse(locks.IsItemLocked("730-Card", "Card", EAssetRarity.Common, 730));
	}

	[TestMethod]
	public void PriceLocksUseThresholds() {
		LockConfig locks = Parse("""{"Lock": {"PriceAboveCents": 50, "PriceBelowCents": 5}}""").Lock;

		Assert.IsTrue(locks.IsPriceLocked(50));
		Assert.IsFalse(locks.IsPriceLocked(49));
		Assert.IsFalse(locks.IsPriceLocked(5));
		Assert.IsTrue(locks.IsPriceLocked(4));
	}

	[TestMethod]
	public void TyposAreRejected() => AssertInvalid("""{"Enabled": true, "Pricng": {}}""", "Pricng");

	private static void AssertInvalid(string json, string expectedInError) {
		using JsonDocument document = JsonDocument.Parse(json);

		Assert.IsNull(MarketSellerConfig.Parse(document.RootElement, out string? error));
		Assert.IsNotNull(error);
		Assert.Contains(expectedInError, error);
	}

	private static MarketSellerConfig Parse(string json) {
		using JsonDocument document = JsonDocument.Parse(json);

		MarketSellerConfig? config = MarketSellerConfig.Parse(document.RootElement, out string? error);

		Assert.IsNotNull(config, error);

		return config;
	}
}
#pragma warning restore CA1812 // False positive, the class is used during MSTest
