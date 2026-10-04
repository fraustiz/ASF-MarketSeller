using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ArchiSteamFarm.Steam.Data;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

internal enum EPriceSource : byte {
	// Cheapest listing of other sellers, what Steam shows as "Starting at"
	LowestSellOrder,

	// Best current buy order, the item sells instantly
	HighestBuyOrder,

	// Volume-weighted average of the sales over the last AverageDays
	AverageSold,

	// FixedCents for every item
	Fixed
}

#pragma warning disable CA1812 // False positive, the class is used during json deserialization
internal sealed partial class MarketSellerConfig {
	internal const string PropertyName = "MarketSeller";

	[JsonInclude]
	public bool AutoConfirm { get; private init; } = true;

	[JsonInclude]
	public string Country { get; private init; } = "FR";

	[JsonInclude]
	public bool DryRun { get; private init; }

	[JsonInclude]
	public bool Enabled { get; private init; }

	[JsonInclude]
	public uint KeepPerItem { get; private init; }

	[JsonInclude]
	public LockConfig Lock { get; private init; } = new();

	[JsonInclude]
	public PricingConfig Pricing { get; private init; } = new();

	[JsonInclude]
	public ushort RepriceIntervalMinutes { get; private init; } = 120;

	[JsonInclude]
	public ushort RepriceThresholdCents { get; private init; } = 1;

	[JsonInclude]
	public ushort RequestDelayMilliseconds { get; private init; } = 3000;

	[JsonInclude]
	public ushort SellIntervalMinutes { get; private init; } = 360;

	[JsonInclude]
	public bool SellOnFarmingFinished { get; private init; } = true;

	[JsonInclude]
	public ImmutableHashSet<EAssetType> Types { get; private init; } = [EAssetType.TradingCard];

	[JsonConstructor]
	internal MarketSellerConfig() { }

	[UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode", Justification = "Plugins are never trimmed")]
	[UnconditionalSuppressMessage("AssemblyLoadAot", "IL3050:RequiresDynamicCode", Justification = "Plugins are never AOT-compiled")]
	internal static MarketSellerConfig? Parse(JsonElement json, out string? error) {
		MarketSellerConfig? config;

		try {
			config = json.Deserialize<MarketSellerConfig>(PluginJson.StrictOptions);
		} catch (JsonException e) {
			error = DescribeJsonError(e);

			return null;
		}

		if (config == null) {
			error = "configuration vide";

			return null;
		}

		error = config.Validate();

		return error == null ? config : null;
	}

	[UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode", Justification = "Plugins are never trimmed")]
	[UnconditionalSuppressMessage("AssemblyLoadAot", "IL3050:RequiresDynamicCode", Justification = "Plugins are never AOT-compiled")]
	internal JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, PluginJson.LenientOptions);

	// System.Text.Json messages are technical and in English, this is shown to the user in logs and on the web page
	private static string DescribeJsonError(JsonException exception) {
		Match unknownProperty = UnknownPropertyRegex().Match(exception.Message);

		if (unknownProperty.Success) {
			return $"réglage inconnu « {unknownProperty.Groups["name"].Value} », vérifie son orthographe";
		}

		string? path = exception.Path?.TrimStart('$').TrimStart('.');

		return string.IsNullOrEmpty(path) ? "JSON invalide" : $"valeur invalide pour {path}";
	}

	[GeneratedRegex("The JSON property '(?<name>[^']+)' could not be mapped", RegexOptions.CultureInvariant)]
	private static partial Regex UnknownPropertyRegex();

	private string? Validate() {
		if (Types.IsEmpty) {
			return $"{nameof(Types)} ne peut pas être vide";
		}

		if (Types.Any(static type => (type == EAssetType.Unknown) || !Enum.IsDefined(type))) {
			return $"{nameof(Types)} contient un type invalide";
		}

		if ((Country.Length != 2) || !Country.All(char.IsAsciiLetter)) {
			return $"{nameof(Country)} doit être un code pays à 2 lettres (ex: FR)";
		}

		if (RepriceThresholdCents == 0) {
			return $"{nameof(RepriceThresholdCents)} doit être au moins 1";
		}

		return Pricing.Validate() ?? Lock.Validate();
	}
}

internal sealed class PricingConfig {
	[JsonInclude]
	public byte AverageDays { get; private init; } = 7;

	[JsonInclude]
	public uint FixedCents { get; private init; }

	[JsonInclude]
	public uint MaxCents { get; private init; }

	[JsonInclude]
	public uint MinCents { get; private init; } = MarketPricing.MinimumBuyerPrice;

