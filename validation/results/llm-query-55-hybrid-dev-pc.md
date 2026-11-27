# 本機 LLM：自然語言轉搜尋條件（#55，ADR-0013）

- 量測：2026-11-27 17:54，開發電腦（16 執行緒），裝置 CPU，ONNX Runtime GenAI 0.20.0，貪婪解碼。
- 題目：虛構；可轉換 50 題、不能轉換（應回答 unsupported）6 題；有範例時附 6 個不在測試題中的範例（`llm-query/questions.json`）。
- 混合做法：時間與設備編號由規則抽出並換成佔位符，LLM 只處理關鍵字、模組、等級與 unsupported；from、to、fields 是規則的結果（模型回答 unsupported 時整題算 unsupported）。
- 「完全正確」：關鍵字三框、時間起訖、模組、等級、解析欄位、unsupported 全部與預期相同（關鍵字不分大小寫、不計順序）。

## 正確率

| 模型 | 組合 | 完全正確（可轉換 50） | 不能轉換時回答 unsupported（6） | 可轉換卻回答 unsupported | 輸出不是有效 JSON | allOf | anyOf | noneOf | from | to | modules | levels | fields |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Phi-4-mini | 只用提示詞／有範例 | 36%（18） | 5／6 | 12 | 1 | 74% | 92% | 94% | 96% | 96% | 86% | 80% | 74% |
| Phi-4-mini | 只用提示詞／沒有範例 | 2%（1） | 6／6 | 22 | 1 | 50% | 70% | 58% | 90% | 88% | 74% | 68% | 66% |
| Phi-4-mini | schema 限制／有範例 | 36%（18） | 5／6 | 14 | 1 | 70% | 92% | 94% | 92% | 92% | 86% | 82% | 74% |
| Phi-4-mini | schema 限制／沒有範例 | 4%（2） | 6／6 | 17 | 0 | 48% | 76% | 74% | 94% | 92% | 82% | 80% | 70% |
| Qwen3-4B | 只用提示詞／有範例 | 40%（20） | 6／6 | 7 | 0 | 72% | 82% | 92% | 98% | 98% | 78% | 92% | 86% |
| Qwen3-4B | 只用提示詞／沒有範例 | 16%（8） | 6／6 | 6 | 0 | 36% | 92% | 86% | 94% | 94% | 62% | 70% | 92% |
| Qwen3-4B | schema 限制／有範例 | 34%（17） | 6／6 | 7 | 1 | 68% | 84% | 86% | 96% | 96% | 76% | 76% | 84% |
| Qwen3-4B | schema 限制／沒有範例 | 20%（10） | 6／6 | 4 | 0 | 42% | 92% | 92% | 96% | 96% | 62% | 62% | 96% |

各部分的比例只算可轉換的 50 題。

## 速度與記憶體

| 模型 | 載入（秒） | 峰值記憶體（MB） | 組合 | 提示詞 token（中位數） | 第一個 token（秒，中位數） | 每題總時間（秒，中位數／最大） | 產生速度（token/秒，中位數） |
|---|---|---|---|---|---|---|---|
| Phi-4-mini | 5.5 | 4,525 | 只用提示詞／有範例 | 613 | 8.13 | 9.1／42.2 | 9.0 |
| Phi-4-mini | 5.5 | 4,525 | 只用提示詞／沒有範例 | 464 | 6.09 | 8.4／39.6 | 9.4 |
| Phi-4-mini | 5.5 | 4,525 | schema 限制／有範例 | 613 | 8.01 | 9.0／41.0 | 9.2 |
| Phi-4-mini | 5.5 | 4,525 | schema 限制／沒有範例 | 464 | 6.06 | 8.4／11.5 | 9.7 |
| Qwen3-4B | 4.2 | 4,138 | 只用提示詞／有範例 | 616 | 10.18 | 11.4／13.6 | 7.3 |
| Qwen3-4B | 4.2 | 4,138 | 只用提示詞／沒有範例 | 416 | 6.81 | 9.5／11.8 | 7.8 |
| Qwen3-4B | 4.2 | 4,138 | schema 限制／有範例 | 616 | 10.34 | 11.9／56.2 | 7.1 |
| Qwen3-4B | 4.2 | 4,138 | schema 限制／沒有範例 | 416 | 7.14 | 10.0／13.0 | 7.5 |

## 錯誤的題目

