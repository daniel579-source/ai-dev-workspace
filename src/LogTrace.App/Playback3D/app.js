// Layout 回放 3D（ADR-0017）：依 C# 傳來的場景資料畫出 Layout 與目前時刻的狀態，回報載入完成、錯誤與點選的元素。
// 不推導任何狀態（世界模型在 C# 引擎，ADR-0005）。座標：Layout 的 (x, y) 對應 three.js 的 (x, z)，高度為 y；單位公尺。
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/OrbitControls.js';
import { CSS2DRenderer, CSS2DObject } from 'three/addons/CSS2DRenderer.js';

const host = window.chrome && window.chrome.webview;
const post = (msg) => { if (host) host.postMessage(msg); };

window.addEventListener('error', (e) => post({ kind: 'error', message: `網頁錯誤：${e.message}` }));
window.addEventListener('unhandledrejection', (e) => post({ kind: 'error', message: `網頁錯誤：${e.reason}` }));

// 圖例的顏色（與 2D 相同，ADR-0008 第 6 點）
const LOOK = {
  unknown: 0xE5E7EB, empty: 0xFFFFFF, occupied: 0xBFDBFE, executing: 0xFDE68A, alarm: 0xFCA5A5,
  auxiliary: 0x94A3B8, doorOpen: 0x86EFAC, doorClosed: 0xF87171,
};
const COLOR = {
  frame: 0x64748B, beam: 0xF59E0B, cargo: 0x93C5FD, rail: 0x94A3B8, conveyor: 0x94A3B8, blocked: 0xDC2626,
  slab: 0xE2E8F0, shaft: 0x93C5FD, selected: 0x2563EB, presenceOn: 0x22C55E, presenceOff: 0x334155, presenceUnknown: 0xCBD5E1,
};

const view = document.getElementById('view');
let renderer;
try {
  renderer = new THREE.WebGLRenderer({ antialias: true });
  if (!renderer.getContext()) throw new Error('沒有 WebGL context');
} catch (err) {
  post({ kind: 'error', message: `WebGL 無法使用（可能是顯示卡驅動程式的問題）：${err && err.message ? err.message : err}` });
  throw err;
}
renderer.setPixelRatio(window.devicePixelRatio);
renderer.setClearColor(0xF8FAFC);
view.appendChild(renderer.domElement);

const labels = new CSS2DRenderer();
labels.domElement.id = 'labels';
view.appendChild(labels.domElement);

const scene = new THREE.Scene();
scene.add(new THREE.HemisphereLight(0xFFFFFF, 0x94A3B8, 2.2));
const sun = new THREE.DirectionalLight(0xFFFFFF, 1.6);
sun.position.set(30, 60, 20);
scene.add(sun);

const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 5000);
const controls = new OrbitControls(camera, renderer.domElement);
// 左鍵旋轉、右鍵平移、滾輪縮放（#113 使用者決定）
controls.mouseButtons = { LEFT: THREE.MOUSE.ROTATE, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.PAN };
controls.enableDamping = false;
controls.maxPolarAngle = Math.PI / 2 - 0.02;

let root = new THREE.Group();
scene.add(root);

/** 元素 id → { data, group, body, parts, label, ... } */
const items = new Map();
let floors = new Map(); // 樓層名稱 → { elevation, height }
let defaultView = null;
let selectionBox = null;
let selectedColumnBox = null;

const unitBox = new THREE.BoxGeometry(1, 1, 1);
const materials = [];
function mat(color, opts = {}) {
  const m = new THREE.MeshLambertMaterial({ color, ...opts });
  materials.push(m);
  return m;
}

function elevationOf(floor) {
  const f = floors.get(floor);
  return f ? f.elevation : 0;
}

/** Layout 元素（左上角、寬、深、順時針角度）放到場景：中心點，繞垂直軸旋轉。 */
function place(group, e, elevation) {
  group.position.set(e.x + e.w / 2, elevation, e.y + e.h / 2);
  group.rotation.y = -THREE.MathUtils.degToRad(e.rotation || 0);
}

function box(w, h, d, material, x = 0, y = 0, z = 0) {
  const m = new THREE.Mesh(unitBox, material);
  m.scale.set(Math.max(w, 0.01), Math.max(h, 0.01), Math.max(d, 0.01));
  m.position.set(x, y, z);
  return m;
}

