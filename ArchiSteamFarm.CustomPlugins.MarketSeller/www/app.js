/*
 * MarketSeller: page added to ASF's dashboard (ASF-ui) by the MarketSeller plugin.
 * ASF-ui has no extension point, so this script adds a route and a navigation link to the running Vue app,
 * then renders the page with plain DOM. It talks to the plugin's /Api/MarketSeller endpoints.
 */
(function () {
	'use strict';

	if (window.__MARKET_SELLER_STARTED__) {
		return;
	}

	window.__MARKET_SELLER_STARTED__ = true;

	const settings = window.__MARKET_SELLER__ || {};
	const BASE_PATH = window.__BASE_PATH__ || '/';
	const VERSION = settings.version || '';
	const ROUTE_NAME = 'market-seller';
	const ROUTE_PATH = '/market-seller';
	const ICON_BASE = 'https://community.cloudflare.steamstatic.com/economy/image/';
	const STORAGE_BOT = 'market-seller:bot';
	const STORAGE_TAB = 'market-seller:tab';

	const TYPES = [
		['TradingCard', 'Cartes'],
		['FoilTradingCard', 'Cartes brillantes'],
		['Emoticon', 'Émoticônes'],
		['ProfileBackground', 'Fonds de profil'],
		['MiniProfileBackground', 'Fonds de mini-profil'],
		['BoosterPack', 'Boosters'],
		['SteamGems', 'Sacs de gemmes'],
		['SaleItem', 'Objets d\'évènement'],
		['Sticker', 'Autocollants'],
		['ChatEffect', 'Effets de chat'],
		['AvatarProfileFrame', 'Cadres d\'avatar'],
		['AnimatedAvatar', 'Avatars animés'],
		['KeyboardSkin', 'Thèmes de clavier'],
		['StartupVideo', 'Vidéos de démarrage'],
		['ProfileModifier', 'Modificateurs de profil'],
		['Consumable', 'Consommables'],
	];

	const TYPE_LABELS = Object.fromEntries(TYPES);

	const RARITIES = [
		['Common', 'Commune'],
		['Uncommon', 'Peu commune'],
		['Rare', 'Rare'],
	];

	const SOURCES = [
		['LowestSellOrder', 'Prix du marché', 'Aligné sur l\'annonce la moins chère des autres vendeurs.'],
		['HighestBuyOrder', 'Meilleur acheteur', 'Vendu tout de suite, au prix de l\'offre d\'achat la plus haute.'],
		['AverageSold', 'Moyenne des ventes', 'Moyenne des ventes réelles des derniers jours.'],
		['Fixed', 'Prix fixe', 'Le même prix pour tous les objets.'],
	];

	const OPERATION_TITLES = {
		SellPreview: 'Aperçu de la vente',
		Sell: 'Vente',
		RepricePreview: 'Vérification des annonces',
		Reprice: 'Réajustement des prix',
	};

	// ---------------------------------------------------------------- Helpers

	function h(tag, props, ...children) {
		const element = document.createElement(tag);

		for (const [key, value] of Object.entries(props || {})) {
			if ((value === null) || (value === undefined) || (value === false)) {
				continue;
			}

			if (key === 'class') {
				element.className = Array.isArray(value) ? value.filter(Boolean).join(' ') : value;
			} else if (key === 'style') {
				Object.assign(element.style, value);
			} else if (key.startsWith('on') && (typeof value === 'function')) {
				element.addEventListener(key.slice(2).toLowerCase(), value);
			} else if (key === 'value') {
				element.value = value;
			} else if (key === 'checked') {
				element.checked = Boolean(value);
			} else {
				element.setAttribute(key, value === true ? '' : value);
			}
		}

		for (const child of children.flat(Infinity)) {
			if ((child === null) || (child === undefined) || (child === false)) {
				continue;
			}

			element.append(child instanceof Node ? child : document.createTextNode(String(child)));
		}

		return element;
	}

	function svg(viewBox, path, className) {
		const ns = 'http://www.w3.org/2000/svg';
		const element = document.createElementNS(ns, 'svg');

		element.setAttribute('viewBox', viewBox);
		element.setAttribute('aria-hidden', 'true');
		element.setAttribute('class', className);

		const shape = document.createElementNS(ns, 'path');

		shape.setAttribute('d', path);
		shape.setAttribute('fill', 'currentColor');
		shape.setAttribute('fill-rule', 'evenodd');
		element.append(shape);

		return element;
	}

	function money(cents, currency) {
		if ((cents === null) || (cents === undefined)) {
			return '–';
		}

		try {
			return new Intl.NumberFormat('fr-FR', { style: 'currency', currency: currency || 'EUR', minimumFractionDigits: 2 }).format(cents / 100);
		} catch {
			return `${(cents / 100).toFixed(2)} ${currency || ''}`.trim();
		}
	}

	function relativeTime(isoDate) {
		const seconds = Math.round((new Date(isoDate).getTime() - Date.now()) / 1000);
		const format = new Intl.RelativeTimeFormat('fr', { numeric: 'auto' });

		if (Math.abs(seconds) < 60) {
			return format.format(seconds, 'second');
		}

		if (Math.abs(seconds) < 3600) {
			return format.format(Math.round(seconds / 60), 'minute');
		}

		if (Math.abs(seconds) < 86400) {
			return format.format(Math.round(seconds / 3600), 'hour');
		}

		return format.format(Math.round(seconds / 86400), 'day');
	}

	function clockTime(isoDate) {
		return new Date(isoDate).toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' });
	}

	function sentence(text) {
		return text ? text.charAt(0).toUpperCase() + text.slice(1) : text;
	}

	function plural(count, singular, pluralForm) {
		return `${count} ${count > 1 ? pluralForm : singular}`;
	}

	// Mirrors the plugin's fee math (Steam's economy_common.js), only used for the live example in the settings
	function buyerPriceFor(sellerPrice) {
		return sellerPrice + Math.max(Math.floor(sellerPrice * 0.05), 1) + Math.max(Math.floor(sellerPrice * 0.10), 1);
	}

	function sellerPriceFor(buyerPrice) {
		if (buyerPrice < 3) {
			return 0;
		}

		let sellerPrice = Math.max(1, Math.floor(buyerPrice / 1.15));

		while (buyerPriceFor(sellerPrice + 1) <= buyerPrice) {
			sellerPrice++;
		}

		while ((sellerPrice > 1) && (buyerPriceFor(sellerPrice) > buyerPrice)) {
			sellerPrice--;
		}

		return sellerPrice;
	}

	// ---------------------------------------------------------------- API

	function readPassword() {
		let raw = null;

		try {
			raw = localStorage.getItem('asf-ui:ipc-password');

			return raw ? JSON.parse(raw) : null;
		} catch {
			return raw;
		}
	}

	function api(method, path, body) {
		return request(method, `Api/MarketSeller${path}`, body);
	}

	async function request(method, path, body) {
		const headers = { Accept: 'application/json' };
		const password = readPassword();

		if (password) {
			headers.Authentication = password;
		}

		if (body !== undefined) {
			headers['Content-Type'] = 'application/json';
		}

		let response;

		try {
			response = await fetch(`${BASE_PATH}${path}`, { body: body === undefined ? undefined : JSON.stringify(body), headers, method });
		} catch {
			throw new Error('ASF ne répond pas. Vérifie qu\'il tourne toujours.');
		}

		let payload = null;

		try {
			payload = await response.json();
		} catch {
			// Empty or non-JSON body, handled below
		}

		if ((response.status === 401) || (response.status === 403)) {
			throw new Error('Accès refusé. Connecte-toi à l\'interface d\'ASF avec ton mot de passe IPC, puis recharge la page.');
		}

		if (response.status === 404) {
			throw new Error('Le plugin MarketSeller ne répond pas. Vérifie qu\'il est bien dans le dossier plugins d\'ASF et qu\'ASF a redémarré.');
		}

		if (!response.ok || !payload || (payload.Success === false)) {
			throw new Error((payload && payload.Message) || `Erreur HTTP ${response.status}`);
		}

		return payload.Result !== undefined && payload.Result !== null ? payload.Result : payload.Message;
	}

	// ---------------------------------------------------------------- Page

	const App = {
		alive: false,
		asfUi: null,
		root: null,
		timer: null,
		settingsPanel: null,
		settingsKey: null,

		state: {
			confirm: null,
			details: null,
			error: null,
			filter: 'sell',
			formDirty: false,
			formMessage: null,
			overview: null,
			saving: false,
			selected: safeStorageGet(STORAGE_BOT),
			tab: safeStorageGet(STORAGE_TAB) || 'inventory',
			update: null,
			updating: null,
		},

		mount(root) {
			this.root = root;
			this.alive = true;
			root.classList.add('ms');
			root.replaceChildren(h('section', { class: 'ms-card' }, h('p', null, 'Chargement…')));
			this.poll();
			this.checkUpdate(false);
		},

		async checkUpdate(refresh) {
			try {
				this.state.update = await api('GET', `/Update${refresh ? '?refresh=true' : ''}`);
			} catch (error) {
				this.state.update = { Error: error.message };
			}

			this.render();
		},

		// ASF downloads the release zip from GitHub, replaces the DLL and restarts; we wait for it to come back with the new version
		async installUpdate() {
			const update = this.state.update;

			if (!update || !update.UpdateAvailable) {
				return;
			}

			if (this.state.confirm !== 'update') {
				this.state.confirm = 'update';
				this.render();

				setTimeout(() => {
					if (this.state.confirm === 'update') {
						this.state.confirm = null;
						this.render();
					}
				}, 5000);

				return;
			}

			this.state.confirm = null;
			this.state.updating = { text: `Téléchargement et installation de la version ${update.LatestVersion}…` };
			this.render();

			try {
				await request('POST', 'Api/Plugins/Update', { Channel: 1, Plugins: [update.AssemblyName] });
			} catch (error) {
				this.state.updating = { error: `ASF n'a pas pu lancer la mise à jour : ${error.message}` };
				this.render();

				return;
			}

			this.state.updating = { text: 'Mise à jour installée, ASF redémarre. La page se rechargera toute seule…' };
			this.render();

			const startedAt = Date.now();
			let restarted = false;

			const waitForRestart = async () => {
				try {
					const info = await api('GET', '/Update');

					if (info.CurrentVersion !== update.CurrentVersion) {
						window.location.reload();

						return;
					}

					// Still the old version and ASF never went down: the update didn't happen
					if (!restarted && (Date.now() - startedAt > 30000)) {
						this.state.updating = { error: 'ASF n\'a pas installé la mise à jour. Regarde son log : le dossier du plugin doit être modifiable par ASF.' };
						this.render();

						return;
					}
				} catch {
					restarted = true;
				}

				if (Date.now() - startedAt > 300000) {
					this.state.updating = { error: 'ASF ne répond toujours pas après 5 minutes. Vérifie qu\'il a bien redémarré.' };
					this.render();

					return;
				}

				setTimeout(waitForRestart, 2000);
			};

			setTimeout(waitForRestart, 3000);
		},

		unmount() {
			this.alive = false;
			clearTimeout(this.timer);
			this.root = null;
			this.settingsPanel = null;
			this.settingsKey = null;
		},

		currentBot() {
			const overview = this.state.overview;

			return overview ? overview.Bots.find(bot => bot.BotName === this.state.selected) || null : null;
		},

		notify(kind, text) {
			const snotify = this.asfUi && this.asfUi.$snotify;

			if (snotify && (typeof snotify[kind] === 'function')) {
				snotify[kind](text, 'MarketSeller');
			}
		},

		async poll() {
			if (!this.alive || (this.state.updating && !this.state.updating.error)) {
				return;
			}

			clearTimeout(this.timer);

			const before = this.currentBot();

			try {
				const overview = await api('GET', '');

				this.state.overview = overview;
				this.state.error = null;

				if (!overview.Bots.some(bot => bot.BotName === this.state.selected)) {
					const fallback = overview.Bots.find(bot => bot.Enabled) || overview.Bots[0];

					this.state.selected = fallback ? fallback.BotName : null;
				}

				const current = this.currentBot();

				if (current && this.needsDetails(current)) {
					await this.loadDetails();
				}

				if (before && current && (before.BotName === current.BotName) && before.Progress && !current.Progress && this.state.details) {
					const reprice = before.Progress.Operation.startsWith('Reprice');
					const result = reprice ? this.state.details.LastReprice : this.state.details.LastSell;

					if (result) {
						const text = result.Error || (reprice ? this.summarizeReprice(result) : this.summarizeSell(result));

						this.notify(result.Error ? 'error' : 'success', `${OPERATION_TITLES[result.Operation]} : ${text}`);
					}
				}
			} catch (error) {
				this.state.error = error.message;
			}

			if (!this.alive) {
				return;
			}

			this.render();

			const running = this.state.overview && this.state.overview.Bots.some(bot => bot.Progress);

			this.timer = setTimeout(() => this.poll(), running ? 1500 : 10000);
		},

		needsDetails(bot) {
			const details = this.state.details;

			if (!details || (details.Overview.BotName !== bot.BotName)) {
				return true;
			}

			const previous = details.Overview;
			const finishedAt = summary => (summary ? summary.FinishedAt : null);

			return (finishedAt(previous.LastSell) !== finishedAt(bot.LastSell)) ||
				(finishedAt(previous.LastReprice) !== finishedAt(bot.LastReprice)) ||
				(previous.Enabled !== bot.Enabled) ||
				(previous.ConfigPresent !== bot.ConfigPresent) ||
				(previous.ConfigError !== bot.ConfigError) ||
				(previous.DryRun !== bot.DryRun);
		},

		async loadDetails() {
			if (!this.state.selected) {
				this.state.details = null;

				return;
			}

			this.state.details = await api('GET', `/${encodeURIComponent(this.state.selected)}`);
		},

		select(botName) {
			if (botName === this.state.selected) {
				return;
			}

			if (this.state.formDirty && !window.confirm('Tes réglages modifiés ne sont pas enregistrés. Changer de bot quand même ?')) {
				return;
			}

			this.state.selected = botName;
			this.state.details = null;
			this.state.formDirty = false;
			this.state.formMessage = null;
			this.state.confirm = null;
			safeStorageSet(STORAGE_BOT, botName);
			this.poll();
		},

		setTab(tab) {
			this.state.tab = tab;
			safeStorageSet(STORAGE_TAB, tab);
			this.render();
		},

		async start(operation) {
			const bot = this.currentBot();

			if (!bot) {
				return;
			}

			// Selling and repricing touch real listings, they need a second click
			if (((operation === 'Sell') || (operation === 'Reprice')) && !bot.DryRun && (this.state.confirm !== operation)) {
				this.state.confirm = operation;
				this.render();

				setTimeout(() => {
					if (this.state.confirm === operation) {
						this.state.confirm = null;
						this.render();
					}
				}, 5000);

				return;
			}

			this.state.confirm = null;

			try {
				await api('POST', `/${encodeURIComponent(bot.BotName)}/Start/${operation}`);
			} catch (error) {
				this.notify('error', error.message);
				this.state.error = error.message;
			}

			if ((operation === 'RepricePreview') || (operation === 'Reprice')) {
				this.setTab('listings');
			} else {
				this.setTab('inventory');
			}

			this.poll();
		},

		render() {
			if (!this.root) {
				return;
			}

			const overview = this.state.overview;

			if (!overview) {
				this.root.replaceChildren(h('section', { class: 'ms-card' }, this.state.error ? h('div', { class: 'ms-notice ms-notice--error', role: 'alert' }, this.state.error) : h('p', null, 'Chargement…')));

				return;
			}

			if (overview.Bots.length === 0) {
				this.root.replaceChildren(h('section', { class: 'ms-card' }, h('div', { class: 'ms-empty' }, h('p', null, 'ASF n\'a aucun bot pour l\'instant. Ajoute un bot dans ASF, puis reviens ici pour configurer la vente de ses objets.'))));

				return;
			}

			const bot = this.currentBot();

			// The settings panel is kept between renders so typing isn't interrupted by polling
			const panel = this.renderPanel(bot);

			this.root.replaceChildren(this.renderHeader(overview, bot), h('section', { class: 'ms-card' }, this.renderTabs(), panel));
		},

		// ------------------------------------------------------------ Header

		renderHeader(overview, bot) {
			const running = Boolean(bot && bot.Progress);
			const ready = Boolean(bot && bot.Enabled && bot.Connected && !running);
			const blockedReason = !bot ? null : !bot.Enabled ? 'MarketSeller n\'est pas activé pour ce bot.' : !bot.Connected ? 'Le bot n\'est pas connecté à Steam.' : running ? 'Une opération est déjà en cours.' : null;

			const actionButton = (operation, label, primary) => {
				const armed = this.state.confirm === operation;

				return h('button', {
					class: ['ms-button', primary && 'ms-button--primary', armed && 'ms-button--armed'],
					disabled: !ready,
					onClick: () => this.start(operation),
					title: blockedReason,
					type: 'button',
				}, armed ? `Confirmer : ${label.toLowerCase()}` : label);
			};

			const dryRun = Boolean(bot && bot.DryRun);

			return h('section', { class: 'ms-card' },
				h('div', { class: 'ms-bots', role: 'tablist', 'aria-label': 'Bots' },
					overview.Bots.map(item => h('button', {
						'aria-selected': String(item.BotName === this.state.selected),
						class: 'ms-bot',
						onClick: () => this.select(item.BotName),
						role: 'tab',
						type: 'button',
					},
					h('span', { class: ['ms-dot', item.Progress ? 'ms-dot--busy' : item.Connected ? 'ms-dot--online' : 'ms-dot--off'] }),
					item.BotName,
					!item.Enabled && h('span', { class: 'ms-bot__hint' }, 'désactivé'))),
				),
				bot && h('div', { class: 'ms-head' },
					h('div', { class: 'ms-head__info' },
						h('h2', { class: 'ms-head__title' }, bot.BotName),
						h('p', { class: 'ms-head__status' }, this.describeBot(bot)),
						this.renderLastRun(bot),
					),
					h('div', { class: 'ms-actions' },
						h('div', { class: 'ms-actions__group' },
							h('span', { class: 'ms-actions__label' }, 'Vente des objets'),
							h('div', { class: 'ms-actions__buttons' },
								actionButton('SellPreview', 'Aperçu', false),
								actionButton('Sell', dryRun ? 'Vendre (simulation)' : 'Vendre maintenant', true),
							),
						),
						h('div', { class: 'ms-actions__group' },
							h('span', { class: 'ms-actions__label' }, 'Annonces en cours'),
							h('div', { class: 'ms-actions__buttons' },
								actionButton('RepricePreview', 'Vérifier les prix', false),
								actionButton('Reprice', dryRun ? 'Réajuster (simulation)' : 'Réajuster', false),
							),
						),
					),
				),
				bot && running && this.renderProgress(bot.Progress),
				this.renderNotices(overview, bot),
				this.renderUpdate(),
			);
		},

		renderUpdate() {
			const update = this.state.update;
			const updating = this.state.updating;

			if (updating) {
				return updating.error ?
					h('div', { class: 'ms-notice ms-notice--error', role: 'alert' }, updating.error) :
					h('div', { class: 'ms-progress', role: 'status', 'aria-live': 'polite' },
						h('div', { class: 'ms-progress__text' }, updating.text),
						h('div', { class: 'ms-progress__bar' }, h('span', { class: 'ms-progress__fill ms-progress__fill--indeterminate' })),
					);
			}

			if (!update) {
				return null;
			}

			if (update.UpdateAvailable) {
				const armed = this.state.confirm === 'update';

				return h('div', { class: 'ms-notice ms-update' },
					h('p', null,
						h('strong', null, `MarketSeller ${update.LatestVersion} est disponible`),
						` (tu as la ${update.CurrentVersion}). ASF redémarre pendant la mise à jour, ce qui déconnecte les bots quelques secondes.`,
					),
					h('div', { class: 'ms-update__actions' },
						h('a', { class: 'ms-button ms-button--quiet', href: update.ReleasePage, rel: 'noopener noreferrer', target: '_blank' }, 'Voir les nouveautés'),
						h('button', { class: ['ms-button', 'ms-button--primary', armed && 'ms-button--armed'], onClick: () => this.installUpdate(), type: 'button' }, armed ? 'Confirmer : mettre à jour' : 'Mettre à jour'),
					),
				);
			}

			const status = update.Error ?
				`impossible de vérifier les mises à jour (${update.Error.replace(/\.+$/, '')})` :
				'à jour';

			return h('p', { class: 'ms-version' },
				update.CurrentVersion ? `MarketSeller ${update.CurrentVersion}, ${status}. ` : `MarketSeller : ${status}. `,
				h('button', { class: 'ms-button ms-button--quiet', onClick: () => this.checkUpdate(true), type: 'button' }, 'Vérifier maintenant'),
			);
		},

		describeBot(bot) {
			const parts = [bot.Connected ? 'connecté' : 'hors ligne'];

			if (bot.Currency) {
				parts.push(`portefeuille en ${bot.Currency}`);
			}

			parts.push(bot.HasMobileAuthenticator ? 'annonces confirmées automatiquement' : 'annonces à confirmer dans l\'application Steam');

			if (bot.DryRun) {
				parts.push('mode simulation');
			}

			return `${sentence(parts.join(', '))}.`;
		},

		// Only when, the details of each run are in the tabs below
		renderLastRun(bot) {
			const runs = [bot.LastSell, bot.LastReprice].filter(Boolean).sort((a, b) => new Date(b.FinishedAt) - new Date(a.FinishedAt));

			if (runs.length === 0) {
				return h('p', { class: 'ms-head__last' }, bot.Enabled ? 'Aucune opération depuis le démarrage d\'ASF.' : null);
			}

			const text = runs.map(run => `${OPERATION_TITLES[run.Operation].toLowerCase()} ${relativeTime(run.FinishedAt)}`).join(', ');

			return h('p', { class: 'ms-head__last' }, `${sentence(text)}.`);
		},

		renderProgress(progress) {
			const hasTotal = progress.Total > 0;
			const percent = hasTotal ? Math.round((progress.Done / progress.Total) * 100) : 0;
			let text = `${OPERATION_TITLES[progress.Operation]} : ${progress.Stage.toLowerCase()}`;

			if (hasTotal) {
				text += `, ${progress.Done} sur ${progress.Total}`;
			}

			if (progress.CurrentItem) {
				text += ` (${progress.CurrentItem})`;
			}

			return h('div', { class: 'ms-progress', role: 'status', 'aria-live': 'polite' },
				h('div', { class: 'ms-progress__text' }, text),
				h('div', { class: 'ms-progress__bar' }, h('span', { class: ['ms-progress__fill', !hasTotal && 'ms-progress__fill--indeterminate'], style: hasTotal ? { width: `${percent}%` } : null })),
			);
		},

		renderNotices(overview, bot) {
			const notices = [];

			if (this.state.error) {
				notices.push(h('div', { class: 'ms-notice ms-notice--error', role: 'alert' }, this.state.error));
			}

			if (overview.RateLimitedUntil) {
				notices.push(h('div', { class: 'ms-notice ms-notice--warn' }, `Steam limite les requêtes au marché. MarketSeller reprendra après ${clockTime(overview.RateLimitedUntil)}.`));
			}

			if (bot && bot.ConfigError) {
				notices.push(h('div', { class: 'ms-notice ms-notice--error' }, `Les réglages de ce bot sont invalides : ${bot.ConfigError.replace(/\.+$/, '')}. Corrige-les dans l'onglet Réglages, ou directement dans le fichier de config du bot.`));
			} else if (bot && !bot.Enabled) {
				notices.push(h('div', { class: 'ms-notice' },
					'MarketSeller n\'est pas activé pour ce bot. ',
					h('button', { class: 'ms-button ms-button--quiet', onClick: () => this.setTab('settings'), type: 'button' }, 'Configurer la vente'),
				));
			} else if (bot && bot.DryRun) {
				notices.push(h('div', { class: 'ms-notice' }, 'Mode simulation : MarketSeller calcule les prix mais ne met rien en vente. Désactive-le dans les réglages pour vendre pour de vrai.'));
			}

			return notices;
		},

		// ------------------------------------------------------------ Tabs

		renderTabs() {
			const tabs = [['inventory', 'Inventaire'], ['listings', 'Annonces en cours'], ['settings', 'Réglages']];

			return h('div', { class: 'ms-tabs', role: 'tablist', 'aria-label': 'Sections' },
				tabs.map(([id, label]) => h('button', {
					'aria-selected': String(this.state.tab === id),
					class: 'ms-tab',
					onClick: () => this.setTab(id),
					role: 'tab',
					type: 'button',
				}, label)),
			);
		},

		renderPanel(bot) {
			const details = this.state.details && bot && (this.state.details.Overview.BotName === bot.BotName) ? this.state.details : null;

			if (!details) {
				return h('p', null, 'Chargement…');
			}

			switch (this.state.tab) {
				case 'listings':
					return this.renderListings(bot, details.LastReprice);
				case 'settings':
					return this.renderSettings(bot, details);
				default:
					return this.renderInventory(bot, details.LastSell);
			}
		},

		emptyState(bot, text, operation, label) {
			return h('div', { class: 'ms-empty' },
				h('p', null, text),
				bot.Enabled ?
					h('button', { class: 'ms-button ms-button--primary', disabled: !bot.Connected || Boolean(bot.Progress), onClick: () => this.start(operation), type: 'button' }, label) :
					h('p', null, h('button', { class: 'ms-button ms-button--quiet', onClick: () => this.setTab('settings'), type: 'button' }, 'Active MarketSeller dans les réglages pour commencer.')),
			);
		},

		resultHeader(result, summaryText) {
			return h('div', { class: 'ms-result' },
				h('p', { class: 'ms-result__text' },
					h('span', { class: 'ms-result__when' }, `${OPERATION_TITLES[result.Operation]}, ${relativeTime(result.FinishedAt)}`),
					sentence(summaryText),
				),
			);
		},

		// ------------------------------------------------------------ Inventory

		renderInventory(bot, result) {
			if (!result) {
				return this.emptyState(bot, 'Aucun aperçu pour l\'instant. Lance un aperçu pour voir ce que MarketSeller vendrait, à quel prix, et pourquoi certains objets restent dans ton inventaire.', 'SellPreview', 'Lancer un aperçu');
			}

			if (result.Error) {
				return h('div', null, this.resultHeader(result, result.Error));
			}

			const groups = {
				sell: line => line.Status === 'Listed',
				kept: line => (line.Status === 'Locked') || (line.Status === 'PriceLocked') || (line.Status === 'Kept'),
				skipped: line => (line.Status === 'Skipped') || (line.Status === 'Failed'),
				all: () => true,
			};

			const count = filter => result.Lines.filter(groups[filter]).reduce((total, line) => total + line.Amount, 0);
			const filters = [
				['sell', result.DryRun ? 'À vendre' : 'Mis en vente'],
				['kept', 'Gardés et verrouillés'],
				['skipped', 'Ignorés et échecs'],
				['all', 'Tout'],
			];

			const filter = groups[this.state.filter] ? this.state.filter : 'sell';
			const statusOrder = ['Listed', 'Failed', 'Skipped', 'PriceLocked', 'Kept', 'Locked'];
			const lines = result.Lines.filter(groups[filter]).sort((a, b) => (statusOrder.indexOf(a.Status) - statusOrder.indexOf(b.Status)) || ((b.BuyerPrice || 0) - (a.BuyerPrice || 0)) || a.Name.localeCompare(b.Name));
			const hasLadder = lines.some(line => line.BuyerPrice && (line.HighestBuyOrder || line.LowestSellOrder));

			return h('div', null,
				this.resultHeader(result, this.summarizeSell(result)),
				h('div', { class: 'ms-filters', role: 'group', 'aria-label': 'Filtrer les objets' },
					filters.map(([id, label]) => h('button', {
						'aria-pressed': String(filter === id),
						class: 'ms-filter',
						onClick: () => {
							this.state.filter = id;
							this.render();
						},
						type: 'button',
					}, `${label} (${count(id)})`)),
				),
				hasLadder && h('div', { class: 'ms-legend' },
					h('span', { class: 'ms-legend__item' }, h('span', { class: 'ms-swatch ms-swatch--buy' }), 'meilleur acheteur'),
					h('span', { class: 'ms-legend__item' }, h('span', { class: 'ms-swatch ms-swatch--sell' }), 'annonce la moins chère'),
					h('span', { class: 'ms-legend__item' }, h('span', { class: 'ms-swatch ms-swatch--pin' }), 'ton prix'),
				),
				lines.length === 0 ?
					h('div', { class: 'ms-empty' }, h('p', null, 'Aucun objet dans cette catégorie.')) :
					h('div', { class: 'ms-table-wrap' },
						h('table', { class: 'ms-table' },
							h('thead', null, h('tr', null,
								h('th', { scope: 'col' }, 'Objet'),
								h('th', { class: 'ms-num', scope: 'col' }, 'Quantité'),
								h('th', { scope: 'col' }, 'Prix'),
								h('th', { scope: 'col' }, 'Statut'),
							)),
							h('tbody', null, lines.map(line => h('tr', null,
								h('td', null, this.renderItem(line.Name, line.IconHash, [TYPE_LABELS[line.Type] || line.Type, line.RealAppID ? `jeu ${line.RealAppID}` : null])),
								h('td', { class: 'ms-num' }, `×${line.Amount}`),
								h('td', null, this.renderPrice(line, result.Currency)),
								h('td', null, this.renderSellStatus(line, result.DryRun, result.Currency)),
							))),
						),
					),
			);
		},

		summarizeSell(result) {
			const listed = result.Lines.filter(line => line.Status === 'Listed');
			const units = listed.reduce((total, line) => total + line.Amount, 0);
			const buyerTotal = listed.reduce((total, line) => total + ((line.BuyerPrice || 0) * line.Amount), 0);
			const sellerTotal = listed.reduce((total, line) => total + ((line.SellerPrice || 0) * line.Amount), 0);
			let text;

			if (units === 0) {
				text = result.DryRun ? 'rien à vendre pour l\'instant.' : 'rien n\'a été mis en vente.';
			} else if (result.DryRun) {
				text = `${plural(units, 'objet serait mis', 'objets seraient mis')} en vente pour ${money(buyerTotal, result.Currency)}, tu recevrais ${money(sellerTotal, result.Currency)} après les frais Steam.`;
			} else {
				text = `${plural(units, 'objet mis', 'objets mis')} en vente pour ${money(buyerTotal, result.Currency)}, tu recevras ${money(sellerTotal, result.Currency)} une fois vendus.`;
			}

			if (!result.DryRun && (result.Confirmed > 0)) {
				text += ` ${plural(result.Confirmed, 'annonce confirmée', 'annonces confirmées')}.`;
			}

			if (result.NeedsManualConfirmation) {
				text += ' Confirme les annonces dans l\'application Steam Mobile.';
			}

			if (result.RateLimitedUntil) {
				text += ` Interrompu : Steam limite les requêtes jusqu'à ${clockTime(result.RateLimitedUntil)}.`;
			}

			return text;
		},

		renderItem(name, iconHash, meta) {
			return h('div', { class: 'ms-item' },
				iconHash ? h('img', { alt: '', class: 'ms-item__icon', loading: 'lazy', src: `${ICON_BASE}${iconHash}/96fx96f` }) : h('span', { class: 'ms-item__icon' }),
				h('div', null,
					h('div', { class: 'ms-item__name' }, name),
					h('div', { class: 'ms-item__meta' }, meta.filter(Boolean).join(', ')),
				),
			);
		},

		renderPrice(line, currency) {
			if (!line.BuyerPrice && !line.AdjustedPrice) {
				return h('span', { class: 'ms-item__meta' }, '–');
			}

			const price = line.BuyerPrice || line.AdjustedPrice;

			return h('div', { class: 'ms-price' },
				this.renderLadder(line, currency),
				h('div', { class: 'ms-price__amount' },
					h('span', { class: 'ms-price__buyer' }, money(price, currency)),
					line.SellerPrice ? h('span', { class: 'ms-price__seller' }, `tu reçois ${money(line.SellerPrice, currency)}`) : null,
				),
			);
		},

		// Where our price sits between the best buy order and the cheapest other listing
		renderLadder(line, currency) {
			const price = line.BuyerPrice;
			const buy = line.HighestBuyOrder;
			const sell = line.LowestSellOrder;

			if (!price || (!buy && !sell)) {
				return null;
			}

			const values = [price, buy, sell].filter(Boolean);
			const low = Math.min(...values);
			const high = Math.max(...values);
			const padding = Math.max((high - low) * 0.15, high * 0.05, 1);
			const from = low - padding;
			const span = (high + padding) - from;
			const position = value => `${((value - from) / span) * 100}%`;

			const label = [
				buy ? `meilleur acheteur ${money(buy, currency)}` : null,
				sell ? `annonce la moins chère ${money(sell, currency)}` : null,
				`ton prix ${money(price, currency)}`,
			].filter(Boolean).join(', ');

			return h('div', { 'aria-label': sentence(label), class: 'ms-ladder', role: 'img', title: sentence(label) },
				h('span', { class: 'ms-ladder__track' }),
				buy && sell && (sell > buy) && h('span', { class: 'ms-ladder__spread', style: { left: position(buy), width: `${((sell - buy) / span) * 100}%` } }),
				buy && h('span', { class: 'ms-ladder__tick ms-ladder__tick--buy', style: { left: position(buy) } }),
				sell && h('span', { class: 'ms-ladder__tick ms-ladder__tick--sell', style: { left: position(sell) } }),
				h('span', { class: 'ms-ladder__pin', style: { left: position(price) } }),
			);
		},

		renderSellStatus(line, dryRun, currency) {
			const statuses = {
				Listed: [dryRun ? 'À vendre' : 'Mis en vente', 'listed'],
				Failed: ['Échec', 'failed'],
				Skipped: ['Ignoré', 'muted'],
				PriceLocked: ['Verrouillé par prix', 'locked'],
				Locked: ['Verrouillé', 'locked'],
				Kept: ['Gardé', 'ok'],
			};

			const [label, tone] = statuses[line.Status] || [line.Status, 'muted'];
			let reason = line.Reason;

			if (line.Status === 'PriceLocked') {
				reason = this.describePriceLock(line.AdjustedPrice, currency);
			} else if (line.Status === 'Kept') {
				const kept = (this.state.details && this.state.details.Config.KeepPerItem) || line.Amount;

				reason = `${plural(kept, 'exemplaire gardé', 'exemplaires gardés')} par objet`;
			}

			return h('div', null,
				h('span', { class: `ms-status ms-status--${tone}` }, h('span', { class: 'ms-dot' }), label),
				reason ? h('span', { class: 'ms-reason' }, sentence(reason)) : null,
			);
		},

		describePriceLock(adjustedPrice, currency) {
			const locks = (this.state.details && this.state.details.Config.Lock) || {};

			if (adjustedPrice && locks.PriceAboveCents && (adjustedPrice >= locks.PriceAboveCents)) {
				return `vaut ${money(adjustedPrice, currency)}, au-dessus de ta limite de ${money(locks.PriceAboveCents, currency)}`;
			}

			if (adjustedPrice && locks.PriceBelowCents && (adjustedPrice < locks.PriceBelowCents)) {
				return `vaut ${money(adjustedPrice, currency)}, en dessous de ta limite de ${money(locks.PriceBelowCents, currency)}`;
			}

			return 'verrouillé par tes limites de prix';
		},

		// ------------------------------------------------------------ Listings

		renderListings(bot, result) {
			if (!result) {
				return this.emptyState(bot, 'Lance une vérification pour comparer tes annonces en cours au prix visé. Rien n\'est modifié tant que tu ne cliques pas sur Réajuster.', 'RepricePreview', 'Vérifier les prix');
			}

			if (result.Error) {
				return h('div', null, this.resultHeader(result, result.Error));
			}

			const statusOrder = ['Repriced', 'Withdrawn', 'Failed', 'Skipped', 'Unchanged'];
			const lines = [...result.Lines].sort((a, b) => (statusOrder.indexOf(a.Status) - statusOrder.indexOf(b.Status)) || a.Name.localeCompare(b.Name));

			return h('div', null,
				this.resultHeader(result, this.summarizeReprice(result)),
				lines.length === 0 ?
					h('div', { class: 'ms-empty' }, h('p', null, 'Aucune annonce gérée par MarketSeller pour l\'instant. Seules les annonces d\'objets déjà vus dans l\'inventaire, et qui passent tes filtres, sont réajustées.')) :
					h('div', { class: 'ms-table-wrap' },
						h('table', { class: 'ms-table' },
							h('thead', null, h('tr', null,
								h('th', { scope: 'col' }, 'Objet'),
								h('th', { class: 'ms-num', scope: 'col' }, 'Prix'),
								h('th', { scope: 'col' }, 'Statut'),
							)),
							h('tbody', null, lines.map(line => h('tr', null,
								h('td', null, this.renderItem(line.Name, line.IconHash, [])),
								h('td', { class: 'ms-num' }, this.renderChange(line, result.Currency)),
								h('td', null, this.renderRepriceStatus(line, result.DryRun, result.Currency)),
							))),
						),
					),
			);
		},

		summarizeReprice(result) {
			const count = status => result.Lines.filter(line => line.Status === status).length;
			const total = result.Lines.length;

			if (total === 0) {
				return 'aucune annonce à vérifier.';
			}

			const changed = count('Repriced');
			const withdrawn = count('Withdrawn');
			let text = `${plural(total, 'annonce vérifiée', 'annonces vérifiées')} : `;

			text += result.DryRun ?
				`${plural(changed, 'serait réajustée', 'seraient réajustées')}, ${plural(withdrawn, 'serait retirée', 'seraient retirées')}, ${count('Unchanged')} au bon prix.` :
				`${plural(changed, 'réajustée', 'réajustées')}, ${plural(withdrawn, 'retirée', 'retirées')}, ${count('Unchanged')} au bon prix.`;

			if (result.Relist) {
				text += ` Remise en vente : ${this.summarizeSell(result.Relist)}`;
			}

			if (result.RateLimitedUntil) {
				text += ` Interrompu : Steam limite les requêtes jusqu'à ${clockTime(result.RateLimitedUntil)}.`;
			}

			return text;
		},

		renderChange(line, currency) {
			if (!line.TargetBuyerPrice || (line.TargetBuyerPrice === line.CurrentBuyerPrice) || (line.Status === 'Unchanged')) {
				return money(line.CurrentBuyerPrice, currency);
			}

			return h('span', { class: 'ms-change' },
				money(line.CurrentBuyerPrice, currency),
				h('span', { class: 'ms-change__arrow', 'aria-label': 'devient' }, '→'),
				h('strong', null, money(line.TargetBuyerPrice, currency)),
			);
		},

		renderRepriceStatus(line, dryRun, currency) {
			const statuses = {
				Unchanged: ['Au bon prix', 'ok'],
				Repriced: [dryRun ? 'À réajuster' : 'Réajusté', 'listed'],
				Withdrawn: [dryRun ? 'À retirer' : 'Retiré', 'locked'],
				Skipped: ['Sans prix de référence', 'muted'],
				Failed: ['Échec', 'failed'],
			};

			const [label, tone] = statuses[line.Status] || [line.Status, 'muted'];
			const reason = line.Status === 'Withdrawn' ? this.describePriceLock(line.AdjustedPrice, currency) : line.Reason;

			return h('div', null,
				h('span', { class: `ms-status ms-status--${tone}` }, h('span', { class: 'ms-dot' }), label),
				reason ? h('span', { class: 'ms-reason' }, sentence(reason)) : null,
			);
		},

		// ------------------------------------------------------------ Settings

		renderSettings(bot, details) {
			const key = `${bot.BotName}:${JSON.stringify(details.Config)}`;

			if (this.settingsPanel && (this.settingsKey === key || this.state.formDirty || this.state.saving)) {
				this.updateSettingsMessage();

				return this.settingsPanel;
			}

			this.settingsKey = key;
			this.settingsPanel = this.buildSettings(bot, details.Config);

			return this.settingsPanel;
		},

		buildSettings(bot, config) {
			const pricing = config.Pricing || {};
			const locks = config.Lock || {};
			const currency = bot.Currency || 'EUR';
			const unit = (() => {
				try {
					return new Intl.NumberFormat('fr-FR', { currency, style: 'currency' }).formatToParts(0).find(part => part.type === 'currency').value;
				} catch {
					return currency;
				}
			})();

			const toUnits = cents => (cents ? (cents / 100).toFixed(2) : '');
			const id = name => `ms-${name}`;

			const field = (name, label, input, help) => h('label', { class: 'ms-field', for: id(name) },
				h('span', { class: 'ms-field__label' }, label),
				input,
				help ? h('span', { class: 'ms-field__help' }, help) : null,
			);

			const numberInput = (name, value, options = {}) => h('input', {
				class: 'ms-input',
				id: id(name),
				inputmode: options.step && (options.step < 1) ? 'decimal' : 'numeric',
				max: options.max,
				min: options.min,
				name,
				placeholder: options.placeholder,
				step: options.step || 1,
				type: 'number',
				value: value === null || value === undefined ? '' : value,
			});

			const moneyInput = (name, cents, options = {}) => h('div', { class: 'ms-affix' },
				numberInput(name, options.keepZero && !cents ? '0.00' : toUnits(cents), { min: options.allowNegative ? null : 0, placeholder: options.placeholder, step: 0.01 }),
				h('span', { class: 'ms-affix__unit' }, unit),
			);

			const check = (name, label, checked, help) => h('label', { class: 'ms-check' },
				h('input', { checked, name, type: 'checkbox' }),
				h('span', null, label, help ? h('span', { class: 'ms-check__help' }, help) : null),
			);

			const message = h('div', { class: 'ms-form__message' });
			const example = h('p', { class: 'ms-example', 'aria-live': 'polite' });

			const form = h('form', { class: 'ms-form', novalidate: true },
				message,

				h('fieldset', { class: 'ms-fieldset' },
					h('legend', null, 'Activation'),
					h('div', { class: 'ms-checks' },
						check('Enabled', 'Activer MarketSeller pour ce bot', config.Enabled),
						check('DryRun', 'Mode simulation', config.DryRun, 'Calcule et affiche les prix sans rien mettre en vente.'),
					),
				),

				h('fieldset', { class: 'ms-fieldset' },
					h('legend', null, 'Prix de vente'),
					h('p', { class: 'ms-fieldset__hint' }, 'Tous les prix sont ceux payés par l\'acheteur. MarketSeller calcule ce que tu reçois après les frais Steam (environ 15 %).'),
					h('div', { class: 'ms-sources', role: 'radiogroup', 'aria-label': 'Source du prix' },
						SOURCES.map(([value, name, help]) => h('label', { class: 'ms-source' },
							h('input', { checked: (pricing.Source || 'LowestSellOrder') === value, name: 'Source', type: 'radio', value }),
							h('span', null, h('span', { class: 'ms-source__name' }, name), h('span', { class: 'ms-source__help' }, help)),
						)),
					),
					h('div', { class: 'ms-grid' },
						h('div', { 'data-source': 'AverageSold' }, field('AverageDays', 'Nombre de jours pris en compte', numberInput('AverageDays', pricing.AverageDays, { max: 90, min: 1 }))),
						h('div', { 'data-source': 'Fixed' }, field('FixedCents', 'Prix fixe', moneyInput('FixedCents', pricing.FixedCents))),
						field('Multiplier', 'Ajustement en pourcentage', h('div', { class: 'ms-affix' }, numberInput('Multiplier', Math.round((pricing.Multiplier || 1) * 10000) / 100, { max: 10000, min: 1, step: 0.5 }), h('span', { class: 'ms-affix__unit' }, '%')), '100 % garde le prix tel quel, 95 % le baisse de 5 %.'),
						field('OffsetCents', 'Décalage', moneyInput('OffsetCents', pricing.OffsetCents, { allowNegative: true, keepZero: true }), 'Ajouté après le pourcentage. -0,01 pour passer juste sous la concurrence.'),
						field('MinCents', 'Prix minimum', moneyInput('MinCents', pricing.MinCents, { placeholder: '0.03' }), 'Jamais en dessous, même si le marché est plus bas.'),
						field('MaxCents', 'Prix maximum', moneyInput('MaxCents', pricing.MaxCents, { placeholder: 'aucun' }), 'Vide pour ne pas plafonner.'),
					),
					example,
				),

				h('fieldset', { class: 'ms-fieldset' },
					h('legend', null, 'Objets à vendre'),
					h('p', { class: 'ms-fieldset__hint' }, 'Seules les catégories cochées sont vendues, tout le reste reste dans l\'inventaire.'),
					h('div', { class: 'ms-checks' }, TYPES.map(([value, label]) => check(`Type:${value}`, label, (config.Types || []).includes(value)))),
					h('div', { class: 'ms-grid', style: { marginTop: '1em' } },
						field('KeepPerItem', 'Exemplaires à garder par objet', numberInput('KeepPerItem', config.KeepPerItem || 0, { min: 0 }), 'Par exemple 1 pour garder une carte de chaque, utile pour fabriquer les badges.'),
					),
				),

				h('fieldset', { class: 'ms-fieldset' },
					h('legend', null, 'Verrous'),
					h('p', { class: 'ms-fieldset__hint' }, 'Un objet verrouillé n\'est jamais mis en vente. Si son prix passe au-dessus ou en dessous des limites, son annonce est retirée au prochain réajustement.'),
					h('div', { class: 'ms-checks' }, RARITIES.map(([value, label]) => check(`Rarity:${value}`, `Rareté ${label.toLowerCase()}`, (locks.Rarities || []).includes(value)))),
					h('div', { class: 'ms-grid', style: { marginTop: '1em' } },
						field('PriceAboveCents', 'Garder ce qui vaut au moins', moneyInput('PriceAboveCents', locks.PriceAboveCents, { placeholder: 'aucune limite' })),
						field('PriceBelowCents', 'Ne pas vendre en dessous de', moneyInput('PriceBelowCents', locks.PriceBelowCents, { placeholder: 'aucune limite' })),
						field('AppIDs', 'Jeux à ne pas vendre', h('input', { class: 'ms-input', id: id('AppIDs'), name: 'AppIDs', placeholder: 'ex : 440, 570', type: 'text', value: (locks.AppIDs || []).join(', ') }), 'AppID Steam des jeux, séparés par des virgules.'),
						field('Names', 'Noms à ne pas vendre', h('textarea', { class: 'ms-input', id: id('Names'), name: 'Names', placeholder: 'un nom par ligne', rows: 3 }, (locks.Names || []).join('\n')), 'Un objet est verrouillé si son nom contient l\'un de ces textes.'),
					),
				),

				h('fieldset', { class: 'ms-fieldset' },
					h('legend', null, 'Automatisation'),
					h('div', { class: 'ms-checks' },
						check('SellOnFarmingFinished', 'Vendre quand ASF a fini de farmer', config.SellOnFarmingFinished),
						check('AutoConfirm', 'Confirmer les annonces automatiquement', config.AutoConfirm, bot.HasMobileAuthenticator ? 'Avec l\'authentificateur importé dans ASF.' : 'Nécessite l\'authentificateur mobile importé dans ASF, absent pour ce bot.'),
					),
					h('div', { class: 'ms-grid', style: { marginTop: '1em' } },
						field('SellIntervalMinutes', 'Vente automatique toutes les', h('div', { class: 'ms-affix' }, numberInput('SellIntervalMinutes', config.SellIntervalMinutes, { max: 65535, min: 0 }), h('span', { class: 'ms-affix__unit' }, 'min')), '0 pour désactiver.'),
						field('RepriceIntervalMinutes', 'Réajustement toutes les', h('div', { class: 'ms-affix' }, numberInput('RepriceIntervalMinutes', config.RepriceIntervalMinutes, { max: 65535, min: 0 }), h('span', { class: 'ms-affix__unit' }, 'min')), '0 pour désactiver.'),
						field('RepriceThresholdCents', 'Écart minimum pour réajuster', moneyInput('RepriceThresholdCents', config.RepriceThresholdCents), 'Une annonce n\'est remise en vente que si son prix doit changer d\'au moins ce montant.'),
					),
				),

				h('details', { class: 'ms-details' },
					h('summary', null, 'Réglages avancés'),
					h('div', { class: 'ms-grid' },
						field('Country', 'Pays', h('input', { class: 'ms-input', id: id('Country'), maxlength: 2, name: 'Country', type: 'text', value: config.Country || 'FR' }), 'Code à 2 lettres envoyé à Steam pour lire le carnet d\'ordres.'),
						field('RequestDelay', 'Pause entre deux requêtes au marché', h('div', { class: 'ms-affix' }, numberInput('RequestDelay', (config.RequestDelayMilliseconds || 3000) / 1000, { max: 60, min: 0, step: 0.5 }), h('span', { class: 'ms-affix__unit' }, 's')), 'Augmente-la si Steam limite souvent les requêtes.'),
					),
				),

				h('div', { class: 'ms-form__footer' },
					h('p', { class: 'ms-form__note' }, 'Enregistrer met à jour le fichier de config du bot. Il se reconnecte quelques secondes à Steam pour appliquer les réglages.'),
					h('div', { class: 'ms-form__buttons' },
						h('button', { class: 'ms-button', name: 'reset', type: 'button' }, 'Annuler les modifications'),
						h('button', { class: 'ms-button ms-button--primary', name: 'save', type: 'submit' }, 'Enregistrer'),
					),
				),
			);

			const refresh = () => {
				const source = (form.elements.Source.value) || 'LowestSellOrder';

				for (const element of form.querySelectorAll('[data-source]')) {
					element.hidden = element.dataset.source !== source;
				}

				example.textContent = this.describeExample(form, currency);
			};

			form.addEventListener('input', () => {
				this.state.formDirty = true;
				this.state.formMessage = null;
				this.updateSettingsMessage();
				refresh();
			});

			form.elements.reset.addEventListener('click', () => {
				this.state.formDirty = false;
				this.state.formMessage = null;
				this.settingsPanel = null;
				this.render();
			});

			form.addEventListener('submit', event => {
				event.preventDefault();
				this.save(bot, form);
			});

			this.settingsForm = form;
			this.settingsMessage = message;
			refresh();
			this.updateSettingsMessage();

			return form;
		},

		describeExample(form, currency) {
			const read = name => parseFloat(String(form.elements[name].value).replace(',', '.'));
			const cents = name => {
				const value = read(name);

				return Number.isFinite(value) ? Math.round(value * 100) : 0;
			};

			const source = form.elements.Source.value || 'LowestSellOrder';
			const base = source === 'Fixed' ? cents('FixedCents') : 10;

			if (!base) {
				return 'Indique un prix fixe pour voir un exemple.';
			}

			const multiplier = Number.isFinite(read('Multiplier')) ? read('Multiplier') / 100 : 1;
			let price = Math.max(1, Math.round(base * multiplier) + cents('OffsetCents'));
			const minimum = Math.max(cents('MinCents'), 3);
			const maximum = cents('MaxCents');

			price = Math.max(price, minimum);

			if (maximum > 0) {
				price = Math.min(price, maximum);
			}

			const seller = sellerPriceFor(price);
			const buyer = buyerPriceFor(seller);
			const reference = {
				LowestSellOrder: `un objet dont l'annonce la moins chère est à ${money(base, currency)}`,
				HighestBuyOrder: `un objet dont le meilleur acheteur propose ${money(base, currency)}`,
				AverageSold: `un objet vendu en moyenne ${money(base, currency)}`,
				Fixed: 'chaque objet',
			}[source];

			return `Exemple : ${reference} serait mis en vente à ${money(buyer, currency)}, et tu recevrais ${money(seller, currency)}.`;
		},

		updateSettingsMessage() {
			const container = this.settingsMessage;

			if (!container) {
				return;
			}

			const message = this.state.formMessage;

			container.replaceChildren(message ? h('div', { class: ['ms-notice', message.kind === 'error' && 'ms-notice--error'], role: message.kind === 'error' ? 'alert' : 'status' }, message.text) : '');
		},

		collect(form) {
			const elements = form.elements;
			const errors = [];

			const number = (name, label, scale = 1) => {
				const raw = String(elements[name].value).trim().replace(',', '.');

				if (raw === '') {
					return 0;
				}

				const value = parseFloat(raw);

				if (!Number.isFinite(value)) {
					errors.push(`${label} n'est pas un nombre valide.`);

					return 0;
				}

				return Math.round(value * scale);
			};

			const checked = name => Boolean(elements[name] && elements[name].checked);

			const appIDs = String(elements.AppIDs.value).split(/[\s,;]+/).filter(Boolean).map(value => {
				const parsed = Number(value);

				if (!Number.isInteger(parsed) || (parsed <= 0)) {
					errors.push(`« ${value} » n'est pas un AppID valide.`);
				}

				return parsed;
			});

			const config = {
				Enabled: checked('Enabled'),
				DryRun: checked('DryRun'),
				AutoConfirm: checked('AutoConfirm'),
				SellOnFarmingFinished: checked('SellOnFarmingFinished'),
				SellIntervalMinutes: number('SellIntervalMinutes', 'La vente automatique'),
				RepriceIntervalMinutes: number('RepriceIntervalMinutes', 'Le réajustement automatique'),
				RepriceThresholdCents: number('RepriceThresholdCents', 'L\'écart minimum', 100),
				RequestDelayMilliseconds: number('RequestDelay', 'La pause entre les requêtes', 1000),
				Country: String(elements.Country.value).trim().toUpperCase(),
				Types: TYPES.map(([value]) => value).filter(value => checked(`Type:${value}`)),
				KeepPerItem: number('KeepPerItem', 'Le nombre d\'exemplaires à garder'),
				Pricing: {
					Source: elements.Source.value || 'LowestSellOrder',
					AverageDays: number('AverageDays', 'Le nombre de jours') || 7,
					FixedCents: number('FixedCents', 'Le prix fixe', 100),
					Multiplier: number('Multiplier', 'Le pourcentage', 100) / 10000,
					OffsetCents: number('OffsetCents', 'Le décalage', 100),
					MinCents: number('MinCents', 'Le prix minimum', 100),
					MaxCents: number('MaxCents', 'Le prix maximum', 100),
				},
				Lock: {
					Rarities: RARITIES.map(([value]) => value).filter(value => checked(`Rarity:${value}`)),
					AppIDs: appIDs,
					Names: String(elements.Names.value).split('\n').map(value => value.trim()).filter(Boolean),
					PriceAboveCents: number('PriceAboveCents', 'La limite haute', 100),
					PriceBelowCents: number('PriceBelowCents', 'La limite basse', 100),
				},
			};

			if (config.Types.length === 0) {
				errors.push('Coche au moins une catégorie d\'objets à vendre.');
			}

			if (config.Pricing.Multiplier <= 0) {
				errors.push('Le pourcentage doit être supérieur à 0.');
			}

			return { config, errors };
		},

		async save(bot, form) {
			const { config, errors } = this.collect(form);

			if (errors.length > 0) {
				this.state.formMessage = { kind: 'error', text: errors.join(' ') };
				this.updateSettingsMessage();

				return;
			}

			this.state.saving = true;
			form.elements.save.disabled = true;
			form.elements.save.textContent = 'Enregistrement…';

			try {
				const message = await api('POST', `/${encodeURIComponent(bot.BotName)}/Config`, config);

				this.state.formDirty = false;
				this.state.formMessage = { kind: 'success', text: message || 'Réglages enregistrés.' };
				this.notify('success', 'Réglages enregistrés.');

				// The bot reloads with its new config, refresh once it's back
				setTimeout(() => this.poll(), 3000);
				setTimeout(() => this.poll(), 8000);
			} catch (error) {
				this.state.formMessage = { kind: 'error', text: error.message };
			} finally {
				this.state.saving = false;
				form.elements.save.disabled = false;
				form.elements.save.textContent = 'Enregistrer';
				this.updateSettingsMessage();
			}
		},
	};

	function safeStorageGet(key) {
		try {
			return localStorage.getItem(key);
		} catch {
			return null;
		}
	}

	function safeStorageSet(key, value) {
		try {
			localStorage.setItem(key, value);
		} catch {
			// Private browsing, the choice simply won't be remembered
		}
	}

	// ---------------------------------------------------------------- ASF-ui integration

	const RouteComponent = {
		name: 'MarketSellerPage',
		metaInfo: { title: 'Marché' },
		render(createElement) {
			return createElement('main', { staticClass: 'main-container main-container--fullheight' });
		},
		mounted() {
			App.mount(this.$el);
		},
		beforeDestroy() {
			App.unmount();
		},
	};

	// Price tag
	const TAG_ICON = 'M2 2h5.6c.4 0 .8.2 1.1.5l5.8 5.8c.6.6.6 1.5 0 2.1l-4.1 4.1c-.6.6-1.5.6-2.1 0L2.5 8.7C2.2 8.4 2 8 2 7.6V2zm3 1.75a1.25 1.25 0 1 0 0 2.5 1.25 1.25 0 0 0 0-2.5z';

	function findAsfUi() {
		const element = document.querySelector('.app');
		const vm = element && element.__vue__;

		return vm && vm.$router ? vm : null;
	}

	function ensureNavigationLink(router) {
		const navigation = document.querySelector('.side-navigation');

		if (!navigation) {
			return;
		}

		const existing = navigation.querySelector('.ms-nav-link');
		const commandsHref = router.resolve({ name: 'commands' }).href;
		const anchor = [...navigation.querySelectorAll('a.navigation-link')].find(link => link.getAttribute('href') === commandsHref);

		// Not logged in to ASF-ui: only the setup link is shown, so we stay out of the way
		if (!anchor) {
			if (existing) {
				existing.remove();
			}

			return;
		}

		if (existing) {
			return;
		}

		const link = h('a', {
			class: 'navigation-link navigation-link--default ms-nav-link',
			href: router.resolve({ name: ROUTE_NAME }).href,
			onClick: event => {
				event.preventDefault();

				if (router.currentRoute.name !== ROUTE_NAME) {
					router.push({ name: ROUTE_NAME }).catch(() => {});
				}
			},
		},
		h('span', { class: 'navigation-link__icon' }, svg('0 0 16 16', TAG_ICON, 'svg-inline--fa fa-fw')),
		h('span', { class: 'navigation-link__name' }, 'Marché'));

		anchor.after(link);
		updateNavigationLink(router.currentRoute);
	}

	function updateNavigationLink(route) {
		const link = document.querySelector('.ms-nav-link');

		if (link) {
			link.classList.toggle('navigation-link--active', Boolean(route && (route.name === ROUTE_NAME)));
		}
	}

	function integrate(vm) {
		const router = vm.$router;

		App.asfUi = vm;

		if (!router.getRoutes().some(route => route.name === ROUTE_NAME)) {
			router.addRoute({ component: RouteComponent, name: ROUTE_NAME, path: ROUTE_PATH });
		}

		router.afterEach((to, from) => {
			updateNavigationLink(to);

			// ASF-ui reopens the last visited page on startup and resets its default view when it doesn't know that page, so it remembers the previous one instead
			if (to.name === ROUTE_NAME) {
				const previous = from && from.name && (from.name !== ROUTE_NAME) ? from.name : 'bots';

				safeStorageSet('asf-ui:last-visited-page', JSON.stringify(previous));
			}
		});

		let scheduled = false;

		new MutationObserver(() => {
			if (scheduled) {
				return;
			}

			scheduled = true;

			requestAnimationFrame(() => {
				scheduled = false;
				ensureNavigationLink(router);
			});
		}).observe(vm.$el, { childList: true, subtree: true });

		ensureNavigationLink(router);

		// Opening /market-seller directly: ASF-ui didn't know the route yet and redirected, so we go back to it
		const initialPath = String(settings.initialPath || '').replace(/\/+$/, '');

		if (initialPath.endsWith(ROUTE_PATH)) {
			router.onReady(() => {
				if (router.currentRoute.name !== ROUTE_NAME) {
					router.replace({ name: ROUTE_NAME }).catch(() => {});
				}
			});
		}
	}

	function loadStyles() {
		if (document.querySelector('link[data-market-seller]')) {
			return;
		}

		document.head.append(h('link', { 'data-market-seller': true, href: `${BASE_PATH}market-seller/app.css?v=${VERSION}`, rel: 'stylesheet' }));
	}

	function boot() {
		loadStyles();

		const standalone = document.getElementById('market-seller-standalone');

		if (standalone) {
			App.mount(standalone);

			return;
		}

		const deadline = Date.now() + 30000;

		(function waitForAsfUi() {
			const vm = findAsfUi();

			if (vm) {
				integrate(vm);
			} else if (Date.now() < deadline) {
				setTimeout(waitForAsfUi, 100);
			}
		})();
	}

	boot();
})();
