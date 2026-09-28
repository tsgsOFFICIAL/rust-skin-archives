// Where to buy a skin on each market. prices.json only stores the market name and price, the link is built here
// from the skin's name (the same name every market lists it under), which keeps prices.json small.
// To add a market: add its name (exactly as the price fetcher writes it) below.
// A prices.json entry can still carry its own "url", that always wins (see marketUrl).
(function () {
	const q = encodeURIComponent;
	// "Abyss Furnace" -> "abyss-furnace", "Boxer's Bandages" -> "boxer's-bandages": the item path some markets use.
	// Spaces become hyphens (" - " becomes one: "Flak Vest - Green" -> "flak-vest-green"), "?" is dropped, other
	// punctuation stays (URL-encoded). Only ASCII letters are lowercased. dotsToHyphens is for markets that also
	// replace dots ("D.O.A Trunk" -> "d-o-a-trunk").
	const slug = (name, dotsToHyphens = false) => {
		let s = name
			.replace(/[A-Z]/g, (c) => c.toLowerCase())
			.replace(/\s+/g, " ")
			.trim();
		s = s.replace(/ - /g, "-").replace(/\?/g, "");
		if (dotsToHyphens) s = s.replace(/\./g, "-");
		return q(s.replace(/\s/g, "-"));
	};
	const isAscii = (s) => /^[\x00-\x7F]*$/.test(s);

	const MARKET_URLS = {
		"Steam": (n) => `https://steamcommunity.com/market/listings/252490/${q(n)}`,
		"Skinport": (n) => `https://skinport.com/rust/market?item=${q(n)}`,
		"CS.Deals": (n) => `https://cs.deals/market/rust?search=${q(n)}&sortBy=price_asc`,
		"DMarket": (n) => `https://dmarket.com/ingame-items/item-list/rust-skins?title=${q(n)}`,
		"Rust.tm": (n) => `https://rust.tm/?s=price&t=all&search=${q(n)}&sd=asc`,
		"Waxpeer": (n) => `https://waxpeer.com/rust?search=${q(n)}`,
		"Lis-Skins": (n) => `https://app.lis-skins.com/market/rust/${slug(n)}/`,
		"Mannco": (n) => `https://mannco.store/rust?&search=${q(n)}&page=1`,
		// Avan transliterates non-latin names, those get their Rust page instead
		"Avan Market": (n) => (isAscii(n) ? `https://avan.market/market/rust/${slug(n, true)}` : "https://avan.market/market/rust"),
		"CS.Trade": (n) => `https://cs.trade/trade?market_name=${q(n)}&sort_by=price_asc`,
		"ShadowPay": (n) => `https://shadowpay.com/en/rust-items?price_from=0&price_to=0&currency=USD&search=${q(n)}&sort_column=price&sort_dir=asc`,
		"Swap.gg": () => "https://swap.gg/?game=252490",
		"Tradeit.gg": (n) => `https://tradeit.gg/rust/store?search=${q(n)}`,
		"SkinSwap": () => "https://skinswap.com/",
		"RapidSkins": () => "https://rapidskins.com/",
		"Loot.farm": () => "https://loot.farm/"
	};

	// entry: a prices.json entry ({ name, price, url? }), skinName: the skin's displayName. Returns "" for an unknown market.
	function marketUrl(entry, skinName) {
		if (entry.url) return entry.url;
		const build = MARKET_URLS[entry.name];
		return build ? build(skinName) : "";
	}

	window.marketUrl = marketUrl;
})();
