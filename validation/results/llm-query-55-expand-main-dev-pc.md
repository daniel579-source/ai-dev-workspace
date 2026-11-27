# 本機 LLM：自然語言轉搜尋條件（#55，ADR-0013）

- 量測：2026-11-27 20:17，開發電腦（16 執行緒），裝置 CPU，ONNX Runtime GenAI 0.20.0，貪婪解碼。
- 題目：虛構；可轉換 50 題、不能轉換（應回答 unsupported）6 題；有範例時附 7 個不在測試題中的範例（`llm-query/questions.json`）。
- **注意**：量測時電腦有其他程式（遊戲）占用 CPU，速度數字不能與前兩輪比較；正確率不受影響（貪婪解碼）
- 第三輪：LLM 另外輸出延伸詞；「完全正確」為程式檢查後（關鍵字、模組、等級必須在問題原文出現；單一 anyOf 改為 allOf），括號內另列檢查前；延伸詞到虛構案件 LOG（`llm-query/case-log.json`）部分比對，有出現才算建議。
- 混合做法：時間與設備編號由規則抽出並換成佔位符，LLM 只處理關鍵字、模組、等級與 unsupported；from、to、fields 是規則的結果（模型回答 unsupported 時整題算 unsupported）。
- 「完全正確」：關鍵字三框、時間起訖、模組、等級、解析欄位、unsupported 全部與預期相同（關鍵字不分大小寫、不計順序）。

## 正確率

| 模型 | 組合 | 完全正確（可轉換 50） | 不能轉換時回答 unsupported（6） | 可轉換卻回答 unsupported | 輸出不是有效 JSON | allOf | anyOf | noneOf | from | to | modules | levels | fields |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Qwen3-4B | 只用提示詞／有範例 | 58%（29；檢查前 21） | 6／6 | 7 | 0 | 78% | 92% | 94% | 100% | 100% | 100% | 90% | 86% |
| Qwen3-4B | schema 限制／有範例 | 58%（29；檢查前 21） | 6／6 | 7 | 0 | 78% | 92% | 92% | 100% | 100% | 100% | 90% | 86% |
| Phi-4-mini | 只用提示詞／有範例 | 38%（19；檢查前 16） | 4／6 | 14 | 1 | 70% | 92% | 96% | 92% | 92% | 96% | 84% | 74% |
| Phi-4-mini | schema 限制／有範例 | 36%（18；檢查前 15） | 4／6 | 14 | 1 | 70% | 92% | 96% | 92% | 92% | 96% | 80% | 74% |

各部分的比例只算可轉換的 50 題。

## 延伸詞建議

| 模型 | 組合 | 有預期用語的題目：建議中有正確用語 | 建議中正確的比例 | 每題平均建議數（有預期用語的題目） | 每題平均建議數（其他題目） | 延伸詞被案件 LOG 擋掉的比例 | 模組／等級提示次數 |
|---|---|---|---|---|---|---|---|
| Qwen3-4B | 只用提示詞／有範例 | － | － | － | 0.1 | 83% | 10 |
| Qwen3-4B | schema 限制／有範例 | － | － | － | 0.2 | 81% | 10 |
| Phi-4-mini | 只用提示詞／有範例 | － | － | － | 0.0 | 94% | 10 |
| Phi-4-mini | schema 限制／有範例 | － | － | － | 0.0 | 93% | 11 |

延伸詞被擋掉：模型提出、但案件 LOG 中沒有出現（或與原文關鍵字相同）的字，不顯示給工程師。

## 速度與記憶體

