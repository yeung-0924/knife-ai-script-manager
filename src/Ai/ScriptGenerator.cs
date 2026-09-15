using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScriptManager.Ai;

/// <summary>AI 生成结果：一个可被 ScriptManager 加载运行的脚本及其索引条目。</summary>
public class AiGeneratedScript
{
    public string Name { get; set; } = "";
    /// <summary>脚本文件名（含扩展名），落到 script/ai-generated/ 下；已做路径安全化。</summary>
    public string FileName { get; set; } = "";
    public string Lang { get; set; } = "python";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
    public List<AiGeneratedParam> Params { get; set; } = new();
}

public class AiGeneratedParam
{
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public string? Type { get; set; }
    public bool Required { get; set; }
    public List<string>? Options { get; set; }
    public string? Default { get; set; }
    public string? Placeholder { get; set; }
    public bool OpenAfterRun { get; set; }
}

/// <summary>
/// AI 脚本生成器：拼装 system prompt（script-writer 技能全文 + 生成约束与 JSON schema）、调用 <see cref="AiClient"/>、
/// 解析结构化 JSON、把脚本写到 script/ai-generated/。同时支持「编辑」模式：载入现有脚本内容让 AI 按描述改写。
/// 脚本文件统一落在 ai-generated/ 隔离目录；索引条目由调用方经 <see cref="ScriptIndexStore"/> 写入唯一的 script/index.json。
/// </summary>
public static class ScriptGenerator
{
    private const string GenDir = "ai-generated";

    /// <summary>system prompt = script-writer 技能全文 + 生成约束与 JSON schema。</summary>
    public static string BuildSystemPrompt()
    {
        var skillPath = Path.Combine(AppConfig.ScriptDir, ".skills", "SKILL.md");
        var skill = File.Exists(skillPath)
            ? File.ReadAllText(skillPath, new UTF8Encoding(false))
            : "(未找到 script-writer 技能文件，请参考项目 script/.skills/SKILL.md)";
        var sb = new StringBuilder();
        sb.AppendLine(skill);
        sb.AppendLine();
        sb.AppendLine("----");
        sb.AppendLine("你是 ScriptManager 的内置「AI 脚本编辑器」。根据用户的自然语言描述，生成一个可被 ScriptManager 直接加载运行的脚本。");
        sb.AppendLine("要求：");
        sb.AppendLine("1. 严格遵循上方「ScriptManager 脚本编写指南」的全部约定：占位符 _p{NAME}、脚本头部「更新时间」行、按语言命名文件、UTF-8 无 BOM、可选 ANSI 颜色、段标题等。");
        sb.AppendLine("2. 脚本内所有可配置项都必须写成 _p{参数名} 占位符，并在返回 JSON 的 params 中声明对应参数；占位符名字必须与 params[].name 字面完全一致（全大写 + 下划线）。");
        sb.AppendLine("3. 若用户指定了语言则使用该语言，否则选择最合适的语言。只从以下 9 种中选择：powershell / pwsh / cmd / bash / java / node / python / go / rust。");
        sb.AppendLine("4. 只返回一个 JSON 对象（不要任何解释文字、不要 markdown 代码块、不要 ``` 包裹），结构如下：");
        sb.AppendLine(@"{
  ""name"": ""界面显示名"",
  ""file_name"": ""脚本文件名（含扩展名，如 do-something.py）"",
  ""lang"": ""python"",
  ""description"": ""一句话说明脚本用途"",
  ""params"": [
    { ""name"": ""TARGET"", ""label"": ""目标地址"", ""type"": ""folder"", ""required"": true, ""placeholder"": ""如 C:\\out"" }
  ],
  ""content"": ""脚本完整源码（UTF-8 无 BOM，含 _p{参数名} 占位符，头含更新时间）""
}");
        sb.AppendLine("5. params 字段说明：name 必填（全大写 + 下划线）；label 为界面标签；type 可选 text/folder/file/select（默认 text）；required 布尔（默认 false）；options 仅 select 时给字符串数组；default/placeholder 可选；open_after_run 仅导出类「目录」参数设为 true。");
        sb.AppendLine("6. 仅返回纯 JSON，便于程序解析。");
        return sb.ToString();
    }

