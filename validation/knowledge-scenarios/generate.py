"""產生 #89 知識檢索驗證的虛構情境資料（validation/knowledge-scenarios）。全部是虛構資料。"""
import json, os, datetime as dt

root = 'validation/knowledge-scenarios'
os.makedirs(root, exist_ok=True)
proto = json.load(open('tests/fixtures/playback-prototype/plant.json', encoding='utf-8'))


def st(i, name, x, y):
    return {"id": f"st-{i}", "type": "station", "name": name, "aliases": [f"A-0{i}", f"100{i}"], "x": x, "y": y, "w": 2, "h": 2, "rotation": 0}


def cv(i, a, b):
    return {"id": f"cv-{i}", "type": "conveyor", "from": a, "to": b}


els = [st(1, "入庫口1", 0, 0), st(2, "輸送站2", 4, 0), st(3, "輸送站3", 8, 0), st(4, "出庫站4", 0, 8), st(5, "出庫站5", 4, 8),
       st(6, "輸送站6", 0, 16), st(7, "輸送站7", 4, 16),
       {"id": "eq-1", "type": "dock", "name": "EQ901", "aliases": ["EQ901"], "x": 8, "y": 8, "w": 2, "h": 2, "rotation": 0},
       {"id": "vh-1", "type": "vehicle", "name": "SSCA01", "aliases": ["SSCA01"], "x": 12, "y": 4, "w": 1, "h": 1, "rotation": 0},
       {"id": "vh-2", "type": "vehicle", "name": "SSCA02", "aliases": ["SSCA02"], "x": 12, "y": 10, "w": 1, "h": 1, "rotation": 0},
       {"id": "vh-3", "type": "vehicle", "name": "SRCA01", "aliases": ["SRCA01"], "x": 16, "y": 4, "w": 1, "h": 1, "rotation": 0},
       {"id": "rk-1", "type": "rack", "name": "料架 R01", "row": "01", "colFrom": 1, "colTo": 10, "levels": 5, "x": 18, "y": 0, "w": 10, "h": 1.5, "rotation": 0},
       cv(1, "st-1", "st-2"), cv(2, "st-2", "st-3"), cv(3, "st-4", "st-5"), cv(4, "st-5", "eq-1"), cv(5, "st-6", "st-7")]
plant = {"formatVersion": 1, "plant": "知識檢索驗證廠（虛構）", "revision": 1, "presenceSignal": "在席", "parseRules": proto["parseRules"],
         "layout": {"storageCodeRule": "RRCCCLL", "elements": els}}
