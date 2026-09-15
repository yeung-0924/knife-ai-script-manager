using System.IO;
using System.Text;
using System.Windows;
using ScriptManager.Ai;
using ScriptManager.ViewModels;

namespace ScriptManager.Views;

/// <summary>
/// 「AI 脚本编辑器」对话框，双模式：
///  - 创建模式（默认）：在描述框写清脚本功能（可一并说明参数与语言），生成后脚本文件写入
///    script/ai-generated/，索引条目经 <see cref="ScriptIndexStore"/> 插入 ParentGroupPath
///    指向的目录节点（null = 根层级）下的唯一 script/index.json；
///  - 编辑模式（EditSeed / EditTreePath / EditFilePath 由主窗口装配）：载入现有脚本让 AI 按修改要求改写，
///    接受后覆盖原脚本文件并按旧树路径更新其索引条目（path 不变）。
/// 两种模式都先在界面预览「脚本正文」与「索引条目」，手动点「接受并写入」才落盘并刷新脚本树。
/// 未配置 AI API 时禁用生成并提示先去「设置 ▸ 编辑配置」。
/// </summary>
public partial class AiScriptGenWindow : Window
{
    /// <summary>宿主主窗口视图模型，接受写入后用于触发左侧目录树重建；可为 null（防御性）。</summary>
    public MainViewModel? OwnerViewModel { get; set; }

    /// <summary>创建模式：新脚本条目要插入的目标目录树路径（null = 根层级）。</summary>
    public string? ParentGroupPath { get; set; }

    /// <summary>编辑模式：被改写的现有脚本（内容 / 参数 / 语言种子）。</summary>
    public AiGeneratedScript? EditSeed { get; set; }

    /// <summary>编辑模式：被改写脚本在索引中的树路径（更新条目用）。</summary>
    public string? EditTreePath { get; set; }

    /// <summary>编辑模式：脚本文件的完整路径（覆盖写入用）。</summary>
    public string? EditFilePath { get; set; }

    private AiGeneratedScript? _result;

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

        BtnGenerate.IsEnabled = false;
        BtnAccept.IsEnabled = false;
        ScriptPreview.Text = "";
        IndexPreview.Text = "";
        StatusText.Text = Strings.AiStatusGenerating;

        try
        {
            // 流式生成：模型每吐一段就把增量追加进脚本预览区（后台线程回调，需调度回 UI 线程）
            var sb = new System.Text.StringBuilder();
            _result = await ScriptGenerator.GenerateAsync(desc, EditSeed, piece =>
            {
                var text = sb.Append(piece).ToString();
                Dispatcher.BeginInvoke(() =>
                {
                    ScriptPreview.Text = text;
                    ScriptPreview.ScrollToEnd();
                    StatusText.Text = string.Format(Strings.AiStatusStreaming, text.Length);
                });
            });
            ScriptPreview.Text = _result.Content;
            // 编辑模式 path 保持不变，条目预览中省略以免误导
            var entry = ScriptGenerator.BuildEntry(_result);
            if (IsEditMode)
                entry.Remove("path");
            IndexPreview.Text = entry.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            StatusText.Text = Strings.AiStatusGenerated;
            BtnAccept.IsEnabled = true;
        }
        catch (System.Exception ex)
        {
            _result = null;
            StatusText.Text = string.Format(Strings.AiStatusGenFail, ex.Message);
            BtnAccept.IsEnabled = false;
        }
        finally
        {
            BtnGenerate.IsEnabled = true;
        }
    }

    private void BtnAccept_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        try
        {
            if (IsEditMode)
            {
                // 覆盖原脚本文件（位置不变），并按旧树路径更新索引条目
                File.WriteAllText(EditFilePath!, _result.Content, new UTF8Encoding(false));
                ScriptIndexStore.UpdateScriptEntry(EditTreePath!, ScriptGenerator.BuildEntry(_result));
                StatusText.Text = Strings.AiStatusEditDone;
            }
            else
            {
                var path = ScriptGenerator.WriteScriptFile(_result);
                ScriptIndexStore.AddScriptEntry(ParentGroupPath, ScriptGenerator.BuildEntry(_result));
                StatusText.Text = Strings.AiStatusWriteDone + "：" + System.IO.Path.GetFileName(path);
            }

            // 写入后刷新左侧目录树，使新脚本/修改立即可见
            OwnerViewModel?.ReloadTree();
            BtnAccept.IsEnabled = false;
            // 清空预览与结果，保留描述框，便于连续操作
            _result = null;
            ScriptPreview.Text = "";
            IndexPreview.Text = "";
        }
        catch (System.Exception ex)
        {
            StatusText.Text = string.Format(Strings.AiStatusGenFail, ex.Message);
        }
    }
}
