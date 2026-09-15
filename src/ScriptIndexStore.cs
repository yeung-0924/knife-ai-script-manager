using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScriptManager;

/// <summary>
/// 唯一脚本索引（script/index.json）的读写层。索引已归集为单文件（不再用 include 分片），
/// 脚本树右键菜单的「创建目录 / 创建脚本 / 编辑 / 删除」全部经本类按「树路径」定位条目后修改并整体写回。
/// 树路径 = 各级 name 以 / 相连（与 <see cref="ScriptTreeItem.Path"/> 同一约定）；
/// 定位时逐级在兄弟节点中匹配首个同名节点，因此同级重名时只作用于第一个。
/// 所有写操作都是「整文件读 → 改 → 整文件写」，UTF-8 无 BOM、缩进 2 空格，与手写格式一致。
/// </summary>
public static class ScriptIndexStore
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>唯一索引文件完整路径（动态取 AppConfig，使「文件▸打开」切换索引后即时生效）。</summary>
    public static string IndexPath => ConfigLoader.ScriptIndexJson;

    /// <summary>读取唯一索引根数组；文件缺失或为空时返回空数组。</summary>
    public static JsonArray LoadRoot()
    {
        if (!File.Exists(IndexPath))
            return new JsonArray();
        try
        {
            return JsonNode.Parse(File.ReadAllText(IndexPath, new UTF8Encoding(false)))?.AsArray() ?? new JsonArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("索引 json 解析失败：" + IndexPath + "（" + ex.Message + "）");
        }
    }

    /// <summary>把根数组整体写回唯一索引文件。</summary>
    public static void Save(JsonArray root)
        => File.WriteAllText(IndexPath, root.ToJsonString(Pretty), new UTF8Encoding(false));

    /// <summary>在指定目录节点（null = 根层级）下新建一个空目录条目。同级重名时报错。</summary>
    public static void AddGroup(string? parentPath, string name)
    {
        var root = LoadRoot();
        var target = ResolveTargetArray(root, parentPath);
        EnsureSiblingNameFree(target, name);
        target.Add(new JsonObject { ["name"] = name, ["children"] = new JsonArray() });
        Save(root);
    }

    /// <summary>在指定目录节点（null = 根层级）下追加一个脚本条目。同级重名时报错。</summary>
    public static void AddScriptEntry(string? parentPath, JsonObject entry)
    {
        var root = LoadRoot();
        var target = ResolveTargetArray(root, parentPath);
        var name = entry["name"]?.GetValue<string>() ?? "";
        EnsureSiblingNameFree(target, name);
        target.Add(entry);
        Save(root);
    }

    /// <summary>
    /// 更新脚本条目（编辑模式）：按旧树路径定位，仅替换 name / lang / params 三个字段，
    /// 其余字段（path、hide、open_after_run 等）与 key 顺序原样保留——文件位置不变。
    /// </summary>
    public static void UpdateScriptEntry(string treePath, JsonObject entry)
    {
        var root = LoadRoot();
        var node = FindByPath(root, treePath)
            ?? throw new InvalidOperationException("未找到待更新的脚本条目：" + treePath);
        node["name"] = entry["name"]?.GetValue<string>() ?? node["name"]?.GetValue<string>() ?? "";
        if (entry["lang"] is not null) node["lang"] = entry["lang"]!.DeepClone();
        if (entry["params"] is not null) node["params"] = entry["params"]!.DeepClone();
        else node.Remove("params");
        Save(root);
    }

    /// <summary>
    /// 按树路径删除条目并写回。返回被删条目（脚本删除时可据此取 path 删文件）。
    /// 只动索引：目录删除不递归删物理文件，脚本删除是否连文件一起删由调用方决定。
    /// </summary>
    public static JsonObject RemoveEntry(string treePath)
    {
        var root = LoadRoot();
        var arr = FindParentArray(root, treePath)
            ?? throw new InvalidOperationException("未找到待删除条目的父级：" + treePath);
        var last = treePath.Split('/')[^1];
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonObject o
                && string.Equals(o["name"]?.GetValue<string>(), last, StringComparison.Ordinal))
            {
                arr.RemoveAt(i);
                Save(root);
                return o;
            }
        }
        throw new InvalidOperationException("未找到待删除的条目：" + treePath);
    }

    // ---- 内部：定位与校验 ----

    /// <summary>解析写入目标数组：parentPath 为空 → 根数组；否则取该目录节点的 children（无则建）。</summary>
    private static JsonArray ResolveTargetArray(JsonArray root, string? parentPath)
    {
        if (string.IsNullOrEmpty(parentPath))
            return root;
        var parent = FindByPath(root, parentPath)
            ?? throw new InvalidOperationException("未找到目标目录：" + parentPath);
        if (parent["children"] is JsonArray a)
            return a;
        var created = new JsonArray();
        parent["children"] = created;
        return created;
    }

    private static void EnsureSiblingNameFree(JsonArray arr, string name)
    {
        foreach (var n in arr)
        {
            if (n is JsonObject o
                && string.Equals(o["name"]?.GetValue<string>(), name, StringComparison.Ordinal))
                throw new InvalidOperationException("同级已存在同名条目：" + name);
        }
    }

    /// <summary>按树路径查找条目；找不到返回 null。</summary>
    public static JsonObject? FindByPath(JsonArray root, string treePath)
    {
        if (string.IsNullOrEmpty(treePath))
            return null;
        var arr = FindParentArray(root, treePath);
        if (arr == null) return null;
        return FindInArray(arr, treePath.Split('/')[^1]);
    }

    /// <summary>取树路径倒数第二级节点的 children（首级则为根数组）；中途缺目录返回 null。</summary>
    private static JsonArray? FindParentArray(JsonArray root, string treePath)
    {
        var segs = treePath.Split('/');
        var arr = root;
        for (var i = 0; i < segs.Length - 1; i++)
        {
            var node = FindInArray(arr, segs[i]);
            if (node == null)
                return null;
            if (node["children"] is not JsonArray a)
                return null;
            arr = a;
        }
        return arr;
    }

    private static JsonObject? FindInArray(JsonArray arr, string name)
    {
        foreach (var n in arr)
        {
            if (n is JsonObject o
                && string.Equals(o["name"]?.GetValue<string>(), name, StringComparison.Ordinal))
                return o;
        }
        return null;
    }
}
