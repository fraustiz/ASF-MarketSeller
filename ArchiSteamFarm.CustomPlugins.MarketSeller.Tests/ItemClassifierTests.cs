using ArchiSteamFarm.Steam.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller.Tests;

#pragma warning disable CA1812 // False positive, the class is used during MSTest
[TestClass]
internal sealed class ItemClassifierTests {
	// Type lines as returned by Steam's market (English)
	[TestMethod]
	[DataRow("Counter-Strike 2 Trading Card", "730-SWAT", EAssetType.TradingCard, EAssetRarity.Common)]
	[DataRow("Portal 2 Foil Trading Card", "620-Wheatley (Foil)", EAssetType.FoilTradingCard, EAssetRarity.Common)]
	[DataRow("Buddy Simulator 1984 Emoticon", "1269950-:LenoxDispleased:", EAssetType.Emoticon, EAssetRarity.Common)]
	[DataRow("Counter-Strike 2 Uncommon Emoticon", "730-:csgoskull:", EAssetType.Emoticon, EAssetRarity.Uncommon)]
	[DataRow("Counter-Strike 2 Uncommon Profile Background", "730-Camo", EAssetType.ProfileBackground, EAssetRarity.Uncommon)]
	[DataRow("Counter-Strike 2 Rare Profile Background", "730-Holding Pattern", EAssetType.ProfileBackground, EAssetRarity.Rare)]
	[DataRow("Portal 2 Booster Pack", "620-Portal 2 Booster Pack", EAssetType.BoosterPack, EAssetRarity.Common)]
	[DataRow("Steam Gems", "753-Sack of Gems", EAssetType.SteamGems, EAssetRarity.Common)]
	[DataRow("Uncommon Spiral Knights item", "Unknown package 35539", EAssetType.Unknown, EAssetRarity.Unknown)]
	[DataRow(null, "730-SWAT", EAssetType.Unknown, EAssetRarity.Unknown)]
	public void TypeLinesAreClassified(string? typeText, string marketHashName, EAssetType expectedType, EAssetRarity expectedRarity) {
		(EAssetType type, EAssetRarity rarity) = ItemClassifier.Classify(typeText, marketHashName);

		Assert.AreEqual(expectedType, type);

		if (expectedType != EAssetType.Unknown) {
			Assert.AreEqual(expectedRarity, rarity);
		}
	}

	[TestMethod]
	public void GameNamesContainingRarityWordsDontChangeTheRarity() {
		Assert.AreEqual((EAssetType.TradingCard, EAssetRarity.Common), ItemClassifier.Classify("Rare Replay Trading Card", "1000-Banjo"));
		Assert.AreEqual((EAssetType.Emoticon, EAssetRarity.Rare), ItemClassifier.Classify("Rare Replay Rare Emoticon", "1000-:banjo:"));
	}

	[TestMethod]
	public void RealAppIDComesFromFeeAppOrHashName() {
		Assert.AreEqual(620U, ItemClassifier.GetRealAppID(620, "730-whatever"));
		Assert.AreEqual(1269950U, ItemClassifier.GetRealAppID(null, "1269950-:LenoxDispleased:"));
		Assert.AreEqual(0U, ItemClassifier.GetRealAppID(null, "Sack of Gems"));
		Assert.AreEqual(0U, ItemClassifier.GetRealAppID(null, null));
	}
}
#pragma warning restore CA1812 // False positive, the class is used during MSTest