function makeLabel(e, aux) {
  const div = document.createElement('button');
  div.type = 'button';
  div.className = 'label' + (aux ? ' aux' : '');
  div.setAttribute('aria-label', e.name); // 固定為元素名稱（UI 自動化以名稱找元素）
  const name = document.createElement('span');
  name.className = 'name';
  name.textContent = e.name;
  const summary = document.createElement('span');
  summary.className = 'summary';
  div.append(name, summary);
  div.addEventListener('click', (ev) => { ev.stopPropagation(); pick(e.id, null); });
  div.addEventListener('pointerdown', (ev) => ev.stopPropagation());
  const obj = new CSS2DObject(div);
  obj.center.set(0.5, 1);
  return { obj, div, name, summary };
}

// ---- 各種元素的外觀（簡化的幾何，#113 使用者確認） ----

function buildStation(e, item) {
  item.body = box(e.w, e.height, e.h, mat(LOOK.unknown), 0, e.height / 2, 0);
  item.group.add(item.body);
  item.light = new THREE.Mesh(new THREE.SphereGeometry(Math.min(0.18, Math.min(e.w, e.h) / 4), 12, 8), mat(COLOR.presenceUnknown, { transparent: true, opacity: 0.6 }));
  item.light.position.set(e.w / 2 - 0.2, e.height + 0.12, -e.h / 2 + 0.2);
  item.group.add(item.light);
  item.cargo = box(Math.min(e.w, 1.1) * 0.8, 0.8, Math.min(e.h, 1.1) * 0.8, mat(COLOR.cargo), 0, e.height + 0.4, 0);
  item.cargo.visible = false;
  item.group.add(item.cargo);
}

function buildVehicle(e, item) {
  // 低矮的軌道車：車體加上方的輸送面
  const bodyH = e.height * 0.75;
  item.body = box(e.w, bodyH, e.h, mat(LOOK.unknown, { transparent: true }), 0, bodyH / 2, 0);
  item.group.add(item.body);
  item.top = box(e.w * 0.9, e.height - bodyH, e.h * 0.7, mat(0x475569, { transparent: true }), 0, bodyH + (e.height - bodyH) / 2, 0);
  item.group.add(item.top);
  item.cargo = box(Math.min(e.w, 1.1) * 0.8, 0.8, Math.min(e.h, 1.1) * 0.8, mat(COLOR.cargo, { transparent: true }), 0, e.height + 0.4, 0);
  item.cargo.visible = false;
  item.group.add(item.cargo);
}

function buildRack(e, item) {
  const levels = e.levels || 1;
  const lh = e.levelHeight || 1.2;
  const cols = e.colFrom != null && e.colTo != null && e.colTo >= e.colFrom ? e.colTo - e.colFrom + 1 : 1;
  item.cols = cols;
  const frame = mat(COLOR.frame);
  const beam = mat(COLOR.beam);
  const post = 0.08;
  const posts = [];
  for (let c = 0; c <= cols; c++) {
    const x = -e.w / 2 + (e.w / cols) * c;
    for (const z of [-e.h / 2, e.h / 2]) posts.push(new THREE.Matrix4().compose(new THREE.Vector3(x, e.height / 2, z), new THREE.Quaternion(), new THREE.Vector3(post, e.height, post)));
  }
  const beams = [];
  for (let l = 0; l <= levels; l++) {
    const y = Math.min(l * lh, e.height - 0.04) + 0.04;
    for (const z of [-e.h / 2, e.h / 2]) beams.push(new THREE.Matrix4().compose(new THREE.Vector3(0, y, z), new THREE.Quaternion(), new THREE.Vector3(e.w, 0.08, 0.06)));
  }
  item.group.add(instanced(posts, frame), instanced(beams, beam));
  // 點選用的透明外框（料架的列由點到的位置換算）
  item.body = box(e.w, e.height, e.h, new THREE.MeshBasicMaterial({ visible: false }), 0, e.height / 2, 0);
  item.group.add(item.body);
  item.slots = new THREE.InstancedMesh(unitBox, mat(COLOR.cargo), cols * levels);
  item.slots.count = 0;
  item.group.add(item.slots);
  item.slotSize = { w: e.w / cols, h: e.h, lh };
}

