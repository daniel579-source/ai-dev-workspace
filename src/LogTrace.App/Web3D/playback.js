// Layout 回放 3D（ADR-0017）：依 C# 傳來的場景資料與目前時刻的狀態繪製，回報載入完成、錯誤與點選的元素。
// 不推導任何狀態（世界模型在 C# 引擎，ADR-0005）。操作：左鍵旋轉、右鍵平移、滾輪縮放。
import { THREE, LOOK, COLOR, post, listen, setColor, setCargoLabel, unitBox, createViewer } from './common.js';

const pick = (id, column) => post({ kind: 'pick', id, column });
const v = createViewer({ onLabelClick: (id) => pick(id, null) });
let selectionBox = null;

function applyState(state) {
  let selected = null;
  for (const s of state.elements) {
    const item = v.items.get(s.id);
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
      case 'rm':
        // RM（ADR-0019）：沿導軌的位置與載貨台高度都是推估
        setColor(item.body, color, faded);
        item.cargo.visible = !!s.loaded;
        if (s.x != null && s.y != null) item.group.position.set(s.x, item.group.position.y, s.y);
        else item.group.position.set(e.x + e.w / 2, item.group.position.y, e.y + e.h / 2);
        item.carriage.position.y = item.baseH + 0.05 + Math.min(Math.max(s.lift ?? 0, 0), Math.max(e.height - 1.2, 0));
        break;
      case 'elevator': {
        setColor(item.body, color, faded);
        item.cargo.visible = !!s.loaded;
        const at = (f) => (v.floors.has(f) ? v.floors.get(f).elevation - item.bottom : null);
        let y = s.elevatorFloor ? at(s.elevatorFloor) : null;
        if (y == null && s.movingFrom && s.movingTo && at(s.movingFrom) != null && at(s.movingTo) != null) y = (at(s.movingFrom) + at(s.movingTo)) / 2; // 移動中：推估
        item.car.visible = y != null; // 目前樓層未取得資訊時不畫台車（不顯示猜測值）
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
    // 搬送中貨物的標示（#125）：搬送機、RM、電梯上的貨箱
    if (e.type === 'vehicle' || e.type === 'rm' || e.type === 'elevator') setCargoLabel(item, s.loaded ? s.cargoLabel : null);
    if (item.label) {
      const l = item.label;
      l.name.textContent = (s.badge ? `[${s.badge}] ` : '') + e.name + (s.inconsistent ? ' ⚠' : '') + (s.estimated && (e.type === 'vehicle' || e.type === 'rm') ? '（推估位置）' : '');
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
  selectionBox = new THREE.BoxHelper(item.data.type === 'elevator' ? item.car : item.group, COLOR.selected);
  v.scene.add(selectionBox);
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

// ---- 點選（拖曳是旋轉，不是點選） ----

let downAt = null;
v.renderer.domElement.addEventListener('pointerdown', (ev) => { downAt = { x: ev.clientX, y: ev.clientY }; });
v.renderer.domElement.addEventListener('pointerup', (ev) => {
  if (!downAt || ev.button !== 0) return;
  const moved = Math.hypot(ev.clientX - downAt.x, ev.clientY - downAt.y);
  downAt = null;
  if (moved > 4) return;
  const found = v.elementAt(ev);
  if (!found) return;
  const { item, hit } = found;
  let column = null;
  if (item.data.type === 'rack') {
    const local = item.group.worldToLocal(hit.point.clone());
    const c = Math.floor((local.x + item.data.w / 2) / item.slotSize.w);
    column = (item.data.colFrom ?? 0) + Math.min(Math.max(c, 0), item.cols - 1);
  }
  pick(item.data.id, column);
});

v.onFrame(() => { if (selectionBox) selectionBox.update(); });

listen({
  scene: (layout) => { updateSelection(null); v.loadScene(layout); },
  state: applyState,
  focus: (id) => v.focus(id),
  resetView: () => v.resetView(),
});

post({ kind: 'ready' });
