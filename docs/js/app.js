let allSkins = [];
let currentFiltered = [];
let currentSort = "default";
let isDayMode = true;
let scrollY = 0;

// --- Virtual Scroll State ---------------------------------------------------
const CARD_HEIGHT_APPROX = 280; // px - recalculated after first render
const BUFFER_ROWS = 3; // rows to render above/below viewport
let colCount = 1;
let cardHeight = CARD_HEIGHT_APPROX;
let cardWidth = 200;
let vsFramePending = false;

// --- Search debounce --------------------------------------------------------
let searchDebounceTimer = null;
function debounce(fn, ms) {
	return (...args) => {
		clearTimeout(searchDebounceTimer);
		searchDebounceTimer = setTimeout(() => fn(...args), ms);
	};
}
const debouncedApplyFilters = debounce(() => applyFilters(), 180);

// --- Utilities --------------------------------------------------
function escapeRegExp(value) {
	return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function isAnyModalOpen() {
	return ["randomModal", "viewerModal", "wheelOverlay"].some((id) => {
		const modal = document.getElementById(id);
		return modal && modal.style.display === "flex";
	});
}

function updateBodyScrollLock() {
	if (isAnyModalOpen()) {
		if (!document.body.classList.contains("scroll-lock")) {
			scrollY = window.scrollY;
			document.body.classList.add("scroll-lock");
			document.body.style.top = `-${scrollY}px`;
		}
	} else {
		if (document.body.classList.contains("scroll-lock")) {
			document.body.classList.remove("scroll-lock");
			document.body.style.top = "";
			window.scrollTo({ top: scrollY, behavior: "instant" });
		}
	}
}

// A skin is "new" when its dateCreated (from skins.json) is within this many days.
const NEW_SKIN_DAYS = 14;
function isNewSkin(skin) {
	const t = Date.parse(skin.dateCreated);
	return !Number.isNaN(t) && Date.now() - t < NEW_SKIN_DAYS * 86400000;
}

function hasValidPrice(skin) {
	return skin.steamPriceInUsdCents > 0 || (skin.externalPrices && skin.externalPrices.length > 0);
}

function getCheapestCents(skin) {
	const steamCents = skin.steamPriceInUsdCents > 0 ? skin.steamPriceInUsdCents : Infinity;
	if (skin.externalPrices && skin.externalPrices.length > 0) {
		const externalMin = Math.min(...skin.externalPrices.map((p) => p.priceInUsdCents || Infinity));
		return Math.min(steamCents, externalMin);
	}
	return steamCents;
}

function formatCurrency(cents) {
	if (cents === 0) return "$0.00";
	if (!isFinite(cents) || cents < 0) return "-";
	return `$${(cents / 100).toFixed(2)}`;
}

function getTotalSteamValue(skins) {
	return skins.reduce((sum, skin) => {
		return sum + (skin.steamPriceInUsdCents > 0 ? skin.steamPriceInUsdCents : 0);
	}, 0);
}

function getTotalBestValue(skins) {
	return skins.reduce((sum, skin) => {
		const cents = getCheapestCents(skin);
		return sum + (cents < Infinity ? cents : 0);
	}, 0);
}

function updateStatusLine(skins) {
	const steamTotal = formatCurrency(getTotalSteamValue(skins));
	const bestTotal = formatCurrency(getTotalBestValue(skins));
	document.getElementById("status").textContent = `Showing ${skins.length} of ${allSkins.length} skins · Steam total ${steamTotal} · Best total ${bestTotal}`;
}

function parseBoolQueryValue(value) {
	return value === "1" || value === "true" || value === "yes";
}

function getQueryParams() {
	return new URLSearchParams(window.location.search);
}

function updateQueryStringFromFilters() {
	const params = new URLSearchParams();
	const term = document.getElementById("searchBox").value.trim();
	const minPrice = document.getElementById("minPrice").value.trim();
	const maxPrice = document.getElementById("maxPrice").value.trim();
	const typeTerm = document.getElementById("typeFilter").value.trim();
	const wantGlow = document.getElementById("glowFilter").checked;
	const wantCutout = document.getElementById("cutoutFilter").checked;
	const wantTwitch = document.getElementById("twitchFilter").checked;

	if (term) params.set("q", term);
	if (minPrice) params.set("minPrice", minPrice);
	if (maxPrice) params.set("maxPrice", maxPrice);
	if (typeTerm) params.set("type", typeTerm);
	if (wantGlow) params.set("glow", "1");
	if (wantCutout) params.set("cutout", "1");
	if (wantTwitch) params.set("twitch", "1");
	if (currentSort && currentSort !== "default") params.set("sort", currentSort);

	const newUrl = `${window.location.pathname}${params.toString() ? `?${params.toString()}` : ""}${window.location.hash}`;
	history.replaceState(null, "", newUrl);
}

function setFiltersFromQuery() {
	const params = getQueryParams();
	document.getElementById("searchBox").value = params.get("q") || "";
	document.getElementById("minPrice").value = params.get("minPrice") || "";
	document.getElementById("maxPrice").value = params.get("maxPrice") || "";
	document.getElementById("typeFilter").value = params.get("type") || "";
	document.getElementById("glowFilter").checked = parseBoolQueryValue(params.get("glow"));
	document.getElementById("cutoutFilter").checked = parseBoolQueryValue(params.get("cutout"));
	document.getElementById("twitchFilter").checked = parseBoolQueryValue(params.get("twitch"));

	const sortParam = params.get("sort") || "default";
	currentSort = ["default", "name-asc", "name-desc", "price-asc", "price-desc", "new"].includes(sortParam) ? sortParam : "default";
	document.querySelectorAll(".sort-btn").forEach((b) => {
		b.classList.toggle("active", b.dataset.sort === currentSort);
	});
}

function init() {
	setFiltersFromQuery();
	setupEventDelegation();
	setupSearchDebounce();
	loadSkins();
}

// --- Search & Filter input wiring -------------------------------------------
// Replace inline onkeyup with debounced version (HTML still calls applyFilters
// via onkeyup/oninput - we override those here so the HTML stays untouched)
function setupSearchDebounce() {
	const searchBox = document.getElementById("searchBox");
	const typeFilter = document.getElementById("typeFilter");
	const minPrice = document.getElementById("minPrice");
	const maxPrice = document.getElementById("maxPrice");

	if (searchBox) {
		searchBox.removeAttribute("onkeyup");
		searchBox.addEventListener("input", debouncedApplyFilters);
	}
	if (typeFilter) {
		typeFilter.removeAttribute("onkeyup");
		typeFilter.addEventListener("input", debouncedApplyFilters);
	}
	if (minPrice) {
		minPrice.removeAttribute("oninput");
		minPrice.addEventListener("input", debouncedApplyFilters);
	}
	if (maxPrice) {
		maxPrice.removeAttribute("oninput");
		maxPrice.addEventListener("input", debouncedApplyFilters);
	}
}

// --- Search logic ------------------------------------------------
const SEARCH_FIELD_MAP = {
	name: ["displayName"],
	type: ["itemShortName"],
	short: ["itemShortName"],
	itemshortname: ["itemShortName"],
	id: ["id"],
	displayname: ["displayName"]
};

function parseSearchQuery(input) {
	const terms = [];
	const freeTerms = [];
	const regex = /(\w+):"([^"]+)"|(\w+):'([^']+)'|(\w+):(\S+)|"([^"]+)"|'([^']+)'|(\S+)/g;
	let match;

	while ((match = regex.exec(input)) !== null) {
		if (match[1]) terms.push({ field: match[1].toLowerCase(), value: match[2].trim() });
		else if (match[3]) terms.push({ field: match[3].toLowerCase(), value: match[4].trim() });
		else if (match[5]) terms.push({ field: match[5].toLowerCase(), value: match[6].trim() });
		else if (match[7]) freeTerms.push(match[7].trim());
		else if (match[8]) freeTerms.push(match[8].trim());
		else if (match[9]) freeTerms.push(match[9].trim());
	}

	if (freeTerms.length > 0) {
		terms.push({ field: null, value: freeTerms.join(" ").trim() });
	}

	return terms.filter((token) => token.value.length > 0);
}

