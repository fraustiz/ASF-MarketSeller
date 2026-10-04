using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam.Data;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

internal sealed record CachedItem(ulong NameID, EAssetType Type, EAssetRarity Rarity, uint RealAppID, string Name, string? IconHash = null);

// Item details per market hash name, shared by all bots and persisted in ASF.db:
// - item_nameid never changes and fetching it costs a heavily rate-limited page load
// - type, rarity and game let us apply the config filters to our market listings, which don't carry those details
internal static class ItemCache {
	private const string StorageKey = $"{nameof(MarketSeller)}.Items";

	private static readonly Dictionary<string, CachedItem> Items = new(StringComparer.Ordinal);
	private static readonly Lock ItemsLock = new();

	private static bool Dirty;
	private static bool Loaded;

	internal static CachedItem? Get(string marketHashName) {
		ArgumentException.ThrowIfNullOrEmpty(marketHashName);

		lock (ItemsLock) {
			EnsureLoaded();

			return Items.GetValueOrDefault(marketHashName);
		}
	}

	internal static void Remember(string marketHashName, EAssetType type, EAssetRarity rarity, uint realAppID, string name, string? iconHash) {
		ArgumentException.ThrowIfNullOrEmpty(marketHashName);
		ArgumentNullException.ThrowIfNull(name);

		lock (ItemsLock) {
			EnsureLoaded();

			CachedItem? existing = Items.GetValueOrDefault(marketHashName);
			CachedItem updated = existing != null ? existing with { Type = type, Rarity = rarity, RealAppID = realAppID, Name = name, IconHash = iconHash ?? existing.IconHash } : new CachedItem(0, type, rarity, realAppID, name, iconHash);

			if (updated != existing) {
				Items[marketHashName] = updated;
				Dirty = true;
			}
		}
	}

	internal static void Save() {
		Dictionary<string, CachedItem> snapshot;

		lock (ItemsLock) {
			if (!Dirty || (ASF.GlobalDatabase == null)) {
				return;
			}

			Dirty = false;
			snapshot = new Dictionary<string, CachedItem>(Items, StringComparer.Ordinal);
		}

		try {
			ASF.GlobalDatabase.SaveToJsonStorage(StorageKey, snapshot);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarningException(e);
		}
	}

	internal static void SetNameID(string marketHashName, ulong nameID) {
		ArgumentException.ThrowIfNullOrEmpty(marketHashName);
		ArgumentOutOfRangeException.ThrowIfZero(nameID);

		lock (ItemsLock) {
			EnsureLoaded();

			CachedItem? existing = Items.GetValueOrDefault(marketHashName);

			Items[marketHashName] = existing != null ? existing with { NameID = nameID } : new CachedItem(nameID, EAssetType.Unknown, EAssetRarity.Unknown, 0, marketHashName);
			Dirty = true;
		}
	}

	[UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode", Justification = "Plugins are never trimmed")]
	[UnconditionalSuppressMessage("AssemblyLoadAot", "IL3050:RequiresDynamicCode", Justification = "Plugins are never AOT-compiled")]
	// Called under ItemsLock
	private static void EnsureLoaded() {
		if (Loaded || (ASF.GlobalDatabase == null)) {
			return;
		}

		JsonElement json = ASF.GlobalDatabase.LoadFromJsonStorage(StorageKey);

		if (json.ValueKind == JsonValueKind.Object) {
			try {
				Dictionary<string, CachedItem>? items = json.Deserialize<Dictionary<string, CachedItem>>(PluginJson.LenientOptions);

				if (items != null) {
					foreach ((string marketHashName, CachedItem item) in items) {
						Items.TryAdd(marketHashName, item);
					}
				}
			} catch (Exception e) {
				// The cache only saves requests, starting from an empty one is fine
				ASF.ArchiLogger.LogGenericWarningException(e);
			}
		}

		Loaded = true;
	}
}