- **Phi-4-mini／只用提示詞／有範例**：q02（unsupported、fields）；q03（unsupported、from、to、fields）；q04（allOf、anyOf）；q05（unsupported、fields）；q06（noneOf、levels）；q07（unsupported、fields）；q08（unsupported、fields）；q10（unsupported、fields）；q11（allOf、levels）；q14（levels）；q15（allOf）；q16（modules）；q18（allOf）；q19（unsupported、fields）；q20（unsupported、fields）；q23（unsupported、fields）；q24（allOf）；q25（allOf）；q28（unsupported、fields）；q31（modules）；q32（unsupported、fields）；q34（allOf、noneOf、levels）；q35（allOf、levels）；q36（unsupported、fields）；q37（modules、levels）；q38（allOf、anyOf）；q39（modules）；q42（modules、levels）；q44（無效 JSON）；q45（allOf、anyOf、modules、levels）；q48（allOf）；q49（allOf、levels）；u04（unsupported、noneOf、fields）
- **Phi-4-mini／只用提示詞／沒有範例**：q01（allOf、modules、levels）；q02（unsupported、fields）；q03（unsupported、from、to、fields）；q04（allOf、anyOf）；q05（unsupported、fields）；q06（unsupported、noneOf、modules、levels）；q07（unsupported、fields）；q08（unsupported、fields）；q10（unsupported、fields）；q11（allOf、anyOf、noneOf、modules）；q12（allOf、anyOf、noneOf、levels）；q13（allOf、noneOf）；q14（allOf、noneOf）；q15（allOf、anyOf）；q16（allOf、noneOf、modules）；q17（unsupported、allOf、noneOf）；q18（allOf、anyOf、noneOf、modules）；q19（unsupported、fields）；q20（unsupported、fields）；q21（unsupported、allOf、fields）；q22（allOf、noneOf）；q23（unsupported、fields）；q24（無效 JSON）；q25（noneOf）；q26（allOf、anyOf、noneOf、modules、levels）；q27（unsupported、from、to）；q28（unsupported、fields）；q29（noneOf、modules、levels）；q30（allOf、anyOf、levels）；q31（allOf）；q32（unsupported、fields）；q33（noneOf）；q34（unsupported、noneOf、levels）；q35（allOf、levels）；q36（unsupported、fields）；q37（allOf、anyOf、noneOf、modules、levels）；q38（allOf、anyOf）；q39（allOf、anyOf、noneOf、modules）；q40（unsupported、from、to）；q41（allOf、noneOf）；q42（noneOf、modules、levels）；q43（unsupported、allOf、modules）；q44（unsupported、noneOf、from、to、levels、fields）；q45（anyOf、levels）；q46（unsupported、to、fields）；q47（allOf、anyOf、levels）；q48（allOf、anyOf、noneOf、modules、levels）；q49（anyOf、levels）；q50（unsupported、allOf、fields）
- **Phi-4-mini／schema 限制／有範例**：q02（unsupported、fields）；q03（unsupported、from、to、fields）；q04（allOf、anyOf）；q05（unsupported、fields）；q06（allOf、noneOf、levels）；q07（unsupported、fields）；q08（unsupported、fields）；q10（unsupported、fields）；q11（allOf、modules）；q13（allOf）；q14（levels）；q19（unsupported、fields）；q20（unsupported、fields）；q22（allOf）；q23（unsupported、fields）；q25（allOf）；q27（unsupported、from、to）；q28（unsupported、fields）；q31（allOf、modules、levels）；q32（unsupported、fields）；q34（allOf、noneOf、levels）；q35（allOf、levels）；q36（unsupported、fields）；q38（allOf、anyOf）；q39（modules）；q40（unsupported、from、to）；q41（allOf、modules）；q42（modules、levels）；q44（無效 JSON）；q45（allOf、anyOf、modules、levels）；q48（allOf）；q49（allOf、levels）；u04（unsupported、noneOf、fields）
- **Phi-4-mini／schema 限制／沒有範例**：q01（allOf）；q02（unsupported、fields）；q03（unsupported、from、to、fields）；q04（allOf、anyOf）；q05（unsupported、fields）；q06（allOf）；q07（unsupported、fields）；q08（unsupported、fields）；q10（unsupported、fields）；q11（allOf、anyOf、noneOf、modules）；q12（allOf、anyOf、noneOf、levels）；q13（allOf、noneOf）；q14（allOf、noneOf）；q15（noneOf）；q16（allOf、noneOf、modules）；q17（modules、levels）；q18（allOf、modules）；q19（unsupported、fields）；q20（unsupported、fields）；q21（unsupported、allOf、fields）；q22（allOf、noneOf）；q23（unsupported、fields）；q24（allOf）；q25（allOf、anyOf）；q26（allOf、anyOf、noneOf、modules、levels）；q27（unsupported、from、to）；q28（unsupported、fields）；q29（noneOf、modules、levels）；q30（anyOf）；q31（allOf）；q32（unsupported、fields）；q33（noneOf）；q34（allOf、anyOf）；q35（allOf、levels）；q36（allOf、noneOf）；q38（allOf）；q39（allOf、anyOf、noneOf、modules）；q40（unsupported、from、to）；q41（allOf）；q42（unsupported、fields）；q43（allOf、levels）；q44（allOf、modules）；q45（anyOf、levels）；q46（unsupported、to、fields）；q47（allOf、anyOf、levels）；q48（allOf、anyOf、noneOf、modules、levels）；q49（anyOf、levels）；q50（unsupported、allOf、fields）
- **Qwen3-4B／只用提示詞／有範例**：q01（allOf、noneOf、modules、levels）；q02（modules）；q03（unsupported、from、to、fields）；q04（allOf、anyOf）；q05（unsupported、fields）；q07（unsupported、fields）；q08（unsupported、fields）；q09（levels）；q10（modules）；q16（anyOf、modules）；q18（allOf、modules、levels）；q19（unsupported、fields）；q20（unsupported、fields）；q21（allOf、noneOf）；q23（modules）；q25（allOf、anyOf）；q28（modules）；q29（allOf、anyOf、modules）；q31（allOf、noneOf、modules）；q32（modules）；q35（levels）；q38（allOf、anyOf）；q39（allOf、anyOf）；q41（allOf、noneOf）；q42（modules）；q43（allOf、anyOf）；q45（allOf、anyOf）；q48（unsupported、fields）；q49（allOf）；q50（allOf、anyOf）
- **Qwen3-4B／只用提示詞／沒有範例**：q01（allOf、levels）；q02（allOf、modules、levels）；q03（allOf、noneOf）；q04（allOf、anyOf）；q05（allOf、modules、levels）；q06（allOf、modules、levels）；q07（allOf）；q08（allOf、modules、levels）；q10（allOf、noneOf、modules）；q12（allOf、noneOf）；q13（allOf）；q14（allOf）；q15（unsupported、fields）；q16（allOf、modules）；q18（allOf、levels）；q19（allOf、modules、levels）；q20（unsupported、fields）；q22（unsupported、from、to、fields）；q23（allOf、modules、levels）；q24（allOf、modules、levels）；q25（allOf、anyOf）；q26（modules、levels）；q27（unsupported、from、to）；q28（allOf、noneOf、modules）；q29（modules、levels）；q30（allOf）；q31（allOf、levels）；q32（allOf、noneOf、modules）；q33（unsupported、fields）；q34（allOf、noneOf、levels）；q36（allOf、noneOf）；q39（modules）；q40（unsupported、from、to）；q41（allOf、modules）；q43（allOf）；q44（allOf、levels）；q45（allOf、anyOf）；q46（allOf、anyOf）；q47（allOf、modules）；q48（allOf、modules、levels）；q49（allOf、modules）；q50（modules）
- **Qwen3-4B／schema 限制／有範例**：q01（allOf、noneOf、modules、levels）；q02（modules）；q03（unsupported、from、to、fields）；q04（allOf、anyOf）；q05（unsupported、fields）；q06（noneOf、levels）；q07（unsupported、fields）；q08（unsupported、fields）；q09（levels）；q10（modules、levels）；q16（anyOf）；q18（allOf、modules、levels）；q19（unsupported、fields）；q20（unsupported、fields）；q21（allOf、noneOf）；q23（modules）；q25（allOf、anyOf）；q26（allOf、noneOf、levels）；q28（modules、levels）；q29（allOf、anyOf、modules）；q31（allOf、modules）；q32（modules、levels）；q35（levels）；q38（allOf、anyOf）；q39（allOf、noneOf、levels）；q41（allOf、noneOf）；q42（modules）；q43（allOf、levels）；q45（allOf、anyOf）；q47（無效 JSON）；q48（unsupported、fields）；q49（allOf）；q50（allOf、anyOf、modules）
- **Qwen3-4B／schema 限制／沒有範例**：q01（allOf、levels）；q02（allOf、modules、levels）；q03（allOf、noneOf）；q04（allOf、anyOf）；q05（allOf、modules、levels）；q06（allOf、modules、levels）；q07（allOf）；q08（allOf、modules、levels）；q10（modules、levels）；q12（allOf、noneOf）；q13（allOf）；q14（allOf）；q15（unsupported、fields）；q16（allOf、modules）；q18（allOf、levels）；q19（allOf、modules、levels）；q20（unsupported、fields）；q23（allOf、modules、levels）；q24（allOf、modules、levels）；q25（allOf、anyOf）；q26（modules、levels）；q27（unsupported、from、to）；q28（modules、levels）；q29（modules、levels）；q30（allOf）；q31（allOf、levels）；q32（modules、levels）；q34（allOf、noneOf、levels）；q36（allOf、noneOf）；q39（modules）；q40（unsupported、from、to）；q41（allOf、modules）；q43（allOf）；q44（allOf、levels）；q45（allOf、anyOf）；q46（allOf、anyOf）；q47（allOf、modules）；q48（allOf、modules、levels）；q49（allOf、modules）；q50（modules、levels）
