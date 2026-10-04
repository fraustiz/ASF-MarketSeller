using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ArchiSteamFarm.Steam.Data;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

internal static class PluginJson {
	// Used for user-written config, rejects unknown properties so typos don't go unnoticed
	internal static readonly JsonSerializerOptions StrictOptions = CreateOptions(JsonUnmappedMemberHandling.Disallow);

	// Used for our own persisted data
	internal static readonly JsonSerializerOptions LenientOptions = CreateOptions(JsonUnmappedMemberHandling.Skip);

	// Trimmed ASF builds (like the Docker image) turn off the default reflection-based resolver, it has to be set explicitly like ASF does
	[UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode", Justification = "Plugins are never trimmed")]
	[UnconditionalSuppressMessage("AssemblyLoadAot", "IL3050:RequiresDynamicCode", Justification = "Plugins are never AOT-compiled")]
	internal static DefaultJsonTypeInfoResolver CreateResolver() => new();

	private static JsonSerializerOptions CreateOptions(JsonUnmappedMemberHandling unmappedMemberHandling) {
		JsonSerializerOptions result = new() {
			AllowTrailingCommas = true,
			PropertyNameCaseInsensitive = true,
			ReadCommentHandling = JsonCommentHandling.Skip,
			TypeInfoResolver = CreateResolver(),
			UnmappedMemberHandling = unmappedMemberHandling
		};

		// The non-generic JsonStringEnumConverter is trimmed away from ASF's Docker image, the generic one isn't
		result.Converters.Add(new JsonStringEnumConverter<EAssetRarity>());
		result.Converters.Add(new JsonStringEnumConverter<EAssetType>());
		result.Converters.Add(new JsonStringEnumConverter<EPriceSource>());
		result.MakeReadOnly();

		return result;
	}
}
