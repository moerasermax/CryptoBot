using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CryptoBot.Application.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoBot.Infrastructure.Ai;

/// <summary>
/// S75：<see cref="IGeminiAcpClient"/> 之 Infrastructure 實作 — 透過 <c>gemini --acp</c> 子 process
/// 走 JSON-RPC 2.0 over stdin/stdout 與 Gemini CLI 對話。
///
/// 取代 S74-C 的 <c>gemini -p</c> single-shot 模式（每次 spawn ~1-3s 冷啟動 + 28K wchar 字串上限）。
///
/// 紀律對齊：
/// <list type="bullet">
///   <item>IRON ⑥：本實作位於 Infrastructure；介面定義於 <see cref="IGeminiAcpClient"/> Application 層；
///         <c>Process</c> / <see cref="JsonElement"/> 等型別不洩漏出介面。</item>
///   <item>IRON ⑩：含中文字串、檔案編碼 UTF-8 with BOM。</item>
///   <item>cwd 隔離（Phase 1 §1 + capsule §6 驗收）：啟動 process 於 dedicated temp dir，
///         避免 cwd 內 <c>GEMINI.md</c> + <c>agent-commons/</c> auto-load 進 PM 角色。</item>
///   <item>無 cancel method（Phase 1 §7 親驗）：in-flight prompt 取消 = stdin close → 重啟 session；
///         Phase 2 不實作 retry / fallback model chain（屬 Phase 3 加固）。</item>
/// </list>
///
/// 生命週期：
/// <list type="bullet">
///   <item>Lazy init — 第一次 <see cref="SendPromptAsync"/> 觸發 <see cref="EnsureSessionAsync"/>。</item>
///   <item>單一 session — instance 生命期內共用同一 sessionId、ACP process。</item>
///   <item>Dispose — 關 stdin → 等 process clean exit (2s)；逾時 fallback <c>Process.Kill(entireProcessTree:true)</c>
///         (Phase 2 minimal；Phase 3 升級為 <c>Win32_Process</c> recursive tree kill)。</item>
/// </list>
/// </summary>
public sealed class GeminiAcpClient : IGeminiAcpClient
{
    private readonly InteractiveCliAdvisorOptions _opts;
    private readonly ILogger<GeminiAcpClient> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _promptGate = new(1, 1);

    private Process? _process;
    private string? _sessionId;
    private string? _tempDir;
    private int _nextId;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly object _pendingLock = new();
    private Channel<string>? _activeChunkChannel;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private volatile bool _disposed;

    public GeminiAcpClient(IOptions<InteractiveCliAdvisorOptions> opts, ILogger<GeminiAcpClient> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task EnsureSessionAsync(CancellationToken ct = default)
    {
        if (_sessionId is not null) return;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessionId is not null) return;
            await InitProcessAndSessionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task InitProcessAndSessionAsync(CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GeminiAcpClient));

        // 1. 建立隔離 cwd（避免 cwd 內 GEMINI.md auto-load）
        _tempDir = Path.Combine(Path.GetTempPath(), "cryptobot-sidekick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logger.LogInformation("[GeminiAcp] 啟動 gemini --acp，cwd={Cwd}", _tempDir);

        // 2. spawn process
        var psi = new ProcessStartInfo
        {
            FileName = _opts.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _tempDir,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--acp");

        _process = new Process { StartInfo = psi };
        if (!_process.Start())
            throw new InvalidOperationException($"gemini --acp Process.Start 回 false（Executable={_opts.Executable}）。");

        // 3. 啟動 background read loop
        _readLoopCts = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));

        // 4. send initialize / authenticate / session/new
        var initResp = await SendRequestAsync("initialize", new { protocolVersion = 1 }, ct).ConfigureAwait(false);
        _logger.LogDebug("[GeminiAcp] initialize response received");

        await SendRequestAsync("authenticate", new { methodId = "oauth-personal" }, ct).ConfigureAwait(false);
        _logger.LogDebug("[GeminiAcp] authenticate (oauth-personal) ok");

        var newResp = await SendRequestAsync(
            "session/new",
            new { cwd = _tempDir, mcpServers = Array.Empty<object>() },
            ct).ConfigureAwait(false);

        _sessionId = newResp.GetProperty("sessionId").GetString();
        if (string.IsNullOrEmpty(_sessionId))
            throw new InvalidOperationException("session/new response 缺 sessionId。");

