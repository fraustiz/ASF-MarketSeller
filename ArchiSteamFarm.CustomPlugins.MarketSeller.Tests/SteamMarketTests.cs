using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller.Tests;

#pragma warning disable CA1812 // False positive, the class is used during MSTest
[TestClass]
internal sealed class SteamMarketTests {
	[TestMethod]
	public void HistoryDateIsParsedAsUtc() {
		Assert.IsTrue(SteamMarket.TryParseHistoryDate("Oct 04 2026 01: +0", out DateTime date));
		Assert.AreEqual(new DateTime(2026, 10, 4, 1, 0, 0, DateTimeKind.Utc), date);
		Assert.AreEqual(DateTimeKind.Utc, date.Kind);
		Assert.IsFalse(SteamMarket.TryParseHistoryDate("not a date", out _));
	}

	[TestMethod]
	public void ListingsAreParsedFromHtml() {
		const string html = """
			<div class="market_listing_row market_recent_listing_row listing_123" id="mylisting_123">
				<div class="market_listing_right_cell market_listing_my_price">
					<span class="market_table_value">
						<span class="market_listing_price">
							<span style="display: inline-block">
								<span title="This is the price the buyer pays.">0,10€</span>
								<br>
								<span title="This is how much you will receive." style="color: #AFAFAF">(0,08€)</span>
							</span>
						</span>
					</span>
				</div>
				<a href="javascript:RemoveMarketListing('mylisting', '123', 753, '6', '456')" class="item_market_action_button">Remove</a>
			</div>
			""";

		string json = JsonSerializer.Serialize(new Dictionary<string, object> {
			["success"] = true,
			["total_count"] = 1,
			["results_html"] = html,
			["assets"] = new Dictionary<string, object> { ["753"] = new Dictionary<string, object> { ["6"] = new Dictionary<string, object> { ["456"] = new Dictionary<string, string> { ["market_hash_name"] = "440-Scout" } } } }
		});

		AssertSingleListing(json, new OwnListing(123, 753, 6, 456, "440-Scout", 10, 8));
	}

	[TestMethod]
	public void ListingsAreParsedFromJson() {
		const string json = """
			{
				"success": true,
				"total_count": 2,
				"listings": [
					{ "listingid": "123", "price": 8, "fee": 2, "asset": { "appid": 753, "contextid": "6", "id": "456", "amount": "1", "market_hash_name": "440-Scout" } }
				],
				"assets": {}
			}
			""";

		AssertSingleListing(json, new OwnListing(123, 753, 6, 456, "440-Scout", 10, 8));
	}

	[TestMethod]
	public void ListingsTakeHashNameFromAssetsWhenMissing() {
		const string json = """
			{
				"success": true,
				"listings": [
					{ "listingid": 123, "price": "8", "fee": "2", "asset": { "appid": "753", "contextid": "6", "id": "456" } }
				],
				"assets": { "753": { "6": { "456": { "market_hash_name": "440-Scout" } } } }
			}
			""";

		AssertSingleListing(json, new OwnListing(123, 753, 6, 456, "440-Scout", 10, 8));
	}

	[TestMethod]
	public void UnknownListingsFormatIsRejected() {
		using JsonDocument document = JsonDocument.Parse("""{"success": true}""");

		Assert.IsNull(SteamMarket.ParseListings(document.RootElement));
	}

	private static void AssertSingleListing(string json, OwnListing expected) {
		using JsonDocument document = JsonDocument.Parse(json);

		List<OwnListing>? listings = SteamMarket.ParseListings(document.RootElement);

		Assert.IsNotNull(listings);
		Assert.HasCount(1, listings);
		Assert.AreEqual(expected, listings[0]);
	}
}
#pragma warning restore CA1812 // False positive, the class is used during MSTest