    /// <summary>追问消息统一追加的后缀：提醒模型仍返回完整 JSON 对象（多轮对话时防止只回改动说明）。</summary>
    public const string FollowUpSuffix = "\n\n请只返回 JSON（返回完整对象，包含全部字段，不要只给改动说明）。";

    /// <summary>
    /// 构建首轮用户消息：创建模式为需求描述；编辑模式把现有脚本内容与参数一并交给 AI 按描述改写。
    /// </summary>
    private static string BuildInitialUserPrompt(string description, AiGeneratedScript? original)
    {
        var user = new StringBuilder();
        if (original == null)
        {
            user.AppendLine("请生成脚本，需求描述如下：");
            user.AppendLine(description);
        }
        else
        {
            user.AppendLine("以下是 ScriptManager 中的现有脚本，请按用户的修改要求改写它（保持可运行、占位符与 params 声明一致）。");
            user.AppendLine("---- 现有脚本 [" + original.Lang + "] " + original.Name + " ----");
            user.AppendLine(original.Content);
            user.AppendLine("---- 现有参数声明 ----");
            var entry = BuildEntry(original);
            user.AppendLine(entry["params"]?.ToJsonString() ?? "（无）");
            user.AppendLine("---- 修改要求 ----");
            user.AppendLine(description);
            user.AppendLine("注意：file_name 保持与现有脚本一致（" + original.FileName + "）；除非用户明确要求换语言，lang 保持不变。");
        }
        user.AppendLine("请只返回 JSON。");
        return user.ToString();
    }

    /// <summary>
    /// 开启一轮新对话并发送首条消息（流式）。返回对话对象与首个解析结果；
    /// 后续可对返回的 <see cref="AiConversation"/> 继续调用 <see cref="AiConversation.SendAsync"/> 追问改写。
    /// </summary>
    public static async Task<(AiConversation Conversation, AiGeneratedScript Script)> StartConversationAsync(
        string description, AiGeneratedScript? original = null, Action<string>? onDelta = null)
    {
        var conv = new AiConversation(BuildSystemPrompt());
        var script = await conv.SendAsync(BuildInitialUserPrompt(description, original), onDelta).ConfigureAwait(false);
        return (conv, script);
    }

    /// <summary>兼容入口：单轮生成 = 开启对话并发送首条消息。</summary>
    public static async Task<AiGeneratedScript> GenerateAsync(string description, AiGeneratedScript? original = null, Action<string>? onDelta = null)
        => (await StartConversationAsync(description, original, onDelta).ConfigureAwait(false)).Script;

    internal static AiGeneratedScript Parse(string raw)
    {
        // 去掉可能的 ```json ... ``` 包裹（部分模型仍会加）
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var first = text.IndexOf('\n');
            var last = text.LastIndexOf("```");
            if (first >= 0 && last > first)
                text = text.Substring(first + 1, last - first - 1).Trim();
        }

        var node = JsonNode.Parse(text)?.AsObject()
            ?? throw new InvalidOperationException("AI 返回内容不是合法 JSON。");

        var result = new AiGeneratedScript
        {
            Name = node["name"]?.GetValue<string>() ?? "未命名脚本",
            FileName = node["file_name"]?.GetValue<string>() ?? node["fileName"]?.GetValue<string>() ?? "",
            Lang = node["lang"]?.GetValue<string>() ?? "python",
            Description = node["description"]?.GetValue<string>() ?? "",
            Content = node["content"]?.GetValue<string>() ?? ""
        };

        if (node["params"] is JsonArray arr)
        {
            foreach (var p in arr)
            {
                if (p is not JsonObject po) continue;
                var gp = new AiGeneratedParam
                {
                    Name = po["name"]?.GetValue<string>() ?? "",
                    Label = po["label"]?.GetValue<string?>(),
                    Type = po["type"]?.GetValue<string?>(),
                    Required = po["required"]?.GetValue<bool>() ?? false,
                    Default = po["default"]?.GetValue<string?>(),
                    Placeholder = po["placeholder"]?.GetValue<string?>(),
                    OpenAfterRun = po["open_after_run"]?.GetValue<bool>() ?? false
                };
                if (po["options"] is JsonArray opts)
                    gp.Options = opts.Select(o => o?.GetValue<string?>()).Where(o => o != null).Select(o => o!).ToList();
                if (!string.IsNullOrWhiteSpace(gp.Name))
                    result.Params.Add(gp);
            }
        }