function instanced(matrices, material) {
  const m = new THREE.InstancedMesh(unitBox, material, matrices.length);
  matrices.forEach((x, i) => m.setMatrixAt(i, x));
  m.instanceMatrix.needsUpdate = true;
  return m;
}

function buildElevator(e, item) {
  const served = (e.servedFloors || []).filter((f) => floors.has(f));
  const elevations = served.map((f) => floors.get(f).elevation);
  const bottom = elevations.length ? Math.min(...elevations) : 0;
  const topFloor = served.length ? served.reduce((a, b) => (floors.get(a).elevation > floors.get(b).elevation ? a : b)) : null;
  const top = topFloor ? floors.get(topFloor).elevation + e.height + 0.3 : e.height;
  // 透明的垂直井道跨越服務的樓層
  const shaft = box(e.w, top - bottom, e.h, mat(COLOR.shaft, { transparent: true, opacity: 0.12, depthWrite: false }), 0, (top - bottom) / 2, 0);
  const edges = new THREE.LineSegments(new THREE.EdgesGeometry(unitBox), new THREE.LineBasicMaterial({ color: 0x60A5FA }));
  edges.scale.copy(shaft.scale);
  edges.position.copy(shaft.position);
  item.group.add(shaft, edges);
  item.group.position.y = bottom;
  item.bottom = bottom;
  item.car = new THREE.Group();
  item.body = box(e.w * 0.9, e.height, e.h * 0.9, mat(LOOK.unknown, { transparent: true }), 0, e.height / 2, 0);
  item.cargo = box(Math.min(e.w, 1.1) * 0.7, 0.8, Math.min(e.h, 1.1) * 0.7, mat(COLOR.cargo, { transparent: true }), 0, e.height / 2, 0);
  item.cargo.visible = false;
  item.car.add(item.body, item.cargo);
  item.car.visible = false; // 目前樓層未取得資訊時不畫台車（不顯示猜測值）
  item.group.add(item.car);
  item.labelHeight = top - bottom;
}

function buildConveyor(e, item) {
  const [[x1, y1], [x2, y2]] = e.path;
  const len = Math.hypot(x2 - x1, y2 - y1);
  const elevation = elevationOf(e.floor);
  item.group.position.set((x1 + x2) / 2, elevation, (y1 + y2) / 2);
  item.group.rotation.y = -Math.atan2(y2 - y1, x2 - x1);
  item.body = box(len, e.height, 0.8, mat(COLOR.conveyor, { transparent: true, opacity: 0.85 }), 0, e.height / 2, 0);
  item.group.add(item.body);
  // 方向箭頭：單向在終點，雙向兩端都有
  const arrow = (dir) => {
    const cone = new THREE.Mesh(new THREE.ConeGeometry(0.35, 0.7, 12), mat(0x475569));
    cone.rotation.z = -dir * Math.PI / 2;
    cone.position.set(dir * (len / 2 - 0.35), e.height + 0.15, 0);
    return cone;
  };
  item.group.add(arrow(1));
  if (e.bidirectional) item.group.add(arrow(-1));
}

function buildRail(e, item) {
  const elevation = elevationOf(e.floor);
  const pts = e.path || [];
  const material = mat(COLOR.rail);
  for (let i = 0; i + 1 < pts.length; i++) {
    const [x1, y1] = pts[i];
    const [x2, y2] = pts[i + 1];
    const len = Math.hypot(x2 - x1, y2 - y1);
    if (len < 1e-6) continue;
    const seg = box(len + 0.05, 0.04, 0.3, material, (x1 + x2) / 2, elevation + 0.02, (y1 + y2) / 2);
    seg.rotation.y = -Math.atan2(y2 - y1, x2 - x1);
    item.group.add(seg);
  }
}