function normalizeSearchText(value) {
	if (value == null) return "";
	if (Array.isArray(value)) {
		return value.map(normalizeSearchText).filter(Boolean).join(" ");
	}
	if (typeof value === "object") return "";

	return String(value)
		.toLowerCase()
		.replace(/[^a-z0-9]+/g, " ")
		.trim()
		.replace(/\s+/g, " ");
}

function normalizeSearchTokens(value) {
	const normalized = normalizeSearchText(value);
	return normalized ? normalized.split(" ") : [];
}

function matchesSearchValue(value, term) {
	if (value == null) return false;
	if (Array.isArray(value)) {
		return value.some((item) => matchesSearchValue(item, term));
	}

	const normalizedValue = normalizeSearchText(value);
	const normalizedTerm = normalizeSearchText(term);
	return normalizedTerm.length > 0 && normalizedValue.includes(normalizedTerm);
}

function matchesSearchValueExact(value, term) {
	if (value == null) return false;
	if (Array.isArray(value)) {
		return value.some((item) => matchesSearchValueExact(item, term));
	}

	const normalizedTerm = normalizeSearchText(term);
	if (!normalizedTerm) return false;
	const tokens = normalizeSearchTokens(value);
	return tokens.some((token) => token === normalizedTerm);
}

function matchesAnyField(skin, term, field) {
	const value = skin[field];
	return matchesSearchValueExact(value, term);
}

