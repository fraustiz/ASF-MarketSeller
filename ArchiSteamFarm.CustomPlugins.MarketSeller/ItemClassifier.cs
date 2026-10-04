using System;
using System.Globalization;
using System.Text.RegularExpressions;
using ArchiSteamFarm.Steam.Data;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

// Market listings don't carry the tags ASF uses to classify inventory items, only the English type line,
// e.g. "Counter-Strike 2 Trading Card", "Portal 2 Uncommon Emoticon", "Payday 2 Rare Profile Background" or "Steam Gems".
// Used for listings of items the plugin has never seen in the inventory, such as the ones created by hand.
internal static partial class ItemClassifier {
	internal static (EAssetType Type, EAssetRarity Rarity) Classify(string? typeText, string? marketHashName) {
		if (string.Equals(marketHashName, "753-Sack of Gems", StringComparison.Ordinal)) {
			return (EAssetType.SteamGems, EAssetRarity.Common);
		}

		if (string.IsNullOrWhiteSpace(typeText)) {
			return (EAssetType.Unknown, EAssetRarity.Unknown);
		}

		Match match = TypeLineRegex().Match(typeText.Trim());

		if (!match.Success) {
			return (EAssetType.Unknown, EAssetRarity.Unknown);
		}

		EAssetType type = match.Groups["kind"].Value.ToUpperInvariant() switch {
			"FOIL TRADING CARD" => EAssetType.FoilTradingCard,
			"TRADING CARD" => EAssetType.TradingCard,
			"EMOTICON" => EAssetType.Emoticon,
			"MINI-PROFILE BACKGROUND" or "MINI PROFILE BACKGROUND" => EAssetType.MiniProfileBackground,
			"PROFILE BACKGROUND" => EAssetType.ProfileBackground,
			"BOOSTER PACK" => EAssetType.BoosterPack,
			"GEMS" => EAssetType.SteamGems,
			"ANIMATED AVATAR" => EAssetType.AnimatedAvatar,
			"AVATAR FRAME" or "PROFILE FRAME" => EAssetType.AvatarProfileFrame,
			"STICKER" => EAssetType.Sticker,
			"CHAT EFFECT" => EAssetType.ChatEffect,
			"KEYBOARD SKIN" => EAssetType.KeyboardSkin,
			"STARTUP MOVIE" or "STARTUP VIDEO" => EAssetType.StartupVideo,
			_ => EAssetType.Unknown
		};

		// Steam only writes the rarity when it isn't common, right before the kind of item
		EAssetRarity rarity = match.Groups["rarity"].Value.ToUpperInvariant() switch {
			"RARE" => EAssetRarity.Rare,
			"UNCOMMON" => EAssetRarity.Uncommon,
			_ => EAssetRarity.Common
		};

		return (type, rarity);
	}

	// Community items are named "<appid>-<name>", the fee app is the game the item belongs to
	internal static uint GetRealAppID(uint? marketFeeApp, string? marketHashName) {
		if (marketFeeApp is > 0) {
			return marketFeeApp.Value;
		}

		int separator = marketHashName?.IndexOf('-', StringComparison.Ordinal) ?? -1;

		return (separator > 0) && uint.TryParse(marketHashName![..separator], NumberStyles.None, CultureInfo.InvariantCulture, out uint appID) ? appID : 0;
	}

	[GeneratedRegex(@"(?:^|\s)(?:(?<rarity>Rare|Uncommon|Common)\s+)?(?<kind>Foil Trading Card|Trading Card|Emoticon|Mini-Profile Background|Mini Profile Background|Profile Background|Booster Pack|Gems|Animated Avatar|Avatar Frame|Profile Frame|Sticker|Chat Effect|Keyboard Skin|Startup Movie|Startup Video)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex TypeLineRegex();
}
