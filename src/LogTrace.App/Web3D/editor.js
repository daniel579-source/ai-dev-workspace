// Layout 編輯器 3D（ADR-0018）：畫出 C# 傳來的場景與選取，並把滑鼠動作換算成目前樓層平面上的座標（公尺）回報 C#。
// 編輯的規則（吸附、選取、復原、存檔前檢查）都在 C# 的 ViewModel；這裡不修改 Layout。
// 操作：左鍵點選、拖曳元素、空白處拖曳框選；右鍵旋轉、中鍵平移、滾輪縮放（#117 使用者決定）。
import { THREE, CSS2DObject, LOOK, COLOR, post, listen, setColor, unitBox, createViewer } from './common.js';

let state = null;
const v = createViewer({
  mouseButtons: { LEFT: null, MIDDLE: THREE.MOUSE.PAN, RIGHT: THREE.MOUSE.ROTATE },
  onLabelClick: (id, ev) => clickElement(id, ev),
});
v.controls.maxPolarAngle = Math.PI / 2 - 0.02;
v.controls.minPolarAngle = 0;
v.setProjection('orthographic'); // 預設正上方俯視（ADR-0018 第 5 點）

const handles = new THREE.Group();
v.scene.add(handles);
let selectionBoxes = [];
let rotateButton = null;
const HANDLE_PX = 10;

const elevation = (floor) => v.elevationOf(floor);
const currentElevation = () => elevation(state ? state.currentFloor : null);

// ---- 狀態：選取、有問題的元素、只顯示目前樓層、把手 ----

function applyState() {
  if (!state) return;
  const selected = new Set(state.selected);
  const issues = new Set(state.issues);
  for (const item of v.items.values()) {
    const id = item.data.id;
    if (item.body && item.data.type !== 'rack' && item.data.type !== 'label' && item.data.type !== 'area')
      setColor(item.body, issues.has(id) ? LOOK.alarm : LOOK.unknown, item.data.type === 'conveyor' ? 0.85 : 1);
    if (item.data.type === 'conveyor' && item.body) setColor(item.body, issues.has(id) ? COLOR.issue : COLOR.conveyor, 0.85);
    if (item.label) {
      item.label.div.classList.toggle('selected', selected.has(id));
      item.label.div.classList.toggle('alarm', issues.has(id));
    }
  }
  for (const b of selectionBoxes) b.removeFromParent();
  selectionBoxes = [];
  for (const id of state.selected) {
    const item = v.items.get(id);
    if (!item) continue;
    const b = new THREE.BoxHelper(item.group, COLOR.selected);
    v.scene.add(b);
    selectionBoxes.push(b);
  }
  v.showOnlyFloor(state.onlyCurrentFloor ? state.currentFloor : null);
  buildHandles();
}

function handleMesh(geometry, color, userData, position) {
  const m = new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({ color, depthTest: false }));
  m.renderOrder = 10;
  m.position.copy(position);
  m.userData = userData;
  const edge = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), new THREE.LineBasicMaterial({ color: COLOR.selected, depthTest: false }));
  edge.renderOrder = 11;
  m.add(edge);
  handles.add(m);
  return m;
}

const cornerGeometry = unitBox;
const pointGeometry = new THREE.SphereGeometry(0.5, 12, 8);
const segmentGeometry = new THREE.OctahedronGeometry(0.6);
const heightGeometry = new THREE.ConeGeometry(0.5, 1, 12);
const transferGeometry = new THREE.CylinderGeometry(0.5, 0.5, 0.4, 16);