        _logger.LogInformation("[GeminiAcp] Session created: {SessionId}", _sessionId);
    }

    public async IAsyncEnumerable<string> SendPromptAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GeminiAcpClient));
        if (string.IsNullOrWhiteSpace(text)) yield break;

        await EnsureSessionAsync(ct).ConfigureAwait(false);
        await _promptGate.WaitAsync(ct).ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        _activeChunkChannel = channel;

        Task<JsonElement>? sendTask = null;
        try
        {
            var promptParams = new
            {
                sessionId = _sessionId!,
                prompt = new[] { new { type = "text", text } },
            };

            sendTask = SendRequestAsync("session/prompt", promptParams, ct);

            // 當 sendTask 完成（成功 / 失敗 / 取消）時、關閉 channel writer。
            _ = sendTask.ContinueWith(
                _ => channel.Writer.TryComplete(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            await foreach (var chunk in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return chunk;
            }

            // sendTask 已完成；await 觀察例外（若有）。
            await sendTask.ConfigureAwait(false);
        }
        finally
        {
            _activeChunkChannel = null;
            channel.Writer.TryComplete();
            _promptGate.Release();
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, object @params, CancellationToken ct)
    {
        if (_process is null) throw new InvalidOperationException("Process not started.");

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pendingRequests[id] = tcs;

        var msg = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params,
        });

        await _process.StandardInput.WriteLineAsync(msg.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_process is null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null) break; // EOF
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.Number)
                    {
                        // response (matches a pending request)
                        int id = idElem.GetInt32();
                        TaskCompletionSource<JsonElement>? tcs = null;
                        lock (_pendingLock)
                        {
                            if (_pendingRequests.TryGetValue(id, out tcs))
                                _pendingRequests.Remove(id);
                        }

                        if (tcs is not null)
                        {
                            if (root.TryGetProperty("error", out var err))
                            {
                                tcs.TrySetException(new InvalidOperationException($"ACP error: {err.GetRawText()}"));
                            }
                            else if (root.TryGetProperty("result", out var result))
                            {
                                tcs.TrySetResult(result.Clone());
                            }
                            else
                            {
                                tcs.TrySetException(new InvalidOperationException($"ACP response missing result/error: {line}"));
                            }
                        }
                    }
                    else if (root.TryGetProperty("method", out var methodElem))
                    {
                        // notification (no id)
                        var method = methodElem.GetString();
                        if (method == "session/update" && root.TryGetProperty("params", out var p))
                        {
                            HandleSessionUpdate(p);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "[GeminiAcp] JSON parse fail: {Line}", line);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GeminiAcp] Read loop crashed");
        }
        finally
        {
            // 喚醒所有 pending requests 為 failure（process exited / read loop died）。
            lock (_pendingLock)
            {
                foreach (var (_, tcs) in _pendingRequests)
                    tcs.TrySetException(new InvalidOperationException("ACP process read loop ended."));
                _pendingRequests.Clear();
            }
        }
    }

    /// <summary>
    /// 處理 session/update notification。已知 discriminator（Phase 1 §4.5 親驗 + §6.3 推導）：
    /// <list type="bullet">
    ///   <item><c>available_commands_update</c> — Phase 2 忽略（內部命令清單，不直接 user-facing）。</item>
    ///   <item><c>agent_message_chunk</c> — 推到 active prompt 的 channel；schema 細節 Phase 4 親驗。</item>
    ///   <item>其他類別 — Phase 2 logger debug 略過。</item>
    /// </list>
    /// </summary>
    private void HandleSessionUpdate(JsonElement paramsElement)
    {
        if (!paramsElement.TryGetProperty("update", out var update)) return;
        if (!update.TryGetProperty("sessionUpdate", out var typeElem)) return;
        var type = typeElem.GetString();

        if (type == "agent_message_chunk")
        {
            // schema 推導（Phase 1 §6 未親驗）：可能在 update.content.text 或 update.text；先試 content.text。
            string? chunk = null;
            if (update.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                    chunk = content.GetString();
                else if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("text", out var textElem))
                    chunk = textElem.GetString();
            }
            else if (update.TryGetProperty("text", out var directText))
            {
                chunk = directText.GetString();
            }

            if (!string.IsNullOrEmpty(chunk))
                _activeChunkChannel?.Writer.TryWrite(chunk);
        }
        else
        {
            _logger.LogDebug("[GeminiAcp] session/update sessionUpdate={Type} (略過)", type);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // 1. cancel read loop
        try { _readLoopCts?.Cancel(); } catch { /* ignore */ }

        // 2. close stdin → process clean exit (timeout 2s)
        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.StandardInput.Close();
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[GeminiAcp] dispose: stdin close 後 2s 未 exit，強制 Kill(entireProcessTree:true)");
                try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GeminiAcp] dispose: 關閉 stdin / 等 exit 失敗，嘗試 Kill。");
                try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
        }

        // 3. cleanup
        try { _process?.Dispose(); } catch { /* ignore */ }

        if (_readLoopTask is not null)
        {
            try { await _readLoopTask.ConfigureAwait(false); } catch { /* ignore */ }
        }

        try { _readLoopCts?.Dispose(); } catch { /* ignore */ }
        try { _initGate.Dispose(); } catch { /* ignore */ }
        try { _promptGate.Dispose(); } catch { /* ignore */ }

        // 4. 清 temp dir
        if (_tempDir is not null)
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "[GeminiAcp] dispose: 清 temp dir {Dir} 失敗，略過。", _tempDir); }
        }
    }
}