| 模型 | 載入（秒） | 峰值記憶體（MB） | 組合 | 提示詞 token（中位數） | 第一個 token（秒，中位數） | 每題總時間（秒，中位數／最大） | 產生速度（token/秒，中位數） |
|---|---|---|---|---|---|---|---|
| Qwen3-4B | 4.2 | 4,280 | 只用提示詞／有範例 | 738 | 13.29 | 15.3／19.3 | 6.7 |
| Qwen3-4B | 4.2 | 4,280 | schema 限制／有範例 | 738 | 14.07 | 16.6／20.7 | 5.8 |
| Phi-4-mini | 9.3 | 4,703 | 只用提示詞／有範例 | 736 | 12.43 | 14.0／64.4 | 7.4 |
| Phi-4-mini | 9.3 | 4,703 | schema 限制／有範例 | 736 | 49.49 | 52.7／233.1 | 1.8 |

## 錯誤的題目

- **Qwen3-4B／只用提示詞／有範例**：q01（allOf、levels）；q03（allOf）；q04（allOf、anyOf）；q05（unsupported、fields）；q06（allOf、levels）；q07（unsupported、fields）；q08（unsupported、fields）；q12（allOf）；q14（allOf）；q18（allOf）；q19（unsupported、fields）；q20（unsupported、fields）；q26（anyOf、noneOf）；q31（allOf、levels）；q34（noneOf）；q36（unsupported、fields）；q39（anyOf、noneOf）；q41（allOf、levels）；q44（allOf、levels）；q45（allOf、anyOf）；q48（unsupported、fields）
- **Qwen3-4B／schema 限制／有範例**：q01（allOf、levels）；q03（allOf）；q04（allOf、anyOf）；q05（unsupported、fields）；q06（allOf、noneOf、levels）；q07（unsupported、fields）；q08（unsupported、fields）；q12（allOf）；q14（allOf）；q18（allOf）；q19（unsupported、fields）；q20（unsupported、fields）；q26（anyOf、noneOf）；q31（allOf、levels）；q34（noneOf）；q36（unsupported、fields）；q39（anyOf、noneOf）；q41（allOf、levels）；q44（allOf、levels）；q45（allOf、anyOf）；q48（unsupported、fields）
- **Phi-4-mini／只用提示詞／有範例**：q01（allOf）；q02（unsupported、fields）；q03（unsupported、from、to、fields）；q04（allOf、anyOf）；q05（unsupported、fields）；q06（allOf、levels）；q07（unsupported、fields）；q08（unsupported、fields）；q10（unsupported、fields）；q12（allOf）；q14（allOf）；q16（allOf、levels）；q18（modules）；q19（unsupported、fields）；q20（unsupported、fields）；q23（unsupported、fields）；q26（levels）；q27（unsupported、from、to）；q28（unsupported、fields）；q30（allOf、anyOf）；q31（allOf、levels）；q32（unsupported、fields）；q33（allOf）；q34（allOf、noneOf、levels）；q36（unsupported、fields）；q40（unsupported、from、to）；q41（無效 JSON）；q44（allOf、levels）；q45（allOf、anyOf）；q48（allOf）；q49（allOf、levels）；u04（unsupported、noneOf、fields）；u06（unsupported、allOf、modules）
- **Phi-4-mini／schema 限制／有範例**：q01（allOf）；q02（unsupported、fields）；q03（unsupported、from、to、fields）；q04（allOf、anyOf）；q05（unsupported、fields）；q06（allOf、levels）；q07（unsupported、fields）；q08（unsupported、fields）；q10（unsupported、fields）；q12（allOf）；q14（allOf、levels）；q16（allOf、levels）；q18（modules）；q19（unsupported、fields）；q20（unsupported、fields）；q23（unsupported、fields）；q26（levels）；q27（unsupported、from、to）；q28（unsupported、fields）；q30（allOf、anyOf）；q31（allOf、levels）；q32（unsupported、fields）；q33（allOf）；q34（allOf、noneOf、levels）；q36（unsupported、fields）；q37（無效 JSON）；q40（unsupported、from、to）；q41（levels）；q44（allOf、levels）；q45（allOf、anyOf）；q48（allOf）；q49（allOf、levels）；u04（unsupported、noneOf、fields）；u06（unsupported、allOf、modules）