function matchesAnyProp(skin, term) {
	if (!term) return true;
	const tokens = parseSearchQuery(term);

	return tokens.every(({ field, value }) => {
		if (!field) {
			for (let key in skin) {
				if (matchesSearchValue(skin[key], value)) {
					return true;
				}
			}
			return false;
		}

		const mappedKeys = SEARCH_FIELD_MAP[field] || [];
		if (mappedKeys.length > 0) {
			return mappedKeys.some((key) => matchesAnyField(skin, value, key));
		}

		return matchesAnyProp(skin, value);
	});
}

function sortSkins(skins) {
	const arr = [...skins];
	switch (currentSort) {
		case "name-asc":
			return arr.sort((a, b) => a.displayName.localeCompare(b.displayName));
		case "name-desc":
			return arr.sort((a, b) => b.displayName.localeCompare(a.displayName));
		case "price-asc":
			return arr.sort((a, b) => {
				const ca = getCheapestCents(a);
				const cb = getCheapestCents(b);
				if (ca === Infinity && cb === Infinity) return 0;
				if (ca === Infinity) return 1;
				if (cb === Infinity) return -1;
				return ca - cb;
			});
		case "price-desc":
			return arr.sort((a, b) => {
				const ca = getCheapestCents(a);
				const cb = getCheapestCents(b);
				if (ca === Infinity && cb === Infinity) return 0;
				if (ca === Infinity) return 1;
				if (cb === Infinity) return -1;
				return cb - ca;
			});
		case "new":
			return arr.sort((a, b) => (isNewSkin(b) ? 1 : 0) - (isNewSkin(a) ? 1 : 0));
		default:
			return arr;
	}
}

function setSort(btn, sortKey) {
	currentSort = sortKey;
	document.querySelectorAll(".sort-btn").forEach((b) => b.classList.remove("active"));
	btn.classList.add("active");
	applyFilters();
}

// --- Card HTML builder (string, no DOM - fast) ------------------------------
function buildCardHTML(skin) {
	const cents = getCheapestCents(skin);
	const priceText = cents > 0 && cents < Infinity ? `$${(cents / 100).toFixed(2)}` : "-";
	const hasModel = skin.modelUrls && skin.modelUrls.length > 0;
	const base = "icons";
	const img = `${base}/${skin.id}.png`;
	const steamPriceText = skin.steamPriceInUsdCents > 0 ? `$${(skin.steamPriceInUsdCents / 100).toFixed(2)}` : "-";

	return `
		<img
			src="${img}"
			alt="${skin.displayName}"
			title="${skin.displayName}"
			loading="lazy"
			data-fallback="https://placehold.co/245x210/111/666?text=No+Image">
		<div class="card-badges">
			${isNewSkin(skin) ? '<span class="badge-new">NEW</span>' : ""}
			${hasModel ? `<button class="view3d-btn" data-action="view3d" data-skin-id="${skin.id}" title="View 3D Model">⬡ 3D</button>` : ""}
		</div>
		<div class="skin-info">
			<h3>${skin.displayName}</h3>
			<p>${skin.itemShortName || ""}</p>
			<div class="card-prices">
				<span class="card-price-best">
					<span class="price-icon">💰</span>
					<span>${priceText}</span>
				</span>
				<span class="card-price-steam">
					<span class="price-icon">🎮</span>
					<span>${steamPriceText}</span>
				</span>
			</div>
		</div>
	`;
}

