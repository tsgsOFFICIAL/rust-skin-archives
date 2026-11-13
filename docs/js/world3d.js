// "In the world" view for the 3D viewer: puts a skin where it lives in the game instead of floating in the studio.
// wearables go on a mannequin, doors / hatches / shop fronts into a wall or floor of a base, furniture and deployables into a
// room, weapons and tools dropped on the floor of a stone base. The scenery is the game's own building pieces and player model.
//
// The GLBs come out of the converter turned to face the camera (RotX / RotY per item type, see OrientationOverrides in the
// pipeline), the viewer undoes that for items that live flat on the floor (hatches, rugs, beds). Placement is by bounding
// box, the item's own origin means different things per item type (a hat is at the origin, a jacket where the body is).
import * as THREE from "three";
import { OBJLoader } from "three/addons/loaders/OBJLoader.js";

// ---- what each item does in the world, by itemShortName ----
// wear:   slot on the mannequin (head / face / torso / legs / feet / hands / back), top / bottom / centre = where the
//         item's bounding box goes on the body (metres, the mannequin is 1.8 tall)
// wall:   door / frame set into a wall opening (x0, x1, y0, y1) at the item's own origin
// floor:  set into a raised floor
// room:   stands (or lies, flat) on the floor of a room
// bench:  dropped on the floor of a base
// yard:   stands outside on the ground
const W = (slot, o = {}) => ({ kind: "wear", slot, ...o });
const WALL = (x0, x1, y1, o = {}) => ({ kind: "wall", open: [x0, x1, 0, y1], ...o });
const FLOOR = (tri) => ({ kind: "floor", raw: true, tri });
const ROOM = (o = {}) => ({ kind: "room", ...o });
const BENCH = (o = {}) => ({ kind: "bench", ...o });

