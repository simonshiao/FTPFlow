# FTPFlow｜開發交接／下一位 Agent 接手入口

> 2026-10-27｜狀態：ACTIVE（可繼續開發）｜版本：1.6.1｜Windows WPF / .NET Framework 4.7.2 / C# 5
> 入口順序：本文件 → `API.md`／`使用說明.md` → 必要的程式碼。交接不需要掃描全部歷史備份。

## 一、目前結論／Source of Truth

- **唯一開發主目錄（@chao3）**：`D:\0_軟體開發區\3.開發中專案區\1.進行中\FTPFlow`。
- 版號：`src/AssemblyInfo.cs` 為 `1.6.1.0`；程式名為 `FTPFlow`（視窗顯示 FTP Flow）。
- **Git**：本機 `handoff/account-manager-ai-api`，撰寫前 HEAD `33462ac`（Default new FTP accounts to root directory），工作區乾淨。歷史有五筆提交。
- **GitHub**：公開儲存庫 https://github.com/simonshiao/FTPFlow ，`main` 已上傳 `33462ac` 並回查遠端 SHA、15 個追蹤檔案。**本機沒有預設 remote/upstream**，後續同步須指定網址與分支。
- 舊位置 `D:\0-自開發\0-NEW\FTPFlow` 已移動至目前目錄；同資料夾樹中其他 `FTPFlow-Release-*`、ZIP、備份僅供歷史查核，不作為開發主線。
- 搬移前主目錄與 1.6.1 RootPath 發行快照比對 14 個主要程式／文件檔，SHA-256 一致；不代表搬移後已重新執行所有功能測試。

## 二、已實作功能（以目前原始碼及使用文件為準）

1. **FTP 檔案管理**：WPF 目錄瀏覽、清單／圖示、搜尋、多選、資料夾新增、上傳／下載／刪除與確認；UTF-8、GBK、Big5 編碼，FTP 被動模式 EPSV／PASV。
2. **多帳號**：左側「＋」新增、下拉切換、編輯／移除；帳號名稱沿用 FTP 使用者名稱，新帳號初始目錄預設 `/`，每組有獨立 Host、Port、User、Password、InitialPath、Encoding。
3. **啟動與資料保存**：開啟時嘗試自動連到目前選取帳號；密碼由 Windows DPAPI 保護並放在 `%LOCALAPPDATA%\FTPFlow\settings.xml`；包含舊版同層設定的首次遷移流程。
4. **本機 AI 控制 API**：預設 `127.0.0.1:17821/api/v1`，Bearer Token 認證；有狀態、清單、導航、建資料夾、上傳、下載、刪除 API。刪除必須帶 `confirm: true`。規格看 `API.md`。
5. **技術限制**：目前只有 FTP，**沒有 FTPS／SFTP**；不能當作加密檔案傳輸產品。

## 三、程式結構／修改入口

| 位置 | 作用 |
| --- | --- |
| `src/Program.cs` | WPF 主流程、帳號管理、設定儲存、UI 操作與 Local API |
| `src/MainWindow.xaml` | 視窗畫面與控制項 |
| `src/FtpClient.cs` | FTP 連線、指令、檔案處理 |
| `src/AssemblyInfo.cs` | 版本來源（1.6.1.0） |
| `src/DefaultSettings.xml` | 空白設定樣板，非個人帳密 |
| `FTPFlow.csproj`、`Build.cmd` | .NET Framework 4.7.2／C# 5 專案與建置入口 |
| `Start.cmd` | 無 EXE 時自動先建置，再啟動 `FTPFlow.exe` |
| `API.md`、`使用說明.md`、`README.md` | 控制 API 合約、使用操作、專案簡介 |
| `tests/`、`Verify.cmd`、`verification/` | 本機測試與歷史證據（Git 忽略）；正式連線前應檢查副作用 |

## 四、驗證現況與尚未完成 Gate

- **2026-10-27 已確認**：專案由 `0-NEW` 移到進行中，原位置不再有同名開發主目錄；Git 歷史、分支、HEAD 保留。
- **2026-10-27 已確認**：原主目錄與 1.6.1 RootPath 快照的 14 個檔案一致；GitHub 公開 `main` 已回查為 `33462ac`。
- 本機 `verification/`、`tests/` 有舊測試結果，但**本次交接未重新執行 Build、GUI、真實 FTP 傳輸或 Local API**；不得把歷史紀錄誤寫成新驗收。
- **待驗證**：新目錄中的 `Build.cmd` 重新編譯；隔離 FTP 測試站的連線／列表／上傳／下載／刪除；API 權杖及錯誤碼；外部 Windows 環境。
- `Verify.cmd` 會編譯 CoreTests 並用 `--ui` 執行；執行前先檢查測試內容及 FTP 目標，**勿以正式客戶資料進行操作**。

## 五、安全邊界／已知風險

- **不得公開或複製個人資料**：`settings.xml`、`original/ftp_manager.py`（舊 Python 版，可能含連線資訊）、`logs/`、`verification/`、`tests/`、EXE 與 ZIP 皆由 `.gitignore` 排除。不要讀取、顯示或上傳原始帳密。
- 不把本機 API Token 寫死在程式、文件、聊天或測試；維持 loopback 僅本機存取。遷移不同 Windows 使用者時 DPAPI 密文不可直接共用。
- FTP 明文傳輸；敏感資料應先評估 FTPS／SFTP，不能直接把現況宣稱成安全加密傳輸。
- FTP 覆寫、刪除有破壞性；測試只用隔離目錄、可回復檔案，刪除前仍須確認。
- GitHub 儲存庫是 **Public**；後續 commit 前檢查 `git status`、`git diff --cached --name-only`，不可強制加入被忽略的私人資料。

## 六、下一位 Agent 優先順序

**P0｜保全與最低驗證**

1. 檢查指定開發路徑、`git status`、`AssemblyInfo.cs`；先讀 HANDOFF／API，不重造 FTP 或帳號系統。
2. 在不影響使用中 EXE 的情況下執行一次 `Build.cmd`，確認 4.7.2 建置與正確輸出；必要時備份原 EXE。
3. 使用隔離測試帳號檢查基本 FTP 操作與 Local API，記錄版本、日期、結果及未通過項目。

**P1｜延續既有功能**

1. 依新需求修改現有 `src/Program.cs`、`FtpClient.cs` 與 XAML，優先處理真實 BUG，不重新建第二套 FTP 系統。
2. 若要擴大 AI／MCP 使用，先透過既有 Bearer Local API 串接；不要公開監聽或繞過權杖。
3. 需要新通訊協定時，先確認 FTPS／SFTP 需求、套件與測試 Gate；完成後同步版本、使用說明、API 合約及本文件。

## 七、最短接手指令（Windows）

```bat
cd /d D:\0_軟體開發區\3.開發中專案區\1.進行中\FTPFlow
git status
git log -1 --oneline
type HANDOFF.md
type API.md
rem 確認不會覆寫正在執行的程式，再開始建置：
Build.cmd
rem 僅在測試環境啟動 UI：
Start.cmd
```

GitHub 來源：https://github.com/simonshiao/FTPFlow 。發布前先確認遠端 `main` 狀態並進行非強制推送，不搬舊快照或設定檔。

## 八、交接一句話

**FTPFlow 1.6.1 已具備 WPF FTP 檔案管理、多帳號＋DPAPI 儲存，以及 loopback Bearer Local API；可開發主線在「進行中／FTPFlow」，GitHub `main` 有既有版本紀錄。下一步應先做移位後的隔離建置與 FTP／API 驗證，再延續現有 C# 程式；保護私人設定與舊版 Python 資料。**
