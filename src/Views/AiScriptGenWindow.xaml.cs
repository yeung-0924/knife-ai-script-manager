using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AIScriptManager.Ai;
using AIScriptManager.ViewModels;

namespace AIScriptManager.Views;

/// <summary>
/// 「AI 脚本编辑器」对话框，双模式：
///  - 创建模式（默认）：在描述框写清脚本功能（可一并说明参数与语言），生成后脚本文件以
///    「{id}」无扩展名平铺写入 script 目录（解释器由 index.json 的 lang 决定），索引条目经 <see cref="ScriptIndexStore"/> 按 id 插入
///    ParentGroupId 指向的目录节点（null = 根层级）下的唯一 script/index.json；
///  - 编辑模式（EditSeed / EditEntryId / EditFilePath 由主窗口装配）：载入现有脚本让 AI 按修改要求改写，
///    接受后覆盖原脚本文件并按条目 id 更新其索引条目（改语言只改 JSON 的 lang 字段，无需改文件名）。
/// 脚本名称**只由用户手动填写且必填**：AI 不参与取名（system prompt 已禁止模型返回 name 字段，
/// 解析结果里的名称一律忽略）；名称为空时「接受并写入」保持禁用、不落库。
/// 生成过程为流式（模型回复实时刷进脚本预览区）；首轮成功后进入多轮对话——描述框清空、
/// 占位符切换为追问提示，再次「生成」即带着历史上下文迭代改写，对话历史持久化到
/// cache/{脚本id}/（每次编辑会话一个文件，删除脚本不清缓存）。
/// 「接受并写入」落库成功后，由 <see cref="ScriptHistory"/> 在 history/{脚本id}/ 留一份内容副本（时间戳命名），
/// 供日后回退（本阶段只记录，不提供历史查看 / 一键回退）。
/// 「接受并写入」即结束本次编辑会话：对话状态重置并关闭弹窗（创建 / 编辑两种模式一致），
/// 宿主主窗口据 <see cref="AcceptedEntryId"/> 展开其父目录并选中该脚本，使结果立刻落在用户视野内。
/// 一次编辑会话的追问轮数受配置 [ai] max_rounds 限制（默认 0 = 仅首轮生成、不追问；
/// N = 原始会话 + N 轮追问），达到上限后「生成」禁用，只能接受当前结果或关闭窗口重新发起。
/// 未配置 AI API 时禁用生成并提示先去「设置 ▸ 编辑配置」。
/// </summary>
public partial class AiScriptGenWindow : Window
{
    /// <summary>宿主主窗口视图模型，接受写入后用于触发左侧目录树重建；可为 null（防御性）。</summary>
    public MainViewModel? OwnerViewModel { get; set; }

    /// <summary>创建模式：新脚本条目要插入的目标目录条目 id（null = 根层级）。</summary>
    public string? ParentGroupId { get; set; }

    /// <summary>编辑模式：被改写的现有脚本（内容 / 参数 / 语言种子，Id 为原条目 id）。</summary>
    public AiGeneratedScript? EditSeed { get; set; }

    /// <summary>编辑模式：被改写脚本在索引中的条目 id（更新条目用，允许同级同名故不用名字定位）。</summary>
    public string? EditEntryId { get; set; }

    /// <summary>编辑模式：脚本文件的完整路径（覆盖写入用）。</summary>
    public string? EditFilePath { get; set; }

    /// <summary>
    /// 本次「接受并写入」落库的条目 id（创建模式 = 新脚本 id；编辑模式 = 原条目 id）；
    /// 未接受（取消 / 直接关闭）时为 null。宿主主窗口在弹窗关闭后据此展开父目录并选中该脚本，
    /// 使新创建 / 刚编辑的脚本立刻处于用户视野内。
    /// </summary>
    public string? AcceptedEntryId { get; private set; }