const SPECS = {
	// head
	"hat.beenie": W("head", { top: 1.76 }),
	"hat.cap": W("head", { top: 1.76 }),
	"hat.boonie": W("head", { top: 1.76 }),
	"hat.miner": W("head", { top: 1.76 }),
	"bucket.helmet": W("head", { top: 1.76 }),
	"riot.helmet": W("head", { top: 1.76 }),
	"wood.armor.helmet": W("head", { top: 1.76 }),
	"burlap.headwrap": W("head", { top: 1.76 }),
	"coffeecan.helmet": W("head", { top: 1.76 }),
	// face
	"mask.balaclava": W("head", { cy: 1.58 }),
	"mask.bandana": W("head", { cy: 1.56 }),
	"metal.facemask": W("head", { cy: 1.58 }),
	"deer.skull.mask": W("head", { cy: 1.62 }),
	// torso
	tshirt: W("torso", { top: 1.5 }),
	"tshirt.long": W("torso", { top: 1.5 }),
	"shirt.collared": W("torso", { top: 1.5 }),
	"shirt.tanktop": W("torso", { top: 1.5 }),
	"burlap.shirt": W("torso", { top: 1.5 }),
	"attire.hide.vest": W("torso", { top: 1.48 }),
	"attire.hide.helterneck": W("torso", { top: 1.48 }),
	"attire.hide.poncho": W("torso", { top: 1.52 }),
	hoodie: W("torso", { top: 1.52 }),
	jacket: W("torso", { top: 1.52 }),
	"jacket.snow": W("torso", { top: 1.52 }),
	"roadsign.jacket": W("torso", { top: 1.48 }),
	"metal.plate.torso": W("torso", { top: 1.48 }),
	"wood.armor.jacket": W("torso", { top: 1.52 }),
	// legs
	pants: W("legs", { top: 1.06 }),
	"pants.shorts": W("legs", { top: 1.06 }),
	"burlap.trousers": W("legs", { top: 1.06 }),
	"attire.hide.pants": W("legs", { top: 1.06 }),
	"attire.hide.skirt": W("legs", { top: 1.06 }),
	"roadsign.kilt": W("legs", { top: 1.06 }),
	"wood.armor.pants": W("legs", { top: 1.06 }),
	// feet
	"shoes.boots": W("feet", { bottom: 0 }),
	"attire.hide.boots": W("feet", { bottom: 0 }),
	"burlap.shoes": W("feet", { bottom: 0 }),
	// hands
	"burlap.gloves": W("hands", { cy: 1.03 }),
	"tactical.gloves": W("hands", { cy: 1.03 }),
	"roadsign.gloves": W("hands", { cy: 1.03 }),
	"woodarmor.gloves": W("hands", { cy: 1.03 }),
	// back
	smallbackpack: W("back", { top: 1.45, raw: true }),
	largebackpack: W("back", { top: 1.47, raw: true }),

	// building
	"door.hinged.wood": WALL(-1.2, 0, 2.1),
	"door.hinged.metal": WALL(-1.2, 0, 2.1),
	"door.hinged.toptier": WALL(-1.2, 0, 2.1),
	"door.double.hinged.wood": WALL(-1.2, 1.2, 2.2),
	"door.double.hinged.metal": WALL(-1.2, 1.2, 2.2),
	"door.double.hinged.toptier": WALL(-1.2, 1.2, 2.2),
	"wall.frame.garagedoor": WALL(-1.47, 1.47, 3.0),
	"wall.frame.shopfront.metal": WALL(-1.43, 1.43, 2.85, { frameless: true }),
	"floor.ladder.hatch": FLOOR(false),
	"floor.ladder.hatch.toptier": FLOOR(false),
	"floor.triangle.ladder.hatch": FLOOR(true),
	"floor.triangle.ladder.hatch.toptier": FLOOR(true),

	// furniture and deployables, they lie flat or stand in a room
	rug: ROOM({ raw: true }),
	"rug.bear": ROOM({ raw: true }),
	sleepingbag: ROOM({ raw: true }),
	bed: ROOM({ raw: true }),
	"spinner.wheel": ROOM({ raw: true }),
	chair: ROOM(),
	table: ROOM(),
	"box.wooden": ROOM(),
	"box.wooden.large": ROOM(),
	locker: ROOM(),
	fridge: ROOM(),
	furnace: ROOM(),
	"electric.furnace": ROOM(),
	bbq: ROOM(),
	"vending.machine": ROOM(),
	"water.purifier": ROOM(),
	autoturret: ROOM(),
	"target.reactive": ROOM(),
	"barricade.concrete": { kind: "yard" },
	"barricade.sandbags": { kind: "yard" },
	"explosive.satchel": BENCH({ upright: true }),
	"grenade.f1": BENCH({ upright: true }),
	rock: BENCH({ upright: true }),
	"fun.guitar": BENCH({ raw: true, noflat: true }),
};
// everything else in the game that isn't listed (weapons, tools) lies on the workbench
const DEFAULT_SPEC = BENCH();

// the specs above are what the item does, specFor adds which tier of building it belongs to (the armored items go with armored
// walls and floors)
export function specFor(shortName) {
	const s = SPECS[shortName] || DEFAULT_SPEC;
	return { ...s, armored: /toptier/.test(shortName), name: shortName };
}

// ---- the game's own pieces ----
// Everything but the grass is from the game: the player (head, torso, legs, hands, feet and the base shorts) in docs/world/player,
// the building pieces (stone and armored walls, wall frames, floors, floor frames) in docs/world/building, all with the building
// atlas (docs/world/tex, made by `RustSkinToGlb --build-world-textures`).
const BASE_URL = new URL("../world/", import.meta.url).href;
const PIECES = {};
let atlas = null;

function loadPieces(names) {
	return Promise.all(names.filter((n) => !PIECES[n]).map(async (n) => { PIECES[n] = await new OBJLoader().loadAsync(BASE_URL + "building/" + n + ".obj"); }));
}

