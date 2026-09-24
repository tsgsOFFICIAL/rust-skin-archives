// three.js model viewer, app.js uses it through window.Viewer3DReady (a promise)
//
// the lighting is a studio environment built in code: a gradient sky and a few softboxes.
// chrome only looks like metal when there's something bright to reflect on every side,
// in a dark environment it just goes black
import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";

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
}

// loads the glb into the canvas, resolves once it's on screen and rejects if it can't be loaded
async function open(canvas, url, day = true) {
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

	const s = { scene, camera, controls, model: null, raf: 0, resizeObserver: null, canvas, day };
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

	// frame the model
	const box = new THREE.Box3().setFromObject(model);
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

	const loop = () => {
		s.raf = requestAnimationFrame(loop);
		controls.update();
		r.render(scene, camera);
	};
	loop();
}

window.__viewer3dResolve({ open, close, setDay });
