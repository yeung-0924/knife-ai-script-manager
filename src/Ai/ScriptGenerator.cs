using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIScriptManager.Ai;

/// <summary>AI 生成结果：一个可被 AIScriptManager 加载运行的脚本及其索引条目。</summary>
public class AiGeneratedScript
{
    /// <summary>条目唯一 id（GUID）。创建流程落盘前由 <see cref="ScriptGenerator.BuildEntry"/> 补齐；编辑模式沿用原条目的 id。</summary>
    public string? Id { get; set; }

    /// <summary>
    /// 界面显示名。**由用户在「AI 脚本编辑器」窗口手动填写（必填），AI 不参与取名**——
    /// system prompt 已明确要求模型不要返回 name 字段，<see cref="ScriptGenerator.Parse"/> 也随之忽略它，
    /// 落库前由窗口用名称框的值覆盖。
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>AI 建议的文件名（仅供参考，程序不使用它命名；物理文件按「{id}」无扩展名存储）。</summary>
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
/// 解析结构化 JSON、把脚本以「{id}」无扩展名平铺写到 script 目录（解释器由 index.json 的 lang 决定）。同时支持「编辑」模式：载入现有脚本内容让 AI 按描述改写。
/// 索引条目由调用方经 <see cref="ScriptIndexStore"/> 写入唯一的 script/index.json。
/// </summary>
public static class ScriptGenerator
{
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
        sb.AppendLine("你是 AIScriptManager 的内置「AI 脚本编辑器」。根据用户的自然语言描述，生成一个可被 AIScriptManager 直接加载运行的脚本。");
        sb.AppendLine("要求：");
        sb.AppendLine("1. 严格遵循上方「AIScriptManager 脚本编写指南」的全部约定：占位符 _p{NAME}、脚本头部「更新时间」行、UTF-8 无 BOM、可选 ANSI 颜色、段标题等（物理文件由程序按 UUID 无扩展名存储，lang 决定解释器，无需在文件命名上纠结）。");
        sb.AppendLine("2. 脚本内所有可配置项都必须写成 _p{参数名} 占位符，并在返回 JSON 的 params 中声明对应参数；占位符名字必须与 params[].name 字面完全一致（全大写 + 下划线）。");
        sb.AppendLine("3. 若用户指定了语言则使用该语言，否则选择最合适的语言。只从以下 9 种中选择：powershell / pwsh / cmd / bash / java / node / python / go / rust。");
        sb.AppendLine("4. 只返回一个 JSON 对象（不要任何解释文字、不要 markdown 代码块、不要 ``` 包裹），结构如下（**不要包含 name 字段**：脚本名称由用户在界面上手动填写、必填，AI 不得代取名，即使返回 name 程序也会忽略）：");
        sb.AppendLine(@"{
  ""file_name"": ""（可选，本程序不使用；脚本文件名由程序按 UUID 无扩展名自动生成）"",
  ""lang"": ""python"",
  ""description"": ""一句话说明脚本用途"",
  ""params"": [
    { ""name"": ""TARGET"", ""label"": ""目标地址"", ""type"": ""folder"", ""required"": true, ""placeholder"": ""如 C:\\out"" }
  ],
  ""content"": ""脚本完整源码（UTF-8 无 BOM，含 _p{参数名} 占位符，头含更新时间）""
}");
        sb.AppendLine("5. params 字段说明：name 必填（全大写 + 下划线）；label 为界面标签；type 可选 text/folder/file/select（默认 text）；required 布尔（默认 false）；options 仅 select 时给字符串数组；default/placeholder 可选；open_after_run 仅导出类「目录」参数设为 true。");
        sb.AppendLine("6. 物理脚本文件由程序按「UUID（无扩展名）」命名并平铺存放在 script 目录下，解释器由 index.json 的 lang 字段决定，与文件名无关；无需遵循上方指南中的文件命名规则，file_name 字段可省略（本程序不使用它命名）。");
        sb.AppendLine("7. 仅返回纯 JSON，便于程序解析。");
        return sb.ToString();
    }