json.dump(plant, open(f'{root}/plant.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)

kb = """代碼,說明,處置,出處
E101,搬送機行走馬達過載（虛構）,停止搬送機後檢查行走馬達與減速機,虛構維修手冊 第 3 章
E101,站點輸送馬達過載（虛構；站點發生時）,檢查輸送帶是否卡料,虛構維修手冊 第 5 章
E201,防火門感知器動作（虛構）,確認現場安全後由工程師復歸,虛構 SOP-01
E301,光電感測器遮斷逾時（虛構）,清潔光電感測器,虛構 SOP-07
E302,輸送馬達過載（虛構）,檢查輸送帶是否卡料、馬達是否過熱,虛構維修手冊 第 5 章
E303,條碼讀取失敗（虛構）,清潔條碼讀取器鏡頭並重新讀取,虛構 SOP-03
E304,條碼重複（虛構）,確認棧板標籤是否重複,虛構 SOP-03
E305,下游滿載（虛構）,等待下游清空後自動恢復,虛構 SOP-04
E306,升降台定位異常（虛構）,以手動模式重新定位,虛構維修手冊 第 7 章
E307,站點定位逾時（虛構）,檢查定位感測器,虛構維修手冊 第 5 章
E308,輸送帶馬達過熱（虛構）,停止輸送帶並通知工程師,虛構維修手冊 第 6 章
"""
open(f'{root}/plant.kb.csv', 'w', encoding='utf-8', newline='').write(kb.replace('\n', '\r\n'))
safety = """類別,關鍵字或情境,警報代碼,對第一線的指示,出處
防火,火警|防火門,E201,立即遠離並通知工程師，不得自行復歸,虛構安全規範 1
冒煙,冒煙|焦味,,立即按下緊急停止並通知工程師,虛構安全規範 2
"""
open(f'{root}/safety.csv', 'w', encoding='utf-8', newline='').write(safety.replace('\n', '\r\n'))
incidents = [
    {"id": "IR-001", "kind": "alarm", "alarmCodes": ["E302"], "devices": ["st-1"], "symptom": "入庫口1 輸送馬達過載，下游貨物停住（虛構）"},
    {"id": "IR-002", "kind": "alarm", "alarmCodes": ["E101"], "devices": ["vh-1"], "symptom": "搬送機 SSCA01 行走馬達過載，貨物晚到（虛構）"},
    {"id": "IR-003", "kind": "stall", "alarmCodes": [], "devices": ["st-5"], "symptom": "出庫站5 貨物停住沒有繼續搬（虛構）"},
    {"id": "IR-004", "kind": "stall", "alarmCodes": [], "devices": ["st-1"], "symptom": "入庫口1 讀到棧板後一直沒有派送（虛構）"},
]
json.dump(incidents, open(f'{root}/incidents.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)


def S(id, title, time, device, question, kb=(), devices=(), alarms=(), mustNot=(), incidents=(), safety=(), stall=False, gap=False):
    return {"id": id, "title": title, "time": f"2030-01-13T{time}", "device": device, "question": question,
            "expected": {"kb": list(kb), "devices": list(devices), "alarms": list(alarms), "incidents": list(incidents),
                         "safety": list(safety), "stall": stall, "noLog": gap},
            "mustNot": {"alarms": list(mustNot)}}


scen = [
    S("S01", "單一警報", "08:03:00", "st-2", "輸送站2 停止 E301", kb=["E301|光電感測器遮斷逾時（虛構）"], devices=["st-2"], alarms=["st-2|E301"]),
    S("S02", "上游的警報影響下游（佈局上下游）", "08:23:00", "st-2", "輸送站2 貨物沒有進來", kb=["E302|輸送馬達過載（虛構）"],
      devices=["st-1", "st-2"], alarms=["st-1|E302"], incidents=["IR-001"]),
    S("S03", "沿搬送路徑才找得到（搬送機警報）", "08:44:00", "st-4", "出庫站4 貨物晚到", kb=["E101|搬送機行走馬達過載（虛構）"],
      devices=["st-4", "vh-1"], alarms=["vh-1|E101"], incidents=["IR-002"]),
    S("S04", "沒有警報代碼的卡住（命令段之後）", "09:06:00", "st-5", "出庫站5 貨物停住", devices=["st-5"], incidents=["IR-003"], stall=True),
    S("S05", "時間窗外的干擾警報", "09:22:00", "st-6", "輸送站6 條碼讀不到", kb=["E303|條碼讀取失敗（虛構）", "E305|下游滿載（虛構）"],
      devices=["st-6", "st-7"], alarms=["st-6|E303", "st-7|E305"], mustNot=["st-6|E304"]),
    S("S06", "不相連設備的同時警報", "09:42:00", "st-1", "入庫口1 光電異常", kb=["E301|光電感測器遮斷逾時（虛構）"], devices=["st-1"],
      alarms=["st-1|E301"], mustNot=["st-7|E306"]),
    S("S07", "與沒有 LOG 的時段重疊", "10:05:00", "st-2", "輸送站2 停住", kb=["E307|站點定位逾時（虛構）"], devices=["st-2"], alarms=["st-2|E307"], gap=True),
    S("S08", "安全清單（警報代碼）", "10:22:00", "vh-2", "SSCA02 停止", kb=["E201|防火門感知器動作（虛構）"], devices=["vh-2"], alarms=["vh-2|E201"], safety=["防火"]),
    S("S09", "安全清單（關鍵字）", "10:42:00", "st-6", "輸送站6 冒煙", kb=["E308|輸送帶馬達過熱（虛構）"], devices=["st-6"], alarms=["st-6|E308"], safety=["冒煙"]),
    S("S10", "過去的異常紀錄（警報代碼相同、設備不同）", "11:02:00", "st-3", "輸送站3 停止 E302", kb=["E302|輸送馬達過載（虛構）"], devices=["st-3"],
      alarms=["st-3|E302"], incidents=["IR-001"]),
    S("S11", "同一代碼有多條知識庫條目（站點發生）", "11:22:00", "st-4", "出庫站4 馬達過載", kb=["E101|站點輸送馬達過載（虛構；站點發生時）"], devices=["st-4"],
      alarms=["st-4|E101"]),
    S("S12", "沒有警報代碼的卡住（讀到棧板後沒有派送）", "11:46:00", "st-1", "入庫口1 讀到棧板後沒有動", devices=["st-1"], incidents=["IR-004"], stall=True),
]
json.dump(scen, open(f'{root}/scenarios.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)

# ---- LOG（虛構）----
L = {'EXE': [], 'PLC': [], 'Server': []}
LOGGER = {'EXE': 'EXEDebug', 'PLC': 'PLCLOG', 'Server': 'WCSDebug'}


def T(h, m, s=0):
    return dt.datetime(2030, 1, 13, h, m, s)


def add(mod, t, msg):
    L[mod].append((t, f"{t:%Y-%m-%d %H:%M:%S}.{t.microsecond // 100:04d} | DEBUG | {LOGGER[mod]} | {msg}"))


t = T(8, 0)
while t <= T(12, 0):
    add('EXE', t + dt.timedelta(seconds=1), 'EXE 心跳')
    add('Server', t + dt.timedelta(seconds=2), 'WCS 心跳')
    if not (T(10, 0) <= t + dt.timedelta(seconds=3) < T(10, 10)):  # S07：PLC 10:00～10:10 沒有 LOG
        add('PLC', t + dt.timedelta(seconds=3), '[A-09] 心跳（A-09 尚未對應到 Layout）')
    t += dt.timedelta(seconds=60)


def alarm(t, who, code, clear=60):
    add('Server', t, f'警報發生 {who} {code}')
    add('Server', t + dt.timedelta(seconds=clear), f'警報復歸 {who} {code}')


def cmd(no, typ, pallet, src, dst, steps, done):
    add('EXE', steps[0][0] - dt.timedelta(seconds=5), f'建立任務 {no} 類型={typ} 棧板={pallet} 來源={src} 目的地={dst}')
    for i, (td, veh, dest, ta) in enumerate(steps, 1):
        add('Server', td, f'派送 {no} 第{i}段 → {veh} 目的地 {dest}')
        add('Server', ta, f'{no} 第{i}段 抵達 {dest}（{veh}）')
    add('EXE', done, f'完成回報 {no}')


alarm(T(8, 2), 'A-02', 'E301')                                   # S01
alarm(T(8, 21), 'A-01', 'E302', 90)                              # S02：上游 st-1
cmd('00060003', 'P', 'PLT-SC-0003', '1003', '1004', [(T(8, 40, 10), 'SSCA01', '1004', T(8, 43))], T(8, 43, 5))  # S03
alarm(T(8, 41), 'SSCA01', 'E101')
cmd('00060004', 'P', 'PLT-SC-0004', '1004', 'EQ901',             # S04：抵達出庫站5 後 7 分鐘才派送下一段
    [(T(8, 59, 50), 'SSCA02', '1005', T(9, 0, 30)), (T(9, 7, 30), 'SSCA02', 'EQ901', T(9, 8))], T(9, 8, 5))
alarm(T(9, 21), 'A-06', 'E303'); alarm(T(9, 23), 'A-07', 'E305'); alarm(T(9, 35), 'A-06', 'E304')  # S05
alarm(T(9, 41), 'A-01', 'E301'); alarm(T(9, 41, 30), 'A-07', 'E306')                              # S06
alarm(T(10, 3), 'A-02', 'E307')                                  # S07
alarm(T(10, 21), 'SSCA02', 'E201')                               # S08
alarm(T(10, 41), 'A-06', 'E308')                                 # S09
alarm(T(11, 1), 'A-03', 'E302')                                  # S10
alarm(T(11, 21), 'A-04', 'E101')                                 # S11
add('PLC', T(11, 40), '[A-01] 讀到棧板條碼 PLT-SC-0012 桶 D-0012-1 D-0012-2')  # S12：讀到後 7 分鐘才派送
cmd('00060012', 'S', 'PLT-SC-0012', '1001', '0100302',
    [(T(11, 47), 'SSCA01', '1003', T(11, 47, 40)), (T(11, 48), 'SRCA01', '0100302', T(11, 48, 50))], T(11, 48, 55))

for mod, ls in L.items():
    d = os.path.join(root, 'logs', '20300113', f'{mod}-2030-01-13')
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, LOGGER[mod] + '.txt'), 'w', encoding='utf-8', newline='') as f:
        for _, line in sorted(ls):
            f.write(line + '  \r\n\r\n')
print({m: len(v) for m, v in L.items()})
