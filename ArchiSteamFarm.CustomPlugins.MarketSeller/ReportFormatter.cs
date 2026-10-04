using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

// Text versions of the run results, used by chat commands, logs and the one-line summaries of the web page
internal static class ReportFormatter {
	private const byte MaxDetailLines = 25;

	internal static string Format(SellRunResult result) {
		ArgumentNullException.ThrowIfNull(result);

		IEnumerable<string> details = result.Lines.Where(static line => line.Status is not (ESellStatus.Locked or ESellStatus.Kept)).Select(line => FormatLine(line, result.Currency));

		return AppendDetails(Summarize(result), details);
	}

	internal static string Format(RepriceRunResult result) {
		ArgumentNullException.ThrowIfNull(result);

		IEnumerable<string> details = result.Lines.Where(static line => line.Status != ERepriceStatus.Unchanged).Select(line => FormatLine(line, result.Currency));
		string text = AppendDetails(Summarize(result), details);

		if (result.Relist != null) {
			text = $"{text}{Environment.NewLine}Remise en vente : {Format(result.Relist)}";
		}

		return text;
	}

	internal static string FormatPrice(uint cents, string? currency) => $"{(cents / 100m).ToString("0.00", CultureInfo.InvariantCulture)} {currency}".TrimEnd();

	internal static string GetTitle(EOperation operation) => operation switch {
		EOperation.SellPreview => "Aperçu de la vente",
		EOperation.Sell => "Vente",
		EOperation.RepricePreview => "Aperçu du réajustement",
		EOperation.Reprice => "Réajustement des prix",
		_ => throw new ArgumentOutOfRangeException(nameof(operation))
	};

	internal static string Summarize(SellRunResult result) {
		ArgumentNullException.ThrowIfNull(result);

		if (result.Error != null) {
			return result.Error;
		}

		uint listed = Sum(result.Lines, ESellStatus.Listed);
		uint skipped = Sum(result.Lines, ESellStatus.Skipped);
		uint failed = Sum(result.Lines, ESellStatus.Failed);
		uint locked = Sum(result.Lines, ESellStatus.Locked) + Sum(result.Lines, ESellStatus.PriceLocked);
		uint kept = Sum(result.Lines, ESellStatus.Kept);

		string summary;

		if ((listed == 0) && (skipped == 0) && (failed == 0)) {
			summary = $"aucun objet à vendre ({locked} verrouillé(s), {kept} gardé(s)).";
		} else {
			ulong buyerTotal = 0;
			ulong sellerTotal = 0;

			foreach (SellLine line in result.Lines.Where(static line => line.Status == ESellStatus.Listed)) {
				buyerTotal += (ulong) (line.BuyerPrice ?? 0) * line.Amount;
				sellerTotal += (ulong) (line.SellerPrice ?? 0) * line.Amount;
			}

			string verb = result.DryRun ? "seraient mis en vente" : "mis en vente";
			string confirmed = result.DryRun ? "" : $", {result.Confirmed} confirmé(s)";

			summary = $"{listed} objet(s) {verb} pour {FormatPrice(Clamp(buyerTotal), result.Currency)} (tu reçois {FormatPrice(Clamp(sellerTotal), result.Currency)}), {skipped} ignoré(s), {locked} verrouillé(s), {kept} gardé(s), {failed} échec(s){confirmed}.";
		}

		return AppendNotices(summary, result.RateLimitedUntil, result.NeedsManualConfirmation);
	}