/** 單選時的把手：四角調整大小、頂面的高度箭頭、旋轉按鈕；導軌的點與弧線中點；輸送線的交接點（固定螢幕大小）。 */
function buildHandles() {
  handles.clear();
  if (rotateButton) { rotateButton.element.remove(); rotateButton.removeFromParent(); rotateButton = null; }
  if (!state || !state.primary) return;
  const item = v.items.get(state.primary);
  if (!item) return;
  const e = item.data;
  const id = e.id;
  const base = e.type === 'elevator' ? currentElevation() : elevation(e.floor);
  if (state.canResize) {
    for (const sx of [-1, 1]) for (const sy of [-1, 1]) {
      const p = item.group.localToWorld(new THREE.Vector3(sx * e.w / 2, 0, sy * e.h / 2));
      p.y = base + 0.05;
      handleMesh(cornerGeometry, 0xFFFFFF, { kind: 'corner', id, sx, sy }, p);
    }
  }
  if (state.canHeight) {
    const p = new THREE.Vector3(e.x + e.w / 2, base + e.height + 0.6, e.y + e.h / 2);
    handleMesh(heightGeometry, 0xFFFFFF, { kind: 'height', id, base }, p);
  }
  for (const h of state.handles) {
    const p = new THREE.Vector3(h.x, elevation(e.floor) + 0.1, h.y);
    if (h.kind === 'railPoint') handleMesh(pointGeometry, h.selected ? 0xF59E0B : 0xFFFFFF, { kind: h.kind, id, index: h.index }, p);
    else if (h.kind === 'railSegment') handleMesh(segmentGeometry, h.selected ? 0xF59E0B : 0xFFFFFF, { kind: h.kind, id, index: h.index }, p);
    else handleMesh(transferGeometry, 0xF59E0B, { kind: h.kind, id }, p);
  }
  if (state.canRotate) {
    const b = new THREE.Box3().expandByObject(item.group);
    const div = document.createElement('button');
    div.type = 'button';
    div.className = 'label rotate';
    div.textContent = '⟳ 90°';
    div.title = '順時針旋轉 90 度';
    div.setAttribute('aria-label', '旋轉 90 度');
    div.addEventListener('pointerdown', (ev) => ev.stopPropagation());
    div.addEventListener('click', (ev) => { ev.stopPropagation(); post({ kind: 'down', target: { kind: 'rotate', id }, floor: null, x: 0, y: 0 }); });
    rotateButton = new CSS2DObject(div);
    rotateButton.position.set(b.max.x, b.max.y, b.min.z);
    rotateButton.center.set(0, 1);
    v.scene.add(rotateButton);
  }
}

// 把手保持固定的螢幕大小；選取框跟著元素
v.onFrame(() => {
  // 正上方俯視時高度無法拖曳（視線與高度方向平行），高度把手只在斜視時顯示
  const tilted = v.controls.getPolarAngle() > 0.35;
  for (const m of handles.children) {
    const s = v.metersPerPixel(m.position) * HANDLE_PX;
    m.scale.set(s, s, s);
    if (m.userData.kind === 'height') m.visible = tilted;
  }
  for (const b of selectionBoxes) b.update();
});

// ---- 滑鼠 ----

const canvas = v.renderer.domElement;
let drag = null;     // { kind, floor, elevation, start: {clientX, clientY}, base }
let pending = null;  // 拖曳中最新的 move 訊息（每個畫面送一次）
let pointer = null;  // 滑鼠在目前樓層平面上的位置（Ctrl+V 貼上用）
let boxLine = null;

/** 元素所在的樓層：電梯取點到的高度所在的服務樓層（不高於點到的位置中最高的一層）。 */
function floorOfHit(item, hit) {
  const e = item.data;
  if (e.type !== 'elevator') return e.floor;
  const served = (e.servedFloors || []).filter((f) => v.floors.has(f)).map((f) => v.floors.get(f)).sort((a, b) => a.elevation - b.elevation);
  if (state && state.onlyCurrentFloor && (e.servedFloors || []).includes(state.currentFloor)) return state.currentFloor;
  let floor = served.length ? served[0].name : e.floor;
  for (const f of served) if (f.elevation <= hit.point.y + 0.01) floor = f.name;
  return floor;
}

function elementUnder(ev) {
  let area = null;
  for (const hit of v.hitAt(ev, v.root.children)) {
    let o = hit.object;
    while (o && !o.userData.id) o = o.parent;
    if (!o) continue;
    const item = v.items.get(o.userData.id);
    if (!item) continue;
    if (item.data.type === 'area') { area = area || { item, hit }; continue; } // 區域在最下層：沒有其他元素時才選它
    return { item, hit };
  }
  return area;
}

function begin(ev, target, floor, extra = {}) {
  const elev = elevation(floor);
  const p = v.projectToPlane(ev, elev) || { x: 0, y: 0 };
  post({ kind: 'down', target, floor, x: p.x, y: p.y, ctrl: ev.ctrlKey, detail: ev.detail });
  drag = { kind: target ? target.kind : 'box', floor, elevation: elev, start: { clientX: ev.clientX, clientY: ev.clientY, x: p.x, y: p.y }, ...extra };
  canvas.setPointerCapture(ev.pointerId);
}

