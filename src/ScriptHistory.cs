using System;
using System.Diagnostics;
using System.IO;

namespace AIScriptManager;

/// <summary>
/// 脚本变更历史（exe 同级 <c>history</c> 目录）。
/// <para>
/// 用途：每次「接受并写入」落库成功后，为脚本留一份内容副本，便于日后回退。
/// 布局 <c>history\{脚本id}\{时间戳}</c>：按脚本 id 分一个目录（与 cache 下的对话历史同构），
/// 目录内每份快照以保存时刻命名（<c>yyyyMMdd-HHmmss</c>，同秒内重复保存则追加 <c>-2</c>、<c>-3</c>…）。
/// 快照文件名不带扩展名，与 script 目录下的物理脚本（<c>{id}</c>，同样无扩展名）保持一致，
/// 将来回退时直接覆盖回去即可，无需处理扩展名。
/// </para>
/// <para>
/// 阶段限定（用户 2026-09-16 明确）：<b>只做历史记录，不提供历史查看 / 一键回退功能</b>。
/// 脚本被删除时，其整个历史目录一并删除（见 <see cref="DeleteFor"/>）；
/// 目录删除（仅摘索引、不删脚本文件）时历史保留，因为脚本本身还在。
/// </para>
/// <para>所有失败仅记调试日志、绝不抛出——历史是辅助产物，不影响脚本保存主流程。</para>
/// </summary>
public static class ScriptHistory
{
    /// <summary>某脚本的历史目录：<c>history\{脚本id}\</c>。</summary>
    public static string DirFor(string scriptId) => Path.Combine(AppConfig.HistoryDir, scriptId);

    /// <summary>
    /// 为脚本留一份快照：把刚写盘的脚本文件复制到 <c>history\{id}\{时间戳}</c>，返回快照完整路径。
    /// 脚本 id 为空、源文件不存在或复制失败时返回 null（仅记调试日志，不抛）。
    /// 调用时机：必须在索引写入成功之后，使「保存失败（如同级重名）」不产生历史。
    /// </summary>
    public static string? Snapshot(string? scriptId, string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(scriptId) || string.IsNullOrWhiteSpace(sourceFile))
            return null;
        try
        {
            if (!File.Exists(sourceFile)) return null;

            var dir = DirFor(scriptId!);
            Directory.CreateDirectory(dir);

            // 时间戳命名（字典序 = 时间序，便于将来列目录回退）；同秒内多次保存追加 -2、-3…
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(dir, stamp);
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(dir, stamp + "-" + n);

            File.Copy(sourceFile!, path);
            return path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ScriptHistory.Snapshot({scriptId}) 失败(可忽略): " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 删除某脚本的全部历史（删除脚本时调用）：整体移除 <c>history\{id}\</c> 目录。
    /// 目录不存在视为已清理；失败仅记调试日志，不抛。
    /// </summary>
    public static void DeleteFor(string? scriptId)
    {
        if (string.IsNullOrWhiteSpace(scriptId)) return;
        try
        {
            var full = Path.GetFullPath(DirFor(scriptId!));
            // 防路径穿越：仅允许删除历史根目录之下的子目录（脚本 id 异常时也不会误删他处）
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppConfig.HistoryDir));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine("ScriptHistory.DeleteFor 拒绝越界路径(可忽略): " + full);
                return;
            }
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ScriptHistory.DeleteFor({scriptId}) 失败(可忽略): " + ex.Message);
        }
    }
}