function buildElement(e) {
  const item = { data: e, group: new THREE.Group() };
  item.group.userData.id = e.id;
  const elevation = elevationOf(e.floor);
  switch (e.type) {
    case 'station': place(item.group, e, elevation); buildStation(e, item); break;
    case 'vehicle': place(item.group, e, elevation); buildVehicle(e, item); break;
    case 'rack': place(item.group, e, elevation); buildRack(e, item); break;
    case 'elevator': place(item.group, e, 0); buildElevator(e, item); break;
    case 'conveyor': buildConveyor(e, item); break;
    case 'rail': buildRail(e, item); break;
    case 'dock':
      place(item.group, e, elevation);
      item.body = box(e.w, e.height, e.h, mat(LOOK.unknown), 0, e.height / 2, 0);
      item.group.add(item.body);
      break;
    case 'fireDoor':
      place(item.group, e, elevation);
      item.body = box(e.w, e.height, e.h, mat(LOOK.unknown, { transparent: true }), 0, e.height / 2, 0);
      item.group.add(item.body);
      break;
    case 'area':
      place(item.group, e, elevation);
      item.body = box(e.w, 0.02, e.h, mat(LOOK.auxiliary, { transparent: true, opacity: 0.15, depthWrite: false }), 0, 0.03, 0);
      item.group.add(item.body);
      break;
    case 'label':
      place(item.group, e, elevation);
      break;
    default:
      place(item.group, e, elevation);
      item.body = box(e.w || 1, e.height, e.h || 1, mat(LOOK.unknown), 0, e.height / 2, 0);
      item.group.add(item.body);
  }
  if (e.type !== 'rail' && e.type !== 'conveyor') {
    const aux = e.type === 'area' || e.type === 'label';
    item.label = makeLabel(e, aux);
    const parent = e.type === 'elevator' ? item.car : item.group;
    item.label.obj.position.set(0, e.type === 'elevator' ? e.height + 0.2 : (e.type === 'area' ? 0.1 : e.height + 0.25), 0);
    parent.add(item.label.obj);
  }
  root.add(item.group);
  items.set(e.id, item);
}

/** 每層的樓板：所有元素範圍再加邊界，半透明；最低層不透明。 */
function buildFloors(layout, bounds) {
  const margin = 4;
  const w = bounds.max.x - bounds.min.x + margin * 2;
  const d = bounds.max.z - bounds.min.z + margin * 2;
  const cx = (bounds.min.x + bounds.max.x) / 2;
  const cz = (bounds.min.z + bounds.max.z) / 2;
  layout.floors.forEach((f, i) => {
    const slab = box(w, 0.1, d, mat(COLOR.slab, { transparent: i > 0, opacity: i > 0 ? 0.25 : 1, depthWrite: i === 0 }), cx, f.elevation - 0.05, cz);
    slab.userData.floor = f.name;
    root.add(slab);
    if (layout.floors.length > 1) {
      const div = document.createElement('div');
      div.className = 'floor-label';
      div.textContent = f.name;
      const obj = new CSS2DObject(div);
      obj.position.set(bounds.min.x - margin + 0.5, f.elevation + 0.1, bounds.min.z - margin + 0.5);
      obj.center.set(0, 1);
      root.add(obj);
    }
  });
}

function clearScene() {
  for (const item of items.values()) item.label?.obj.removeFromParent();
  root.traverse((o) => { if (o.isCSS2DObject) o.element.remove(); });
  scene.remove(root);
  root = new THREE.Group();
  scene.add(root);
  items.clear();
  materials.length = 0;
  selectionBox = null;
  selectedColumnBox = null;
}

function loadScene(layout) {
  clearScene();
  floors = new Map(layout.floors.map((f) => [f.name, f]));
  for (const e of layout.elements) buildElement(e);
  const bounds = new THREE.Box3();
  for (const item of items.values()) bounds.expandByObject(item.group);
  if (bounds.isEmpty()) bounds.set(new THREE.Vector3(0, 0, 0), new THREE.Vector3(60, 0, 40));
  buildFloors(layout, bounds);
  const top = Math.max(...layout.floors.map((f) => f.elevation + f.height));
  bounds.max.y = Math.max(bounds.max.y, top);
  defaultView = bounds;
  resetView();
  post({ kind: 'loaded', elements: layout.elements.length, floors: layout.floors.length });
}

/** 預設視角：斜上方 45 度，自動縮放到看得到所有樓層（#113 使用者決定）。 */
function resetView() {
  if (!defaultView) return;
  const center = defaultView.getCenter(new THREE.Vector3());
  const radius = Math.max(defaultView.getSize(new THREE.Vector3()).length() / 2, 5);
  const fov = THREE.MathUtils.degToRad(camera.fov);
  const dist = radius / Math.sin(Math.min(fov, fov * camera.aspect) / 2);
  const dir = new THREE.Vector3(Math.SQRT1_2 * 0.5, Math.SQRT1_2, Math.SQRT1_2 * 0.866).normalize(); // 斜上方 45 度，從右前方看
  camera.position.copy(center).addScaledVector(dir, dist);
  camera.near = Math.max(0.1, dist / 1000);
  camera.far = dist * 20;
  camera.updateProjectionMatrix();
  controls.target.copy(center);
  controls.update();
}