// --- Event Delegation (replaces per-card listeners) -------------------------
// One listener on the grid handles ALL card clicks and 3D button clicks.
const skinIndexById = new Map();

function setupEventDelegation() {
	const grid = document.getElementById("grid");

	grid.addEventListener("click", (e) => {
		// 3D button
		const btn3d = e.target.closest("[data-action='view3d']");
		if (btn3d) {
			e.stopPropagation();
			const skin = skinIndexById.get(btn3d.dataset.skinId);
			if (skin) {
				openViewer(skin);
				const toggle = document.querySelector("#dayNightToggle");
				if (toggle) toggle.checked = true;
			}
			return;
		}

		// Card click → modal
		const card = e.target.closest(".skin[data-skin-id]");
		if (card) {
			const skin = skinIndexById.get(card.dataset.skinId);
			if (skin) showSkinModal(skin, false);
		}
	});

	// Single delegated image error handler
	grid.addEventListener(
		"error",
		(e) => {
			if (e.target.tagName === "IMG" && e.target.dataset.fallback) {
				e.target.src = e.target.dataset.fallback;
				delete e.target.dataset.fallback;
			}
		},
		true
	); // capture phase to catch img errors
}

// --- Virtual Scroll ----------------------------------------------------------
function measureGrid() {
	const grid = document.getElementById("grid");

	// Temporarily restore display:grid so CSS can tell us the real column count
	// and so the probe card gets naturally sized by the grid's column definition.
	const prevDisplay = grid.style.display;
	const prevPosition = grid.style.position;
	const prevHeight = grid.style.height;

	grid.style.display = ""; // let CSS grid kick in
	grid.style.position = "";
	grid.style.height = "";

	const gridRect = grid.getBoundingClientRect();
	if (!gridRect.width) {
		// restore and bail
		grid.style.display = prevDisplay;
		grid.style.position = prevPosition;
		grid.style.height = prevHeight;
		return;
	}

	// Read column count directly from the computed CSS grid template -
	// this is always correct regardless of card sizes or screen width.
	const computedCols = getComputedStyle(grid).gridTemplateColumns.trim().split(/\s+/).filter(Boolean).length;

	colCount = Math.max(1, computedCols);

	// Inject a probe card to measure natural card height
	let probe = document.createElement("article");
	probe.className = "skin";
	probe.style.cssText = "visibility:hidden;pointer-events:none;";
	probe.innerHTML = buildCardHTML(currentFiltered[0] || allSkins[0]);
	grid.appendChild(probe);

	const rect = probe.getBoundingClientRect();
	grid.removeChild(probe);

	// Restore virtual-scroll display state
	grid.style.display = prevDisplay;
	grid.style.position = prevPosition;
	grid.style.height = prevHeight;

	if (!rect.width) return;

	const gap = 16;
	cardHeight = rect.height + gap;
	cardWidth = rect.width + gap;
}

function getVisibleRange() {
	const rowCount = Math.ceil(currentFiltered.length / colCount);
	const totalH = rowCount * cardHeight;

	// viewport top relative to grid top
	const grid = document.getElementById("grid");
	const gridTop = grid.getBoundingClientRect().top + window.scrollY;
	const vpTop = window.scrollY - gridTop;
	const vpBottom = vpTop + window.innerHeight;

	const firstRow = Math.max(0, Math.floor(vpTop / cardHeight) - BUFFER_ROWS);
	const lastRow = Math.min(rowCount - 1, Math.ceil(vpBottom / cardHeight) + BUFFER_ROWS);

	const firstIdx = firstRow * colCount;
	const lastIdx = Math.min(currentFiltered.length - 1, (lastRow + 1) * colCount - 1);

	return { firstIdx, lastIdx, totalH, firstRow };
}