        if (string.IsNullOrWhiteSpace(result.FileName))
            throw new InvalidOperationException("AI 未返回有效的 file_name。");
        if (string.IsNullOrWhiteSpace(result.Content))
            throw new InvalidOperationException("AI 未返回脚本内容。");
        return result;
    }

    /// <summary>预览：返回将写入 index.json 的单个条目（pretty JSON）。</summary>
    public static string PreviewIndexEntry(AiGeneratedScript script)
    {
        var entry = BuildEntry(script);
        return entry.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>文件名安全化：只取文件名，去掉任何路径分隔与前导点，避免目录穿越；空则给时间戳兜底名。</summary>
    public static string SafeFileName(AiGeneratedScript script)
    {
        var safeName = Path.GetFileName(script.FileName.Replace('/', '\\').Trim().TrimStart('\\', '.'));
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "script_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".py";
        return safeName;
    }

    /// <summary>
    /// 只把生成的脚本写到 script/ai-generated/（文件隔离目录），不触碰索引；
    /// 索引条目由调用方经 <see cref="ScriptIndexStore"/> 插入唯一的 script/index.json。返回脚本文件完整路径。
    /// </summary>
    public static string WriteScriptFile(AiGeneratedScript script)
    {
        var baseDir = Path.Combine(AppConfig.ScriptDir, GenDir);
        Directory.CreateDirectory(baseDir);
        var scriptPath = Path.Combine(baseDir, SafeFileName(script));
        File.WriteAllText(scriptPath, script.Content, new UTF8Encoding(false));
        return scriptPath;
    }

    /// <summary>构建该脚本对应的索引条目（path 固定指向 ai-generated/ 下的文件，相对 script 根目录）。</summary>
    public static JsonObject BuildEntry(AiGeneratedScript script)
    {
        var entry = new JsonObject
        {
            ["name"] = script.Name,
            ["path"] = "./" + GenDir + "/" + SafeFileName(script),
            ["lang"] = script.Lang
        };
        if (script.Params.Count > 0)
        {
            var paramsArr = new JsonArray();
            foreach (var p in script.Params)
            {
                var po = new JsonObject { ["name"] = p.Name };
                if (!string.IsNullOrEmpty(p.Label)) po["label"] = p.Label;
                if (!string.IsNullOrEmpty(p.Type)) po["type"] = p.Type;
                if (p.Required) po["required"] = true;
                if (!string.IsNullOrEmpty(p.Default)) po["default"] = p.Default;
                if (!string.IsNullOrEmpty(p.Placeholder)) po["placeholder"] = p.Placeholder;
                if (p.Options != null && p.Options.Count > 0)
                {
                    var oa = new JsonArray();
                    foreach (var o in p.Options) oa.Add(o);
                    po["options"] = oa;
                }
                if (p.OpenAfterRun) po["open_after_run"] = true;
                paramsArr.Add(po);
            }
            entry["params"] = paramsArr;
        }
        return entry;
    }
}

/// <summary>
/// 与 AI 的多轮对话（system prompt 固定为 script-writer 生成约束，历史消息含此前各轮的 user/assistant 原文）。
/// 每轮 <see cref="SendAsync"/> 流式返回并解析为 <see cref="AiGeneratedScript"/>；解析成功才把 assistant
/// 回复写入历史，失败则回退本轮 user 消息（下次发送不残留半截对话）。
/// </summary>
public sealed class AiConversation
{
    private readonly List<Dictionary<string, string>> _messages = new();

    internal AiConversation(string systemPrompt)
        => _messages.Add(new() { ["role"] = "system", ["content"] = systemPrompt });

    /// <summary>
    /// 发送一轮新的用户消息（instruction 为完整用户内容），流式回调增量，返回解析后的脚本结果。
    /// </summary>
    public async Task<AiGeneratedScript> SendAsync(string instruction, Action<string>? onDelta = null)
    {
        _messages.Add(new() { ["role"] = "user", ["content"] = instruction });
        try
        {
            var raw = await AiClient.ChatStreamAsync(_messages, jsonMode: true, onDelta).ConfigureAwait(false);
            var script = ScriptGenerator.Parse(raw);   // 解析失败同样走回退，不污染历史
            _messages.Add(new() { ["role"] = "assistant", ["content"] = raw });
            return script;
        }
        catch
        {
            _messages.RemoveAt(_messages.Count - 1);
            throw;
        }
    }
}