function atlasMat() {
	if (atlas) return atlas;
	const tl = new THREE.TextureLoader();
	const tex = (kind, srgb) => {
		const t = tl.load(BASE_URL + "tex/atlas_wood_stone_metal_" + kind + ".jpg");
		t.wrapS = t.wrapT = THREE.RepeatWrapping;
		t.anisotropy = 8;
		if (srgb) t.colorSpace = THREE.SRGBColorSpace;
		return t;
	};
	const orm = tex("orm");
	atlas = new THREE.MeshStandardMaterial({ map: tex("color", true), normalMap: tex("normal"), roughnessMap: orm, metalnessMap: orm, aoMap: orm, side: THREE.DoubleSide });
	return atlas;
}

// a copy of a building piece at a position, turned around Y
function piece(name, x, y, z, ry = 0) {
	const o = PIECES[name].clone(true);
	o.traverse((m) => { if (m.isMesh) { m.material = atlasMat(); m.castShadow = m.receiveShadow = true; } });
	o.position.set(x, y, z);
	o.rotation.y = ry;
	return o;
}

// the armored wall and floor pieces use a wood patterned part of the atlas, so every scene is built from the stone ones (an armored
// hatch still gets the armored floor frame)
const tierOf = () => "stone";

function neededPieces(spec) {
	const T = tierOf(spec);
	switch (spec.kind) {
		case "wall": {
			const single = spec.open[1] - spec.open[0] < 1.5;
			return [single ? T + ".doorway" : "wallframe.stone", T + ".wall", T + ".floor"];
		}
		case "floor":
			return [(spec.tri ? "trifloorframe." : "floorframe.") + (spec.armored ? "toptier" : "stone"), T + ".wall", T + ".floor"];
		case "wear":
		case "yard":
			return ["stone.floor"];
		default:
			return ["stone.floor", "stone.wall", "stone.doorway"];
	}
}

const ENV_URL = BASE_URL + "env/";
const PLAYER_PARTS = ["head", "eyes", "torso", "legs", "hands", "feet", "shorts"];
let playerPromise = null;
let PLAYER = null;

function tex(file, srgb, repeat) {
	const t = new THREE.TextureLoader().load(ENV_URL + file);
	t.wrapS = t.wrapT = THREE.RepeatWrapping;
	t.anisotropy = 8;
	if (srgb) t.colorSpace = THREE.SRGBColorSpace;
	if (repeat) t.repeat.set(repeat, repeat);
	return t;
}

// the game's male player model in the pose the clothing meshes were made for (arms out to the sides, 1.8 m, facing +Z), with the
// game's own skin textures (light skin, "Kevin" head) and its shorts as the base layer
function loadPlayer() {
	if (!playerPromise) {
		const loader = new OBJLoader();
		const body = new THREE.MeshStandardMaterial({ map: tex("body.jpg", true), normalMap: tex("body_n.jpg"), roughness: 0.62 });
		const head = new THREE.MeshStandardMaterial({ map: tex("head.jpg", true), normalMap: tex("head_n.jpg"), roughness: 0.6 });
		const eyes = new THREE.MeshStandardMaterial({ color: 0xe9e6e0, roughness: 0.15 });
		const mats = { head, eyes, shorts: mat("shorts") };
		playerPromise = Promise.all(PLAYER_PARTS.map((p) => loader.loadAsync(BASE_URL + "player/" + p + ".obj"))).then((parts) => {
			const g = new THREE.Group();
			parts.forEach((o, i) => {
				o.name = PLAYER_PARTS[i];
				// the shorts mesh is made in a frame 0.6 m below the body
				if (o.name === "shorts") { o.position.y = 0.6; o.scale.set(1.12, 1, 1.18); }
				o.traverse((m) => { if (m.isMesh) { m.material = mats[o.name] || body; m.castShadow = true; m.receiveShadow = true; } });
				g.add(o);
			});
			return g;
		});
	}
	return playerPromise;
}

