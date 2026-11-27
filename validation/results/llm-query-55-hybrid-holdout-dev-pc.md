# 本機 LLM：自然語言轉搜尋條件（#55，ADR-0013）

- 量測：2026-11-27 18:14，開發電腦（16 執行緒），裝置 CPU，ONNX Runtime GenAI 0.20.0，貪婪解碼。
- 題目：虛構；可轉換 25 題、不能轉換（應回答 unsupported）5 題；有範例時附 6 個不在測試題中的範例（`llm-query/questions.json`）。
- 混合做法：時間與設備編號由規則抽出並換成佔位符，LLM 只處理關鍵字、模組、等級與 unsupported；from、to、fields 是規則的結果（模型回答 unsupported 時整題算 unsupported）。
- 「完全正確」：關鍵字三框、時間起訖、模組、等級、解析欄位、unsupported 全部與預期相同（關鍵字不分大小寫、不計順序）。

## 正確率

| 模型 | 組合 | 完全正確（可轉換 25） | 不能轉換時回答 unsupported（5） | 可轉換卻回答 unsupported | 輸出不是有效 JSON | allOf | anyOf | noneOf | from | to | modules | levels | fields |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Phi-4-mini | 只用提示詞／有範例 | 48%（12） | 5／5 | 1 | 0 | 72% | 92% | 100% | 96% | 96% | 72% | 76% | 96% |
| Phi-4-mini | 只用提示詞／沒有範例 | 0%（0） | 3／5 | 10 | 2 | 36% | 64% | 60% | 84% | 88% | 84% | 68% | 64% |
| Phi-4-mini | schema 限制／有範例 | 36%（9） | 5／5 | 2 | 2 | 52% | 84% | 92% | 84% | 84% | 68% | 72% | 88% |
| Phi-4-mini | schema 限制／沒有範例 | 0%（0） | 3／5 | 8 | 1 | 48% | 60% | 48% | 92% | 92% | 92% | 64% | 68% |
| Qwen3-4B | 只用提示詞／有範例 | 52%（13） | 5／5 | 0 | 1 | 76% | 80% | 92% | 96% | 96% | 68% | 92% | 96% |
| Qwen3-4B | 只用提示詞／沒有範例 | 20%（5） | 4／5 | 5 | 0 | 52% | 92% | 88% | 92% | 92% | 72% | 88% | 84% |
| Qwen3-4B | schema 限制／有範例 | 56%（14） | 5／5 | 0 | 0 | 76% | 88% | 96% | 96% | 96% | 76% | 80% | 100% |
| Qwen3-4B | schema 限制／沒有範例 | 20%（5） | 4／5 | 5 | 0 | 56% | 92% | 92% | 92% | 92% | 72% | 84% | 84% |

各部分的比例只算可轉換的 25 題。

## 速度與記憶體

| 模型 | 載入（秒） | 峰值記憶體（MB） | 組合 | 提示詞 token（中位數） | 第一個 token（秒，中位數） | 每題總時間（秒，中位數／最大） | 產生速度（token/秒，中位數） |
|---|---|---|---|---|---|---|---|
| Phi-4-mini | 5.4 | 4,520 | 只用提示詞／有範例 | 612 | 8.07 | 9.2／10.1 | 9.1 |
| Phi-4-mini | 5.4 | 4,520 | 只用提示詞／沒有範例 | 463 | 6.02 | 8.0／40.6 | 9.7 |
| Phi-4-mini | 5.4 | 4,520 | schema 限制／有範例 | 612 | 8.04 | 9.3／43.3 | 9.3 |
| Phi-4-mini | 5.4 | 4,520 | schema 限制／沒有範例 | 463 | 6.05 | 8.2／38.8 | 9.8 |
| Qwen3-4B | 4.1 | 4,138 | 只用提示詞／有範例 | 616 | 10.42 | 12.0／14.5 | 7.1 |
| Qwen3-4B | 4.1 | 4,138 | 只用提示詞／沒有範例 | 416 | 7.12 | 9.8／13.3 | 7.5 |
| Qwen3-4B | 4.1 | 4,138 | schema 限制／有範例 | 616 | 10.27 | 11.9／14.0 | 7.2 |
| Qwen3-4B | 4.1 | 4,138 | schema 限制／沒有範例 | 416 | 6.82 | 9.4／13.2 | 7.7 |

## 錯誤的題目

