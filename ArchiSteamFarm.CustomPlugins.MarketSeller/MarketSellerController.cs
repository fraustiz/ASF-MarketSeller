using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.IPC.Controllers.Api;
using ArchiSteamFarm.IPC.Responses;
using ArchiSteamFarm.Steam;
using Microsoft.AspNetCore.Mvc;

namespace ArchiSteamFarm.CustomPlugins.MarketSeller;

// Behind ASF's IPC authentication like every /Api endpoint, the web page sends the IPCPassword saved by ASF-ui
[Route("/Api/MarketSeller")]
public sealed class MarketSellerController : ArchiController {
	[HttpGet]
	[ProducesResponseType<GenericResponse<MarketSellerOverview>>((int) HttpStatusCode.OK)]
	public ActionResult<GenericResponse<MarketSellerOverview>> Get() => Ok(new GenericResponse<MarketSellerOverview>(MarketSellerPlugin.GetOverview()));

	[HttpGet("Update")]
	[ProducesResponseType<GenericResponse<UpdateInfo>>((int) HttpStatusCode.OK)]
	public async Task<ActionResult<GenericResponse<UpdateInfo>>> GetUpdate([FromQuery] bool refresh = false) => Ok(new GenericResponse<UpdateInfo>(await UpdateChecker.GetAsync(refresh).ConfigureAwait(false)));

	[HttpGet("{botName:required}")]
	[ProducesResponseType<GenericResponse<BotDetails>>((int) HttpStatusCode.OK)]
	[ProducesResponseType<GenericResponse>((int) HttpStatusCode.BadRequest)]
	public ActionResult<GenericResponse> GetBot(string botName) {
		ArgumentException.ThrowIfNullOrEmpty(botName);

		Bot? bot = Bot.GetBot(botName);

		return bot != null ? Ok(new GenericResponse<BotDetails>(MarketSellerPlugin.GetBotDetails(bot))) : BadRequest(new GenericResponse(false, $"Bot introuvable : {botName}"));
	}

	// POST rather than PUT: HttpPutAttribute is trimmed away from ASF's Docker image
	[HttpPost("{botName:required}/Config")]
	[ProducesResponseType<GenericResponse>((int) HttpStatusCode.OK)]
	[ProducesResponseType<GenericResponse>((int) HttpStatusCode.BadRequest)]
	public async Task<ActionResult<GenericResponse>> SaveConfig(string botName, [FromBody] JsonElement config) {
		ArgumentException.ThrowIfNullOrEmpty(botName);

		Bot? bot = Bot.GetBot(botName);

		if (bot == null) {
			return BadRequest(new GenericResponse(false, $"Bot introuvable : {botName}"));
		}

		MarketSellerConfig? parsedConfig = MarketSellerConfig.Parse(config, out string? error);

		if (parsedConfig == null) {
			return BadRequest(new GenericResponse(false, $"Réglages invalides : {error}"));
		}

		string? writeError = await BotConfigWriter.WriteAsync(bot, parsedConfig).ConfigureAwait(false);

		return writeError == null ? Ok(new GenericResponse(true, "Réglages enregistrés, le bot se reconnecte pour les appliquer.")) : BadRequest(new GenericResponse(false, writeError));
	}

	[HttpPost("{botName:required}/Start/{operation:required}")]
	[ProducesResponseType<GenericResponse>((int) HttpStatusCode.OK)]
	[ProducesResponseType<GenericResponse>((int) HttpStatusCode.BadRequest)]
	public ActionResult<GenericResponse> Start(string botName, EOperation operation) {
		ArgumentException.ThrowIfNullOrEmpty(botName);

		if (!Enum.IsDefined(operation)) {
			return BadRequest(new GenericResponse(false, $"Opération inconnue : {operation}"));
		}

		Bot? bot = Bot.GetBot(botName);

		if (bot == null) {
			return BadRequest(new GenericResponse(false, $"Bot introuvable : {botName}"));
		}

		if (!MarketSellerPlugin.TryGetSeller(bot, out BotMarketSeller? seller)) {
			return BadRequest(new GenericResponse(false, "MarketSeller n'est pas activé pour ce bot."));
		}

		return seller.TryStart(operation) ? Ok(new GenericResponse(true, $"{ReportFormatter.GetTitle(operation)} lancé.")) : BadRequest(new GenericResponse(false, "Une opération est déjà en cours pour ce bot."));
	}
}