	internal static string Summarize(RepriceRunResult result) {
		ArgumentNullException.ThrowIfNull(result);

		if (result.Error != null) {
			return result.Error;
		}

		if (result.Lines.Count == 0) {
			return AppendNotices("aucune annonce en cours sur le marché.", result.RateLimitedUntil, false);
		}

		string verb = result.DryRun ? "seraient" : "ont été";
		int ignored = Count(result.Lines, ERepriceStatus.Ignored);
		int managed = result.Lines.Count - ignored;

		string summary = $"{result.Lines.Count} annonce(s) en vente, {managed} gérée(s) dont {result.Lines.Count(static line => (line.Status != ERepriceStatus.Ignored) && !line.CreatedByPlugin)} créée(s) à la main : {Count(result.Lines, ERepriceStatus.Repriced)} {verb} réajustée(s), {Count(result.Lines, ERepriceStatus.Withdrawn)} {verb} retirée(s), {Count(result.Lines, ERepriceStatus.Unchanged) + Count(result.Lines, ERepriceStatus.Kept)} inchangée(s), {Count(result.Lines, ERepriceStatus.Skipped)} sans prix de référence, {Count(result.Lines, ERepriceStatus.Failed)} échec(s). {ignored} non gérée(s).";

		return AppendNotices(summary, result.RateLimitedUntil, false);
	}

	private static string AppendDetails(string summary, IEnumerable<string> details) {
		StringBuilder builder = new(summary);
		int count = 0;

		foreach (string detail in details) {
			if (++count > MaxDetailLines) {
				continue;
			}

			builder.AppendLine();
			builder.Append("- ").Append(detail);
		}

		if (count > MaxDetailLines) {
			builder.AppendLine();
			builder.Append(CultureInfo.InvariantCulture, $"- … et {count - MaxDetailLines} autre(s)");
		}

		return builder.ToString();
	}

	private static string AppendNotices(string summary, DateTime? rateLimitedUntil, bool needsManualConfirmation) {
		if (rateLimitedUntil.HasValue) {
			summary += $" Interrompu : Steam limite les requêtes du marché jusqu'à {rateLimitedUntil.Value.ToLocalTime():HH:mm}.";
		}

		if (needsManualConfirmation) {
			summary += " Confirme les annonces dans l'application Steam Mobile.";
		}

		return summary;
	}

	private static uint Clamp(ulong value) => (uint) Math.Min(value, uint.MaxValue);

	private static int Count(IEnumerable<RepriceLine> lines, ERepriceStatus status) => lines.Count(line => line.Status == status);

	private static string FormatLine(SellLine line, string? currency) => line.Status switch {
		ESellStatus.Listed => $"{line.Name} x{line.Amount} : {FormatPrice(line.BuyerPrice ?? 0, currency)} (tu reçois {FormatPrice(line.SellerPrice ?? 0, currency)})",
		ESellStatus.PriceLocked => $"{line.Name} x{line.Amount} : verrouillé par prix ({FormatPrice(line.AdjustedPrice ?? 0, currency)})",
		ESellStatus.Failed => $"{line.Name} x{line.Amount} : échec ({line.Reason ?? "raison inconnue"})",
		_ => $"{line.Name} x{line.Amount} : ignoré ({line.Reason ?? "raison inconnue"})"
	};

	private static string FormatLine(RepriceLine line, string? currency) => line.Status switch {
		ERepriceStatus.Repriced => $"{line.Name} : {FormatPrice(line.CurrentBuyerPrice, currency)} → {FormatPrice(line.TargetBuyerPrice ?? 0, currency)}",
		ERepriceStatus.Withdrawn => $"{line.Name} : retiré de la vente, {line.Reason}",
		ERepriceStatus.Kept => $"{line.Name} : laissé en vente, {line.Reason}",
		ERepriceStatus.Ignored => $"{line.Name} : non gérée ({line.Reason})",
		ERepriceStatus.Failed => $"{line.Name} : échec ({line.Reason ?? "raison inconnue"})",
		_ => $"{line.Name} : prix laissé tel quel ({line.Reason ?? "raison inconnue"})"
	};

	private static uint Sum(IEnumerable<SellLine> lines, ESellStatus status) => (uint) Math.Min(lines.Where(line => line.Status == status).Sum(static line => (long) line.Amount), uint.MaxValue);
}
