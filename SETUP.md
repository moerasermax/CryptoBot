# CryptoBot · 環境設置指引

本文件涵蓋本機開發啟動步驟與外部存取（ngrok）配置。其他策略 / Lab / AI 相關細節見 `ai_ops/` 下的膠囊與實作報告。

---

## 本機啟動

前置：
- .NET 8 SDK
- Windows PowerShell 5.1+ 或 bash（均可）

```bash
dotnet restore
dotnet build CryptoBot.sln
dotnet run --project src/CryptoBot.ConsoleApp
```

預設 Kestrel 綁定在 `http://0.0.0.0:5000`（`src/CryptoBot.ConsoleApp/appsettings.json :: Kestrel:Endpoints:Http:Url`），Web UI 走該位址。

### IP 白名單
`appsettings.json :: Security:AllowedIPs` 控制 IP 白名單。**空清單＝不啟用白名單**（全部放行）；列出後只有名單內 IP 可通。

```json
"Security": {
  "AllowedIPs": [ "127.0.0.1", "::1", "192.168.0.99", "YOUR_HOME_IP" ]
}
```

開發環境務必保留 `127.0.0.1` 與 `::1`，否則瀏覽器本地存取會被擋下。

---

## S27-NGROK：外部安全隧道

用途：從公司電腦、手機或其他外部網路透過 HTTPS 存取本機 Lab。

### 為什麼要 ForwardedHeaders
ngrok 會把客戶端真實 IP 放在 `X-Forwarded-For` header，Kestrel 的 `Connection.RemoteIpAddress` 預設只看到 `127.0.0.1`（ngrok 把流量從本機代理進來）。因此：

- `Program.cs` 已註冊 `app.UseForwardedHeaders()`，排在 `IpWhitelistMiddleware` **之前**
- `ForwardedHeadersOptions.KnownNetworks / KnownProxies` 清空，因為 ngrok 代理 IP 是動態的
- 白名單比對的是經 ForwardedHeaders 改寫後的真實 client IP

### 安裝 ngrok

```powershell
winget install Ngrok.Ngrok
```

或到 <https://ngrok.com/download> 下載後加進 PATH。

### 設定 authtoken
至 <https://dashboard.ngrok.com/get-started/your-authtoken> 取得個人 token，一次性註冊：

```powershell
ngrok config add-authtoken <你的 token>
```

### 啟動隧道

1. 先起 CryptoBot：`dotnet run --project src/CryptoBot.ConsoleApp`
2. 另開終端跑：

```powershell
.\scripts\start-ngrok.ps1
```

腳本會先檢查 ngrok 是否在 PATH、port 5000 是否有服務在聽，然後 `ngrok http 5000`。

### 把手機 / 外部 IP 加進白名單
1. 在手機打開 `https://www.whatismyip.com/` 取 public IP
2. 編輯 `src/CryptoBot.ConsoleApp/appsettings.json` 的 `Security:AllowedIPs` 加入該 IP
3. 重啟 CryptoBot（`IOptionsMonitor` 本身會 reload，但為避免 transient state 建議重啟）
4. 用手機開啟 ngrok 給的 HTTPS URL（例 `https://abcd-12-34-56-78.ngrok-free.app`）

若沒加白名單就開 ngrok URL → 應回 `403 Forbidden: source IP not in whitelist.`（日誌會印 `🛡 IP whitelist REJECT <真實IP> → GET /`，而不是 127.0.0.1）。

### 驗證 X-Forwarded-For 正確生效
日誌應顯示 client 的真實 public IP；若還是 `127.0.0.1`，代表 `UseForwardedHeaders` 沒成功 — 檢查：
- `Program.cs` 的 `builder.Services.Configure<ForwardedHeadersOptions>(...)` 有清空 `KnownNetworks` / `KnownProxies`
- `app.UseForwardedHeaders()` 確實在 `IpWhitelistMiddleware` 之前
- ngrok 確實連到 `http://localhost:5000`（不是其他 port）

### 安全提醒
- ngrok 免費方案提供 HTTPS；**永遠用 `https://...` 的 forwarded URL**，不要用 http 版（雖然 ngrok 兩者都開）
- Authtoken 等同帳號密碼 — 不要 commit 到 git
- 完成遠端連線後請關掉 ngrok（Ctrl+C）；不要留隧道整天開著
