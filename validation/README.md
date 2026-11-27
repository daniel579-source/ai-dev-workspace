# 技術驗證（#15）

驗證 ADR-0007（用戶端技術）與 ADR-0011（儲存與索引）的候選方案是否可行。這裡的程式是**驗證用**，不是正式產品；之後的骨架（#16）可以參考或沿用其中的做法。

> 所有測試資料都由 `TechValidation.DataGen` 產生，是**虛構**的資料（IP 使用文件專用網段 192.0.2.0/24，電話、命令、站點、設備都是編造的）。真實 LOG 不得放進這裡，也不得把真實 LOG 的內容寫進結果（CLAUDE.md 第 13 條、ADR-0009）。

## 專案

| 專案 | 用途 | 對應項目 |
|---|---|---|
| `src/TechValidation.Core` | 解析、正規化、匯入、SQLite 儲存與搜尋、狀態快照（簡化版） | ADR-0004、ADR-0010、ADR-0011 |
| `src/TechValidation.DataGen` | 虛構 LOG 產生器（「日期 → 模組」結構、Big5／UTF-8、延續行、輪替檔） | T-02 |
| `src/TechValidation.Bench` | 匯入、索引大小、搜尋、正規表示式、串連、快照、排序、可攜性的量測，並與 DuckDB、Lucene.NET 比較 | T-03～T-13 |
| `src/TechValidation.UiSpike` | WPF＋AvalonDock：10 萬筆事件清單、時間軸、2D 畫布、版面儲存與還原；自我檢查 | ADR-0007 第 1～4 項、T-01 |
| `src/TechValidation.KnowledgeGraph` | 知識檢索的規則關聯圖（ADR-0015）：以 `knowledge-scenarios\` 的虛構情境集量測知識庫條目、相關設備與警報、異常紀錄、安全清單的召回與精確度，並與關鍵字比對比較（#89）；另以本機 Ollama 的 embedding 比較向量搜尋（ADR-0022）。參考 `src/LogTrace.Engine` | ADR-0015 |
| `src/TechValidation.LlmQuery` | 本機 LLM 把自然語言問題轉成搜尋條件（ADR-0013 第 5、8 點）：以 `llm-query\` 的虛構問題集（可轉換 50 題、不能轉換 6 題），用 ONNX Runtime GenAI 在 CPU 比較 Phi-4-mini 與 Qwen3-4B，各跑「只用提示詞／以 JSON schema 限制」×「有範例／沒有範例」4 種組合，量測正確率、速度與記憶體（#55）。第二輪加上 `--hybrid`：時間與設備編號由規則抽出、換成佔位符，LLM 只處理關鍵字、模組、等級與 unsupported，另以 `questions-holdout.json`（規則寫好後才撰寫）檢查是否只對原題目有效。第三輪加上 `--expand`：LLM 另外輸出延伸詞，以 `case-log.json`（虛構案件 LOG）驗證後只當建議，原文關鍵字以程式檢查，並以 `questions-expand.json`（中文提問、LOG 用英文）量測建議的命中率。第四輪（只量 Qwen3-4B）修改 `--expand` 的提示詞與範例，程式檢查加上「關鍵字與已填的模組或等級相同時刪除」，並以改提示詞之前撰寫的 `questions-holdout2.json` 檢查；第三輪的提示詞與檢查見 git 紀錄。模型放在 repo 以外 | ADR-0013 |
| `tests/TechValidation.Tests` | 正規化（T-13）與解析規則的單元測試 | — |

## 需要

- .NET 10 SDK（開發與建置）。執行 UiSpike 的發佈版**不需要**安裝 .NET。

## 執行

```powershell
cd validation

# 單元測試
dotnet test

# 1. 產生虛構資料（預設 7 天、每天 500 MB，約 3.5 GB）
dotnet run -c Release --project src/TechValidation.DataGen -- --out D:\tv-data\logs

# 2. 量測（需要約 30 GB 磁碟空間），報告寫到 results\
dotnet run -c Release --project src/TechValidation.Bench -- --logs D:\tv-data\logs --work D:\tv-data\work --report results\bench-<電腦名稱>.md

# 3. 冷快取：重新開機後，沿用已匯入的案件只跑查詢
dotnet run -c Release --project src/TechValidation.Bench -- --logs D:\tv-data\logs --work D:\tv-data\work --reuse --skip-compare --cold --report results\bench-<電腦名稱>-cold.md

# 知識檢索的規則關聯圖（#89，ADR-0015）：虛構情境集在 knowledge-scenarios\（generate.py 可以重新產生）
dotnet run -c Release --project src/TechValidation.KnowledgeGraph -- --data knowledge-scenarios --report results\knowledge-graph-89-<電腦名稱>.md
# 向量搜尋比較（#89，ADR-0022）：需要本機 Ollama 與模型（只送虛構資料到 127.0.0.1）；模型以「名稱|e5|來源」列出，第二欄為 e5 時加 query:/passage: 前綴
dotnet run -c Release --project src/TechValidation.KnowledgeGraph -- --data knowledge-scenarios --models "e5test-c3f16|e5|來源;bge-m3||來源" --vector-report results\knowledge-vector-89-<電腦名稱>.md
# 「用詞不同」情境集（#89，ADR-0022）：虛構資料在 knowledge-wording\（kb.csv 40 條、questions.json 60 題，只有問題文字、不看 LOG）
dotnet run -c Release --project src/TechValidation.KnowledgeGraph -- --wording knowledge-wording --models "e5test-c3f16|e5|來源;bge-m3||來源" --vector-report results\knowledge-wording-89-<電腦名稱>.md

