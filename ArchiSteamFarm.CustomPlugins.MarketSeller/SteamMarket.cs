using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Data;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Web;
using ArchiSteamFarm.Web.Responses;
using SteamKit2;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

internal sealed record OrderHistogram(uint? HighestBuyOrder, uint? LowestSellOrder, ImmutableArray<SellOrderLevel> SellOrderGraph);

internal sealed record OwnListing(ulong ListingID, uint AppID, ulong ContextID, ulong AssetID, string? MarketHashName, uint BuyerPrice, uint SellerPrice) {
	// Read from the item description attached to the listing, see ItemClassifier
	internal string? IconHash { get; init; }
	internal string? Name { get; init; }
	internal EAssetRarity Rarity { get; init; }
	internal uint RealAppID { get; init; }
	internal EAssetType Type { get; init; }
}

internal readonly record struct SellResult(bool Success, bool NeedsConfirmation, string? Message);

public sealed class MarketRateLimitedException : Exception {
	public MarketRateLimitedException() { }
	public MarketRateLimitedException(string message) : base(message) { }
	public MarketRateLimitedException(string message, Exception innerException) : base(message, innerException) { }
}

// Every call goes through a single process-wide throttle, since Steam rate-limits the market per IP, not per account
internal static partial class SteamMarket {
	internal static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(15);

	private const byte ListingsPageSize = 100;

	// Steam answers market errors (e.g. sellitem failures) with 4xx/5xx codes and a JSON body we want to read, and a single try is enough
	private const WebBrowser.ERequestOptions ErrorTolerantOptions = WebBrowser.ERequestOptions.ReturnClientErrors | WebBrowser.ERequestOptions.ReturnServerErrors | WebBrowser.ERequestOptions.AllowInvalidBodyOnErrors;

	private static readonly Uri CommunityURL = ArchiWebHandler.SteamCommunityURL;

	// Steam's new market page loads everything client-side and no longer contains Market_LoadOrderSpread(item_nameid),
	// this cookie asks for the classic page instead, same as the BoosterManager plugin does.
	// Steam's firewall answers 429 to that page unless the request looks like a browser, hence SteamWafWorkarounds below.
	private static readonly KeyValuePair<string, string>[] ClassicMarketHeaders = [new("Cookie", "bMarketOptOut=1")];

	// Market writes go through Steam's web application firewall, which wants browser-like headers, and are never retried to avoid duplicates
	private const WebBrowser.ERequestOptions MarketWriteOptions = ErrorTolerantOptions | WebBrowser.ERequestOptions.SteamWafWorkarounds;
	private static readonly SemaphoreSlim RequestsSemaphore = new(1, 1);

	internal static DateTime RateLimitedUntil { get; private set; } = DateTime.MinValue;

	private static DateTime LastRequest = DateTime.MinValue;

	internal static async Task<ulong?> GetItemNameIDAsync(Bot bot, string marketHashName, ushort delay) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentException.ThrowIfNullOrEmpty(marketHashName);

		Uri request = new(CommunityURL, $"/market/listings/{Asset.SteamAppID}/{Uri.EscapeDataString(marketHashName)}");

		StreamResponse? response = await Throttled(delay, () => bot.ArchiWebHandler.WebBrowser.UrlGetToStream(request, ClassicMarketHeaders, requestOptions: WebBrowser.ERequestOptions.ReturnClientErrors | WebBrowser.ERequestOptions.ReturnServerErrors | WebBrowser.ERequestOptions.SteamWafWorkarounds)).ConfigureAwait(false);

		if (response == null) {
			return null;
		}

		await using (response.ConfigureAwait(false)) {
			if (!IsSuccess(response.StatusCode) || (response.Content == null)) {
				return null;
			}

			using StreamReader reader = new(response.Content);

			string html = await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);

			Match match = ItemNameIDRegex().Match(html);

