using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using AIScriptManager.Cache;

namespace AIScriptManager;

/// <summary>
/// 按 lang 维护运行程序路径（每种语言一条），持久化到 cache/runtimes.json（IO 见 RuntimeConfigCache）。
/// 缺失的路径由 <see cref="EnsureAutoDetected"/> 在首次启动时尝试自动检测。
/// 自动检测优先级（2026-09-16 起）：免安装运行时目录（runtime_dir）→ 系统 PATH → Windows 系统目录兜底；
/// 即「绿色版自带运行时」优先于机器上安装的版本，见 <see cref="TryDetect"/>。
/// </summary>
public static class RuntimeConfig
{
    /// <summary>加载全部 lang→路径。文件缺失/解析失败/未配置项都返回空字典（调用方需自行检测或让用户选择）。</summary>
    public static Dictionary<string, string?> Load() => RuntimeConfigCache.Load();

    /// <summary>保存指定 lang 的路径；其他 lang 保持不变。空字符串视为"用户取消选择"，写入空以禁用自动检测覆盖。</summary>
    public static void Save(string lang, string? path) => RuntimeConfigCache.Save(lang, path);

    /// <summary>首次启动对每个 lang 尝试自动检测；只填充"未配置"项，不覆盖用户已设置的值。</summary>
    public static void EnsureAutoDetected()
    {
        var all = RuntimeConfigCache.Load();
        foreach (var (lang, hint) in DefaultCandidates)
        {
            if (!string.IsNullOrWhiteSpace(all.GetValueOrDefault(lang)))
                continue; // 用户已配置，跳过
            var found = TryDetect(hint);
            if (found != null) all[lang] = found;
        }
        RuntimeConfigCache.SaveAll(all);
    }

    /// <summary>取某个 lang 的当前路径（可能为 null：未配置或自动检测失败）。</summary>
    public static string? Get(string lang) => RuntimeConfigCache.Load().GetValueOrDefault(lang);

    /// <summary>
    /// 仅尝试自动检测某语言可执行文件（不落盘），找到返回完整路径，否则 null。供 UI 校验时即时带出。
    /// 顺序：免安装运行时目录（runtime_dir）→ 系统 PATH → Windows 系统目录。
    /// </summary>
    public static string? Detect(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        if (DefaultCandidates.TryGetValue(lang!, out var candidates))
            return TryDetect(candidates);
        return null;
    }

    /// <summary>每个 lang 在自动检测时尝试的执行文件名候选（按顺序匹配）。</summary>
    private static readonly Dictionary<string, string[]> DefaultCandidates = new()
    {
        // 顺序遵循朝云约定：cmd → powershell → powershell7 → bash → java → nodejs → python → go → rust
        [ScriptLangs.Cmd]        = new[] { "cmd.exe" },
        // 【刻意不列 pwsh.exe】：powershell 与 pwsh 视为两个完全不同的语言（2026-09-16 定），
        // 各自强绑定：powershell ≡ Windows PowerShell 5.1，pwsh ≡ PowerShell 6+。
        // 若此处把 pwsh.exe 排在前面，装了 PowerShell 7 的机器上「powershell」会被绑到 7，
        // 与 pwsh 混用（且 RuntimeProbe 的版本闸会把它判负，变成「明明有 5.1 却不可用」）。
        [ScriptLangs.PowerShell] = new[] { "powershell.exe" },
        // 只认 pwsh.exe（PowerShell 6+）。【刻意不回退】到 powershell.exe：
        // 回退会让「指定 pwsh」退化成可能跑在 5.1 上，与 powershell 失去区分意义。
        // 这是产品决策而非实现疏漏——机器未装 PS7 时，pwsh 脚本就该「检测不到运行时」并标红置灰，
        // 宁可不可用，也不要静默降级到 5.1（2026-09-16 确认）。改此处前请先确认该决策已变更。
        [ScriptLangs.Pwsh]       = new[] { "pwsh.exe" },
        [ScriptLangs.Bash]       = new[] { "bash.exe" },
        [ScriptLangs.Java]       = new[] { "java.exe" },
        [ScriptLangs.Node]       = new[] { "node.exe" },
        [ScriptLangs.Python]     = new[] { "python.exe", "python3.exe" },
        [ScriptLangs.Go]         = new[] { "go.exe" },
        [ScriptLangs.Rust]       = new[] { "rustc.exe", "cargo.exe" }
    };

