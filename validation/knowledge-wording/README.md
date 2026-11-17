# 「用詞不同」虛構情境集（#89，ADR-0015、ADR-0022）

**本資料夾的內容全部是虛構的**（代碼、說明、處置、出處、日文條目與題目都由開發者編寫），不來自任何真實設備、LOG 或規格書。

- `kb.csv`：知識庫 40 條（格式同 ADR-0013 第 2 點），其中 8 條為日文說明，刻意放入意思相近的條目作為干擾。
- `questions.json`：60 題第一線的口語描述，每題的正解為一條知識庫條目（代碼）。`kind` 為用詞差異的類型（同義、現象、口語）；`crossLingual` 表示正解為日文條目。
- 執行方式見 `validation/README.md`；結果在 `validation/results/knowledge-wording-89-dev-pc.md`。