/** 相機轉向元素（跟隨，#113）：目標移到元素中心，保持目前的方向，拉近到能看清楚的距離。 */
function focus(id) {
  const item = items.get(id);
  if (!item) return;
  const target = item.car && item.car.visible ? item.car : item.group;
  const b = new THREE.Box3().expandByObject(target);
  if (b.isEmpty()) return;
  const center = b.getCenter(new THREE.Vector3());
  const size = Math.max(b.getSize(new THREE.Vector3()).length(), 4);
  const dir = camera.position.clone().sub(controls.target).normalize();
  controls.target.copy(center);
  camera.position.copy(center).addScaledVector(dir, Math.max(size * 3, 15));
  controls.update();
}

// ---- 目前時刻的狀態 ----

function setColor(mesh, color, opacity = 1) {
  if (!mesh) return;
  mesh.material.color.setHex(color);
  mesh.material.opacity = opacity;
  mesh.material.transparent = opacity < 1 || mesh.material.transparent;
}

function applyState(state) {
  let selected = null;
  for (const s of state.elements) {
    const item = items.get(s.id);
    if (!item) continue;
    const e = item.data;
    const color = LOOK[s.look] ?? LOOK.unknown;
    const faded = s.estimated ? 0.5 : 1;
    switch (e.type) {
      case 'station':
        setColor(item.body, color);
        setColor(item.light, s.presence === 'on' ? COLOR.presenceOn : s.presence === 'off' ? COLOR.presenceOff : COLOR.presenceUnknown, s.presence === 'unknown' ? 0.5 : 1);
        item.cargo.visible = !!s.loaded;
        break;
      case 'vehicle':
        setColor(item.body, color, faded);
        setColor(item.top, 0x475569, faded);
        item.cargo.visible = !!s.loaded;
        if (s.x != null && s.y != null) item.group.position.set(s.x, item.group.position.y, s.y);
        else item.group.position.set(e.x + e.w / 2, item.group.position.y, e.y + e.h / 2);
        break;
      case 'elevator': {
        setColor(item.body, color, faded);
        item.cargo.visible = !!s.loaded;
        const at = (f) => (floors.has(f) ? floors.get(f).elevation - item.bottom : null);
        let y = s.elevatorFloor ? at(s.elevatorFloor) : null;
        if (y == null && s.movingFrom && s.movingTo && at(s.movingFrom) != null && at(s.movingTo) != null) y = (at(s.movingFrom) + at(s.movingTo)) / 2; // 移動中：推估
        item.car.visible = y != null;
        if (y != null) item.car.position.y = y;
        break;
      }
      case 'rack': {
        const slots = s.rackSlots || [];
        const m = new THREE.Matrix4();
        const { w, h, lh } = item.slotSize;
        const levels = e.levels || 1;
        slots.forEach(([col, level], i) => {
          const c = col - (e.colFrom ?? col);
          if (c < 0 || c >= item.cols || level < 1 || level > levels) return;
          m.compose(new THREE.Vector3(-e.w / 2 + w * (c + 0.5), (level - 1) * lh + 0.08 + Math.min(lh * 0.7, 1) / 2, 0),
            new THREE.Quaternion(), new THREE.Vector3(w * 0.75, Math.min(lh * 0.7, 1), h * 0.8));
          item.slots.setMatrixAt(i, m);
        });
        item.slots.count = slots.length;
        item.slots.instanceMatrix.needsUpdate = true;
        updateColumnBox(item, s.selectedColumn);
        break;
      }
      case 'conveyor':
        setColor(item.body, s.blocked ? COLOR.blocked : COLOR.conveyor, s.blocked && s.blockedEstimated ? 0.5 : 0.85);
        break;
      case 'fireDoor':
      case 'dock':
        setColor(item.body, color, faded);
        break;
    }
    if (item.label) {
      const l = item.label;
      l.name.textContent = (s.badge ? `[${s.badge}] ` : '') + e.name + (s.inconsistent ? ' ⚠' : '') + (s.estimated && e.type === 'vehicle' ? '（推估位置）' : '');
      l.summary.textContent = s.summary || '';
      l.div.title = s.inconsistent ? '不一致（點選看詳細）' : '';
      l.div.classList.toggle('alarm', s.look === 'alarm');
      l.div.classList.toggle('selected', !!s.selected);
      l.div.classList.toggle('estimated', !!s.estimated);
    }
    if (s.selected) selected = item;
  }
  updateSelection(selected);
}