    /// <summary>
    /// 自动检测顺序（2026-09-16 调整）：① 免安装运行时目录（配置的 runtime_dir，留空则 exe 同级 runtime）
    /// ② 系统环境变量 PATH ③ Windows 系统目录（System32 / SysWOW64）兜底。都找不到返回 null。
    /// 免安装目录置于首位，是为了让「绿色版自带运行时」自足：把 JDK 解压进 runtime 即被优先采用，
    /// 不必依赖机器上安装的版本。
    /// </summary>
    private static string? TryDetect(string[] candidates)
    {
        // 1) 免安装运行时目录（优先于系统环境）
        var viaRuntimeDir = FindInRuntimeDir(candidates);
        if (viaRuntimeDir != null) return viaRuntimeDir;

        foreach (var name in candidates)
        {
            // 2) PATH 解析（兼容普通环境）
            var viaPath = FindOnPath(name);
            if (viaPath != null) return viaPath;
            // 3) Windows 系统目录兜底（cmd.exe / powershell.exe 几乎一定在 System32）
            var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemDir = Path.Combine(sysRoot, "System32");
            var sysFull = Path.Combine(systemDir, name);
            if (File.Exists(sysFull)) return sysFull;
            var wowFull = Path.Combine(sysRoot, "SysWOW64", name);
            if (File.Exists(wowFull)) return wowFull;
        }
        return null;
    }

    /// <summary>免安装运行时目录下的最大向下搜索层数（层数再深说明布局非预期，不再浪费 IO）。</summary>
    private const int RuntimeDirMaxDepth = 4;

    /// <summary>
    /// 在免安装运行时目录（<see cref="AppConfig.RuntimeDir"/>：配置的 runtime_dir，留空则 exe 同级 runtime）
    /// 下查找该语言的候选可执行文件。命中顺序「浅层优先」，避免对大目录树做全量遍历：
    ///   ① runtime\&lt;exe&gt; ｜ runtime\bin\&lt;exe&gt;（整个运行时或 bin 内容直接摊在根）
    ///   ② 逐层向下（BFS，最多 <see cref="RuntimeDirMaxDepth"/> 层）：&lt;子目录&gt;\bin\&lt;exe&gt; ｜ &lt;子目录&gt;\&lt;exe&gt;
    /// 覆盖 runtime\jdk-25\bin\java.exe、runtime\java\bin\java.exe 等常见绿色版布局。
    /// 目录不存在 / 无匹配 / 访问异常返回 null（交由后续 PATH 检测）。
    /// </summary>
    private static string? FindInRuntimeDir(string[] candidates)
    {
        string root;
        try
        {
            root = AppConfig.RuntimeDir;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RuntimeConfig] 读取 runtime_dir 失败：{ex.Message}");
            return null;
        }
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

        // ① runtime 根 / runtime\bin
        foreach (var name in candidates)
        {
            var direct = Path.Combine(root, name);
            if (File.Exists(direct)) return Path.GetFullPath(direct);
            var inBin = Path.Combine(root, "bin", name);
            if (File.Exists(inBin)) return Path.GetFullPath(inBin);
        }

        // ② BFS 逐层向下（浅层优先，命中即返回）
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth >= RuntimeDirMaxDepth) continue;

            string[] subs;
            try
            {
                subs = Directory.GetDirectories(dir);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RuntimeConfig] 枚举 runtime 子目录失败 {dir}：{ex.Message}");
                continue;
            }

            foreach (var sub in subs)
            {
                foreach (var name in candidates)
                {
                    var inBin = Path.Combine(sub, "bin", name);
                    if (File.Exists(inBin)) return Path.GetFullPath(inBin);
                    var flat = Path.Combine(sub, name);
                    if (File.Exists(flat)) return Path.GetFullPath(flat);
                }
                queue.Enqueue((sub, depth + 1));
            }
        }
        return null;
    }

    private static string? FindOnPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return Path.GetFullPath(full);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RuntimeConfig] PATH 探测异常 {dir}：{ex.Message}");
            }
        }
        return null;
    }
}