// Pool of reusable card DOM nodes
const cardPool = [];
// Map: pool index → rendered skin id
const renderedCards = new Map(); // skinId → card element

function vsRender() {
	vsFramePending = false;
	const grid = document.getElementById("grid");
	if (!currentFiltered.length) {
		grid.innerHTML = "";
		grid.style.display = "";
		grid.style.height = "";
		grid.style.position = "";
		return;
	}

	// Measure on first call or after filter change
	if (!colCount || colCount < 1) measureGrid();

	const { firstIdx, lastIdx, totalH, firstRow } = getVisibleRange();

	// Switch grid from CSS grid layout to a plain positioning context.
	// absolute-positioned cards don't play well with display:grid.
	grid.style.display = "block";
	grid.style.position = "relative";
	grid.style.height = `${totalH}px`;

	// Find which skin IDs should be visible
	const needed = new Set();
	for (let i = firstIdx; i <= lastIdx; i++) {
		needed.add(currentFiltered[i].id);
	}

	// Remove cards that scrolled out of view - return to pool
	for (const [skinId, card] of renderedCards) {
		if (!needed.has(skinId)) {
			card.style.display = "none";
			cardPool.push(card);
			renderedCards.delete(skinId);
		}
	}

	// Add cards that are now in view
	for (let i = firstIdx; i <= lastIdx; i++) {
		const skin = currentFiltered[i];
		if (renderedCards.has(skin.id)) continue; // already in DOM

		// Grab from pool or create fresh
		let card = cardPool.pop();
		if (!card) {
			card = document.createElement("article");
			card.className = "skin";
		}

		card.style.display = "";
		card.dataset.skinId = skin.id;
		card.innerHTML = buildCardHTML(skin);

		// Position absolutely within the grid
		const row = Math.floor(i / colCount);
		const col = i % colCount;
		card.style.position = "absolute";
		card.style.top = `${row * cardHeight}px`;
		card.style.left = `${col * (100 / colCount)}%`;
		card.style.width = `calc(${100 / colCount}% - 16px)`;

		grid.appendChild(card);
		// Suppress entrance animation for cards added during scroll
		requestAnimationFrame(() => card.setAttribute("data-vs-ready", "1"));
		renderedCards.set(skin.id, card);
	}
}

function scheduleVsRender() {
	if (vsFramePending) return;
	vsFramePending = true;
	requestAnimationFrame(vsRender);
}

// --- renderSkins: replaces old innerHTML loop --------------------------------
function renderSkins(skins) {
	currentFiltered = sortSkins(skins);

	// Rebuild id → skin lookup
	skinIndexById.clear();
	for (const s of allSkins) skinIndexById.set(s.id, s);

	// Reset virtual scroll state
	renderedCards.clear();
	cardPool.length = 0;
	colCount = 0; // force re-measure

	const grid = document.getElementById("grid");
	grid.innerHTML = "";
	grid.style.position = "";
	grid.style.height = "";

	// Double-rAF: first frame lets the browser apply grid CSS,
	// second frame guarantees getBoundingClientRect has real values.
	requestAnimationFrame(() =>
		requestAnimationFrame(() => {
			measureGrid();
			vsRender();
		})
	);
}

// --- Scroll & Resize listeners -----------------------------------------------
window.addEventListener("scroll", scheduleVsRender, { passive: true });
window.addEventListener(
	"resize",
	() => {
		colCount = 0; // force re-measure on resize
		scheduleVsRender();
	},
	{ passive: true }
);

