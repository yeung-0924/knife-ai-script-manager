using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ScriptManager.Ai;

/// <summary>
/// OpenAI 兼容的聊天补全客户端（通用于 OpenAI / DeepSeek / 通义 / 本地 Ollama 等，只要提供 base_url + key）。
/// 仅负责「组装请求 / 发送 / 取回 message.content」，不含任何业务逻辑。
/// 配置来自 <see cref="AppConfig"/> 的 [ai] 节（api_key / base_url / model）。
/// </summary>
public static class AiClient
{
    private static readonly HttpClient Http = new();

    /// <summary>
    /// 发起一次聊天补全。
    /// </summary>
    /// <param name="systemPrompt">系统提示（本应用恒为 script-writer 技能全文 + 生成约束）。</param>
    /// <param name="userPrompt">用户输入（描述 / 语言 / 参数说明）。</param>
    /// <param name="jsonMode">是否要求模型返回 JSON 对象（response_format=json_object）。</param>
    /// <returns>模型回复的 message content 文本。</returns>
    public static async Task<string> ChatAsync(string systemPrompt, string userPrompt, bool jsonMode)
    {
        var baseUrl = AppConfig.AiBaseUrl.Trim().TrimEnd('/');
        // 容错：用户若把完整端点粘贴进来（…/v1/chat/completions），自动剥离后缀，避免拼出重复路径
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^"/chat/completions".Length].TrimEnd('/');
        var url = baseUrl + "/chat/completions";

        var messages = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "system", ["content"] = systemPrompt },
            new() { ["role"] = "user", ["content"] = userPrompt }
        };

        var reqObj = new Dictionary<string, object?>
        {
            ["model"] = AppConfig.AiModel,
            ["messages"] = messages,
            ["temperature"] = 0.2
        };
        if (jsonMode)
            reqObj["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };

        var json = JsonSerializer.Serialize(reqObj);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppConfig.AiApiKey);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI API 返回错误 ({(int)resp.StatusCode})：{body}");

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        return content ?? "";
    }

    /// <summary>
    /// 流式聊天补全（stream=true，SSE 逐段读取）。每收到一段增量文本就回调 <paramref name="onDelta"/>，
    /// 全部接收后返回完整 content。供生成界面做「边生成边显示」；回调在后台线程触发，UI 侧自行调度。
    /// </summary>
    public static async Task<string> ChatStreamAsync(string systemPrompt, string userPrompt, bool jsonMode, Action<string>? onDelta)
    {
        var baseUrl = AppConfig.AiBaseUrl.Trim().TrimEnd('/');
        // 容错：用户若把完整端点粘贴进来（…/v1/chat/completions），自动剥离后缀，避免拼出重复路径
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^"/chat/completions".Length].TrimEnd('/');
        var url = baseUrl + "/chat/completions";

        var messages = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "system", ["content"] = systemPrompt },
            new() { ["role"] = "user", ["content"] = userPrompt }
        };

        var reqObj = new Dictionary<string, object?>
        {
            ["model"] = AppConfig.AiModel,
            ["messages"] = messages,
            ["temperature"] = 0.2,
            ["stream"] = true
        };
        if (jsonMode)
            reqObj["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };

        var json = JsonSerializer.Serialize(reqObj);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppConfig.AiApiKey);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        // ResponseHeadersRead：头部到达即返回，边读流边回调，不等整个响应体
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"AI API 返回错误 ({(int)resp.StatusCode})：{errBody}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var full = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;                       // SSE 事件分隔空行
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;  // 注释/其它字段忽略
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            try
            {
                using var d = JsonDocument.Parse(data);
                if (!d.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var piece = c.GetString() ?? "";
                    if (piece.Length > 0)
                    {
                        full.Append(piece);
                        onDelta?.Invoke(piece);
                    }
                }
                // delta.reasoning_content（DeepSeek 等思考型模型）等其它字段忽略
            }
            catch
            {
                // 单个 chunk 解析失败不影响整体流（部分网关会发心跳等非常规 data）
            }
        }
        return full.ToString();
    }
}
