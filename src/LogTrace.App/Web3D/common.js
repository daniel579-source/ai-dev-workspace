// 3D 畫面的共用部分（ADR-0017、ADR-0018）：Layout 回放（playback.js）與 Layout 編輯器（editor.js）共用的繪製。
// 依 C# 傳來的場景資料畫出 Layout；不推導任何狀態、不保存 Layout。
// 座標：Layout 的 (x, y) 對應 three.js 的 (x, z)，高度為 y；單位公尺。
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/OrbitControls.js';
import { CSS2DRenderer, CSS2DObject } from 'three/addons/CSS2DRenderer.js';

export { THREE, CSS2DObject };

const host = window.chrome && window.chrome.webview;
export const post = (msg) => { if (host) host.postMessage(msg); };

window.addEventListener('error', (e) => post({ kind: 'error', message: `網頁錯誤：${e.message}` }));
window.addEventListener('unhandledrejection', (e) => post({ kind: 'error', message: `網頁錯誤：${e.reason}` }));

/** C# 傳來的訊息：handlers[kind](data)。 */
export function listen(handlers) {
  if (!host) return;
  host.addEventListener('message', (ev) => {
    const msg = ev.data;
    try {
      const h = handlers[msg.kind];
      if (h) h(msg.data);
    } catch (err) {
      post({ kind: 'error', message: `3D 畫面處理失敗：${err && err.message ? err.message : err}` });
    }
  });
}

// 圖例的顏色（ADR-0008 第 6 點；異常中、防火門開／關在 3D 改為較深的顏色，ADR-0017 第 11 點）
export const LOOK = {
  unknown: 0xE5E7EB, empty: 0xFFFFFF, occupied: 0xBFDBFE, executing: 0xFDE68A, alarm: 0xFCA5A5,
  auxiliary: 0x94A3B8, doorOpen: 0x86EFAC, doorClosed: 0xF87171,
};
export const COLOR = {
  frame: 0x64748B, beam: 0xF59E0B, cargo: 0x93C5FD, rail: 0x94A3B8, conveyor: 0x94A3B8, blocked: 0xDC2626,
  slab: 0xE2E8F0, shaft: 0x93C5FD, selected: 0x2563EB, issue: 0xDC2626, presenceOn: 0x22C55E, presenceOff: 0x334155, presenceUnknown: 0xCBD5E1,
};

const unitBox = new THREE.BoxGeometry(1, 1, 1);
/** 半徑 1、長 1、軸沿 z 的圓柱（滾筒、車輪）。 */
const rollerGeometry = new THREE.CylinderGeometry(1, 1, 1, 12).rotateX(Math.PI / 2);
export { unitBox };

export function box(w, h, d, material, x = 0, y = 0, z = 0) {
  const m = new THREE.Mesh(unitBox, material);
  m.scale.set(Math.max(w, 0.01), Math.max(h, 0.01), Math.max(d, 0.01));
  m.position.set(x, y, z);
  return m;
}

export function setColor(mesh, color, opacity = 1) {
  if (!mesh) return;
  mesh.material.color.setHex(color);
  mesh.material.opacity = opacity;
  mesh.material.transparent = opacity < 1 || mesh.material.transparent;
}

/**
 * 建立 3D 畫面。opts.mouseButtons：OrbitControls 的滑鼠分配；opts.onLabelClick(id)：點元素名稱；
 * opts.labelAria：標籤的無障礙名稱是否固定為元素名稱（UI 自動化以名稱找元素）。
 */