// ---- outdoors: the game's grass, oak and Douglas fir trees (LOD1 meshes with the tree atlas), a painted sky ----
const TREE_FILES = [["oak_a", "oak"], ["oak_c", "oak"], ["fir_a", "fir"], ["fir_c", "fir"]];
let treesPromise = null;
let TREES = null;
function loadTrees() {
	if (!treesPromise) {
		const loader = new OBJLoader();
		const mats = {
			oak: new THREE.MeshStandardMaterial({ map: tex("oak_leaf.png", true), normalMap: tex("oak_leaf_n.jpg"), color: 0x8fb04a, alphaTest: 0.35, side: THREE.DoubleSide, roughness: 0.9 }),
			fir: new THREE.MeshStandardMaterial({ map: tex("fir.png", true), normalMap: tex("fir_n.jpg"), color: 0x6f9a55, alphaTest: 0.35, side: THREE.DoubleSide, roughness: 0.9 }),
		};
		treesPromise = Promise.all(TREE_FILES.map(([n]) => loader.loadAsync(BASE_URL + "env/" + n + ".obj"))).then((objs) => {
			objs.forEach((o, i) => o.traverse((m) => { if (m.isMesh) m.material = mats[TREE_FILES[i][1]]; }));
			return objs;
		});
	}
	return treesPromise;
}

// a ring of trees around the scene, the same every time
function forest() {
	const g = new THREE.Group();
	let s = 1337;
	const rnd = () => { s = (s * 16807) % 2147483647; return s / 2147483647; };
	for (let i = 0; i < 46; i++) {
		const kind = rnd() < 0.55 ? (rnd() < 0.5 ? 0 : 1) : (rnd() < 0.5 ? 2 : 3);
		const a = rnd() * Math.PI * 2;
		const r = 32 + Math.pow(rnd(), 0.7) * 120;
		const tree = TREES[kind].clone(true);
		const sc = (kind < 2 ? 0.85 : 0.7) + rnd() * 0.35;
		tree.scale.setScalar(sc);
		tree.rotation.y = rnd() * Math.PI * 2;
		tree.position.set(Math.cos(a) * r, -0.12, Math.sin(a) * r);
		g.add(tree);
	}
	return g;
}

// loads what a spec needs, call and wait for it before buildWorld
export async function preloadWorld(spec) {
	const tasks = [loadPieces(neededPieces(spec)), loadTrees().then((t) => { TREES = t; })];
	if (spec.kind === "wear" && !PLAYER) tasks.push(loadPlayer().then((p) => { PLAYER = p; }));
	await Promise.all(tasks);
	atlasMat();
}

const MATS = {};
function mat(name) {
	if (MATS[name]) return MATS[name];
	const make = {
		shorts: () => new THREE.MeshStandardMaterial({ color: 0x3f4349, roughness: 0.9, side: THREE.DoubleSide }),
	}[name]();
	return (MATS[name] = make);
}

function grass() {
	// the terrain texture covers about 5 m, tiled over 400 m
	const m = new THREE.MeshStandardMaterial({ map: tex("grass_color.jpg", true, 80), normalMap: tex("grass_normal.jpg", false, 80), roughness: 1, color: 0x9fb85a });
	const mesh = new THREE.Mesh(new THREE.PlaneGeometry(400, 400), m);
	mesh.rotation.x = -Math.PI / 2;
	mesh.position.y = -0.12;
	mesh.receiveShadow = true;
	return mesh;
}