    private AiGeneratedScript? _result;
    // 多轮对话状态：首轮成功后保留，之后每次「生成」都作为追问发给 AI；「接受并写入」后重置
    private AiConversation? _conversation;
    // 本会话内脚本的稳定 id：首轮分配 / 编辑模式取种子，追问轮沿用（文件名、索引条目、缓存目录都认它）
    private string? _scriptId;
    // 本会话已完成的对话轮数（首轮 = 1，追问逐次累加）；追问已用次数 = _roundsUsed - 1，
    // 对照 [ai] max_rounds（最大追问轮次，默认 0 = 不追问）判断能否继续；「接受并写入」后归零
    private int _roundsUsed;
    // 脚本名称：只由用户在名称框里手动填写（必填）——AI 不参与取名，生成结果里的名称一律忽略，
    // 故不再需要「AI 回填 / 用户手改」那套判定状态。
    // 程序预填（编辑模式带入原名）/ 清空名称框时抑制 TextChanged，避免误触发按钮刷新与必填提示
    private bool _suppressNameChanged;
    // 生成进行中：期间「接受并写入」保持禁用（结果未定），名称框编辑不得把它重新点亮
    private bool _isGenerating;

    private bool IsEditMode => EditSeed != null;

    public AiScriptGenWindow()
    {
        InitializeComponent();
        // 模式相关初始化（标题 / 预填名称 / 预览现状）必须放在 Loaded：本窗口由调用方用对象初始化器
        //   new AiScriptGenWindow { EditSeed = seed, ... } 构造，C# 语义是先跑无参构造函数、再应用初始化器，
        //   故构造函数体内 EditSeed 尚为 null，IsEditMode 恒为假——编辑分支是死代码、标题会被误设为「创建脚本」。
        //   Loaded 在属性全部赋值、ShowDialog 之前触发，此时才是正确的判断时机。
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsEditMode)
        {
            Title = Strings.TitleAiEdit;
            DescLabel.Text = Strings.AiGenEditLabel;
            DescPlaceholder.Text = Strings.AiGenEditPlaceholder;
            // 预览区先展示现状：脚本当前内容（此时「接受并写入」禁用，必须先按修改要求重新生成）
            ScriptPreview.Text = EditSeed!.Content;
            _scriptId = EditSeed.Id;
            StatusText.Text = string.Format(Strings.AiStatusEditMode, EditSeed.Name);
            // 名称框预填当前脚本名（编辑模式可直接改）
            _suppressNameChanged = true;
            NameBox.Text = EditSeed.Name;
            _suppressNameChanged = false;
        }
        else
        {
            Title = Strings.TitleAiCreate;
        }

