using System.IO;
using System.Text;
using System.Windows;
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
/// 生成过程为流式（模型回复实时刷进脚本预览区）；首轮成功后进入多轮对话——描述框清空、
/// 占位符切换为追问提示，再次「生成」即带着历史上下文迭代改写，对话历史持久化到
/// cache/{脚本id}/（每次编辑会话一个文件，删除脚本不清缓存）。
/// 「接受并写入」即结束本次编辑会话：对话状态重置，下次生成重新发起（编辑模式直接关闭窗口）。
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

    private AiGeneratedScript? _result;
    // 多轮对话状态：首轮成功后保留，之后每次「生成」都作为追问发给 AI；「接受并写入」后重置
    private AiConversation? _conversation;
    // 本会话内脚本的稳定 id：首轮分配 / 编辑模式取种子，追问轮沿用（文件名、索引条目、缓存目录都认它）
    private string? _scriptId;
    // 本会话已完成的对话轮数（首轮 = 1，追问逐次累加）；追问已用次数 = _roundsUsed - 1，
    // 对照 [ai] max_rounds（最大追问轮次，默认 0 = 不追问）判断能否继续；「接受并写入」后归零
    private int _roundsUsed;

    private bool IsEditMode => EditSeed != null;

    public AiScriptGenWindow()
    {
        InitializeComponent();

        if (IsEditMode)
        {
            Title = Strings.TitleAiEdit;
            DescLabel.Text = Strings.AiGenEditLabel;
            DescPlaceholder.Text = Strings.AiGenEditPlaceholder;
            // 预览区先展示现状：脚本当前内容（此时「接受并写入」禁用，必须先按修改要求重新生成）
            ScriptPreview.Text = EditSeed!.Content;
            _scriptId = EditSeed.Id;
            StatusText.Text = string.Format(Strings.AiStatusEditMode, EditSeed.Name);
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
        BtnAccept.IsEnabled = false;
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
            _roundsUsed++;
            // 已达追问上限（默认 0 = 首轮后即止）：状态栏提示收尾，按钮在 finally 中保持禁用
            StatusText.Text = _roundsUsed - 1 >= AppConfig.AiMaxRounds
                ? string.Format(Strings.AiStatusRoundLimit, AppConfig.AiMaxRounds)
                : Strings.AiStatusGenerated;
            BtnAccept.IsEnabled = true;

            // 多轮对话：清空描述框等待下一轮修改要求（占位符切换为追问提示）
            DescBox.Clear();
            DescPlaceholder.Text = Strings.AiGenFollowUpPlaceholder;
        }
        catch (System.Exception ex)
        {
            _result = null;
            PreviewScriptLabel.Text = Strings.AiGenPreviewScript;
            StatusText.Text = string.Format(Strings.AiStatusGenFail, ex.Message);
            BtnAccept.IsEnabled = false;
        }
        finally
        {
            // 已用追问数（_roundsUsed - 1）达上限后禁止再次生成（失败不计入轮数；首轮失败时为 -1，恒允许重试）
            BtnGenerate.IsEnabled = _roundsUsed - 1 < AppConfig.AiMaxRounds;
        }
    }

    private void BtnAccept_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        try
        {
            if (IsEditMode)
            {
                // 覆盖原脚本文件（位置不变，文件名即 id、无扩展名），并按条目 id 更新索引条目（改语言只改 JSON 的 lang 字段）
                File.WriteAllText(EditFilePath!, _result.Content, new UTF8Encoding(false));
                ScriptIndexStore.UpdateScriptEntry(EditEntryId!, ScriptGenerator.BuildEntry(_result));
                StatusText.Text = Strings.AiStatusEditDone;

                // 保存即结束本次编辑会话：重置对话，下次打开/生成重新发起
                _conversation = null;
                _scriptId = null;
                _result = null;
                _roundsUsed = 0;
                ScriptPreview.Text = "";
                DescBox.Clear();
                DescPlaceholder.Text = Strings.AiGenDescPlaceholder;
                BtnAccept.IsEnabled = false;
                OwnerViewModel?.ReloadTree();
                Close();
                return;
            }

            // 创建模式：写文件（{id} 无扩展名平铺）+ 按目录 id 插入索引条目
            var entry = ScriptGenerator.BuildEntry(_result);
            var path = ScriptGenerator.WriteScriptFile(_result);
            ScriptIndexStore.AddScriptEntry(ParentGroupId, entry);
            StatusText.Text = Strings.AiStatusWriteDone + "：" + _result.Name;

            // 保存即结束本次编辑会话：重置对话与预览，便于从头创建下一个脚本
            _conversation = null;
            _scriptId = null;
            _result = null;
            _roundsUsed = 0;
            ScriptPreview.Text = "";
            DescBox.Clear();
            DescPlaceholder.Text = Strings.AiGenDescPlaceholder;
            PreviewScriptLabel.Text = Strings.AiGenPreviewScript;
            BtnAccept.IsEnabled = false;
            // 新会话轮次从零起算，重新启用生成（可能刚因达到轮次上限被禁用）
            BtnGenerate.IsEnabled = AppConfig.AiEnabled;

            // 写入后刷新左侧目录树，使新脚本立即可见
            OwnerViewModel?.ReloadTree();
        }
        catch (System.Exception ex)
        {
            StatusText.Text = string.Format(Strings.AiStatusGenFail, ex.Message);
        }
    }
}