export function createViewer(opts = {}) {
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

  const perspective = new THREE.PerspectiveCamera(45, 1, 0.1, 5000);
  const orthographic = new THREE.OrthographicCamera(-10, 10, 10, -10, -5000, 5000);
  let camera = perspective;
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.mouseButtons = opts.mouseButtons || { LEFT: THREE.MOUSE.ROTATE, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.PAN };
  controls.enableDamping = false;
  controls.maxPolarAngle = Math.PI / 2 - 0.02;

  let root = new THREE.Group();
  scene.add(root);
  let slabs = new THREE.Group();
  scene.add(slabs);

  /** 元素 id → { data, group, body, … } */
  const items = new Map();
  let floors = new Map(); // 樓層名稱 → { name, elevation, height }
  let floorList = [];
  let defaultView = null;
  let projection = 'perspective';
  const frameHooks = [];

  const mat = (color, o = {}) => new THREE.MeshLambertMaterial({ color, ...o });
  const elevationOf = (floor) => (floors.has(floor) ? floors.get(floor).elevation : 0);

  function place(group, e, elevation) {
    group.position.set(e.x + e.w / 2, elevation, e.y + e.h / 2);
    group.rotation.y = -THREE.MathUtils.degToRad(e.rotation || 0);
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
    div.addEventListener('click', (ev) => { ev.stopPropagation(); if (opts.onLabelClick) opts.onLabelClick(e.id, ev); });
    div.addEventListener('pointerdown', (ev) => ev.stopPropagation());
    const obj = new CSS2DObject(div);
    obj.center.set(0.5, 1);
    return { obj, div, name, summary };
  }

  // ---- 各種元素的外觀（簡化的幾何，ADR-0017 第 11 點；設備特徵 2026-11-14 補充） ----
  // 一般自動倉儲設備的樣式，不使用任何廠商的模型或標誌。回放的狀態顏色只塗在主體（item.body），
  // 滾筒、立柱、車輪、保險桿等細節維持固定顏色，俯視時也看得出種類。

  const frameMat = mat(COLOR.frame);
  const rollerMat = mat(0x94A3B8);
  const darkMat = mat(0x1F2937);
  const bumperMat = mat(0xFACC15);
  const markMat = mat(0xF59E0B);

  function instanced(matrices, material, geometry = unitBox) {
    const m = new THREE.InstancedMesh(geometry, material, Math.max(matrices.length, 1));
    matrices.forEach((x, i) => m.setMatrixAt(i, x));
    m.count = matrices.length;
    m.instanceMatrix.needsUpdate = true;
    return m;
  }

  const q0 = new THREE.Quaternion();
  const at = (x, y, z, sx, sy, sz) => new THREE.Matrix4().compose(new THREE.Vector3(x, y, z), q0, new THREE.Vector3(sx, sy, sz));

  /** 滾筒：軸沿 z（深度方向），沿 x 依間距排列；length 為排列的長度、width 為滾筒長度。 */
  function rollers(length, width, y, pitch = 0.18, r = 0.04) {
    const n = Math.max(1, Math.min(400, Math.floor(length / pitch)));
    const step = length / n;
    const list = [];
    for (let i = 0; i < n; i++) list.push(at(-length / 2 + step * (i + 0.5), y, 0, r, r, width));
    return instanced(list, rollerMat, rollerGeometry);
  }

  /** 四支腳：寬 w、深 d 的四角，高 h。 */
  function legs(w, d, h, size = 0.07) {
    const list = [];
    for (const sx of [-1, 1]) for (const sz of [-1, 1]) list.push(at(sx * (w / 2 - size), h / 2, sz * (d / 2 - size), size, h, size));
    return instanced(list, frameMat);
  }

  function buildStation(e, item) {
    // 輸送站：四支腳、兩側護欄、台面（狀態顏色）與上面的滾筒
    const top = e.height;
    item.group.add(legs(e.w, e.h, top - 0.1));
    item.body = box(e.w - 0.08, 0.05, e.h - 0.08, mat(LOOK.unknown), 0, top - 0.11, 0);
    item.group.add(item.body);
    item.group.add(instanced([at(0, top - 0.02, -e.h / 2 + 0.03, e.w, 0.1, 0.06), at(0, top - 0.02, e.h / 2 - 0.03, e.w, 0.1, 0.06)], frameMat));
    item.group.add(rollers(e.w - 0.1, e.h - 0.16, top - 0.05));
    item.light = new THREE.Mesh(new THREE.SphereGeometry(Math.min(0.18, Math.min(e.w, e.h) / 4), 12, 8), mat(COLOR.presenceUnknown, { transparent: true, opacity: 0.6 }));
    item.light.position.set(e.w / 2 - 0.2, top + 0.12, -e.h / 2 + 0.2);
    item.group.add(item.light);
    item.cargo = box(Math.min(e.w, 1.1) * 0.8, 0.8, Math.min(e.h, 1.1) * 0.8, mat(COLOR.cargo), 0, top + 0.4, 0);
    item.cargo.visible = false;
    item.group.add(item.cargo);
  }

  function buildVehicle(e, item) {
    // 軌道車：低矮的車體（狀態顏色）、四個車輪、兩端黃色保險桿、頂面的輸送滾筒
    const bodyH = e.height * 0.7;
    const clearance = 0.08;
    item.body = box(e.w - 0.16, bodyH - clearance, e.h * 0.85, mat(LOOK.unknown, { transparent: true }), 0, clearance + (bodyH - clearance) / 2, 0);
    item.group.add(item.body);
    const wr = Math.min(0.14, bodyH / 2);
    const wheels = [];
    for (const sx of [-1, 1]) for (const sz of [-1, 1]) wheels.push(at(sx * (e.w / 2 - 0.3), wr, sz * (e.h * 0.425 + 0.03), wr, wr, 0.08));
    item.group.add(instanced(wheels, darkMat, rollerGeometry));
    item.group.add(instanced([at(-e.w / 2 + 0.04, clearance + bodyH * 0.35, 0, 0.08, bodyH * 0.5, e.h * 0.85), at(e.w / 2 - 0.04, clearance + bodyH * 0.35, 0, 0.08, bodyH * 0.5, e.h * 0.85)], bumperMat));
    item.top = box(e.w * 0.8, e.height - bodyH, e.h * 0.7, mat(0x475569, { transparent: true }), 0, bodyH + (e.height - bodyH) / 2, 0);
    item.group.add(item.top);
    item.group.add(rollers(e.w * 0.78, e.h * 0.62, e.height + 0.02, 0.18, 0.035));
    item.cargo = box(Math.min(e.w, 1.1) * 0.8, 0.8, Math.min(e.h, 1.1) * 0.8, mat(COLOR.cargo, { transparent: true }), 0, e.height + 0.45, 0);
    item.cargo.visible = false;
    item.group.add(item.cargo);
  }

  function buildRm(e, item) {
    // RM（堆高起重機，ADR-0019 第 6 點）：底部走行台車（狀態顏色）、兩支立柱、頂部連桿、沿立柱上下的載貨台
    const mast = Math.min(0.18, e.w / 8);
    const baseH = 0.35;
    item.body = box(e.w, baseH, e.h, mat(LOOK.unknown, { transparent: true }), 0, baseH / 2, 0);
    item.group.add(item.body);
    item.group.add(instanced([
      at(-e.w / 2 + mast / 2, e.height / 2, 0, mast, e.height, mast * 1.6),
      at(e.w / 2 - mast / 2, e.height / 2, 0, mast, e.height, mast * 1.6),
    ], frameMat));
    item.group.add(box(e.w, 0.15, mast * 1.6, frameMat, 0, e.height - 0.075, 0));
    item.group.add(instanced([at(-e.w / 2 + 0.05, baseH * 0.5, 0, 0.08, baseH * 0.6, e.h * 0.9), at(e.w / 2 - 0.05, baseH * 0.5, 0, 0.08, baseH * 0.6, e.h * 0.9)], bumperMat));
    // 載貨台：在兩支立柱之間，向料架兩側伸出（深 e.h）；高度由狀態的 lift 決定
    item.carriage = new THREE.Group();
    item.carriage.add(box(e.w - mast * 2 - 0.04, 0.12, e.h * 1.1, mat(0x475569, { transparent: true }), 0, 0.06, 0));
    item.carriage.add(rollers(e.w - mast * 2 - 0.1, e.h, 0.15, 0.18, 0.035));
    item.cargo = box(Math.min(e.w, 1.1) * 0.7, 0.8, Math.min(e.h, 1.1) * 0.8, mat(COLOR.cargo, { transparent: true }), 0, 0.6, 0);
    item.cargo.visible = false;
    item.carriage.add(item.cargo);
    item.carriage.position.y = baseH + 0.05;
    item.baseH = baseH;
    item.group.add(item.carriage);
  }

  function buildDock(e, item) {
    // Conv（對接設備，例如升降移載機）：底座、兩支立柱與頂部橫樑、中間的升降平台（狀態顏色）與滾筒
    const mast = Math.min(0.14, e.w / 6);
    item.group.add(box(e.w, 0.08, e.h, darkMat, 0, 0.04, 0));
    item.group.add(instanced([at(-e.w / 2 + mast / 2, e.height / 2, 0, mast, e.height, e.h * 0.9), at(e.w / 2 - mast / 2, e.height / 2, 0, mast, e.height, e.h * 0.9)], frameMat));
    item.group.add(box(e.w, 0.1, mast, frameMat, 0, e.height - 0.05, 0));
    item.body = box(e.w - mast * 2 - 0.04, 0.12, e.h * 0.85, mat(LOOK.unknown), 0, e.height * 0.45, 0);
    item.group.add(item.body);
    item.group.add(rollers(e.w - mast * 2 - 0.1, e.h * 0.75, e.height * 0.45 + 0.08, 0.18, 0.035));
  }

  function buildFireDoor(e, item) {
    // 防火門：兩端的門框立柱與門板（狀態顏色：開綠、關紅）
    const along = e.w >= e.h;
    const len = along ? e.w : e.h;
    const t = Math.max(along ? e.h : e.w, 0.1);
    const post = Math.min(0.18, len / 6);
    const posts = along
      ? [at(-len / 2 + post / 2, e.height / 2, 0, post, e.height, t + 0.1), at(len / 2 - post / 2, e.height / 2, 0, post, e.height, t + 0.1)]
      : [at(0, e.height / 2, -len / 2 + post / 2, t + 0.1, e.height, post), at(0, e.height / 2, len / 2 - post / 2, t + 0.1, e.height, post)];
    item.group.add(instanced(posts, frameMat));
    item.body = along
      ? box(len - post * 2, e.height * 0.95, t, mat(LOOK.unknown, { transparent: true }), 0, e.height * 0.475, 0)
      : box(t, e.height * 0.95, len - post * 2, mat(LOOK.unknown, { transparent: true }), 0, e.height * 0.475, 0);
    item.group.add(item.body);
  }

  function buildArea(e, item) {
    // 區域：地板上的淡色塊加黃色邊線
    item.body = box(e.w, 0.02, e.h, mat(LOOK.auxiliary, { transparent: true, opacity: 0.15, depthWrite: false }), 0, 0.03, 0);
    item.group.add(item.body);
    const t = 0.08;
    item.group.add(instanced([
      at(0, 0.045, -e.h / 2 + t / 2, e.w, 0.01, t), at(0, 0.045, e.h / 2 - t / 2, e.w, 0.01, t),
      at(-e.w / 2 + t / 2, 0.045, 0, t, 0.01, e.h), at(e.w / 2 - t / 2, 0.045, 0, t, 0.01, e.h),
    ], markMat));
  }

  function buildRack(e, item) {
    const levels = e.levels || 1;
    const lh = e.levelHeight || 1.2;
    const cols = e.colFrom != null && e.colTo != null && e.colTo >= e.colFrom ? e.colTo - e.colFrom + 1 : 1;
    item.cols = cols;
    const postSize = 0.08;
    const posts = [];
    const ties = [];
    for (let c = 0; c <= cols; c++) {
      const x = -e.w / 2 + (e.w / cols) * c;
      for (const z of [-e.h / 2, e.h / 2]) posts.push(at(x, e.height / 2, z, postSize, e.height, postSize));
      // 前後立柱之間的連桿（頂部與底部）：俯視時看得出每一列
      ties.push(at(x, e.height - 0.03, 0, 0.05, 0.05, e.h), at(x, 0.1, 0, 0.05, 0.05, e.h));
    }
    const beams = [];
    for (let l = 0; l <= levels; l++) {
      const y = Math.min(l * lh, e.height - 0.04) + 0.04;
      for (const z of [-e.h / 2, e.h / 2]) beams.push(at(0, y, z, e.w, 0.08, 0.06));
    }
    item.group.add(instanced(posts, frameMat), instanced(ties, frameMat), instanced(beams, mat(COLOR.beam)));
    // 點選用的透明外框（料架的列由點到的位置換算）
    item.body = box(e.w, e.height, e.h, new THREE.MeshBasicMaterial({ visible: false }), 0, e.height / 2, 0);
    item.group.add(item.body);
    item.slots = new THREE.InstancedMesh(unitBox, mat(COLOR.cargo), cols * levels);
    item.slots.count = 0;
    item.group.add(item.slots);
    item.slotSize = { w: e.w / cols, h: e.h, lh };
  }

  function buildElevator(e, item) {
    const served = (e.servedFloors || []).filter((f) => floors.has(f));
    const elevations = served.map((f) => floors.get(f).elevation);
    const bottom = elevations.length ? Math.min(...elevations) : 0;
    const top = elevations.length ? Math.max(...elevations) + e.height + 0.3 : e.height;
    const H = top - bottom;
    // 垂直井道跨越服務的樓層：四角立柱、頂部橫樑、透明壁面
    const shaft = box(e.w, H, e.h, mat(COLOR.shaft, { transparent: true, opacity: 0.1, depthWrite: false }), 0, H / 2, 0);
    item.group.add(shaft);
    const p = 0.1;
    const corners = [];
    for (const sx of [-1, 1]) for (const sz of [-1, 1]) corners.push(at(sx * (e.w / 2 - p / 2), H / 2, sz * (e.h / 2 - p / 2), p, H, p));
    item.group.add(instanced(corners, frameMat));
    item.group.add(instanced([at(0, H - 0.05, -e.h / 2 + p / 2, e.w, 0.1, p), at(0, H - 0.05, e.h / 2 - p / 2, e.w, 0.1, p)], frameMat));
    item.group.position.y = bottom;
    item.bottom = bottom;
    // 台車：車廂（狀態顏色）加頂面滾筒
    item.car = new THREE.Group();
    item.body = box(e.w * 0.8, e.height * 0.85, e.h * 0.8, mat(LOOK.unknown, { transparent: true }), 0, e.height * 0.425, 0);
    item.cargo = box(Math.min(e.w, 1.1) * 0.6, 0.8, Math.min(e.h, 1.1) * 0.6, mat(COLOR.cargo, { transparent: true }), 0, e.height + 0.4, 0);
    item.cargo.visible = false;
    item.car.add(item.body, item.cargo, rollers(e.w * 0.75, e.h * 0.7, e.height * 0.85 + 0.04, 0.18, 0.035));
    item.group.add(item.car);
  }

  function buildConveyor(e, item) {
    // 輸送線：台面（阻斷時變紅）、兩側護欄、滾筒、每 2 公尺一對腳與方向箭頭
    const [[x1, y1], [x2, y2]] = e.path;
    const len = Math.max(Math.hypot(x2 - x1, y2 - y1), 0.01);
    const width = 0.8;
    item.group.position.set((x1 + x2) / 2, elevationOf(e.floor), (y1 + y2) / 2);
    item.group.rotation.y = -Math.atan2(y2 - y1, x2 - x1);
    item.body = box(len, 0.05, width - 0.1, mat(COLOR.conveyor, { transparent: true, opacity: 0.85 }), 0, e.height - 0.1, 0);
    item.group.add(item.body);
    item.group.add(instanced([at(0, e.height - 0.02, -width / 2 + 0.03, len, 0.1, 0.06), at(0, e.height - 0.02, width / 2 - 0.03, len, 0.1, 0.06)], frameMat));
    item.group.add(rollers(len, width - 0.16, e.height - 0.05));
    const n = Math.max(1, Math.round(len / 2));
    const legList = [];
    for (let i = 0; i <= n; i++) for (const sz of [-1, 1]) legList.push(at(-len / 2 + (len / n) * i, (e.height - 0.1) / 2, sz * (width / 2 - 0.05), 0.06, e.height - 0.1, 0.06));
    item.group.add(instanced(legList, frameMat));
    // 方向箭頭：單向在終點，雙向兩端都有
    const arrow = (dir) => {
      const cone = new THREE.Mesh(new THREE.ConeGeometry(0.3, 0.6, 12), mat(0x475569));
      cone.rotation.z = -dir * Math.PI / 2;
      cone.position.set(dir * (len / 2 - 0.3), e.height + 0.15, 0);
      return cone;
    };
    item.group.add(arrow(1));
    if (e.bidirectional) item.group.add(arrow(-1));
  }

  function buildRail(e, item) {
    // 導軌：兩條平行的鋼軌（間距 0.7 公尺）
    const elevation = elevationOf(e.floor);
    const pts = e.path || [];
    const material = mat(0x64748B);
    for (let i = 0; i + 1 < pts.length; i++) {
      const [x1, y1] = pts[i];
      const [x2, y2] = pts[i + 1];
      const len = Math.hypot(x2 - x1, y2 - y1);
      if (len < 1e-6) continue;
      const seg = new THREE.Group();
      seg.position.set((x1 + x2) / 2, elevation, (y1 + y2) / 2);
      seg.rotation.y = -Math.atan2(y2 - y1, x2 - x1);
      seg.add(box(len + 0.05, 0.06, 0.07, material, 0, 0.03, -0.35), box(len + 0.05, 0.06, 0.07, material, 0, 0.03, 0.35));
      item.group.add(seg);
    }
  }

  function disposeItem(item) {
    item.group.traverse((o) => { if (o.isCSS2DObject) o.element.remove(); });
    item.group.removeFromParent();
  }

  function buildElement(e) {
    const old = items.get(e.id);
    if (old) disposeItem(old);
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
      case 'dock': place(item.group, e, elevation); buildDock(e, item); break;
      case 'rm': place(item.group, e, elevation); buildRm(e, item); break;
      case 'fireDoor': place(item.group, e, elevation); buildFireDoor(e, item); break;
      case 'area': place(item.group, e, elevation); buildArea(e, item); break;
      case 'label':
        place(item.group, e, elevation);
        // 文字：點選用的透明範圍
        item.body = box(e.w || 1, 0.02, e.h || 1, new THREE.MeshBasicMaterial({ visible: false }), 0, 0.02, 0);
        item.group.add(item.body);
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
    return item;
  }

  function contentBounds() {
    const b = new THREE.Box3();
    for (const item of items.values()) b.expandByObject(item.group);
    if (b.isEmpty()) b.set(new THREE.Vector3(0, 0, 0), new THREE.Vector3(60, 0, 40));
    return b;
  }

  /** 每層的樓板：所有元素範圍再加邊界，半透明；最低層不透明。 */
  function buildFloors() {
    slabs.traverse((o) => { if (o.isCSS2DObject) o.element.remove(); });
    slabs.removeFromParent();
    slabs = new THREE.Group();
    scene.add(slabs);
    const bounds = contentBounds();
    const margin = 4;
    const minX = Math.min(bounds.min.x, 0) - margin, minZ = Math.min(bounds.min.z, 0) - margin;
    const w = bounds.max.x - minX + margin, d = bounds.max.z - minZ + margin;
    floorList.forEach((f, i) => {
      const slab = box(w, 0.1, d, mat(COLOR.slab, { transparent: i > 0, opacity: i > 0 ? 0.25 : 1, depthWrite: i === 0 }), minX + w / 2, f.elevation - 0.05, minZ + d / 2);
      slab.userData.floor = f.name;
      slabs.add(slab);
      if (floorList.length > 1) {
        const div = document.createElement('div');
        div.className = 'floor-label';
        div.textContent = f.name;
        const obj = new CSS2DObject(div);
        obj.position.set(minX + 0.5, f.elevation + 0.1, minZ + 0.5);
        obj.center.set(0, 1);
        obj.userData.floor = f.name;
        slabs.add(obj);
      }
    });
    bounds.max.y = Math.max(bounds.max.y, ...floorList.map((f) => f.elevation + f.height));
    return bounds;
  }

  function clearScene() {
    for (const item of items.values()) disposeItem(item);
    items.clear();
    root.removeFromParent();
    root = new THREE.Group();
    scene.add(root);
  }

  /** 整個場景重畫；keepView 時保留目前的視角。 */
  function loadScene(layout, keepView = false) {
    clearScene();
    floorList = layout.floors;
    floors = new Map(layout.floors.map((f) => [f.name, f]));
    for (const e of layout.elements) buildElement(e);
    defaultView = buildFloors();
    if (!keepView) resetView();
    post({ kind: 'loaded', elements: layout.elements.length, floors: layout.floors.length });
  }

  /** 只重畫改變的元素（編輯器拖曳時）。 */
  function patch(elements, removed) {
    for (const id of removed || []) {
      const item = items.get(id);
      if (item) disposeItem(item);
      items.delete(id);
    }
    for (const e of elements || []) buildElement(e);
    defaultView = buildFloors();
    post({ kind: 'loaded', elements: items.size, floors: floorList.length });
  }

  function aspect() {
    return (view.clientWidth || 1) / (view.clientHeight || 1);
  }

  /**
   * 預設視角：回放為斜上方 45 度（ADR-0017）；編輯器的正交投影為正上方俯視（ADR-0018 第 5 點）。自動縮放到看得到所有樓層。
   */
  function resetView() {
    if (!defaultView) return;
    const center = defaultView.getCenter(new THREE.Vector3());
    const size = defaultView.getSize(new THREE.Vector3());
    const radius = Math.max(size.length() / 2, 5);
    if (projection === 'orthographic') {
      const halfH = Math.max(size.z / 2, size.x / 2 / aspect(), 5) * 1.1;
      orthographic.top = halfH;
      orthographic.bottom = -halfH;
      orthographic.left = -halfH * aspect();
      orthographic.right = halfH * aspect();
      orthographic.zoom = 1;
      // 正上方俯視：稍微往 +z 偏，畫面上方對應 Layout 的上方（y 小），x 向右
      orthographic.position.set(center.x, center.y + radius * 4, center.z + 0.001);
      orthographic.updateProjectionMatrix();
    } else {
      const fov = THREE.MathUtils.degToRad(perspective.fov);
      const dist = radius / Math.sin(Math.min(fov, fov * aspect()) / 2);
      const dir = new THREE.Vector3(Math.SQRT1_2 * 0.5, Math.SQRT1_2, Math.SQRT1_2 * 0.866).normalize(); // 斜上方 45 度，從右前方看
      perspective.position.copy(center).addScaledVector(dir, dist);
      perspective.near = Math.max(0.1, dist / 1000);
      perspective.far = dist * 20;
      perspective.updateProjectionMatrix();
    }
    controls.target.copy(center);
    controls.update();
  }

  /** 切換投影：perspective（斜視）或 orthographic（俯視）。 */
  function setProjection(p) {
    projection = p;
    camera = p === 'orthographic' ? orthographic : perspective;
    controls.object = camera;
    resize();
    resetView();
  }

  /** 相機轉向元素：目標移到元素中心，保持目前的方向，拉近到能看清楚的距離。 */
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

  /** 只顯示 visibleFloor 這一層（null＝全部顯示）。電梯在服務的樓層中有這一層時顯示。 */
  function showOnlyFloor(visibleFloor) {
    for (const item of items.values()) {
      const e = item.data;
      item.group.visible = !visibleFloor || (e.type === 'elevator' ? (e.servedFloors || []).includes(visibleFloor) : e.floor === visibleFloor);
    }
    slabs.children.forEach((o) => { o.visible = !visibleFloor || o.userData.floor === visibleFloor; });
  }

  const raycaster = new THREE.Raycaster();

  function ndc(ev) {
    const rect = renderer.domElement.getBoundingClientRect();
    return new THREE.Vector2(((ev.clientX - rect.left) / rect.width) * 2 - 1, -((ev.clientY - rect.top) / rect.height) * 2 + 1);
  }

  function visible(o) {
    for (let x = o; x; x = x.parent) if (!x.visible) return false; // 透明材質的點選範圍（料架、文字）仍可點選
    return true;
  }

  /** 滑鼠下的物件（只看看得到的）：objects 為要測試的物件。 */
  function hitAt(ev, objects) {
    raycaster.setFromCamera(ndc(ev), camera);
    return raycaster.intersectObjects(objects, true).filter((h) => visible(h.object));
  }

  /** 滑鼠下的元素（輔助的區域與導軌可以略過）；傳回 { item, hit }。 */
  function elementAt(ev, { skipAux = true } = {}) {
    for (const hit of hitAt(ev, root.children)) {
      let o = hit.object;
      while (o && !o.userData.id) o = o.parent;
      if (!o) continue;
      const item = items.get(o.userData.id);
      if (!item) continue;
      if (skipAux && (item.data.type === 'area' || item.data.type === 'rail')) continue;
      return { item, hit };
    }
    return null;
  }

  /** 滑鼠下的樓板（樓層名稱）。 */
  function slabAt(ev) {
    for (const hit of hitAt(ev, slabs.children)) if (hit.object.userData.floor) return hit.object.userData.floor;
    return null;
  }

  /** 滑鼠投影到高度 elevation 的水平面上的 Layout 座標 { x, y }；平行於視線時為 null。 */
  function projectToPlane(ev, elevation) {
    raycaster.setFromCamera(ndc(ev), camera);
    const p = new THREE.Vector3();
    return raycaster.ray.intersectPlane(new THREE.Plane(new THREE.Vector3(0, 1, 0), -elevation), p) ? { x: p.x, y: p.z } : null;
  }

  /** 滑鼠投影到通過 point、面向相機的垂直面上的高度（世界座標 y）。 */
  function projectToVertical(ev, point) {
    raycaster.setFromCamera(ndc(ev), camera);
    const n = camera.position.clone().sub(point).setY(0);
    if (n.lengthSq() < 1e-9) n.set(0, 0, 1);
    const p = new THREE.Vector3();
    return raycaster.ray.intersectPlane(new THREE.Plane().setFromNormalAndCoplanarPoint(n.normalize(), point), p) ? p.y : null;
  }

  /** 某個位置每像素對應的公尺數（把手保持固定螢幕大小用）。 */
  function metersPerPixel(position) {
    const h = view.clientHeight || 1;
    if (camera.isOrthographicCamera) return (camera.top - camera.bottom) / camera.zoom / h;
    const dist = camera.position.distanceTo(position);
    return (2 * dist * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2)) / h;
  }

  function resize() {
    const w = view.clientWidth || 1;
    const h = view.clientHeight || 1;
    renderer.setSize(w, h);
    labels.setSize(w, h);
    perspective.aspect = w / h;
    perspective.updateProjectionMatrix();
    const halfH = (orthographic.top - orthographic.bottom) / 2;
    orthographic.left = -halfH * (w / h);
    orthographic.right = halfH * (w / h);
    orthographic.updateProjectionMatrix();
  }
  new ResizeObserver(resize).observe(view);
  resize();

  renderer.domElement.addEventListener('webglcontextlost', (ev) => {
    ev.preventDefault();
    post({ kind: 'error', message: 'WebGL 中斷（顯示卡驅動程式重設或資源不足）；請關閉後重新開啟' });
  });

  renderer.setAnimationLoop(() => {
    controls.update();
    for (const f of frameHooks) f();
    renderer.render(scene, camera);
    labels.render(scene, camera);
  });

  return {
    scene, renderer, controls, items, get root() { return root; }, get camera() { return camera; },
    get floors() { return floors; }, get floorList() { return floorList; },
    elevationOf, loadScene, patch, resetView, setProjection, focus, showOnlyFloor,
    hitAt, elementAt, slabAt, projectToPlane, projectToVertical, metersPerPixel, onFrame: (f) => frameHooks.push(f),
  };
}