canvas.addEventListener('pointerdown', (ev) => {
  if (ev.button !== 0) return;
  // 把手優先
  const h = v.hitAt(ev, handles.children).find((x) => x.object.userData && x.object.userData.kind);
  if (h) {
    const d = h.object.userData;
    begin(ev, { kind: d.kind, id: d.id, sx: d.sx, sy: d.sy, index: d.index }, state.currentFloor,
      d.kind === 'height' ? { base: d.base, point: h.object.position.clone() } : {});
    return;
  }
  const found = elementUnder(ev);
  if (found) {
    begin(ev, { kind: 'element', id: found.item.data.id }, floorOfHit(found.item, found.hit));
    return;
  }
  begin(ev, null, v.slabAt(ev) || (state ? state.currentFloor : null)); // 空白處：框選
});

canvas.addEventListener('pointermove', (ev) => {
  if (state) pointer = v.projectToPlane(ev, currentElevation());
  if (!drag) return;
  const p = v.projectToPlane(ev, drag.elevation);
  if (!p) return;
  if (drag.kind === 'box') {
    drawBox(drag.start, p, drag.elevation);
    return;
  }
  let h = 0;
  if (drag.kind === 'height') {
    const y = v.projectToVertical(ev, drag.point);
    if (y == null) return;
    h = y - drag.base;
  }
  pending = { kind: 'move', x: p.x, y: p.y, h };
});

canvas.addEventListener('pointerleave', () => { if (!drag) pointer = null; });

function end(ev) {
  if (!drag) return;
  const p = v.projectToPlane(ev, drag.elevation) || { x: drag.start.x, y: drag.start.y };
  if (pending) { post(pending); pending = null; }
  const moved = Math.hypot(ev.clientX - drag.start.clientX, ev.clientY - drag.start.clientY) > 4;
  post({ kind: 'up', x: p.x, y: p.y, ctrl: ev.ctrlKey, moved });
  drag = null;
  if (boxLine) { boxLine.removeFromParent(); boxLine = null; }
}
canvas.addEventListener('pointerup', (ev) => { if (ev.button === 0) end(ev); });
canvas.addEventListener('pointercancel', end);

v.onFrame(() => {
  if (pending) { post(pending); pending = null; }
});

/** 框選的範圍：畫在目前樓層的地板上。 */
function drawBox(a, b, elev) {
  if (boxLine) boxLine.removeFromParent();
  const y = elev + 0.05;
  const g = new THREE.BufferGeometry().setFromPoints([
    new THREE.Vector3(a.x, y, a.y), new THREE.Vector3(b.x, y, a.y), new THREE.Vector3(b.x, y, b.y), new THREE.Vector3(a.x, y, b.y),
  ]);
  boxLine = new THREE.LineLoop(g, new THREE.LineBasicMaterial({ color: COLOR.selected, depthTest: false }));
  boxLine.renderOrder = 12;
  v.scene.add(boxLine);
}

/** 點元素的名稱：與點元素本身相同（選取它）。 */
function clickElement(id, ev) {
  const item = v.items.get(id);
  if (!item) return;
  const e = item.data;
  const floor = e.type === 'elevator' && state && (e.servedFloors || []).includes(state.currentFloor) ? state.currentFloor : e.floor;
  post({ kind: 'down', target: { kind: 'element', id }, floor, x: e.x, y: e.y, ctrl: ev.ctrlKey, detail: 1 });
  post({ kind: 'up', x: e.x, y: e.y, ctrl: ev.ctrlKey, moved: false });
}

// ---- 鍵盤：Del、Ctrl+C／V／Z 轉給 C#（WebView2 的按鍵不經過 WPF，#117 使用者決定與 2D 相同） ----

document.addEventListener('keydown', (ev) => {
  const key = ev.key.toLowerCase();
  const ctrl = ev.ctrlKey && !ev.altKey && !ev.shiftKey;
  if (!(key === 'delete' && !ev.ctrlKey) && !(ctrl && ['c', 'v', 'z'].includes(key))) return;
  ev.preventDefault();
  const msg = { kind: 'key', key, ctrl: ev.ctrlKey };
  if (pointer) { msg.x = pointer.x; msg.y = pointer.y; }
  post(msg);
});

// ---- 與 C# 的訊息 ----

listen({
  scene: ({ layout, keepView }) => { v.loadScene(layout, keepView); applyState(); },
  patch: ({ elements, removed }) => { v.patch(elements, removed); applyState(); },
  editorState: (s) => { state = s; applyState(); },
  resetView: () => v.resetView(),
  projection: (p) => v.setProjection(p),
});

post({ kind: 'ready' });
