# 本機 LLM：自然語言轉搜尋條件（#55，ADR-0013）

- 量測：2026-11-27 22:52，開發電腦（16 執行緒），裝置 CPU，ONNX Runtime GenAI 0.20.0，貪婪解碼。
- 題目：虛構；可轉換 25 題、不能轉換（應回答 unsupported）5 題；有範例時附 7 個不在測試題中的範例（`llm-query/questions.json`）。
- **注意**：量測時電腦有其他程式（遊戲）占用 CPU，速度數字不能與前兩輪比較；正確率不受影響（貪婪解碼）
- 第三輪：LLM 另外輸出延伸詞；「完全正確」為程式檢查後（關鍵字、模組、等級必須在問題原文出現；單一 anyOf 改為 allOf），括號內另列檢查前；延伸詞到虛構案件 LOG（`llm-query/case-log.json`）部分比對，有出現才算建議。
- 混合做法：時間與設備編號由規則抽出並換成佔位符，LLM 只處理關鍵字、模組、等級與 unsupported；from、to、fields 是規則的結果（模型回答 unsupported 時整題算 unsupported）。
- 「完全正確」：關鍵字三框、時間起訖、模組、等級、解析欄位、unsupported 全部與預期相同（關鍵字不分大小寫、不計順序）。

## 正確率

| 模型 | 組合 | 完全正確（可轉換 25） | 不能轉換時回答 unsupported（5） | 可轉換卻回答 unsupported | 輸出不是有效 JSON | allOf | anyOf | noneOf | from | to | modules | levels | fields |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Qwen3-4B | 只用提示詞／有範例 | 68%（17；檢查前 10） | 5／5 | 0 | 0 | 72% | 92% | 100% | 96% | 96% | 96% | 88% | 100% |
| Qwen3-4B | schema 限制／有範例 | 64%（16；檢查前 9） | 5／5 | 0 | 0 | 72% | 92% | 92% | 96% | 96% | 96% | 92% | 100% |
| Phi-4-mini | 只用提示詞／有範例 | 36%（9；檢查前 6） | 4／5 | 2 | 4 | 48% | 76% | 80% | 76% | 76% | 84% | 64% | 80% |
| Phi-4-mini | schema 限制／有範例 | 36%（9；檢查前 6） | 4／5 | 2 | 3 | 48% | 80% | 84% | 80% | 80% | 88% | 68% | 84% |

各部分的比例只算可轉換的 25 題。

## 延伸詞建議

| 模型 | 組合 | 有預期用語的題目：建議中有正確用語 | 建議中正確的比例 | 每題平均建議數（有預期用語的題目） | 每題平均建議數（其他題目） | 延伸詞被案件 LOG 擋掉的比例 | 模組／等級提示次數 |
|---|---|---|---|---|---|---|---|
| Qwen3-4B | 只用提示詞／有範例 | － | － | － | 0.2 | 84% | 5 |
| Qwen3-4B | schema 限制／有範例 | － | － | － | 0.2 | 87% | 3 |
| Phi-4-mini | 只用提示詞／有範例 | － | － | － | 0.0 | 100% | 5 |
| Phi-4-mini | schema 限制／有範例 | － | － | － | 0.0 | 100% | 5 |

延伸詞被擋掉：模型提出、但案件 LOG 中沒有出現（或與原文關鍵字相同）的字，不顯示給工程師。

## 速度與記憶體

| 模型 | 載入（秒） | 峰值記憶體（MB） | 組合 | 提示詞 token（中位數） | 第一個 token（秒，中位數） | 每題總時間（秒，中位數／最大） | 產生速度（token/秒，中位數） |
|---|---|---|---|---|---|---|---|
| Qwen3-4B | 7.7 | 4,266 | 只用提示詞／有範例 | 738 | 59.91 | 70.9／90.8 | 1.2 |
| Qwen3-4B | 7.7 | 4,266 | schema 限制／有範例 | 738 | 60.34 | 74.6／91.5 | 1.2 |
| Phi-4-mini | 8.6 | 4,697 | 只用提示詞／有範例 | 735 | 49.73 | 54.9／233.3 | 1.8 |
| Phi-4-mini | 8.6 | 4,697 | schema 限制／有範例 | 735 | 11.92 | 13.5／50.5 | 8.7 |

## 錯誤的題目

- **Qwen3-4B／只用提示詞／有範例**：h01（allOf）；h02（allOf、levels）；h07（allOf、from、to）；h10（anyOf、levels）；h16（allOf、levels）；h18（allOf、anyOf）；h22（allOf）；h24（allOf、modules）
- **Qwen3-4B／schema 限制／有範例**：h01（allOf）；h02（allOf、levels）；h07（allOf、from、to、modules）；h10（anyOf、levels）；h16（allOf、noneOf）；h18（allOf、anyOf）；h20（noneOf）；h22（allOf）；h24（allOf）
- **Phi-4-mini／只用提示詞／有範例**：h01（allOf）；h02（allOf、levels）；h03（allOf）；h06（allOf、anyOf）；h07（allOf、from、to、levels）；h10（無效 JSON）；h12（unsupported、from、to）；h13（無效 JSON）；h16（allOf、levels）；h17（unsupported、fields）；h18（anyOf、noneOf）；h19（無效 JSON）；h20（無效 JSON）；h22（allOf、levels）；h23（allOf、levels）；h24（allOf）；hu5（unsupported、allOf、fields）
- **Phi-4-mini／schema 限制／有範例**：h01（allOf）；h02（allOf、levels）；h03（allOf）；h06（allOf、anyOf）；h07（allOf、from、to、levels）；h10（allOf）；h12（unsupported、from、to）；h13（無效 JSON）；h16（allOf、levels）；h17（unsupported、fields）；h18（anyOf、noneOf）；h19（無效 JSON）；h20（無效 JSON）；h22（allOf、levels）；h23（allOf、levels）；h24（allOf）；hu5（unsupported、allOf、fields）