// --- applyFilters ------------------
function applyFilters(skipUrlUpdate = false) {
	const term = document.getElementById("searchBox").value.trim();
	let minPrice = parseFloat(document.getElementById("minPrice").value) || 0;
	let maxPrice = parseFloat(document.getElementById("maxPrice").value) || Infinity;
	const typeTerm = document.getElementById("typeFilter").value.toLowerCase().trim();

	const wantGlow = document.getElementById("glowFilter").checked;
	const wantCutout = document.getElementById("cutoutFilter").checked;
	const wantTwitch = document.getElementById("twitchFilter").checked;

	const filtered = allSkins.filter((skin) => {
		if (!matchesAnyProp(skin, term)) return false;

		if (minPrice > 0 || maxPrice < Infinity) {
			if (!hasValidPrice(skin)) return false;
			const cents = getCheapestCents(skin);
			if (cents < minPrice * 100 || cents > maxPrice * 100) return false;
		}

		const normalizedType = skin.itemShortName?.toLowerCase().trim();
		if (typeTerm && (!normalizedType || normalizedType !== typeTerm)) return false;

		if (wantGlow && skin.glow !== true) return false;
		if (wantCutout && skin.cutout !== true) return false;
		if (wantTwitch && skin.twitchDrop !== true) return false;
		if (!wantTwitch && skin.twitchDrop === true) return false;

		return true;
	});

	renderSkins(filtered);
	updateStatusLine(filtered);

	if (!skipUrlUpdate) {
		updateQueryStringFromFilters();
	}
}

function clearFilters() {
	document.getElementById("searchBox").value = "";
	document.getElementById("minPrice").value = "";
	document.getElementById("maxPrice").value = "";
	document.getElementById("typeFilter").value = "";
	document.getElementById("glowFilter").checked = false;
	document.getElementById("cutoutFilter").checked = false;
	document.getElementById("twitchFilter").checked = false;
	applyFilters();
}

const WHEEL_ITEM_WIDTH = 156;
const WHEEL_REEL_SPIN_ITEMS = 38;
const WHEEL_SPIN_MS = 4500;
let isWheelSpinning = false;
let wheelSpinTimer = null;

function shuffleInPlace(array) {
	for (let i = array.length - 1; i > 0; i--) {
		const j = Math.floor(Math.random() * (i + 1));
		[array[i], array[j]] = [array[j], array[i]];
	}
	return array;
}

function buildWheelReel(winnerSkin) {
	const reel = document.getElementById("wheelSpinner");
	const iconBase = "icons";
	const fallback = "https://placehold.co/245x210/111/666?text=No+Image";
	const pool = shuffleInPlace([...currentFiltered]);
	const reelSkins = [];

	for (let i = 0; i < WHEEL_REEL_SPIN_ITEMS; i++) {
		reelSkins.push(pool[i % pool.length]);
	}
	reelSkins.push(winnerSkin);
	for (let i = 0; i < 4; i++) {
		reelSkins.push(pool[(WHEEL_REEL_SPIN_ITEMS + i) % pool.length]);
	}

	reel.innerHTML = "";
	reel.style.setProperty("--item-width", `${WHEEL_ITEM_WIDTH}px`);
	const wrap = reel.closest(".wheel-viewport-wrap");
	if (wrap) wrap.style.setProperty("--item-width", `${WHEEL_ITEM_WIDTH}px`);

	reelSkins.forEach((skin, index) => {
		const item = document.createElement("div");
		item.className = "wheel-item";

		const visual = document.createElement("div");
		visual.className = "wheel-item-visual";

		const img = document.createElement("img");
		img.src = `${iconBase}/${skin.id}.png`;
		img.alt = skin.displayName;
		img.loading = "eager";
		img.onerror = function onWheelImgError() {
			this.onerror = null;
			this.src = fallback;
		};

		const name = document.createElement("span");
		name.className = "wheel-item-name";
		name.textContent = skin.displayName;
		name.title = skin.displayName;

		visual.appendChild(img);
		item.appendChild(visual);
		item.appendChild(name);
		reel.appendChild(item);
	});

	return WHEEL_REEL_SPIN_ITEMS;
}

function getWheelCenterOffset(viewport) {
	return (viewport.clientWidth - WHEEL_ITEM_WIDTH) / 2;
}

function showWheelOverlay() {
	const overlay = document.getElementById("wheelOverlay");
	overlay.style.display = "flex";
	overlay.setAttribute("aria-hidden", "false");
	overlay.setAttribute("aria-busy", "true");
	updateBodyScrollLock();
}

function hideWheelOverlay() {
	const overlay = document.getElementById("wheelOverlay");
	overlay.style.display = "none";
	overlay.setAttribute("aria-hidden", "true");
	overlay.removeAttribute("aria-busy");
}

