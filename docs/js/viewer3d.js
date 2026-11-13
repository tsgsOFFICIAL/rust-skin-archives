// three.js model viewer, app.js uses it through window.Viewer3DReady (a promise)
//
// the lighting is a studio environment built in code: a gradient sky and a few softboxes.
// chrome only looks like metal when there's something bright to reflect on every side,
// in a dark environment it just goes black
import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { buildWorld, setWorldDay, specFor, preloadWorld } from "./world3d.js";

const DAY_ENV = 1.0; // overall brightness, higher = brighter metal
const NIGHT_ENV = 0.03;
const DAY_EMISSIVE = 1.5;
const NIGHT_EMISSIVE = 3.0;

let renderer = null;
let envTexture = null;
let state = null; // { scene, camera, controls, model, raf, resizeObserver, canvas }

function buildStudioEnvironment() {
	const scene = new THREE.Scene();
	const R = 40;
	const geo = new THREE.SphereGeometry(R, 48, 32);
	const pos = geo.attributes.position;
	const top = new THREE.Color(1, 1, 1).multiplyScalar(0.9);
	const horizon = new THREE.Color(1, 1, 1).multiplyScalar(0.45);
	const bottom = new THREE.Color(1, 1, 1).multiplyScalar(0.12);
	const colors = [];
	for (let i = 0; i < pos.count; i++) {
		const y = pos.getY(i) / R;
		const c = y >= 0 ? horizon.clone().lerp(top, y) : horizon.clone().lerp(bottom, -y);
		colors.push(c.r, c.g, c.b);
	}
	geo.setAttribute("color", new THREE.Float32BufferAttribute(colors, 3));
	scene.add(new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ vertexColors: true, side: THREE.BackSide })));

	const softbox = (w, h, x, y, z, intensity) => {
		const m = new THREE.Mesh(
			new THREE.PlaneGeometry(w, h),
			new THREE.MeshBasicMaterial({ color: new THREE.Color(1, 1, 1).multiplyScalar(intensity), side: THREE.DoubleSide })
		);
		m.position.set(x, y, z);
		m.lookAt(0, 0, 0);
		scene.add(m);
	};
	softbox(18, 8, -20, 12, 18, 7);
	softbox(18, 8, 22, 10, 16, 7);
	softbox(10, 20, 0, 8, -25, 7);
	return scene;
}

function ensureRenderer(canvas) {
	if (renderer && renderer.domElement === canvas) return renderer;
	renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false });
	renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
	renderer.outputColorSpace = THREE.SRGBColorSpace;
	renderer.toneMapping = THREE.ACESFilmicToneMapping;
	renderer.toneMappingExposure = 1.0;
	renderer.shadowMap.enabled = true; // only used by the in-world view, the studio has no lights
	renderer.shadowMap.type = THREE.PCFSoftShadowMap;
	const pmrem = new THREE.PMREMGenerator(renderer);
	envTexture = pmrem.fromScene(buildStudioEnvironment(), 0.04).texture;
	pmrem.dispose();
	return renderer;
}

function disposeModel(model) {
	model.traverse((o) => {
		if (o.geometry) o.geometry.dispose();
		const mats = Array.isArray(o.material) ? o.material : o.material ? [o.material] : [];
		for (const m of mats) {
			for (const key of Object.keys(m)) if (m[key] && m[key].isTexture) m[key].dispose();
			m.dispose();
		}
	});
}

// ---- open / close animation (ladder hatches) ----
// the pipeline writes each moving piece as a node with extras { pivot, axis, rot: [closed, open] degrees, move: [closed, open],
// win: [start, end] }: the piece turns around the line through pivot along axis and is then moved, both relative to the
// geometry as stored. win is the part of the 0 (closed) .. 1 (open) timeline it moves in, so closing is the same
// sequence backwards (the ladder folds up before the lower lid swings shut and the lid last)
const ANIM_SECONDS = 1.8;
const _m = new THREE.Matrix4();
const _t1 = new THREE.Matrix4();
const _t2 = new THREE.Matrix4();
const _axis = new THREE.Vector3();

function collectAnimNodes(model) {
	const nodes = [];
	model.traverse((o) => {
		const u = o.userData;
		if (u && Array.isArray(u.pivot) && Array.isArray(u.rot)) {
			o.updateMatrix();
			nodes.push({ node: o, base: o.matrix.clone(), a: u });
		}
	});
	return nodes;
}

