# Louisa Wi-Fi for Windows（開發中）

Mac 版 [Louisa Wi-Fi](../README.md) 的 Windows 移植：走進路易莎，Windows 筆電自動連上 Wi-Fi。門市資料與 Mac 版共用 [`data/stores.json`](../data/stores.json)，程式在 [`src/`](src/)，測試在 [`tests/`](tests/)。

需要 Windows 10 2004（build 19041）或更新版本。

## 功能

與 Mac 版相近：自動連線、附近門市與依城市選店、搜尋門市、輸入店內公告的密碼、連線統計、登入時開啟、命令列與自動更新。

- **系統匣圖示**：左鍵或右鍵都會開啟選單。圖示為彩色表示已連上路易莎，有橘點表示連線中，灰色表示未連上。要隱藏圖示，可把它拖進工作列的 ^ 溢位區，程式仍會在背景自動連線。
- **選單**：有定位時列出附近 6 家門市，並可「用密碼連線」最近一家；沒有定位時可「連線到最近的門市」。另有「所有門市」（依城市分組）、「搜尋門市…」、「連線統計…」，以及「自動連線」、「只顯示連過的門市」、「登入時開啟」三個開關。
- **搜尋門市**：開啟後可直接打字，↑／↓ 選擇，Enter 自動嘗試，Esc 關閉。搜尋框空白時，先列出附近 5 家門市，再列出連過的門市（由近到遠）。
- **連線統計**：每日方格圖，點格子可看當天的門市與時數；下方可切換門市、月份、年份。按 Esc 關閉。
- **Wi-Fi 名稱**：認得 `LouisaCoffee`、帶數字的 `LouisaCoffee879` 等，以及 `Louisa Coffee`。

## 安裝

從 [Releases](https://github.com/LIAN26880912/projLouisaWIFI/releases/latest) 下載 `LouisaWiFi-windows-x64.zip`（ARM 電腦請用 `-arm64`）。解壓縮到例如 `%LOCALAPPDATA%\Programs\LouisaWiFi\`，再執行 `LouisaWiFi.exe`，不需另外安裝 .NET。

目前沒有程式碼簽章。第一次執行若出現 SmartScreen，請按「其他資訊」→「仍要執行」。

## 自動連線怎麼運作

「自動連線」預設開啟，可從選單關閉，設定會保留。這是 App 自己的功能，只有 App 在執行時才會運作，與 Windows「已知網路」中的「在範圍內時自動連線」無關。

1. 優先辨識已連過的基地台。
2. 要找新門市時，只採用最近 5 分鐘內、誤差小於 500 公尺的位置（手機熱點常讓 Windows 回報剛好 500 公尺、位置錯誤的結果，會被排除），再依序嘗試 5 公里內最近的 3 家門市。
3. 都失敗，或根本沒有可嘗試的門市（沒有可用定位、5 公里內沒有門市）時，會跳出通知請你手動選店，並對同一基地台暫停自動連線 10 分鐘，與 Mac 版相同。通知顯示期間點它可以立即重試；從選單選門市或「連線到最近的門市」也會忽略暫停。

自動連線不會切換正在使用的 Wi-Fi，也不會切換已可上網的連線。手動選店則與 Mac 版相同：會換掉目前的其他 Wi-Fi（例如手機熱點），只有透過有線網路等非 Wi-Fi 連線上網時才會保留。

本程式會改寫 Windows 中路易莎 Wi-Fi（例如 `LouisaCoffee`）的設定檔，寫入要嘗試的門市密碼；連線失敗時會還原原本的設定。程式不會變更 Windows 的「在範圍內時自動連線」：既有設定檔維持原本的選擇，新建立的設定檔則不勾選。因此 App 未執行時，Windows 預設不會自行連上路易莎；若你想讓 Windows 自動連線，請在 Windows 設定中自行勾選。

請只在你有權使用的網路上測試。

## 定位權限

首次需要定位或 Wi-Fi 資訊時，請依提示允許 Windows 的定位權限，也可以從選單開啟定位設定。較新的 Windows 在定位未獲授權時，可能同時禁止掃描與讀取 Wi-Fi 基地台。

- **已授權，但離線或暫時無法定位**：仍可使用已學習的基地台。
- **未授權**：手動選店時會嘗試不指定基地台連線。能否成功取決於 Windows 與網卡，而且無法記住基地台。

Windows 筆電大多沒有 GPS，離線時的位置主要靠附近的 Wi-Fi 推算，在陌生地點可能不準（例如連著手機熱點時被定位到別處）。因此第一次去某家店，偶爾需要手動選一次；選過之後就會記住。

## 命令列

同一使用者只會執行一個實例。再次執行時，指令會轉交給已在執行的程式，結果以系統通知顯示：

```powershell
LouisaWiFi.exe --nearest            # 連線到最近的門市（忽略自動連線的暫停）
LouisaWiFi.exe --connect 金城延和   # 連線到指定門市，可省略「門市」
LouisaWiFi.exe --stats              # 開啟連線統計
LouisaWiFi.exe --update             # 檢查更新；已知有新版時直接更新
LouisaWiFi.exe --show               # 顯示目前狀態
```

「登入時開啟」只在直接執行 `LouisaWiFi.exe` 時可用（會寫入目前使用者的「啟動」項目）。移動 exe 之後，請重新勾選。

## 自動更新

發佈版會在啟動 30 秒後檢查一次 fork 的最新 release，之後每 6 小時再檢查一次。有新版時會通知你，從選單選「更新到 X」即可；也可以選「檢查更新」手動檢查。

下載的檔案會以 GitHub 提供的 SHA-256 驗證，並確認版本，替換後自動重新啟動。新版若無法啟動，會還原為原本的版本。

- 程式放在無法寫入的位置（例如 Program Files）時，只會開啟下載頁，不會要求系統管理員權限。
- 開發中的建置只在手動選「檢查更新」時檢查，也不會替換自己。

## 資料與記錄

設定與資料都放在 `%LOCALAPPDATA%\LouisaWiFi\`：

| 檔案 | 內容 |
|---|---|
| `learned.json` | 已學習的基地台 |
| `usage.json` | 連線統計，格式與 Mac 版相同（連上路易莎 Wi-Fi 時每分鐘累計） |
| `LouisaWiFi.log` | 程式記錄：狀態變化、嘗試了哪些門市與結果 |

- 手動輸入的密碼以 Windows DPAPI 保護。
- `LouisaWiFi.log` 不含密碼、基地台或座標。
- 設定或資料檔損毀時，程式會把它改名為 `*.bad` 保留，改用預設值繼續執行，並顯示通知。

## 開發

需要 .NET 10 SDK：

```powershell
dotnet test windows\LouisaWiFi.Windows.slnx
dotnet run --project windows\src\LouisaWiFi.Windows\LouisaWiFi.Windows.csproj
```

也可以直接雙擊 `windows\louisawifi.cmd`。它會建置 Release 版，再啟動 `LouisaWiFi.exe`；若程式已在執行，只會顯示目前狀態。

## 版本與發佈（維護者）

- Windows 與 Mac 共用同一個 tag。`release.yml` 會在同一個 release 附上兩種 Windows zip。
- 只有 fork 的 Windows 修正使用第 4 碼（例如 `v0.1.6.1`）。
- fork 上的 `sync-upstream.yml` 每週一 12:00（台灣時間）檢查上游的新 tag，合併後打同名 tag 並發佈。
- 門市資料隨上游更新，`refresh-stores.yml` 只在上游執行。
- fork 需先在 GitHub 的 Actions 頁面啟用 workflow，才會排程執行。
