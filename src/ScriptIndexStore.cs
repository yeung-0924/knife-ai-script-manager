using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIScriptManager;

/// <summary>
/// 唯一脚本索引（script/index.json）的读写层。索引为单文件，脚本树右键菜单的
/// 「创建目录 / 创建脚本 / 重命名 / 编辑 / 删除」全部经本类按条目 id（GUID）定位后修改并整体写回。
/// 允许脚本同级同名，故一律不用名字定位；条目缺 id 时在首次读写时自动补齐（旧索引零迁移成本）。
/// 物理布局：所有脚本文件以「{id}」无扩展名平铺在 script 目录下（与 index.json 同级），
/// 解释器完全由索引条目的 lang 字段决定（与文件名无关），故改语言 / 内容只改 JSON 与文件内容、不动文件名；
/// 条目的 path 字段固定为 ./&lt;id&gt;；重命名 / 层级移动只改 JSON，不动物理文件。
/// 所有写操作都是「整文件读 → 改 → 整文件写」，UTF-8 无 BOM、缩进 2 空格。
/// </summary>
public static class ScriptIndexStore
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>唯一索引文件完整路径（动态取 AppConfig，使「文件▸重载脚本文件」切换索引后即时生效）。</summary>
    public static string IndexPath => ConfigLoader.ScriptIndexJson;

    /// <summary>生成新的条目 id（GUID，标准带连字符格式）。</summary>
    public static string NewId() => Guid.NewGuid().ToString("D");

    /// <summary>
    /// 读取唯一索引根数组，并为缺少 id 的条目自动补 GUID（有补齐则整体写回）。
    /// 旧索引无需手工迁移；文件缺失或为空时返回空数组。
    /// </summary>
    public static JsonArray LoadRoot()
    {
        if (!File.Exists(IndexPath))
            return new JsonArray();
        JsonArray root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(IndexPath, new UTF8Encoding(false)))?.AsArray()
                   ?? new JsonArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("索引 json 解析失败：" + IndexPath + "（" + ex.Message + "）");
        }
        if (AssignMissingIds(root))
            Save(root);
        return root;
    }

    /// <summary>确保索引中所有条目都有 id（缺则补齐并写回）。供树构建前调用，保证节点可被 id 定位。</summary>
    public static void EnsureIds()
    {
        if (!File.Exists(IndexPath))
            return;
        _ = LoadRoot();
    }

    /// <summary>把根数组整体写回唯一索引文件。</summary>
    public static void Save(JsonArray root)
        => File.WriteAllText(IndexPath, root.ToJsonString(Pretty), new UTF8Encoding(false));

    /// <summary>递归给缺 id 的条目补 id（id 置于首位）。返回是否有改动。</summary>
    private static bool AssignMissingIds(JsonArray arr)
    {
        var changed = false;
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject o) continue;
            var id = o["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                // 重建对象把 id 放到首位，其余键原序保留
                var clone = new JsonObject { ["id"] = NewId() };
                foreach (var kv in o)
                    clone[kv.Key] = kv.Value?.DeepClone();
                arr[i] = clone;
                o = clone;
                changed = true;
            }
            if (o["children"] is JsonArray children)
                changed |= AssignMissingIds(children);
        }
        return changed;
    }

    /// <summary>在指定目录条目（parentId 为空 = 根层级）下新建一个空目录条目。同级重名时报错。</summary>
    public static void AddGroup(string? parentId, string name)
    {
        var root = LoadRoot();
        var target = ResolveTargetArray(root, parentId);
        EnsureSiblingNameFree(target, name, selfId: null);
        target.Add(new JsonObject { ["id"] = NewId(), ["name"] = name, ["children"] = new JsonArray() });
        Save(root);
    }

    /// <summary>在指定目录条目（parentId 为空 = 根层级）下追加一个脚本条目。条目缺 id 时自动补。同级重名时报错。</summary>
    public static void AddScriptEntry(string? parentId, JsonObject entry)
    {
        var root = LoadRoot();
        var target = ResolveTargetArray(root, parentId);
        if (string.IsNullOrWhiteSpace(entry["id"]?.GetValue<string>()))
        {
            // id 放首位重建，其余键原序保留
            var clone = new JsonObject { ["id"] = NewId() };
            foreach (var kv in entry)
                clone[kv.Key] = kv.Value?.DeepClone();
            entry = clone;
        }
        var name = entry["name"]?.GetValue<string>() ?? "";
        EnsureSiblingNameFree(target, name, selfId: null);
        target.Add(entry);
        Save(root);
    }

    /// <summary>
    /// 重命名条目（目录或脚本，按 id 定位）：仅改显示名 name。
    /// 物理文件（脚本按 id 命名平铺）不受影响；层级移动同理只需改 JSON 结构，均不动文件。
    /// 同级重名时报错（排除自身）。
    /// </summary>
    public static void RenameEntry(string entryId, string newName)
    {
        var root = LoadRoot();
        var parentArr = FindParentArrayById(root, entryId)
                        ?? throw new InvalidOperationException("未找到待重命名的条目：" + entryId);
        var node = FindInArrayById(parentArr, entryId)
                   ?? throw new InvalidOperationException("未找到待重命名的条目：" + entryId);
        EnsureSiblingNameFree(parentArr, newName, selfId: entryId);
        node["name"] = newName;
        Save(root);
    }

    /// <summary>
    /// 更新脚本条目（编辑模式，按 id 定位）：仅替换 name / lang / params 三个字段，
    /// 其余字段（id、path、hide 等）与 key 顺序原样保留。
    /// 物理文件无扩展名、仅由 lang 决定解释器，故改语言无需改文件名，本方法不触碰物理文件。
    /// </summary>
    public static void UpdateScriptEntry(string entryId, JsonObject entry)
    {
        var root = LoadRoot();
        var node = FindById(root, entryId)
                   ?? throw new InvalidOperationException("未找到待更新的脚本条目：" + entryId);

        node["name"] = entry["name"]?.GetValue<string>() ?? node["name"]?.GetValue<string>() ?? "";
        if (entry["lang"] is not null) node["lang"] = entry["lang"]!.DeepClone();
        if (entry["params"] is not null) node["params"] = entry["params"]!.DeepClone();
        else node.Remove("params");

        Save(root);
    }

    /// <summary>
    /// 按 id 删除条目并写回。返回被删条目（脚本删除时可据此取 path 清理物理文件）。
    /// 目录删除只移除索引子树，不递归删物理文件（脚本文件按 id 平铺、互不嵌套，删除脚本时单独清）。
    /// </summary>
    public static JsonObject RemoveEntry(string entryId)
    {
        var root = LoadRoot();
        var arr = FindParentArrayById(root, entryId)
                  ?? throw new InvalidOperationException("未找到待删除条目的父级：" + entryId);
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonObject o
                && string.Equals(o["id"]?.GetValue<string>(), entryId, StringComparison.Ordinal))
            {
                arr.RemoveAt(i);
                Save(root);
                return o;
            }
        }
        throw new InvalidOperationException("未找到待删除的条目：" + entryId);
    }

    /// <summary>
    /// 移动条目到新父级（根层级 / 某个目录条目之下），改变其在树中的显示层级；仅改 JSON 结构，不动物理脚本文件。
    /// <paramref name="anchorSiblingId"/> 非空时插入到该同级项之前 / 之后（拖拽排序），为空则追加到目标父末尾。
    /// 约束：① 不能把条目拖到它自身之下；② 目录不能拖到它自己的子孙目录里（会形成环）；
    /// ③ 目录移动到目标同级若存在不同 id 的同名项则拒绝（脚本允许同级同名，与创建语义一致）。
    /// </summary>
    public static void MoveEntry(string entryId, string? newParentId, string? anchorSiblingId = null, bool insertAfter = false)
    {
        var root = LoadRoot();
        var srcParent = FindParentArrayById(root, entryId)
                        ?? throw new InvalidOperationException("未找到待移动的条目：" + entryId);
        var srcNode = FindInArrayById(srcParent, entryId)
                      ?? throw new InvalidOperationException("未找到待移动的条目：" + entryId);

        // ① 不能拖到自身
        if (string.Equals(newParentId, entryId, StringComparison.Ordinal))
            throw new InvalidOperationException("不能把目录或脚本放到它自己下面");

        // ② 目录不能拖到自身子孙（环检测）
        if (srcNode["children"] is JsonArray)
        {
            if (newParentId != null && ContainsId(srcNode, newParentId))
                throw new InvalidOperationException("不能把目录拖到它自己的子目录里");
        }

        // 解析目标父数组（空 = 根）
        JsonArray targetArr;
        if (string.IsNullOrEmpty(newParentId))
        {
            targetArr = root;
        }
        else
        {
            var parent = FindById(root, newParentId)
                         ?? throw new InvalidOperationException("未找到目标目录：" + newParentId);
            targetArr = parent["children"] as JsonArray ?? (JsonArray)(parent["children"] = new JsonArray());
        }

        // ③ 目录同级重名校验（脚本宽松，不校验）
        if (srcNode["children"] is JsonArray)
            EnsureSiblingNameFree(targetArr, srcNode["name"]?.GetValue<string>() ?? "", entryId);

        // 先从原父移除（断开引用），再按锚点插入目标数组；锚点为空 = 追加末尾
        // （同一 JsonObject 引用改挂到新父，JSON 结构即更新；先移除再定位锚点，索引才是移除后的正确值）
        srcParent.Remove(srcNode);
        if (!string.IsNullOrEmpty(anchorSiblingId))
        {
            var idx = -1;
            for (var i = 0; i < targetArr.Count; i++)
            {
                if (targetArr[i] is JsonObject o
                    && string.Equals(o["id"]?.GetValue<string>(), anchorSiblingId, StringComparison.Ordinal))
                { idx = i; break; }
            }
            if (idx >= 0)
            {
                targetArr.Insert(insertAfter ? idx + 1 : idx, srcNode);
                Save(root);
                return;
            }
        }
        targetArr.Add(srcNode);
        Save(root);
    }

    /// <summary>查找条目所在的父级 id（根层级返回 null）。按 id 定位，不依赖名字。</summary>
    public static string? FindParentId(string entryId)
    {
        string? Search(JsonArray arr, string? parentId)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                if (string.Equals(o["id"]?.GetValue<string>(), entryId, StringComparison.Ordinal))
                    return parentId;
                if (o["children"] is JsonArray c)
                {
                    var r = Search(c, o["id"]?.GetValue<string>());
                    if (r != null) return r;
                }
            }
            return null;
        }
        return Search(LoadRoot(), null);
    }

    // ---- 内部：定位与校验 ----

    /// <summary>递归判断 node 子树（含自身）是否包含指定 id（用于目录拖拽的环检测）。</summary>
    private static bool ContainsId(JsonObject node, string id)
    {
        if (string.Equals(node["id"]?.GetValue<string>(), id, StringComparison.Ordinal))
            return true;
        if (node["children"] is JsonArray c)
        {
            foreach (var n in c)
                if (n is JsonObject o && ContainsId(o, id))
                    return true;
        }
        return false;
    }


    /// <summary>解析写入目标数组：parentId 为空 → 根数组；否则取该目录条目的 children（无则建）。</summary>
    private static JsonArray ResolveTargetArray(JsonArray root, string? parentId)
    {
        if (string.IsNullOrEmpty(parentId))
            return root;
        var parent = FindById(root, parentId)
                     ?? throw new InvalidOperationException("未找到目标目录：" + parentId);
        if (parent["children"] is JsonArray a)
            return a;
        var created = new JsonArray();
        parent["children"] = created;
        return created;
    }

    /// <summary>同级重名校验；selfId 用于重命名时排除自身（同名条目通过 id 区分彼此）。</summary>
    private static void EnsureSiblingNameFree(JsonArray arr, string name, string? selfId)
    {
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            if (selfId != null
                && string.Equals(o["id"]?.GetValue<string>(), selfId, StringComparison.Ordinal))
                continue;
            if (string.Equals(o["name"]?.GetValue<string>(), name, StringComparison.Ordinal))
                throw new InvalidOperationException("同级已存在同名条目：" + name);
        }
    }

    /// <summary>递归按 id 查找条目；找不到返回 null。</summary>
    public static JsonObject? FindById(JsonArray arr, string id)
    {
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            if (string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.Ordinal))
                return o;
            if (o["children"] is JsonArray c)
            {
                var hit = FindById(c, id);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    /// <summary>递归查找包含指定 id 条目的数组（其父级 children 或根数组）。</summary>
    private static JsonArray? FindParentArrayById(JsonArray arr, string id)
    {
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            if (string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.Ordinal))
                return arr;
            if (o["children"] is JsonArray c)
            {
                var hit = FindParentArrayById(c, id);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    private static JsonObject? FindInArrayById(JsonArray arr, string id)
    {
        foreach (var n in arr)
        {
            if (n is JsonObject o
                && string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.Ordinal))
                return o;
        }
        return null;
    }
}
