using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

// Writes our section into the bot's own config file, ASF notices the change and reloads the bot with it.
// Only uses JsonDocument and JsonSerializer: JsonNode is mostly trimmed away from ASF's Docker image.
internal static class BotConfigWriter {
	private static readonly JsonDocumentOptions ReadOptions = new() {
		AllowTrailingCommas = true,
		CommentHandling = JsonCommentHandling.Skip
	};

	// Same formatting as the files ASF writes itself
	private static readonly JsonSerializerOptions WriteOptions = new() {
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		IndentCharacter = '\t',
		IndentSize = 1,
		TypeInfoResolver = PluginJson.CreateResolver(),
		WriteIndented = true
	};

	[SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "The path is built by ASF for a bot it has registered, callers look the bot up first, the request never provides a path")]
	[UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode", Justification = "Plugins are never trimmed")]
	[UnconditionalSuppressMessage("AssemblyLoadAot", "IL3050:RequiresDynamicCode", Justification = "Plugins are never AOT-compiled")]
	internal static async Task<string?> WriteAsync(Bot bot, MarketSellerConfig config) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(config);

		string filePath = Bot.GetFilePath(bot.BotName, Bot.EFileType.Config);

		if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
			return "fichier de config du bot introuvable";
		}

		// Insertion order is kept, so the other settings stay where the user put them
		Dictionary<string, JsonElement> root = new(StringComparer.Ordinal);

		try {
			string content = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);

			using JsonDocument document = JsonDocument.Parse(content, ReadOptions);

			if (document.RootElement.ValueKind != JsonValueKind.Object) {
				return "le fichier de config du bot n'est pas un objet JSON";
			}

			foreach (JsonProperty property in document.RootElement.EnumerateObject()) {
				root[property.Name] = property.Value.Clone();
			}
		} catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
			return $"lecture de la config du bot impossible : {e.Message}";
		}

		root[MarketSellerConfig.PropertyName] = config.ToJsonElement();

		// Written next to it then renamed, so ASF never reads a half-written file
		string temporaryFilePath = $"{filePath}.new";

		try {
			await File.WriteAllTextAsync(temporaryFilePath, JsonSerializer.Serialize(root, WriteOptions)).ConfigureAwait(false);

			File.Move(temporaryFilePath, filePath, true);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return $"écriture de la config du bot impossible : {e.Message}";
		}

		return null;
	}
}
