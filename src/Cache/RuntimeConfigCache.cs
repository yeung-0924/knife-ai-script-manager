using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using AIScriptManager.Cache;

namespace AIScriptManager.Cache;

/// <summary>
/// 运行时路径缓存：按 lang 维护各语言的执行程序路径（每种语言一条）。
/// 数据持久化到 cache/runtimes.json。纯 IO 封装，探测逻辑（TryDetect 等）在 RuntimeConfig 内。
/// <para>
/// 线程安全：Save 是「读改写」，读与写都持同一把锁。原因不只是防丢更新——<see cref="File.WriteAllText(string,string)"/>
/// 并非原子替换，若另一个线程在写的中途读到半截文件，<c>Load</c> 会解析失败并返回空字典，
/// 紧接着一次 Save 就会把其余语言的路径全部抹掉。UI 线程与运行时自愈的后台线程都会写此文件，故必须加锁。
/// </para>
/// </summary>
public static class RuntimeConfigCache
{
    private const string FileName = "runtimes.json";

    /// <summary>保护 Load / Save / SaveAll 的进程内互斥锁（Monitor 可重入，Save 内部再调 Load 不会自锁死）。</summary>
    private static readonly object IoLock = new();

    private static string FilePath => Path.Combine(CacheStore.CacheRoot, FileName);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>加载全部 lang→路径。文件缺失/解析失败都返回空字典（调用方需自行检测或让用户选择）。</summary>
    public static Dictionary<string, string?> Load()
    {
        lock (IoLock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(FilePath);
                var dto = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
                // 语言名按大小写不敏感归并：lang 若写成 "PowerShell"，也应命中 "powershell" 的配置项，
                // 否则会出现「同一个语言两条记录 / 检测到了却读不出来」这类不一致。
                // 注意 Save 是"读改写"，更新已有键时 Dictionary 会保留原键的大小写形态，因此不会篡改既有数据。
                if (dto == null) return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                return new Dictionary<string, string?>(dto, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RuntimeConfigCache] 加载失败 {FilePath}：{ex.Message}");
                return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>保存指定 lang 的路径；其他 lang 保持不变。null/空视为"用户取消选择"，写入空以禁用自动检测覆盖。</summary>
    public static void Save(string lang, string? path)
    {
        lock (IoLock)   // 整段「读改写」持锁：调用方既有 UI 线程也有运行时自愈的后台线程
        {
            try
            {
                var all = Load();   // 内部同样持锁（Monitor 可重入，不会自锁死）
                all[lang] = string.IsNullOrWhiteSpace(path) ? null : path;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all, JsonOpts));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RuntimeConfigCache] 保存失败 {FilePath}：{ex.Message}");
            }
        }
    }

    /// <summary>整体覆盖保存（供首次自动检测后落盘）。</summary>
    public static void SaveAll(Dictionary<string, string?> all)
    {
        lock (IoLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all, JsonOpts));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RuntimeConfigCache] 保存失败 {FilePath}：{ex.Message}");
            }
        }
    }
}