- **Phi-4-mini／只用提示詞／有範例**：h01（modules、levels）；h06（allOf、anyOf、levels）；h07（from、to、modules、levels）；h09（modules、levels）；h10（levels）；h14（allOf）；h15（allOf、modules）；h16（allOf、modules、levels）；h17（unsupported、fields）；h18（allOf、anyOf）；h20（modules）；h24（allOf、modules）；h25（allOf）
- **Phi-4-mini／只用提示詞／沒有範例**：h01（allOf、noneOf）；h02（levels）；h03（allOf、anyOf、noneOf）；h04（unsupported、allOf、fields）；h05（allOf、noneOf）；h06（allOf、anyOf、noneOf）；h07（無效 JSON）；h08（unsupported、fields）；h09（allOf、anyOf、noneOf、modules、levels）；h10（allOf、modules）；h11（allOf、noneOf、levels）；h12（unsupported、from、to）；h13（unsupported、fields）；h14（unsupported、from、fields）；h15（unsupported、allOf、fields）；h16（allOf、modules、levels）；h17（unsupported、fields）；h18（unsupported、anyOf、fields）；h19（unsupported、fields）；h20（allOf、anyOf、noneOf、levels）；h21（allOf、anyOf、noneOf）；h22（anyOf、levels）；h23（allOf、noneOf、levels）；h24（allOf、anyOf）；h25（unsupported、allOf、from、to）；hu2（無效 JSON）；hu4（unsupported、anyOf）
- **Phi-4-mini／schema 限制／有範例**：h01（無效 JSON）；h05（allOf）；h06（anyOf、modules、levels）；h07（allOf、from、to、modules、levels）；h10（allOf、levels）；h11（allOf、levels）；h12（unsupported、from、to）；h14（allOf）；h15（allOf、modules）；h16（allOf、modules、levels）；h17（unsupported、fields）；h18（allOf、anyOf）；h20（modules）；h21（無效 JSON）；h24（allOf、modules）；h25（allOf）
- **Phi-4-mini／schema 限制／沒有範例**：h01（allOf、noneOf）；h02（levels）；h03（allOf、anyOf、noneOf）；h04（unsupported、allOf、fields）；h05（allOf、noneOf）；h06（allOf、anyOf、noneOf）；h07（無效 JSON）；h08（unsupported、fields）；h09（anyOf、noneOf）；h10（allOf、modules）；h11（allOf、noneOf、levels）；h12（unsupported、from、to）；h13（unsupported、fields）；h14（allOf、anyOf、noneOf、levels）；h15（unsupported、allOf、fields）；h16（anyOf、levels）；h17（unsupported、fields）；h18（unsupported、anyOf、fields）；h19（unsupported、fields）；h20（allOf、anyOf、noneOf、levels）；h21（allOf、noneOf）；h22（anyOf、levels）；h23（anyOf、noneOf、levels）；h24（allOf、noneOf）；h25（noneOf、levels）；hu3（unsupported、allOf、levels）；hu4（unsupported、anyOf）
- **Qwen3-4B／只用提示詞／有範例**：h01（allOf、modules）；h02（modules）；h04（allOf、anyOf）；h06（noneOf）；h07（無效 JSON）；h10（modules）；h11（allOf、anyOf）；h15（allOf、anyOf、modules）；h17（modules）；h20（modules）；h23（modules）；h25（allOf、anyOf、levels）
- **Qwen3-4B／只用提示詞／沒有範例**：h01（allOf、levels）；h02（allOf、modules）；h04（noneOf）；h05（allOf）；h06（allOf、anyOf）；h07（allOf、from、to）；h08（unsupported、fields）；h09（unsupported、fields）；h10（allOf、modules）；h12（unsupported、from、to）；h13（unsupported、fields）；h15（modules）；h16（allOf、modules、levels）；h17（allOf、noneOf、modules）；h18（allOf、anyOf、modules、levels）；h19（unsupported、fields）；h20（noneOf）；h22（allOf）；h23（allOf、modules）；h24（allOf）；hu3（unsupported、allOf、levels）
- **Qwen3-4B／schema 限制／有範例**：h01（allOf、modules）；h04（allOf、anyOf）；h07（allOf、from、to、modules）；h11（allOf、levels）；h15（allOf、anyOf、modules）；h16（noneOf、levels）；h17（modules、levels）；h20（modules）；h23（modules）；h24（levels）；h25（allOf、anyOf、levels）
- **Qwen3-4B／schema 限制／沒有範例**：h01（allOf、levels）；h02（allOf、modules）；h04（noneOf）；h05（allOf）；h06（allOf、anyOf）；h07（allOf、from、to）；h08（unsupported、fields）；h09（unsupported、fields）；h10（allOf、modules）；h12（unsupported、from、to）；h13（unsupported、fields）；h15（modules）；h16（allOf、modules、levels）；h17（modules、levels）；h18（allOf、anyOf、modules、levels）；h19（unsupported、fields）；h20（noneOf）；h22（allOf）；h23（allOf、modules）；h24（allOf）；hu3（unsupported、allOf、levels）
