using System;
using System.Collections.Generic;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

// All prices are in cents of the wallet currency. "Buyer price" is what the buyer pays, "seller price" is what we receive.
internal readonly record struct PriceQuote(uint BasePrice, uint AdjustedPrice, uint BuyerPrice, uint SellerPrice);

internal readonly record struct SellOrderLevel(uint Price, uint CumulativeQuantity);

internal readonly record struct PriceHistoryEntry(DateTime Date, decimal Price, uint Volume);

internal static class MarketPricing {
	// Seller receives 1 cent, Steam and the publisher take 1 cent each
	internal const uint MinimumBuyerPrice = 3;

	private const uint MaximumBuyerPrice = 10_000_000;
	private const uint PublisherFeePercent = 10;
	private const uint SteamFeePercent = 5;

	internal static PriceQuote CreateQuote(uint basePrice, PricingConfig pricing, bool applyAdjustments = true) {
		ArgumentOutOfRangeException.ThrowIfZero(basePrice);
		ArgumentNullException.ThrowIfNull(pricing);

		decimal adjusted = applyAdjustments ? RoundHalfUp(basePrice * pricing.Multiplier) + pricing.OffsetCents : basePrice;
		uint adjustedPrice = (uint) Math.Clamp(adjusted, 1, MaximumBuyerPrice);

		uint buyerPrice = Math.Max(adjustedPrice, Math.Max(pricing.MinCents, MinimumBuyerPrice));

		if (pricing.MaxCents > 0) {
			buyerPrice = Math.Min(buyerPrice, pricing.MaxCents);
		}

		uint sellerPrice = GetSellerPrice(buyerPrice);

		// Fees are rounded, so the closest listable price can be slightly below the requested one
		return new PriceQuote(basePrice, adjustedPrice, GetBuyerPrice(sellerPrice), sellerPrice);
	}

	// Mirrors Steam's CalculateAmountToSendForDesiredReceivedAmount() from economy_common.js, integer division rounds down like its Math.floor()
	internal static uint GetBuyerPrice(uint sellerPrice) {
		ArgumentOutOfRangeException.ThrowIfZero(sellerPrice);

		uint steamFee = Math.Max(sellerPrice * SteamFeePercent / 100, 1);
		uint publisherFee = Math.Max(sellerPrice * PublisherFeePercent / 100, 1);

		return sellerPrice + steamFee + publisherFee;
	}

	// Cheapest price among other sellers, after removing our own listings from the order book
	internal static uint? GetLowestCompetitorPrice(IReadOnlyList<SellOrderLevel> sellOrderGraph, IReadOnlyDictionary<uint, uint>? ownListingsPerPrice) {
		ArgumentNullException.ThrowIfNull(sellOrderGraph);

		uint previousCumulativeQuantity = 0;

		foreach ((uint price, uint cumulativeQuantity) in sellOrderGraph) {
			uint quantity = cumulativeQuantity > previousCumulativeQuantity ? cumulativeQuantity - previousCumulativeQuantity : 0;

			previousCumulativeQuantity = Math.Max(previousCumulativeQuantity, cumulativeQuantity);

			uint ownQuantity = ownListingsPerPrice?.GetValueOrDefault(price) ?? 0;

			if (quantity > ownQuantity) {
				return price;
			}
		}

		return null;
	}

	// Volume-weighted average of the sales since the given date, in cents
	internal static uint? GetAverageSoldPrice(IEnumerable<PriceHistoryEntry> history, DateTime since) {
		ArgumentNullException.ThrowIfNull(history);

		decimal total = 0;
		ulong volume = 0;

		foreach ((DateTime date, decimal price, uint entryVolume) in history) {
			if ((date < since) || (entryVolume == 0) || (price <= 0)) {
				continue;
			}

			total += price * entryVolume;
			volume += entryVolume;
		}

		if (volume == 0) {
			return null;
		}

		uint result = ToCents(total / volume);

		return result > 0 ? result : null;
	}

	// Highest amount we can receive while the buyer pays at most the given price, 0 if the price is below the market minimum
	internal static uint GetSellerPrice(uint buyerPrice) {
		if (buyerPrice < MinimumBuyerPrice) {
			return 0;
		}

		uint sellerPrice = Math.Max(1, buyerPrice * 100 / (100 + SteamFeePercent + PublisherFeePercent));

		while (GetBuyerPrice(sellerPrice + 1) <= buyerPrice) {
			sellerPrice++;
		}

		while ((sellerPrice > 1) && (GetBuyerPrice(sellerPrice) > buyerPrice)) {
			sellerPrice--;
		}

		return sellerPrice;
	}

	internal static uint ToCents(decimal currencyUnits) => currencyUnits <= 0 ? 0 : (uint) Math.Min(RoundHalfUp(currencyUnits * 100), MaximumBuyerPrice);

	// Math.Round() overloads taking a MidpointRounding aren't available in ASF's trimmed Docker image, values here are never negative
	private static decimal RoundHalfUp(decimal value) => Math.Floor(value + 0.5m);
}