function spinWheelToSkin(winnerSkin) {
	if (wheelSpinTimer) {
		clearTimeout(wheelSpinTimer);
		wheelSpinTimer = null;
	}

	isWheelSpinning = true;
	hideModal(true);

	const winIndex = buildWheelReel(winnerSkin);
	showWheelOverlay();

	const reel = document.getElementById("wheelSpinner");
	const viewport = reel.parentElement;
	const centerOffset = getWheelCenterOffset(viewport);
	const startX = centerOffset;
	const finalX = centerOffset - winIndex * WHEEL_ITEM_WIDTH;

	reel.style.transition = "none";
	reel.style.transform = `translateX(${startX}px)`;
	void reel.offsetWidth;

	reel.style.transition = `transform ${WHEEL_SPIN_MS}ms cubic-bezier(0.08, 0.82, 0.12, 1)`;
	reel.style.transform = `translateX(${finalX}px)`;

	wheelSpinTimer = setTimeout(() => {
		wheelSpinTimer = null;
		isWheelSpinning = false;
		hideWheelOverlay();
		showSkinModal(winnerSkin, true);
	}, WHEEL_SPIN_MS + 280);
}

function pickRandomSkin() {
	if (isWheelSpinning) return;

	if (currentFiltered.length === 0) {
		alert("No skins match your current filters!");
		return;
	}

	const skin = currentFiltered[Math.floor(Math.random() * currentFiltered.length)];
	spinWheelToSkin(skin);
}

// --- Modal ------------------------------------------------------
function showSkinModal(skin, showRollAgain = false) {
	const base = "icons";
	document.getElementById("modalImg").src = `${base}/${skin.id}.png`;
	document.getElementById("modalName").textContent = skin.displayName;
	document.getElementById("modalShort").textContent = skin.itemShortName || "";

	const pricesDiv = document.getElementById("modalPrices");
	pricesDiv.innerHTML = "<strong>Prices:</strong>";

	const externalPricesRaw = (skin.externalPrices || []).filter((p) => p.priceInUsdCents > 0);
	const steamExternal = externalPricesRaw.find((p) => p.marketId?.toLowerCase() === "steam");
	const externalPrices = externalPricesRaw.filter((p) => p.marketId?.toLowerCase() !== "steam");
	const steamCents = skin.steamPriceInUsdCents > 0 ? skin.steamPriceInUsdCents : steamExternal ? steamExternal.priceInUsdCents : Infinity;
	const cheapestCents = Math.min(steamCents, ...externalPrices.map((p) => p.priceInUsdCents || Infinity));

	function appendPriceRow(label, cents, isCheapest, url) {
		const row = document.createElement("div");
		row.className = "modal-price-row";
		if (isCheapest) row.classList.add("cheapest");
		if (url) {
			row.classList.add("clickable");
			row.addEventListener("click", () => window.open(url, "_blank", "noopener noreferrer"));
			row.addEventListener("keydown", (event) => {
				if (event.key === "Enter" || event.key === " ") {
					event.preventDefault();
					window.open(url, "_blank", "noopener noreferrer");
				}
			});
			row.tabIndex = 0;
			row.setAttribute("role", "button");
		}

		const labelSpan = document.createElement("div");
		labelSpan.className = "price-label";

		const labelText = document.createElement("span");
		labelText.className = "price-label-text";
		labelText.textContent = label;
		labelSpan.appendChild(labelText);

		if (isCheapest) {
			const badge = document.createElement("span");
			badge.className = "price-badge";
			badge.textContent = "BEST";
			labelSpan.appendChild(badge);
		}

		const priceSpan = document.createElement("span");
		priceSpan.className = "price-value";
		priceSpan.textContent = `$${(cents / 100).toFixed(2)}`;
		row.appendChild(labelSpan);
		row.appendChild(priceSpan);
		pricesDiv.appendChild(row);
	}

	if (steamCents !== Infinity) {
		appendPriceRow("Steam", steamCents, steamCents === cheapestCents, steamExternal?.externalUrl);
	}

	if (externalPrices.length) {
		externalPrices.forEach((p) => {
			appendPriceRow(p.marketId, p.priceInUsdCents, p.priceInUsdCents === cheapestCents, p.externalUrl);
		});
	}

	if (steamCents === Infinity && externalPrices.length === 0) {
		pricesDiv.innerHTML += "No market prices available";
	}

	const open3dButton = document.getElementById("open3dButton");
	if (skin.modelUrls && skin.modelUrls.length > 0) {
		open3dButton.style.display = "inline-flex";
		open3dButton.onclick = () => {
			hideModal(true); // silent - keep scroll lock alive during handoff
			openViewer(skin);
			const toggle = document.querySelector("#dayNightToggle");
			if (toggle) toggle.checked = true;
		};
	} else {
		open3dButton.style.display = "none";
	}

	document.getElementById("randomModal").style.display = "flex";
	document.getElementById("rollAgainButton").style.display = showRollAgain ? "block" : "none";
	updateBodyScrollLock();
}