# 本機 LLM：自然語言轉搜尋條件（#55，ADR-0013）。模型放在 repo 以外，例如 D:\_case\models\（Hugging Face：microsoft/Phi-4-mini-instruct-onnx 的 cpu-int4-rtn-block-32-acc-level-4、
# onnx-community/Qwen3-4B-ONNX 的 onnxruntime/cpu_and_mobile/cpu-int4-kld-block-128；下載後核對 SHA-256）。只用 CPU，每個模型約 50 分鐘
dotnet run -c Release --project src/TechValidation.LlmQuery -- --model D:\_case\models\phi-4-mini-cpu-int4 --family phi --name Phi-4-mini --questions llm-query\questions.json --out results\llm-query-55-phi-<電腦名稱>.json
dotnet run -c Release --project src/TechValidation.LlmQuery -- --model D:\_case\models\qwen3-4b-cpu-int4 --family qwen --name Qwen3-4B --questions llm-query\questions.json --out results\llm-query-55-qwen-<電腦名稱>.json
dotnet run -c Release --project src/TechValidation.LlmQuery -- --summarize results\llm-query-55-phi-<電腦名稱>.json results\llm-query-55-qwen-<電腦名稱>.json --report results\llm-query-55-<電腦名稱>.md
# 第三輪（延伸詞＋程式檢查，只跑有範例的組合）：--expand；--case-log 預設 llm-query\case-log.json；--summarize 可加 --note 註記量測條件
dotnet run -c Release --project src/TechValidation.LlmQuery -- --expand --model D:\_case\models\qwen3-4b-cpu-int4 --family qwen --name Qwen3-4B --questions llm-query\questions-expand.json --examples llm-query\questions.json --out results\llm-query-55-expand-words-qwen-<電腦名稱>.json
# 第四輪：同上，另加新保留測試集
dotnet run -c Release --project src/TechValidation.LlmQuery -- --expand --model D:\_case\models\qwen3-4b-cpu-int4 --family qwen --name Qwen3-4B --questions llm-query\questions-holdout2.json --examples llm-query\questions.json --out results\llm-query-55-r4-holdout2-qwen-<電腦名稱>.json
# 第二輪（混合做法）：只檢查規則，或加上 --hybrid 執行；保留測試集用 --examples 沿用主題目檔的範例
dotnet run -c Release --project src/TechValidation.LlmQuery -- --check-rules --hybrid --family qwen --questions llm-query\questions-holdout.json --examples llm-query\questions.json
dotnet run -c Release --project src/TechValidation.LlmQuery -- --hybrid --model D:\_case\models\qwen3-4b-cpu-int4 --family qwen --name Qwen3-4B --questions llm-query\questions.json --out results\llm-query-55-hybrid-qwen-<電腦名稱>.json

# 4. UI 驗證：發佈 self-contained 單一資料夾
dotnet publish src/TechValidation.UiSpike -c Release -o publish\UiSpike
publish\UiSpike\TechValidation.UiSpike.exe --selftest results\uispike-<電腦名稱>.json --exit
```

資料與工作資料夾請放在 repo **以外**的位置。

## 需要手動確認的項目

自動量測無法取代的部分，請在對應的電腦上操作並把結果記到 `results\`。工程師電腦上用發佈版 LogTrace 的步驟見 [`engineer-pc-checklist.md`](engineer-pc-checklist.md)（#47 A 類）。

| 項目 | 做法 |
|---|---|
| 乾淨、離線的 Windows 10（ADR-0007 第 3 項、T-01） | 在沒有安裝 .NET、拔掉網路的 Windows 10 上，複製 `publish\UiSpike` 整個資料夾，執行 `TechValidation.UiSpike.exe --selftest result.json`。確認程式能開啟，且 `self_contained=true`、`fts5_trigram_ok=true`、`nfkc_ok=true` |
| 捲動、縮放、拖曳的體感（ADR-0007 第 1、4 項） | 在 UiSpike 中用滑鼠捲動事件清單、拖曳捲軸、用滾輪縮放時間軸與畫布、拖曳畫布上的元素。自我檢查以「p95 格時間 ≤ 33 ms（約 30 fps）」為**建議**門檻，最後以實際操作的感受判斷 |
| 版面（ADR-0007 第 2 項） | 拖動面板、浮動時間軸 →「儲存版面」→ 關閉程式 → 重開 →「載入版面」 |
| 複製案件到另一台電腦（T-12） | 把 Bench 產生的 `case-1day-parallel` 資料夾複製到另一台電腦，以 Bench 的 `--reuse` 執行，比對查詢結果 |
| 不同 Windows 版本的正規化（T-13） | 在其他版本的 Windows 執行 UiSpike 自我檢查，比對 `normalization_cases_passed` 與 `icu` |
| 目標硬體（T-14） | 確認工程師電腦的磁碟類型與記憶體；若與開發電腦差異大，在該電腦重跑 Bench |