// a painted sky dome: horizon haze, a gradient up and soft clouds by day, stars at night
function skyTexture(day) {
	const W = 1024, H = 512;
	const c = document.createElement("canvas");
	c.width = W;
	c.height = H;
	const g = c.getContext("2d");
	const grad = g.createLinearGradient(0, 0, 0, H);
	if (day) { grad.addColorStop(0, "#2f67b3"); grad.addColorStop(0.3, "#5b93d0"); grad.addColorStop(0.46, "#a9cbe6"); grad.addColorStop(0.5, "#dce8ee"); grad.addColorStop(1, "#dce8ee"); }
	else { grad.addColorStop(0, "#02040a"); grad.addColorStop(0.35, "#070c18"); grad.addColorStop(0.49, "#1a2233"); grad.addColorStop(0.5, "#1a2233"); grad.addColorStop(1, "#1a2233"); }
	g.fillStyle = grad;
	g.fillRect(0, 0, W, H);
	let s = 99;
	const rnd = () => { s = (s * 16807) % 2147483647; return s / 2147483647; };
	if (day) {
		for (let i = 0; i < 70; i++) {
			const x = rnd() * W, y = H * (0.2 + rnd() * 0.26), w = 60 + rnd() * 160, h = 10 + rnd() * 26;
			for (const dx of [-W, 0, W]) {
				const rg = g.createRadialGradient(x + dx, y, 0, x + dx, y, w);
				rg.addColorStop(0, "rgba(255,255,255,0.42)");
				rg.addColorStop(1, "rgba(255,255,255,0)");
				g.save();
				g.translate(x + dx, y);
				g.scale(1, h / w);
				g.translate(-(x + dx), -y);
				g.fillStyle = rg;
				g.beginPath();
				g.arc(x + dx, y, w, 0, Math.PI * 2);
				g.fill();
				g.restore();
			}
		}
	} else {
		for (let i = 0; i < 260; i++) {
			g.fillStyle = "rgba(255,255,255," + (0.25 + rnd() * 0.6) + ")";
			const r = rnd() < 0.9 ? 0.8 : 1.6;
			g.fillRect(rnd() * W, rnd() * H * 0.48, r, r);
		}
	}
	const t = new THREE.CanvasTexture(c);
	t.colorSpace = THREE.SRGBColorSpace;
	return t;
}

function skyDome(day) {
	const m = new THREE.Mesh(
		new THREE.SphereGeometry(350, 48, 24),
		new THREE.MeshBasicMaterial({ map: skyTexture(day), side: THREE.BackSide, fog: false, depthWrite: false, toneMapped: false })
	);
	m.renderOrder = -10;
	m.name = "sky";
	return m;
}

// front-to-back extent of a body part at the height window [y0, y1] within |x| < w: the trunk without arms and feet
function trunkZ(part, [y0, y1, w]) {
	let lo = Infinity, hi = -Infinity;
	const v = new THREE.Vector3();
	part.updateMatrixWorld(true);
	part.traverse((m) => {
		if (!m.isMesh) return;
		const p = m.geometry.attributes.position;
		for (let i = 0; i < p.count; i++) {
			v.fromBufferAttribute(p, i).applyMatrix4(m.matrixWorld);
			if (v.y >= y0 && v.y <= y1 && Math.abs(v.x) < w) { lo = Math.min(lo, v.z); hi = Math.max(hi, v.z); }
		}
	});
	return [lo, hi];
}