// puts every moving piece where it is at t (0 closed .. 1 open)
function applyPose(nodes, t) {
	for (const { node, base, a } of nodes) {
		const [s, e] = a.win || [0, 1];
		let k = e > s ? Math.min(Math.max((t - s) / (e - s), 0), 1) : t >= e ? 1 : 0;
		k = k * k * (3 - 2 * k); // smoothstep, the pieces speed up and settle like a real hinge
		const deg = a.rot[0] + (a.rot[1] - a.rot[0]) * k;
		const mv = a.move || [[0, 0, 0], [0, 0, 0]];
		const dx = mv[0][0] + (mv[1][0] - mv[0][0]) * k;
		const dy = mv[0][1] + (mv[1][1] - mv[0][1]) * k;
		const dz = mv[0][2] + (mv[1][2] - mv[0][2]) * k;
		_axis.set(a.axis[0], a.axis[1], a.axis[2]).normalize();
		// move * translate(pivot) * rotate * translate(-pivot), then the piece's own transform from the file
		_m.makeTranslation(-a.pivot[0], -a.pivot[1], -a.pivot[2]);
		_t1.makeRotationAxis(_axis, THREE.MathUtils.degToRad(deg));
		_m.premultiply(_t1);
		_t2.makeTranslation(a.pivot[0] + dx, a.pivot[1] + dy, a.pivot[2] + dz);
		_m.premultiply(_t2);
		_m.premultiply(base);
		_m.decompose(node.position, node.quaternion, node.scale);
		node.updateMatrix();
	}
}

function hasAnimation() {
	return !!(state && state.anim && state.anim.length);
}

function isOpen() {
	return !!(state && state.animTarget === 1);
}

// opens (true) or closes (false) the model with the animation, or toggles when called with no argument. Returns the new state
function setOpen(open) {
	if (!hasAnimation()) return false;
	state.animTarget = open === undefined ? (state.animTarget === 1 ? 0 : 1) : open ? 1 : 0;
	return state.animTarget === 1;
}

// ---- in the world ----
// The converter turns every skin to face the camera (a floor hatch is stood up like a card), the node rotation it wrote is
// what has to go for items that belong flat on the floor. The rotation sits on the top level nodes of the model.
function undoOrientation(raw) {
	if (!state || !state.model) return;
	for (const n of state.model.children) {
		if (!n.userData._q0) n.userData._q0 = n.quaternion.clone();
		if (raw) n.quaternion.identity();
		else n.quaternion.copy(n.userData._q0);
		n.updateMatrix();
	}
}

function boundsOf(model) {
	model.updateMatrixWorld(true);
	return new THREE.Box3().setFromObject(model);
}

function hasWorld() {
	return !!state && !!state.model;
}

function isWorld() {
	return !!state && state.worldOn;
}

// turns the in-world view on or off, returns whether it's on. The studio camera comes back when it's turned off
async function setWorld(on) {
	if (!state || !state.model) return false;
	const s = state;
	if (on === undefined) on = !s.worldOn;
	if (on === s.worldOn) return on;
	if (on) {
		s.studio = { pos: s.camera.position.clone(), target: s.controls.target.clone(), min: s.controls.minDistance, max: s.controls.maxDistance, bg: s.scene.background };
		s.scene.remove(s.model);
		const spec = specFor(s.shortName);
		await preloadWorld(spec);
		if (state !== s) return false; // closed while the scenery was loading
		s.world = buildWorld(spec, s.model, { undoOrientation, bounds: boundsOf });
		s.model.traverse((o) => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = true; } });
		s.scene.add(s.world.group);
		s.scene.fog = s.world.fog;
		s.scene.background = setWorldDay(s.world, s.day);
		const c = s.world.camera;
		s.camera.position.set(...c.pos);
		s.controls.target.set(...c.target);
		s.controls.minDistance = 0.4;
		s.controls.maxDistance = 40;
		s.camera.far = 400;
		s.camera.near = 0.05;
		s.camera.updateProjectionMatrix();
	} else {
		s.scene.remove(s.world.group);
		s.scene.fog = null;
		s.world.group.remove(s.world.holder);
		s.world.holder.remove(s.model);
		s.model.traverse((o) => { if (o.isMesh) { o.castShadow = false; o.receiveShadow = false; } });
		undoOrientation(false);
		s.scene.add(s.model);
		s.world = null;
		if (s.scene.background && s.scene.background.isTexture) s.scene.background.dispose();
		s.scene.background = s.studio.bg;
		s.camera.position.copy(s.studio.pos);
		s.controls.target.copy(s.studio.target);
		s.controls.minDistance = s.studio.min;
		s.controls.maxDistance = s.studio.max;
		s.camera.near = Math.max(s.controls.minDistance * 0.03, 0.001);
		s.camera.far = s.studio.max + 100;
		s.camera.updateProjectionMatrix();
	}
	s.worldOn = on;
	setDay(s.day); // envMapIntensity of the new scenery, the sky
	s.controls.update();
	return on;
}