function hideModal(silent = false) {
	document.getElementById("randomModal").style.display = "none";
	if (!silent) updateBodyScrollLock();
}

function openViewer(skin) {
	const modal = document.getElementById("viewerModal");
	const loader = document.getElementById("viewerLoader");
	const noModel = document.getElementById("viewerNoModel");
	const hint = document.querySelector(".viewer-hint");
	const container = document.getElementById("viewerContainer");

	document.getElementById("viewerName").textContent = skin.displayName;

	const hasModel = skin.modelUrls && skin.modelUrls.length > 0;

	if (!hasModel) {
		container.style.display = "none";
		noModel.style.display = "block";
		if (hint) hint.style.display = "none";
		modal.style.display = "flex";
		updateBodyScrollLock();
		return;
	}

	container.style.display = "block";
	noModel.style.display = "none";
	if (hint) hint.style.display = "block";
	loader.style.display = "flex";

	modal.style.display = "flex";
	updateBodyScrollLock();

	const url = `models/${skin.modelUrls[0]}`;
	const failed = (err) => {
		console.error(err);
		loader.style.display = "none";
		noModel.style.display = "block";
	};
	window.Viewer3DReady.then((v) => v.open(document.getElementById("renderCanvas"), url, isDayMode))
		.then(() => { loader.style.display = "none"; })
		.catch(failed);
}

function hideViewer() {
	document.getElementById("viewerModal").style.display = "none";
	updateBodyScrollLock();

	if (window.Viewer3DReady) window.Viewer3DReady.then((v) => v.close());

	// After scroll position is restored, re-render visible cards.
	// Without this, the grid can appear empty if scrollY was non-zero.
	requestAnimationFrame(() => requestAnimationFrame(vsRender));
}

// prices.json is updated on its own schedule (GitHub Action, every few hours), separately from skins.json.
// One block per source: "steam" becomes steamPriceInUsdCents, every other source becomes an externalPrices entry.
async function mergePrices(skins) {
	try {
		const res = await fetch("prices.json", { cache: "no-cache" });
		if (!res.ok) return;
		const sources = (await res.json()).sources || {};
		const byId = new Map(skins.map((s) => [s.id, s]));
		for (const [market, block] of Object.entries(sources)) {
			for (const [id, item] of Object.entries(block.items || {})) {
				const skin = byId.get(id);
				if (!skin || !(item.cents > 0)) continue;
				if (market === "steam") skin.steamPriceInUsdCents = item.cents;
				else (skin.externalPrices ||= []).push({ marketId: market, priceInUsdCents: item.cents, externalUrl: item.url });
			}
		}
	} catch (e) {
		console.warn("No prices available:", e);
	}
}

function loadSkins() {
	const status = document.getElementById("status");

	fetch("skins.json")
		.then(async (res) => {
			allSkins = await res.json();
			await mergePrices(allSkins);
			currentFiltered = allSkins;
			// applyFilters calls renderSkins internally - no need to call both
			applyFilters(true);
		})
		.catch(() => {
			status.textContent = "Failed to load skins.json";
		});
}

document.addEventListener("DOMContentLoaded", init);

function toggleDayNight(enabled) {
	isDayMode = enabled;
	if (window.Viewer3DReady) window.Viewer3DReady.then((v) => v.setDay(enabled));
}

window.onkeydown = (event) => {
	if (event.key === "Escape") {
		hideModal();
		hideViewer();
	}
};