// The garment's sleeves hang lower and less far forward than the body's arms, so they stick out behind the arm as flaps. Measure
// how far apart the two are at the cuff and bend the garment's sleeves (both sides) onto the arm, fading in from the shoulder.
function fitSleeves(torso, model) {
	const v = new THREE.Vector3();
	const mean = (root, a0, a1) => {
		let n = 0, y = 0, z = 0;
		root.updateMatrixWorld(true);
		root.traverse((m) => {
			if (!m.isMesh) return;
			const p = m.geometry.attributes.position;
			for (let i = 0; i < p.count; i++) {
				v.fromBufferAttribute(p, i).applyMatrix4(m.matrixWorld);
				const a = Math.abs(v.x);
				if (a >= a0 && a <= a1) { n++; y += v.y; z += v.z; }
			}
		});
		return n > 8 ? { y: y / n, z: z / n } : null;
	};
	const b = mean(torso, 0.42, 0.52), g = mean(model, 0.42, 0.52);
	if (!b || !g) return;
	const clamp = (x) => Math.max(-0.2, Math.min(0.2, x));
	const dy = clamp(b.y - g.y), dz = clamp(b.z - g.z);
	const inv = new THREE.Matrix4();
	model.traverse((m) => {
		if (!m.isMesh) return;
		m.geometry = m.geometry.clone(); // geometry is shared between clones of the model
		const p = m.geometry.attributes.position;
		inv.copy(m.matrixWorld).invert();
		for (let i = 0; i < p.count; i++) {
			v.fromBufferAttribute(p, i).applyMatrix4(m.matrixWorld);
			const t = Math.min(1, Math.max(0, (Math.abs(v.x) - 0.2) / 0.3));
			const s = t * t * (3 - 2 * t);
			v.y += dy * s;
			v.z += dz * s;
			v.applyMatrix4(inv);
			p.setXYZ(i, v.x, v.y, v.z);
		}
		p.needsUpdate = true;
		m.geometry.computeBoundingBox();
		m.geometry.computeBoundingSphere();
	});
}

