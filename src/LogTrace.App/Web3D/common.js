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

  // ---- 各種元素的外觀（簡化的幾何，ADR-0017 第 11 點） ----

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

  function instanced(matrices, material) {
    const m = new THREE.InstancedMesh(unitBox, material, Math.max(matrices.length, 1));
    matrices.forEach((x, i) => m.setMatrixAt(i, x));
    m.count = matrices.length;
    m.instanceMatrix.needsUpdate = true;
    return m;
  }

  function buildRack(e, item) {
    const levels = e.levels || 1;
    const lh = e.levelHeight || 1.2;
    const cols = e.colFrom != null && e.colTo != null && e.colTo >= e.colFrom ? e.colTo - e.colFrom + 1 : 1;
    item.cols = cols;
    const postSize = 0.08;
    const posts = [];
    for (let c = 0; c <= cols; c++) {
      const x = -e.w / 2 + (e.w / cols) * c;
      for (const z of [-e.h / 2, e.h / 2]) posts.push(new THREE.Matrix4().compose(new THREE.Vector3(x, e.height / 2, z), new THREE.Quaternion(), new THREE.Vector3(postSize, e.height, postSize)));
    }
    const beams = [];
    for (let l = 0; l <= levels; l++) {
      const y = Math.min(l * lh, e.height - 0.04) + 0.04;
      for (const z of [-e.h / 2, e.h / 2]) beams.push(new THREE.Matrix4().compose(new THREE.Vector3(0, y, z), new THREE.Quaternion(), new THREE.Vector3(e.w, 0.08, 0.06)));
    }
    item.group.add(instanced(posts, mat(COLOR.frame)), instanced(beams, mat(COLOR.beam)));
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
    item.group.add(item.car);
  }

  function buildConveyor(e, item) {
    const [[x1, y1], [x2, y2]] = e.path;
    const len = Math.max(Math.hypot(x2 - x1, y2 - y1), 0.01);
    item.group.position.set((x1 + x2) / 2, elevationOf(e.floor), (y1 + y2) / 2);
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
