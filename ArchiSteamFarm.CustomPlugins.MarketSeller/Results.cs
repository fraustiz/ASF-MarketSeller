using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArchiSteamFarm.Steam.Data;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

// Everything here is returned by the IPC API to the web page, prices are in cents of the wallet currency

[JsonConverter(typeof(JsonStringEnumConverter<EOperation>))]
public enum EOperation : byte {
	SellPreview,
	Sell,
	RepricePreview,
	Reprice
}

[JsonConverter(typeof(JsonStringEnumConverter<ESellStatus>))]
public enum ESellStatus : byte {
	Listed,
	Failed,
	Skipped,
	PriceLocked,
	Locked,
	Kept
}

[JsonConverter(typeof(JsonStringEnumConverter<ERepriceStatus>))]
public enum ERepriceStatus : byte {
	Unchanged,
	Repriced,
	Withdrawn,
	Skipped,
	Failed
}

public sealed record SellLine {
	public required uint Amount { get; init; }
	public uint? AdjustedPrice { get; init; }
	public uint? BasePrice { get; init; }
	public uint? BuyerPrice { get; init; }
	public uint? HighestBuyOrder { get; init; }
	public string? IconHash { get; init; }
	public uint? LowestSellOrder { get; init; }
	public required string MarketHashName { get; init; }
	public required string Name { get; init; }

	[JsonConverter(typeof(JsonStringEnumConverter<EAssetRarity>))]
	public EAssetRarity Rarity { get; init; }

	public uint RealAppID { get; init; }
	public string? Reason { get; init; }
	public uint? SellerPrice { get; init; }
	public required ESellStatus Status { get; init; }

	[JsonConverter(typeof(JsonStringEnumConverter<EAssetType>))]
	public EAssetType Type { get; init; }
}

public sealed record SellRunResult {
	public uint Confirmed { get; init; }
	public string? Currency { get; init; }
	public bool DryRun => Operation == EOperation.SellPreview;
	public string? Error { get; init; }
	public required DateTime FinishedAt { get; init; }
	public IReadOnlyList<SellLine> Lines { get; init; } = [];
	public bool NeedsManualConfirmation { get; init; }
	public required EOperation Operation { get; init; }
	public DateTime? RateLimitedUntil { get; init; }
	public required DateTime StartedAt { get; init; }
}

public sealed record RepriceLine {
	public uint? AdjustedPrice { get; init; }
	public required uint CurrentBuyerPrice { get; init; }
	public string? IconHash { get; init; }

	// String because listing IDs don't fit in a JavaScript number
	public required string ListingID { get; init; }

	public required string MarketHashName { get; init; }
	public required string Name { get; init; }
	public string? Reason { get; init; }
	public required ERepriceStatus Status { get; init; }
	public uint? TargetBuyerPrice { get; init; }
}

public sealed record RepriceRunResult {
	public string? Currency { get; init; }
	public bool DryRun => Operation == EOperation.RepricePreview;
	public string? Error { get; init; }
	public required DateTime FinishedAt { get; init; }
	public IReadOnlyList<RepriceLine> Lines { get; init; } = [];
	public required EOperation Operation { get; init; }
	public DateTime? RateLimitedUntil { get; init; }
	public SellRunResult? Relist { get; init; }
	public required DateTime StartedAt { get; init; }
}

public sealed record OperationProgress {
	public string? CurrentItem { get; init; }
	public uint Done { get; init; }
	public required EOperation Operation { get; init; }
	public required string Stage { get; init; }
	public required DateTime StartedAt { get; init; }
	public uint Total { get; init; }
}

public sealed record RunSummary {
	public required DateTime FinishedAt { get; init; }
	public required EOperation Operation { get; init; }
	public required string Text { get; init; }
}

public sealed record BotOverview {
	public required string BotName { get; init; }
	public string? ConfigError { get; init; }
	public bool ConfigPresent { get; init; }
	public bool Connected { get; init; }
	public string? Currency { get; init; }
	public bool DryRun { get; init; }
	public bool Enabled { get; init; }
	public bool HasMobileAuthenticator { get; init; }
	public RunSummary? LastReprice { get; init; }
	public RunSummary? LastSell { get; init; }
	public OperationProgress? Progress { get; init; }
}

public sealed record BotDetails {
	public required JsonElement Config { get; init; }
	public RepriceRunResult? LastReprice { get; init; }
	public SellRunResult? LastSell { get; init; }
	public required BotOverview Overview { get; init; }
}

public sealed record UpdateInfo {
	// What ASF's /Api/Plugins/Update endpoint expects to identify the plugin
	public required string AssemblyName { get; init; }

	public required DateTime CheckedAt { get; init; }
	public required string CurrentVersion { get; init; }
	public string? Error { get; init; }
	public string? LatestVersion { get; init; }
	public DateTime? PublishedAt { get; init; }
	public string? ReleaseNotes { get; init; }
	public required Uri ReleasePage { get; init; }
	public bool UpdateAvailable { get; init; }
}

public sealed record MarketSellerOverview {
	public required IReadOnlyList<BotOverview> Bots { get; init; }
	public DateTime? RateLimitedUntil { get; init; }
	public required string Version { get; init; }
}