function close() {
	if (!state) return;
	cancelAnimationFrame(state.raf);
	state.resizeObserver.disconnect();
	state.controls.dispose();
	if (state.model) disposeModel(state.model);
	state = null;
}

function setDay(day) {
	if (!state) return;
	state.day = day;
	state.scene.traverse((o) => {
		const mats = Array.isArray(o.material) ? o.material : o.material ? [o.material] : [];
		for (const m of mats) {
			m.envMapIntensity = day ? DAY_ENV : NIGHT_ENV;
			if (m.emissiveMap) m.emissiveIntensity = day ? DAY_EMISSIVE : NIGHT_EMISSIVE;
		}
	});
	if (state.worldOn && state.world) {
		if (state.scene.background && state.scene.background.isTexture) state.scene.background.dispose();
		state.scene.background = setWorldDay(state.world, day);
	}
}

// loads the glb into the canvas, resolves once it's on screen and rejects if it can't be loaded
// meta.shortName (the game's item name, "door.hinged.metal") decides where the skin goes in the world view
async function open(canvas, url, day = true, meta = {}) {
	close();
	const r = ensureRenderer(canvas);

	const scene = new THREE.Scene();
	scene.background = new THREE.Color(0x0d0d0d);
	scene.environment = envTexture;

	const camera = new THREE.PerspectiveCamera(35, 1, 0.01, 200);
	const controls = new OrbitControls(camera, canvas);
	controls.enableDamping = true;
	controls.dampingFactor = 0.12;
	controls.screenSpacePanning = true;

	const s = { scene, camera, controls, model: null, raf: 0, resizeObserver: null, canvas, day, shortName: meta.shortName || "", world: null, worldOn: false, studio: null };
	state = s;

	const resize = () => {
		const w = Math.max(canvas.clientWidth, 1);
		const h = Math.max(canvas.clientHeight, 1);
		r.setSize(w, h, false);
		camera.aspect = w / h;
		camera.updateProjectionMatrix();
	};
	s.resizeObserver = new ResizeObserver(resize);
	s.resizeObserver.observe(canvas);
	resize();

	const gltf = await new GLTFLoader().loadAsync(url);
	if (state !== s) { disposeModel(gltf.scene); return; } // closed or replaced while loading
	const model = gltf.scene;
	const maxAniso = r.capabilities.getMaxAnisotropy();
	model.traverse((o) => {
		const mats = Array.isArray(o.material) ? o.material : o.material ? [o.material] : [];
		for (const m of mats) for (const key of Object.keys(m)) if (m[key] && m[key].isTexture) m[key].anisotropy = maxAniso;
	});
	scene.add(model);
	s.model = model;
	setDay(day);

	// models with moving pieces start closed and are framed so they fit open as well
	s.anim = collectAnimNodes(model);
	s.animT = 0;
	s.animTarget = 0;
	const box = new THREE.Box3();
	if (s.anim.length) {
		applyPose(s.anim, 1);
		model.updateMatrixWorld(true);
		box.expandByObject(model);
		applyPose(s.anim, 0);
		model.updateMatrixWorld(true);
	}

	// frame the model
	box.expandByObject(model);
	const sphere = box.getBoundingSphere(new THREE.Sphere());
	const vFov = THREE.MathUtils.degToRad(camera.fov);
	const hFov = 2 * Math.atan(Math.tan(vFov / 2) * camera.aspect);
	const size = box.getSize(new THREE.Vector3());
	// fit width and height, a bounding sphere is way too loose for long thin things like guns
	const dist = Math.max(size.y / 2 / Math.tan(vFov / 2), size.x / 2 / Math.tan(hFov / 2)) * 1.2 + size.z / 2;
	camera.position.copy(sphere.center).add(new THREE.Vector3(0, sphere.radius * 0.15, dist));
	camera.near = Math.max(sphere.radius * 0.01, 0.001);
	camera.far = dist + sphere.radius * 20;
	camera.updateProjectionMatrix();
	controls.target.copy(sphere.center);
	controls.minDistance = sphere.radius * 0.3;
	controls.maxDistance = dist * 4;
	controls.update();

	let last = performance.now();
	const loop = () => {
		s.raf = requestAnimationFrame(loop);
		const now = performance.now();
		const dt = Math.min((now - last) / 1000, 0.1);
		last = now;
		if (s.anim.length && s.animT !== s.animTarget) {
			const step = dt / ANIM_SECONDS;
			s.animT = s.animTarget > s.animT ? Math.min(s.animT + step, 1) : Math.max(s.animT - step, 0);
			applyPose(s.anim, s.animT);
		}
		controls.update();
		r.render(scene, camera);
	};
	loop();
}

window.__viewer3dResolve({ open, close, setDay, hasAnimation, isOpen, setOpen, hasWorld, isWorld, setWorld });