	[JsonInclude]
	public decimal Multiplier { get; private init; } = 1;

	[JsonInclude]
	public int OffsetCents { get; private init; }

	[JsonInclude]
	public EPriceSource Source { get; private init; } = EPriceSource.LowestSellOrder;

	[JsonConstructor]
	internal PricingConfig() { }

	internal string? Validate() {
		if (!Enum.IsDefined(Source)) {
			return $"{nameof(MarketSellerConfig.Pricing)}.{nameof(Source)} invalide (LowestSellOrder, HighestBuyOrder, AverageSold ou Fixed)";
		}

		if (Multiplier is <= 0 or > 100) {
			return $"{nameof(MarketSellerConfig.Pricing)}.{nameof(Multiplier)} doit être entre 0 (exclu) et 100";
		}

		if ((Source == EPriceSource.AverageSold) && (AverageDays == 0)) {
			return $"{nameof(MarketSellerConfig.Pricing)}.{nameof(AverageDays)} doit être au moins 1";
		}

		if ((Source == EPriceSource.Fixed) && (FixedCents < MarketPricing.MinimumBuyerPrice)) {
			return $"{nameof(MarketSellerConfig.Pricing)}.{nameof(FixedCents)} doit être au moins {MarketPricing.MinimumBuyerPrice} avec la source Fixed";
		}

		if ((MaxCents > 0) && (MaxCents < Math.Max(MinCents, MarketPricing.MinimumBuyerPrice))) {
			return $"{nameof(MarketSellerConfig.Pricing)}.{nameof(MaxCents)} doit être supérieur ou égal à {nameof(MinCents)} (et à {MarketPricing.MinimumBuyerPrice})";
		}

		return null;
	}
}

internal sealed class LockConfig {
	[JsonInclude]
	public ImmutableHashSet<uint> AppIDs { get; private init; } = [];

	[JsonInclude]
	public ImmutableHashSet<string> Names { get; private init; } = [];

	[JsonInclude]
	public uint PriceAboveCents { get; private init; }

	[JsonInclude]
	public uint PriceBelowCents { get; private init; }

	[JsonInclude]
	public ImmutableHashSet<EAssetRarity> Rarities { get; private init; } = [];

	[JsonConstructor]
	internal LockConfig() { }

	// Human-readable reason when the item is locked, null otherwise
	internal string? GetItemLockReason(string marketHashName, string name, EAssetRarity rarity, uint realAppID) {
		ArgumentNullException.ThrowIfNull(marketHashName);
		ArgumentNullException.ThrowIfNull(name);

		if (Rarities.Contains(rarity)) {
			return $"rareté {GetRarityName(rarity)}";
		}

		if ((realAppID > 0) && AppIDs.Contains(realAppID)) {
			return $"jeu {realAppID}";
		}

		string? lockedName = Names.FirstOrDefault(lockedName => marketHashName.Contains(lockedName, StringComparison.OrdinalIgnoreCase) || name.Contains(lockedName, StringComparison.OrdinalIgnoreCase));

		return lockedName != null ? $"nom contenant « {lockedName} »" : null;
	}

	internal bool IsItemLocked(string marketHashName, string name, EAssetRarity rarity, uint realAppID) => GetItemLockReason(marketHashName, name, rarity, realAppID) != null;

	// Compared against the price computed from the source and adjustments, before MinCents/MaxCents clamping
	internal bool IsPriceLocked(uint adjustedPrice) => ((PriceAboveCents > 0) && (adjustedPrice >= PriceAboveCents)) || ((PriceBelowCents > 0) && (adjustedPrice < PriceBelowCents));

	internal string? Validate() {
		if (Names.Any(string.IsNullOrWhiteSpace)) {
			return $"{nameof(MarketSellerConfig.Lock)}.{nameof(Names)} ne peut pas contenir de nom vide";
		}

		if ((PriceAboveCents > 0) && (PriceBelowCents > 0) && (PriceAboveCents <= PriceBelowCents)) {
			return $"{nameof(MarketSellerConfig.Lock)}.{nameof(PriceAboveCents)} doit être supérieur à {nameof(PriceBelowCents)}, sinon tout est verrouillé";
		}

		return null;
	}

	private static string GetRarityName(EAssetRarity rarity) => rarity switch {
		EAssetRarity.Common => "commune",
		EAssetRarity.Uncommon => "peu commune",
		EAssetRarity.Rare => "rare",
		_ => rarity.ToString()
	};
}
#pragma warning restore CA1812 // False positive, the class is used during json deserialization