// ---- the scenes ----
// buildWorld sets the model down, in metres, y up, facing +Z (the camera looks from +Z so an item's face is what you see). The
// scenery is real building pieces: floors are 3 m tiles (top at y = 0.1), walls 3 m wide and high.
export function buildWorld(spec, model, helpers) {
	const { undoOrientation, bounds } = helpers;
	const T = tierOf(spec);
	const group = new THREE.Group();
	const holder = new THREE.Group();
	const sun = new THREE.DirectionalLight(0xfff0d8, 2.6);
	sun.position.set(7, 11, 9);
	sun.castShadow = true;
	sun.shadow.mapSize.set(2048, 2048);
	Object.assign(sun.shadow.camera, { left: -9, right: 9, top: 9, bottom: -9, near: 1, far: 60 });
	sun.shadow.bias = -0.0004;
	const hemi = new THREE.HemisphereLight(0xcfe3ff, 0x59603c, 0.9);
	const lamp = new THREE.PointLight(0xffb26b, 0, 24, 1.5); // a lantern, switched on at night
	const fog = new THREE.Fog(0xdce8ee, 40, 300);
	const sky = skyDome(true);
	group.add(sun, hemi, lamp, holder, grass(), forest(), sky);
	const cam = { pos: [3.2, 2, 5.5], target: [0, 1, 0] };
	undoOrientation(!!spec.raw);
	holder.add(model);
	const place = (fn) => { holder.position.set(0, 0, 0); holder.rotation.set(0, 0, 0); holder.updateMatrixWorld(true); fn(bounds(model)); };
	const tiles = (xs, zs, y = 0, name = T + ".floor") => { for (const x of xs) for (const z of zs) group.add(piece(name, x, y, z)); };
	// A wall has a rough stone face and a smooth ("soft") one, the soft one is raw +X. (ix, iz) is the way the soft face looks: into
	// the room, so the rough stone is what you see from outside
	const wall = (x, z, ix, iz, name = T + ".wall", y = 0.1) => group.add(piece(name, x, y, z, Math.atan2(-iz, ix)));

	switch (spec.kind) {
		case "wear": {
			group.add(piece("stone.floor", 0, -0.1, 0));
			const body = PLAYER.clone(true);
			// the underwear stays under legwear: ripped or cut-out trousers show it, not bare skin
			// the game hides the body under clothes, here the legs are slimmed so they stay inside flat trouser panels
			if (spec.slot === "torso") body.getObjectByName("torso").scale.set(0.96, 1, 0.85);
			if (spec.slot === "legs") for (const n of ["shorts", "legs"]) body.getObjectByName(n).scale.set(0.93, 1, 0.8);
			group.add(body);
			group.updateMatrixWorld(true);
			place((b) => {
				const c = b.getCenter(new THREE.Vector3());
				let dy = 0;
				if (spec.top !== undefined) dy = spec.top - b.max.y;
				else if (spec.bottom !== undefined) dy = spec.bottom - b.min.y;
				else if (spec.cy !== undefined) dy = spec.cy - c.y;
				// clothing meshes are made around the body part they cover, so centre them on that part of this body (front to back too)
				const part = body.getObjectByName({ torso: "torso", legs: "legs", head: "head", feet: "feet" }[spec.slot]);
				const pc = part ? new THREE.Box3().setFromObject(part).getCenter(new THREE.Vector3()) : null;
				const dz = spec.slot === "back" ? -0.17 - c.z : spec.slot === "hands" ? 0.33 - c.z : (pc ? pc.z : 0) - c.z;
				// the garment is thinner front to back than the body it goes over: stretch it over the trunk so the back is not inside the body
				let sz = 1, zc = pc ? pc.z : 0;
				const win = { torso: [1.1, 1.45, 0.18], legs: [0.85, 1.05, 0.3] }[spec.slot];
				if (part && win) {
					const [z0, z1] = trunkZ(part, win);
					sz = 1.15; // a bit thicker than the mesh so no body skin comes through
					zc = (z0 + z1) / 2;
				}
				if (spec.slot === "hands") {
					// gloves are made as a pair around the middle of the two hands
					const hc = new THREE.Box3().setFromObject(body.getObjectByName("hands")).getCenter(new THREE.Vector3());
					holder.position.set(hc.x - c.x, hc.y - c.y, hc.z - c.z);
					return;
				}
				holder.scale.z = sz;
				holder.position.set(-c.x, dy, zc - c.z * sz);
			});
			if (spec.slot === "torso") { holder.updateMatrixWorld(true); fitSleeves(body.getObjectByName("torso"), model); }
			const head = spec.slot === "head";
			const feet = spec.slot === "feet";
			cam.target = head ? [0, 1.6, 0] : feet ? [0, 0.35, 0] : spec.slot === "legs" ? [0, 0.7, 0] : [0, 1.15, 0];
			cam.pos = head ? [0.9, 1.8, 1.55] : feet ? [1.2, 0.8, 1.9] : spec.slot === "back" ? [-1.8, 1.7, -3.2] : [1.6, 1.5, 3.2];
			break;
		}
		case "wall": {
			// the front of a stone base: the wall with the item set into it, a floor in front and behind
			const single = spec.open[1] - spec.open[0] < 1.5;
			tiles([-3, 0, 3], [-3, 0, 3]);
			wall(-3, -1.5, 0, -1);
			wall(3, -1.5, 0, -1);
			group.add(piece(single ? T + ".doorway" : "wallframe.stone", 0, 0.1, -1.5, Math.PI / 2));
			for (const x of [-3, 0, 3]) wall(x, -4.5, 0, 1);
			wall(-4.5, -3, 1, 0);
			wall(4.5, -3, -1, 0);
			holder.position.set(single ? 0.6 : 0, 0.1, -1.5);
			if (single) {
				// the doorway opening is 2.2 m tall, stretch the door to the top of it so no gap is left above
				place((b) => {
					const s = 2.2 / (b.max.y - b.min.y);
					holder.scale.set(1, s, 1);
					holder.position.set(0.6, 0.1 - b.min.y * s, -1.5);
				});
			}
			cam.target = [0, 1.4, -1.5];
			cam.pos = [3.6, 1.9, 6.8];
			break;
		}
		case "floor": {
			// the floor frame in the deck of the first floor, a stone room under it so the ladder lands on a floor
			const y = 3, top = spec.armored ? 3.01 : 3.05;
			group.add(piece(neededPieces(spec)[0], 0, y, 0, 0));
			if (spec.tri) {
				// the triangle points along +Z in the game, the item's own point is along +X
				holder.rotation.y = -Math.PI / 2;
				tiles([0], [1.3]);
				wall(0, 0, 0, 1);
				wall(-0.75, 1.3, 0.75, -0.43);
				wall(0.75, 1.3, -0.75, -0.43);
				cam.target = [0, 1.9, 1.1];
				cam.pos = [3.8, 7, 6.3];
			} else {
				tiles([0], [0]);
				tiles([-3, 3], [-3, 0, 3], top);
				tiles([0], [-3, 3], top);
				wall(-1.5, 0, 1, 0);
				wall(1.5, 0, -1, 0);
				wall(0, -1.5, 0, 1);
				wall(0, 1.5, 0, -1);
				cam.target = [0, 1.8, 0];
				cam.pos = [3.6, 7, 5.6];
			}
			// the triangle item's origin is 0.68 behind its base, its base goes on the base of the floor frame
			holder.position.set(0, y, spec.tri ? 0.65 : 0);
			sun.position.set(4, 14, 7);
			break;
		}
		case "bench": // dropped on the floor of a base: a laid down weapon or tool
		case "room":
		default: {
			// inside a stone base: floor, back and side walls, a doorway on the right
			tiles([-3, 0, 3], [-3, 0, 3]);
			for (const x of [-3, 0, 3]) wall(x, -4.5, 0, 1);
			for (const z of [-3, 0, 3]) wall(-4.5, z, 1, 0);
			for (const z of [-3, 3]) wall(4.5, z, -1, 0);
			wall(4.5, 0, -1, 0, "stone.doorway");
			const drop = spec.kind === "bench";
			place((b) => {
				const size = b.getSize(new THREE.Vector3());
				// side-view items (thin in depth) are laid on their side like a dropped item
				if (drop && !spec.upright && !spec.noflat && size.z < size.y * 0.9) holder.rotation.x = -Math.PI / 2;
				holder.updateMatrixWorld(true);
				const b2 = bounds(model);
				const c = b2.getCenter(new THREE.Vector3());
				holder.position.set(-c.x, 0.1 - b2.min.y, -c.z - (drop ? 0.4 : 1.2));
			});
			const size = bounds(model).getSize(new THREE.Vector3());
			const big = Math.max(size.x, size.y, size.z);
			const zc = drop ? -0.4 : -1.2;
			if (drop) {
				const d = Math.max(big * 1.5, 0.7);
				cam.target = [0, 0.15, zc];
				cam.pos = [d * 0.35, 0.25 + d * 0.85, zc + d * 0.95];
			} else {
				const d = Math.max(big * 2.1, 2.8);
				cam.target = [0, Math.min(size.y, 2) * 0.5 + 0.1, zc];
				cam.pos = [d * 0.55, Math.min(size.y, 2) * 0.6 + big * 0.6 + 0.6, zc + d];
			}
		}
	}
	return { group, holder, camera: cam, lights: { sun, hemi, lamp }, fog, sky, spec };
}

// day / night for the whole scene, returns the sky texture the viewer uses as the background
export function setWorldDay(world, day) {
	const { sun, hemi, lamp } = world.lights;
	sun.intensity = day ? 2.8 : 0.3;
	sun.color.set(day ? 0xfff0d8 : 0x8fa8ff);
	hemi.intensity = day ? 0.9 : 0.16;
	lamp.intensity = day ? 0 : 34;
	lamp.position.set(world.camera.target[0] + 1.2, (world.camera.target[1] || 1) + 2.2, world.camera.target[2] + 2.2);
	world.fog.color.set(day ? 0xdce8ee : 0x1a2233);
	if (world.sky.material.map) world.sky.material.map.dispose();
	world.sky.material.map = skyTexture(day);
	world.sky.material.needsUpdate = true;
	return world.fog.color.clone(); // the scene background behind the dome
}