function updateSelection(item) {
  if (selectionBox) { selectionBox.removeFromParent(); selectionBox = null; }
  if (!item) return;
  const target = item.data.type === 'elevator' ? item.car : item.group;
  selectionBox = new THREE.BoxHelper(target, COLOR.selected);
  scene.add(selectionBox);
}

function updateColumnBox(item, column) {
  if (item.columnBox) { item.columnBox.removeFromParent(); item.columnBox = null; }
  if (column == null) return;
  const e = item.data;
  const c = column - (e.colFrom ?? column);
  const w = item.slotSize.w;
  const helper = new THREE.LineSegments(new THREE.EdgesGeometry(unitBox), new THREE.LineBasicMaterial({ color: COLOR.selected }));
  helper.scale.set(w, e.height, e.h);
  helper.position.set(-e.w / 2 + w * (c + 0.5), e.height / 2, 0);
  item.group.add(helper);
  item.columnBox = helper;
}

// ---- 點選 ----

const raycaster = new THREE.Raycaster();
let downAt = null;
renderer.domElement.addEventListener('pointerdown', (ev) => { downAt = { x: ev.clientX, y: ev.clientY }; });
renderer.domElement.addEventListener('pointerup', (ev) => {
  if (!downAt || ev.button !== 0) return;
  const moved = Math.hypot(ev.clientX - downAt.x, ev.clientY - downAt.y);
  downAt = null;
  if (moved > 4) return; // 拖曳是旋轉，不是點選
  const rect = renderer.domElement.getBoundingClientRect();
  const ndc = new THREE.Vector2(((ev.clientX - rect.left) / rect.width) * 2 - 1, -((ev.clientY - rect.top) / rect.height) * 2 + 1);
  raycaster.setFromCamera(ndc, camera);
  for (const hit of raycaster.intersectObjects(root.children, true)) {
    let o = hit.object;
    while (o && !o.userData.id) o = o.parent;
    if (!o) continue;
    const item = items.get(o.userData.id);
    if (!item || item.data.type === 'area' || item.data.type === 'rail') continue; // 輔助元素不擋住點選
    let column = null;
    if (item.data.type === 'rack') {
      const local = item.group.worldToLocal(hit.point.clone());
      const c = Math.floor((local.x + item.data.w / 2) / item.slotSize.w);
      column = (item.data.colFrom ?? 0) + Math.min(Math.max(c, 0), item.cols - 1);
    }
    pick(item.data.id, column);
    return;
  }
});

function pick(id, column) {
  post({ kind: 'pick', id, column });
}

// ---- 與 C# 的訊息 ----

if (host) {
  host.addEventListener('message', (ev) => {
    const msg = ev.data;
    try {
      if (msg.kind === 'scene') loadScene(msg.data);
      else if (msg.kind === 'state') applyState(msg.data);
      else if (msg.kind === 'focus') focus(msg.data);
      else if (msg.kind === 'resetView') resetView();
    } catch (err) {
      post({ kind: 'error', message: `3D 畫面處理失敗：${err && err.message ? err.message : err}` });
    }
  });
}

function resize() {
  const w = view.clientWidth || 1;
  const h = view.clientHeight || 1;
  renderer.setSize(w, h);
  labels.setSize(w, h);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
}
new ResizeObserver(resize).observe(view);
resize();

renderer.domElement.addEventListener('webglcontextlost', (ev) => {
  ev.preventDefault();
  post({ kind: 'error', message: 'WebGL 中斷（顯示卡驅動程式重設或資源不足）；請重新開啟案件。' });
});

renderer.setAnimationLoop(() => {
  controls.update();
  if (selectionBox) selectionBox.update();
  renderer.render(scene, camera);
  labels.render(scene, camera);
});

post({ kind: 'ready' });
