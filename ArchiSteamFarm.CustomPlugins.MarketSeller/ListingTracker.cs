using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ArchiSteamFarm.Steam;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

// Remembers which market listings the plugin created, from the confirmations it accepted, persisted in the bot's database.
// Those are fully managed. Listings created by hand are repriced too, but never withdrawn by a price lock: the user chose to sell them.
internal sealed class ListingTracker {
	private const string StorageKey = $"{nameof(MarketSeller)}.Listings";

	// Listing IDs only grow, the oldest ones are long gone from the market when this many are kept
	private const ushort MaxListings = 5000;

	private readonly Bot Bot;
	private readonly HashSet<ulong> ListingIDs = [];
	private readonly Lock StateLock = new();

	internal ListingTracker(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		Bot = bot;

		Load();
	}

	internal void Add(IEnumerable<ulong> listingIDs) {
		ArgumentNullException.ThrowIfNull(listingIDs);

		lock (StateLock) {
			bool changed = false;

			foreach (ulong listingID in listingIDs) {
				changed |= (listingID > 0) && ListingIDs.Add(listingID);
			}

			if (!changed) {
				return;
			}

			if (ListingIDs.Count > MaxListings) {
				List<ulong> sortedListingIDs = [.. ListingIDs];

				sortedListingIDs.Sort();

				foreach (ulong oldListingID in sortedListingIDs.Take(ListingIDs.Count - MaxListings)) {
					ListingIDs.Remove(oldListingID);
				}
			}

			Save();
		}
	}

	internal bool IsCreatedByPlugin(ulong listingID) {
		lock (StateLock) {
			return ListingIDs.Contains(listingID);
		}
	}

	private void Load() {
		try {
			JsonElement json = Bot.BotDatabase.LoadFromJsonStorage(StorageKey);

			if (json.ValueKind != JsonValueKind.Array) {
				return;
			}

			foreach (JsonElement listingID in json.EnumerateArray()) {
				if ((listingID.ValueKind == JsonValueKind.String) && ulong.TryParse(listingID.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsedListingID)) {
					ListingIDs.Add(parsedListingID);
				}
			}
		} catch (Exception e) {
			// Only makes us treat our own listings as hand-made ones, which is the cautious side
			Bot.ArchiLogger.LogGenericWarningException(e);
		}
	}

	// Called under StateLock
	private void Save() {
		try {
			Bot.BotDatabase.SaveToJsonStorage(StorageKey, ListingIDs.Select(static listingID => listingID.ToString(CultureInfo.InvariantCulture)).ToList());
		} catch (Exception e) {
			Bot.ArchiLogger.LogGenericWarningException(e);
		}
	}
}