			if (match.Success && ulong.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong nameID) && (nameID > 0)) {
				return nameID;
			}

			bot.ArchiLogger.LogGenericWarning($"Identifiant introuvable dans la page du marché de {marketHashName} (HTTP {(int) response.StatusCode}, {html.Length} caractères), Steam a peut-être changé cette page");

			return null;
		}
	}

	internal static async Task<List<OwnListing>?> GetMyListingsAsync(Bot bot, ushort delay) {
		ArgumentNullException.ThrowIfNull(bot);

		List<OwnListing> result = [];

		for (uint start = 0; ; start += ListingsPageSize) {
			Uri request = new(CommunityURL, $"/market/mylistings?norender=1&l=english&start={start}&count={ListingsPageSize}");

			ObjectResponse<JsonElement>? response = await Throttled(delay, () => bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<JsonElement>(request, requestOptions: ErrorTolerantOptions)).ConfigureAwait(false);

			if (response?.Content is not { ValueKind: JsonValueKind.Object } json || !GetBool(json, "success")) {
				return null;
			}

			List<OwnListing>? page = ParseListings(json);

			if (page == null) {
				bot.ArchiLogger.LogGenericWarning("Format de réponse inattendu pour la liste de tes annonces Steam");

				return null;
			}

			result.AddRange(page);

			uint totalCount = GetUInt(json, "total_count") ?? 0;

			if ((page.Count == 0) || (start + ListingsPageSize >= totalCount)) {
				return result;
			}
		}
	}

	internal static async Task<OrderHistogram?> GetOrderHistogramAsync(Bot bot, ulong itemNameID, string country, ECurrencyCode currency, ushort delay) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentOutOfRangeException.ThrowIfZero(itemNameID);
		ArgumentException.ThrowIfNullOrEmpty(country);

		Uri request = new(CommunityURL, $"/market/itemordershistogram?country={country}&language=english&currency={(uint) currency}&item_nameid={itemNameID}&two_factor=0");

		ObjectResponse<JsonElement>? response = await Throttled(delay, () => bot.ArchiWebHandler.WebBrowser.UrlGetToJsonObject<JsonElement>(request, requestOptions: ErrorTolerantOptions)).ConfigureAwait(false);

		if (response?.Content is not { ValueKind: JsonValueKind.Object } json || !GetBool(json, "success")) {
			return null;
		}

		ImmutableArray<SellOrderLevel>.Builder graph = ImmutableArray.CreateBuilder<SellOrderLevel>();

		if (TryGetMember(json, "sell_order_graph", out JsonElement graphJson) && (graphJson.ValueKind == JsonValueKind.Array)) {
			foreach (JsonElement level in graphJson.EnumerateArray()) {
				if ((level.ValueKind != JsonValueKind.Array) || (level.GetArrayLength() < 2) || !TryGetDecimal(level[0], out decimal price) || !TryGetDecimal(level[1], out decimal cumulativeQuantity)) {
					continue;
				}

				graph.Add(new SellOrderLevel(MarketPricing.ToCents(price), (uint) Math.Clamp(cumulativeQuantity, 0, uint.MaxValue)));
			}
		}

		return new OrderHistogram(GetUInt(json, "highest_buy_order"), GetUInt(json, "lowest_sell_order"), graph.ToImmutable());
	}

	internal static async Task<List<PriceHistoryEntry>?> GetPriceHistoryAsync(Bot bot, string marketHashName, ushort delay) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentException.ThrowIfNullOrEmpty(marketHashName);

		Uri request = new(CommunityURL, $"/market/pricehistory/?appid={Asset.SteamAppID}&market_hash_name={Uri.EscapeDataString(marketHashName)}");

		ObjectResponse<JsonElement>? response = await Throttled(delay, () => bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<JsonElement>(request, requestOptions: ErrorTolerantOptions)).ConfigureAwait(false);

		if (response?.Content is not { ValueKind: JsonValueKind.Object } json || !GetBool(json, "success")) {
			return null;
		}

		List<PriceHistoryEntry> result = [];

		if (!TryGetMember(json, "prices", out JsonElement prices) || (prices.ValueKind != JsonValueKind.Array)) {
			return result;
		}

		foreach (JsonElement entry in prices.EnumerateArray()) {
			if ((entry.ValueKind != JsonValueKind.Array) || (entry.GetArrayLength() < 3) || (entry[0].ValueKind != JsonValueKind.String) || !TryGetDecimal(entry[1], out decimal price) || !TryGetDecimal(entry[2], out decimal volume)) {
				continue;
			}

			if (TryParseHistoryDate(entry[0].GetString(), out DateTime date)) {
				result.Add(new PriceHistoryEntry(date, price, (uint) Math.Clamp(volume, 0, uint.MaxValue)));
			}
		}

		return result;
	}

	internal static async Task<bool> RemoveListingAsync(Bot bot, ulong listingID, ushort delay) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentOutOfRangeException.ThrowIfZero(listingID);

		Uri request = new(CommunityURL, $"/market/removelisting/{listingID}");
		Uri referer = new(CommunityURL, "/market/");

		return await Throttled(delay, () => bot.ArchiWebHandler.UrlPostWithSession(request, referer: referer, requestOptions: WebBrowser.ERequestOptions.SteamWafWorkarounds, maxTries: 1)).ConfigureAwait(false);
	}

	internal static async Task<SellResult> SellItemAsync(Bot bot, ulong assetID, uint amount, uint sellerPrice, ushort delay) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentOutOfRangeException.ThrowIfZero(assetID);
		ArgumentOutOfRangeException.ThrowIfZero(amount);
		ArgumentOutOfRangeException.ThrowIfZero(sellerPrice);

		Uri request = new(CommunityURL, "/market/sellitem/");
		Uri referer = new(CommunityURL, "/market/");

		// Extra entry for sessionID
		Dictionary<string, string> data = new(6, StringComparer.Ordinal) {
			{ "appid", Asset.SteamAppID.ToString(CultureInfo.InvariantCulture) },
			{ "contextid", Asset.SteamCommunityContextID.ToString(CultureInfo.InvariantCulture) },
			{ "assetid", assetID.ToString(CultureInfo.InvariantCulture) },
			{ "amount", amount.ToString(CultureInfo.InvariantCulture) },
			{ "price", sellerPrice.ToString(CultureInfo.InvariantCulture) }
		};

		ObjectResponse<JsonElement>? response = await Throttled(delay, () => bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<JsonElement>(request, data: data, referer: referer, requestOptions: MarketWriteOptions, maxTries: 1)).ConfigureAwait(false);

		if (response == null) {
			return new SellResult(false, false, "pas de réponse de Steam");
		}

		if (response.Content.ValueKind != JsonValueKind.Object) {
			return new SellResult(false, false, $"réponse invalide de Steam (HTTP {(int) response.StatusCode})");
		}

		JsonElement json = response.Content;

		return new SellResult(GetBool(json, "success"), GetBool(json, "requires_confirmation") || GetBool(json, "needs_mobile_confirmation"), GetString(json, "message"));
	}

	internal static List<OwnListing>? ParseListings(JsonElement json) {
		if (TryGetMember(json, "listings", out JsonElement listings) && (listings.ValueKind == JsonValueKind.Array)) {
			return ParseListingsJson(json, listings);
		}

		string? html = GetString(json, "results_html");

		return html != null ? ParseListingsHtml(json, html) : null;
	}

	internal static bool TryParseHistoryDate(string? text, out DateTime date) {
		date = default;

		if (string.IsNullOrEmpty(text)) {
			return false;
		}

		// "Oct 04 2026 01: +0"
		int separator = text.IndexOf(':', StringComparison.Ordinal);

		if (separator > 0) {
			text = text[..separator];
		}

		return DateTime.TryParseExact(text, "MMM dd yyyy HH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
	}

	private static bool GetBool(JsonElement json, string propertyName) {
		if (!TryGetMember(json, propertyName, out JsonElement value)) {
			return false;
		}

		return value.ValueKind switch {
			JsonValueKind.True => true,
			JsonValueKind.Number => value.TryGetInt64(out long number) && (number != 0),
			JsonValueKind.String => value.GetString() is "1" or "true",
			_ => false
		};
	}

	private static string? GetString(JsonElement json, string propertyName) => TryGetMember(json, propertyName, out JsonElement value) && (value.ValueKind == JsonValueKind.String) ? value.GetString() : null;

	private static uint? GetUInt(JsonElement json, string propertyName) {
		ulong? value = GetULong(json, propertyName);

		return value <= uint.MaxValue ? (uint) value.Value : null;
	}

	private static ulong? GetULong(JsonElement json, string propertyName) {
		if (!TryGetMember(json, propertyName, out JsonElement value)) {
			return null;
		}

		return value.ValueKind switch {
			JsonValueKind.Number when value.TryGetUInt64(out ulong number) => number,
			JsonValueKind.String when ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) => number,
			_ => null
		};
	}

	private static bool IsSuccess(HttpStatusCode statusCode) => (int) statusCode is >= 200 and < 300;

	[GeneratedRegex(@"Market_LoadOrderSpread\(\s*(?<id>\d+)\s*\)", RegexOptions.CultureInvariant)]
	private static partial Regex ItemNameIDRegex();

	// The "assets" object maps appid -> contextid -> assetid -> item description
	private static JsonElement? LookupAsset(JsonElement json, uint appID, ulong contextID, ulong assetID) {
		if (!TryGetMember(json, "assets", out JsonElement assets) || (assets.ValueKind != JsonValueKind.Object)) {
			return null;
		}

		if (!TryGetMember(assets, appID.ToString(CultureInfo.InvariantCulture), out JsonElement app) || (app.ValueKind != JsonValueKind.Object)) {
			return null;
		}

		if (!TryGetMember(app, contextID.ToString(CultureInfo.InvariantCulture), out JsonElement context) || (context.ValueKind != JsonValueKind.Object)) {
			return null;
		}

		return TryGetMember(context, assetID.ToString(CultureInfo.InvariantCulture), out JsonElement asset) && (asset.ValueKind == JsonValueKind.Object) ? asset : null;
	}

	private static OwnListing WithDescription(OwnListing listing, JsonElement? description) {
		string? marketHashName = listing.MarketHashName ?? (description.HasValue ? GetString(description.Value, "market_hash_name") : null);

		if (!description.HasValue) {
			return listing with { MarketHashName = marketHashName, RealAppID = ItemClassifier.GetRealAppID(null, marketHashName) };
		}

		JsonElement item = description.Value;
		(EAssetType type, EAssetRarity rarity) = ItemClassifier.Classify(GetString(item, "type"), marketHashName);

		return listing with {
			IconHash = GetString(item, "icon_url") is { Length: > 0 } iconHash ? iconHash : null,
			MarketHashName = marketHashName,
			Name = GetString(item, "name") is { Length: > 0 } name ? name : null,
			Rarity = rarity,
			RealAppID = ItemClassifier.GetRealAppID(GetUInt(item, "market_fee_app"), marketHashName),
			Type = type
		};
	}

	[GeneratedRegex(@"<span[^>]*\btitle=""[^""]*""[^>]*>\s*\(?(?<price>[^<()]*?\d[^<()]*?)\)?\s*</span>", RegexOptions.CultureInvariant)]
	private static partial Regex ListingPriceRegex();

	private static List<OwnListing> ParseListingsHtml(JsonElement json, string html) {
		List<OwnListing> result = [];

		MatchCollection removeLinks = RemoveListingRegex().Matches(html);

		foreach (Match removeLink in removeLinks) {
			if (!ulong.TryParse(removeLink.Groups["listing"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong listingID) ||
				!uint.TryParse(removeLink.Groups["app"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint appID) ||
				!ulong.TryParse(removeLink.Groups["context"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong contextID) ||
				!ulong.TryParse(removeLink.Groups["asset"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong assetID)) {
				continue;
			}

			// Row of this listing: from its id until the next listing row
			int rowStart = html.IndexOf($"id=\"mylisting_{listingID}\"", StringComparison.Ordinal);

			if (rowStart < 0) {
				continue;
			}

			int rowEnd = html.IndexOf("id=\"mylisting_", rowStart + 1, StringComparison.Ordinal);
			string row = rowEnd > rowStart ? html[rowStart..rowEnd] : html[rowStart..];

			int priceStart = row.IndexOf("market_listing_price", StringComparison.Ordinal);

			if (priceStart < 0) {
				continue;
			}

			MatchCollection prices = ListingPriceRegex().Matches(row, priceStart);

			// First price is what the buyer pays, second one (in parentheses) is what we receive
			if ((prices.Count < 2) || !TryParseDisplayedPrice(prices[0].Groups["price"].Value, out uint buyerPrice) || !TryParseDisplayedPrice(prices[1].Groups["price"].Value, out uint sellerPrice)) {
				continue;
			}

			result.Add(WithDescription(new OwnListing(listingID, appID, contextID, assetID, null, buyerPrice, sellerPrice), LookupAsset(json, appID, contextID, assetID)));
		}

		return result;
	}

	private static List<OwnListing> ParseListingsJson(JsonElement json, JsonElement listings) {
		List<OwnListing> result = [];

		foreach (JsonElement listing in listings.EnumerateArray()) {
			if ((listing.ValueKind != JsonValueKind.Object) || !TryGetMember(listing, "asset", out JsonElement asset) || (asset.ValueKind != JsonValueKind.Object)) {
				continue;
			}

			ulong? listingID = GetULong(listing, "listingid");
			uint? appID = GetUInt(asset, "appid");
			ulong? contextID = GetULong(asset, "contextid");
			ulong? assetID = GetULong(asset, "id");

			// "price" is what we receive, "fee" is the sum of Steam and publisher fees
			uint? sellerPrice = GetUInt(listing, "price");
			uint? fee = GetUInt(listing, "fee");

			if (listingID is not > 0 || appID is not > 0 || contextID is not > 0 || assetID is not > 0 || sellerPrice is not > 0 || !fee.HasValue) {
				continue;
			}

			// The listing's own "asset" usually carries the full description, the top-level "assets" object is the fallback
			JsonElement? description = TryGetMember(asset, "type", out _) ? asset : LookupAsset(json, appID.Value, contextID.Value, assetID.Value) ?? asset;

			result.Add(WithDescription(new OwnListing(listingID.Value, appID.Value, contextID.Value, assetID.Value, GetString(asset, "market_hash_name"), sellerPrice.Value + fee.Value, sellerPrice.Value), description));
		}

		return result;
	}

	[GeneratedRegex(@"RemoveMarketListing\(\s*'mylisting'\s*,\s*'(?<listing>\d+)'\s*,\s*(?<app>\d+)\s*,\s*'(?<context>\d+)'\s*,\s*'(?<asset>\d+)'\s*\)", RegexOptions.CultureInvariant)]
	private static partial Regex RemoveListingRegex();

	private static async Task<T> Throttled<T>(ushort delay, Func<Task<T>> request) {
		await RequestsSemaphore.WaitAsync().ConfigureAwait(false);

		try {
			if (DateTime.UtcNow < RateLimitedUntil) {
				throw new MarketRateLimitedException();
			}

			TimeSpan wait = LastRequest.AddMilliseconds(delay) - DateTime.UtcNow;

			if (wait > TimeSpan.Zero) {
				await Task.Delay(wait).ConfigureAwait(false);
			}

			T result;

			try {
				result = await request().ConfigureAwait(false);
			} finally {
				LastRequest = DateTime.UtcNow;
			}

			if (result is BasicResponse { StatusCode: HttpStatusCode.TooManyRequests }) {
				RateLimitedUntil = DateTime.UtcNow + RateLimitCooldown;

				if (result is IAsyncDisposable disposable) {
					await disposable.DisposeAsync().ConfigureAwait(false);
				}

				throw new MarketRateLimitedException();
			}

			return result;
		} finally {
			RequestsSemaphore.Release();
		}
	}

	// Keeps digits only, displayed prices always have 2 decimals for the currencies Steam uses on the market ("0,05€", "$1,234.56")
	private static bool TryParseDisplayedPrice(string text, out uint cents) {
		cents = 0;

		foreach (char character in text) {
			if (!char.IsAsciiDigit(character)) {
				continue;
			}

			if (cents > (uint.MaxValue - 9) / 10) {
				return false;
			}

			cents = (cents * 10) + (uint) (character - '0');
		}

		return cents > 0;
	}

	// JsonElement.TryGetProperty() is trimmed away from ASF's Docker image (trimmed build), so properties are looked up by hand
	private static bool TryGetMember(JsonElement json, string propertyName, out JsonElement value) {
		if (json.ValueKind == JsonValueKind.Object) {
			foreach (JsonProperty property in json.EnumerateObject()) {
				if (property.Name == propertyName) {
					value = property.Value;

					return true;
				}
			}
		}

		value = default;

		return false;
	}

	private static bool TryGetDecimal(JsonElement json, out decimal value) {
		value = 0;

		return json.ValueKind switch {
			JsonValueKind.Number => json.TryGetDecimal(out value),
			JsonValueKind.String => decimal.TryParse(json.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value),
			_ => false
		};
	}
}
