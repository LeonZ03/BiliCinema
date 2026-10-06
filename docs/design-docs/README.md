# Design Documents

此目錄只保存無法由 current code、tests 或 `ARCHITECTURE.md` 直接推導，且仍需理解
取捨理由的設計決策。Current topology 與 invariant 以根層 `ARCHITECTURE.md` 為準；
目前工作以使用者需求與明確指派的 BiliCinema Issue／PR 為準；完成證據由 Git 與 Release 保存。

- `aria2-rpc-client-ownership.md`：aria2 RPC compatibility adapter 的責任分割。
- `desktop-feature-locality.md`：拒絕全域 FeatureRegistry、feature-module framework 與第二 router 的理由。
- `logging-ownership-sink-adr.md`：logging privacy boundary、Infrastructure owner、rolling sink、retention 與 diagnostic export 決策。
- 根層 `ARCHITECTURE.md`：current owner、依賴方向、invariant 與可執行防線的權威入口。
- `../watch-together.md`：已實作的房間、聊天與播放器互動約定；早期 UI 預覽稿不作為現行規格。

設計文件不保存 current work、舊 run、Gate、SHA 或可由程式產生的 inventory。