    /// <summary>追问消息统一追加的后缀：提醒模型仍返回完整 JSON 对象（多轮对话时防止只回改动说明）。</summary>
    public const string FollowUpSuffix = "\n\n请只返回 JSON（返回完整对象，不要只给改动说明；不要返回 name 字段——脚本名称由用户在界面手动填写）。";

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
            user.AppendLine("以下是 AIScriptManager 中的现有脚本，请按用户的修改要求改写它（保持可运行、占位符与 params 声明一致）。");
            user.AppendLine("---- 现有脚本 [" + original.Lang + "] " + original.Name + " ----");
            user.AppendLine(original.Content);
            user.AppendLine("---- 现有参数声明 ----");
            var entry = BuildEntry(original);
            user.AppendLine(entry["params"]?.ToJsonString() ?? "（无）");
            user.AppendLine("---- 修改要求 ----");
            user.AppendLine(description);
            user.AppendLine("注意：除非用户明确要求换语言，lang 保持不变；file_name 字段可省略（程序不使用它命名）。");
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
            // 名称不由 AI 提供：脚本名称在界面上由用户手动填写（必填），
            // 故此处固定留空（即使模型仍返回 name 也忽略），落库前由 AiScriptGenWindow 用名称框的值覆盖
            Name = "",
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

    /// <summary>
    /// 脚本物理文件名：就是条目的 id（UUID），无扩展名，平铺在 script 目录下与 index.json 同级。
    /// 解释器完全由 index.json 的 lang 字段决定，与文件名无关——改语言 / 内容都只改 JSON 与文件内容，
    /// 无需改文件名。id 缺失时补新 id。
    /// </summary>
    public static string FileNameFor(AiGeneratedScript script)
    {
        script.Id ??= ScriptIndexStore.NewId();
        return script.Id;
    }

    /// <summary>
    /// 把生成的脚本写到 script 目录（{id} 无扩展名平铺命名，与 index.json 同级），不触碰索引；
    /// 索引条目由调用方经 <see cref="ScriptIndexStore"/> 插入。返回脚本文件完整路径。
    /// </summary>
    public static string WriteScriptFile(AiGeneratedScript script)
    {
        var fileName = FileNameFor(script);
        var scriptPath = Path.Combine(AppConfig.ScriptDir, fileName);
        File.WriteAllText(scriptPath, script.Content, new UTF8Encoding(false));
        return scriptPath;
    }

    /// <summary>
    /// 构建该脚本对应的索引条目：id + path（./&lt;id&gt;，无扩展名，相对 script 根目录）按唯一约定生成；
    /// 编辑模式沿用原条目 id（script.Id 已在载入种子时赋值）。
    /// </summary>
    public static JsonObject BuildEntry(AiGeneratedScript script)
    {
        var fileName = FileNameFor(script);
        var entry = new JsonObject
        {
            ["id"] = script.Id,
            ["name"] = script.Name,
            ["path"] = "./" + fileName,
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
/// 对话历史持久化到缓存：每个脚本一个以脚本 id（UUID）命名的文件夹，每次编辑会话一个文件
/// （同一会话内每轮成功后覆盖更新）；删除脚本时不清理对应缓存（历史保留）。
/// </summary>
public sealed class AiConversation
{
    private readonly List<Dictionary<string, string>> _messages = new();
    // 会话起始时间：用作缓存文件名（一次编辑会话 = 一个文件）
    private readonly DateTime _startedAt = DateTime.Now;
    private string? _cacheFile;

    internal AiConversation(string systemPrompt)
        => _messages.Add(new() { ["role"] = "system", ["content"] = systemPrompt });

    /// <summary>脚本 id（缓存文件夹名）。首轮结果产生后由窗口赋值；未赋值前持久化跳过。</summary>
    public string? ScriptId { get; set; }

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

    /// <summary>
    /// 把当前对话历史持久化到缓存（cache/{脚本id}/{会话起始时间}.json）。
    /// 写失败只吞掉（缓存是辅助产物，不影响生成主流程）。
    /// </summary>
    public void PersistToCache()
    {
        if (string.IsNullOrWhiteSpace(ScriptId)) return;
        try
        {
            _cacheFile ??= Path.Combine(AppConfig.CacheDir, ScriptId!, _startedAt.ToString("yyyyMMdd-HHmmss") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var messages = new JsonArray();
            foreach (var m in _messages)
                messages.Add(new JsonObject { ["role"] = m["role"], ["content"] = m["content"] });
            var root = new JsonObject
            {
                ["script_id"] = ScriptId,
                ["started_at"] = _startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                ["messages"] = messages
            };
            File.WriteAllText(_cacheFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch
        {
            // 缓存写失败不影响主流程
        }
    }
}