        if (!AppConfig.AiEnabled)
        {
            StatusText.Text = Strings.AiStatusNoApi;
            BtnGenerate.IsEnabled = false;
        }
    }

    private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
    {
        var desc = DescBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(desc))
        {
            StatusText.Text = Strings.AiStatusNeedDesc;
            return;
        }

        // 追问轮次上限（[ai] max_rounds = 首轮之外允许的追问数，默认 0 = 不追问）：
        // 已用追问数 = _roundsUsed - 1（防御性检查，正常路径已在生成完成后禁用按钮）
        var maxRounds = AppConfig.AiMaxRounds;
        if (_roundsUsed - 1 >= maxRounds)
        {
            StatusText.Text = string.Format(Strings.AiStatusRoundLimit, maxRounds);
            BtnGenerate.IsEnabled = false;
            return;
        }

        BtnGenerate.IsEnabled = false;
        _isGenerating = true;
        // 生成期间「接受并写入」保持禁用；完成 / 失败后由 finally 里的 RefreshAcceptEnabled 统一刷新
        RefreshAcceptEnabled();
        ScriptPreview.Text = "";
        // 生成中：脚本预览区临时充当「实时回复」流式窗口
        PreviewScriptLabel.Text = Strings.AiGenStreamingLabel;
        StatusText.Text = Strings.AiStatusGenerating;

        // 流式增量回调：后台线程逐段追加，调度回 UI 线程刷新预览区并自动滚动
        var sb = new System.Text.StringBuilder();
        void OnDelta(string piece)
        {
            var text = sb.Append(piece).ToString();
            Dispatcher.BeginInvoke(() =>
            {
                ScriptPreview.Text = text;
                ScriptPreview.ScrollToEnd();
                StatusText.Text = string.Format(Strings.AiStatusStreaming, text.Length);
            });
        }

        try
        {
            if (_conversation == null)
            {
                // 首轮：开启对话（编辑模式把现有脚本内容一并入上下文）
                (_conversation, _result) = await ScriptGenerator.StartConversationAsync(desc, EditSeed, OnDelta);
            }
            else
            {
                // 追问轮：历史已在对话里，只发新的修改要求
                _result = await _conversation.SendAsync(desc + ScriptGenerator.FollowUpSuffix, OnDelta);
            }

            // 统一脚本 id：首轮分配 / 追问轮与编辑模式沿用（文件名、索引条目、缓存目录都以它为准）
            _result.Id ??= _scriptId ?? EditSeed?.Id ?? ScriptIndexStore.NewId();
            _scriptId = _result.Id;
            // 对话历史持久化到 cache/{脚本id}/（每次编辑会话一个文件，覆盖更新）
            _conversation.ScriptId = _scriptId;
            _conversation.PersistToCache();

            // 完成：恢复预览区标题，显示解析后的脚本正文
            PreviewScriptLabel.Text = Strings.AiGenPreviewScript;
            ScriptPreview.Text = _result.Content;
            // 名称框不由 AI 写入（脚本名称仅手动填写、必填）：生成成功后「接受并写入」是否可点
            // 取决于名称框是否有值，由 finally 里的 RefreshAcceptEnabled 决定
            _roundsUsed++;
            // 已达追问上限（默认 0 = 首轮后即止）：状态栏提示收尾，按钮在 finally 中保持禁用
            StatusText.Text = _roundsUsed - 1 >= AppConfig.AiMaxRounds
                ? string.Format(Strings.AiStatusRoundLimit, AppConfig.AiMaxRounds)
                : Strings.AiStatusGenerated;

            // 多轮对话：清空描述框等待下一轮修改要求（占位符切换为追问提示）
            DescBox.Clear();
            DescPlaceholder.Text = Strings.AiGenFollowUpPlaceholder;
        }
        catch (System.Exception ex)
        {
            _result = null;
            PreviewScriptLabel.Text = Strings.AiGenPreviewScript;
            StatusText.Text = string.Format(Strings.AiStatusGenFail, ex.Message);
        }
        finally
        {
            // 已用追问数（_roundsUsed - 1）达上限后禁止再次生成（失败不计入轮数；首轮失败时为 -1，恒允许重试）
            BtnGenerate.IsEnabled = _roundsUsed - 1 < AppConfig.AiMaxRounds;
            _isGenerating = false;
            // 名称为空时「接受并写入」保持禁用（脚本名称必填，AI 不代取名）
            RefreshAcceptEnabled();
        }
    }

    private void BtnAccept_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        // 脚本名称必填、且只能手动填写（AI 不代取名）：名称为空则不落库，留在窗口让用户补填。
        // 正常路径下名称为空时「接受并写入」本身是禁用的，这里再兜一道（防御性，如程序化调用）。
        var finalName = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(finalName))
        {
            StatusText.Text = Strings.AiStatusNeedName;
            NameBox.Focus();
            return;
        }
        _result.Name = finalName;
        try
        {
            if (IsEditMode)
            {
                // 覆盖原脚本文件（位置不变，文件名即 id、无扩展名），并按条目 id 更新索引条目（改语言只改 JSON 的 lang 字段）
                File.WriteAllText(EditFilePath!, _result.Content, new UTF8Encoding(false));
                ScriptIndexStore.UpdateScriptEntry(EditEntryId!, ScriptGenerator.BuildEntry(_result));
                // 落库成功后留一份历史副本（history\{脚本id}\{时间戳}），供日后回退；
                // 放在索引写入之后，使保存失败（索引异常等）不产生历史
                ScriptHistory.Snapshot(EditEntryId, EditFilePath);
                StatusText.Text = Strings.AiStatusEditDone;
                // 告知宿主：本次落库的条目 id（同名脚本多，宿主必须按 id 定位并选中它）
                AcceptedEntryId = EditEntryId;

                // 保存即结束本次编辑会话：重置对话，下次打开/生成重新发起
                _conversation = null;
                _scriptId = null;
                _result = null;
                _roundsUsed = 0;
                ScriptPreview.Text = "";
                DescBox.Clear();
                DescPlaceholder.Text = Strings.AiGenDescPlaceholder;
                _suppressNameChanged = true;
                NameBox.Clear();
                _suppressNameChanged = false;
                BtnAccept.IsEnabled = false;
                OwnerViewModel?.ReloadTree();
                Close();
                return;
            }

            // 创建模式：写文件（{id} 无扩展名平铺）+ 按目录 id 插入索引条目
            var entry = ScriptGenerator.BuildEntry(_result);
            var path = ScriptGenerator.WriteScriptFile(_result);
            ScriptIndexStore.AddScriptEntry(ParentGroupId, entry);
            // 落库成功后留一份历史副本（history\{新脚本id}\{时间戳}），供日后回退；
            // 必须在 AddScriptEntry 之后：同级重名等失败时不留历史，与「保存未成功」语义一致
            ScriptHistory.Snapshot(entry["id"]?.GetValue<string>(), path);
            StatusText.Text = Strings.AiStatusWriteDone + "：" + _result.Name;
            // 告知宿主：新脚本的条目 id（同名脚本多，宿主必须按 id 定位并选中它）
            AcceptedEntryId = entry["id"]?.GetValue<string>();

            // 保存即结束本次编辑会话：重置对话与预览
            _conversation = null;
            _scriptId = null;
            _result = null;
            _roundsUsed = 0;
            ScriptPreview.Text = "";
            DescBox.Clear();
            DescPlaceholder.Text = Strings.AiGenDescPlaceholder;
            _suppressNameChanged = true;
            NameBox.Clear();
            _suppressNameChanged = false;
            PreviewScriptLabel.Text = Strings.AiGenPreviewScript;
            BtnAccept.IsEnabled = false;

            // 写入后刷新左侧目录树并关闭弹窗：一次「创建」= 一次完整操作，收尾与编辑模式一致；
            // 展开父目录 + 选中新脚本由宿主主窗口按 AcceptedEntryId 完成
            OwnerViewModel?.ReloadTree();
            Close();
        }
        catch (System.Exception ex)
        {
            StatusText.Text = string.Format(Strings.AiStatusGenFail, ex.Message);
        }
    }

    /// <summary>
    /// 名称框内容变化：刷新「接受并写入」的可用性；名称为空时给出必填提示（名称只能手动填写，AI 不代取名）。
    /// 程序预填 / 清空（编辑模式带入原名、写入后清空）已由 _suppressNameChanged 抑制，不会误触发。
    /// </summary>
    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressNameChanged) return;
        RefreshAcceptEnabled();
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            if (_result != null) StatusText.Text = Strings.AiStatusNeedName;
        }
        else if (StatusText.Text == Strings.AiStatusNeedName)
        {
            // 补填名称后撤下必填提示，回到常态文案（已达追问上限时仍显示上限提示）
            StatusText.Text = _roundsUsed - 1 >= AppConfig.AiMaxRounds
                ? string.Format(Strings.AiStatusRoundLimit, AppConfig.AiMaxRounds)
                : Strings.AiStatusGenerated;
        }
    }

    /// <summary>
    /// 刷新「接受并写入」的可用性：需已有生成结果、当前未在生成中、且脚本名称已手动填写（名称必填）。
    /// </summary>
    private void RefreshAcceptEnabled()
        => BtnAccept.IsEnabled = _result != null && !_isGenerating && !string.IsNullOrWhiteSpace(NameBox.Text);
}
