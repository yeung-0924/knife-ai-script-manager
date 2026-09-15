using System.Windows;
using System.Windows.Controls;
using ScriptManager.Ai;
using ScriptManager.ViewModels;

namespace ScriptManager.Views;

/// <summary>
/// 「AI 生成脚本」对话框：用户在一个描述框里用自然语言写明脚本功能，并可一并说明需要的参数与语言，
/// 调用 <see cref="ScriptGenerator"/>（底层走 OpenAI 兼容的 <see cref="AiClient"/>）生成结构化脚本，
/// 先在界面预览「脚本正文」与「将写入 index.json 的条目」，用户手动点「接受并写入」后才落盘到
/// script/ai-generated/ 并刷新左侧脚本树。未配置 AI API 时禁用生成并提示先去「设置 ▸ 编辑配置」。
/// </summary>
public partial class AiScriptGenWindow : Window
{
    /// <summary>宿主主窗口视图模型，接受写入后用于触发左侧目录树按新脚本索引重建；可为 null（防御性）。</summary>
    public MainViewModel? OwnerViewModel { get; set; }

    private AiGeneratedScript? _result;

    public AiScriptGenWindow()
    {
        InitializeComponent();

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
            StatusText.Text = "请先填写脚本功能描述。";
            return;
        }

        BtnGenerate.IsEnabled = false;
        BtnAccept.IsEnabled = false;
        ScriptPreview.Text = "";
        IndexPreview.Text = "";
        StatusText.Text = Strings.AiStatusGenerating;

        try
        {
            _result = await ScriptGenerator.GenerateAsync(desc);
            ScriptPreview.Text = _result.Content;
            IndexPreview.Text = ScriptGenerator.PreviewIndexEntry(_result);
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
            var path = ScriptGenerator.WriteToDisk(_result);
            // 写入后刷新左侧目录树，使新脚本立即可见、可运行
            OwnerViewModel?.ReloadTree();
            StatusText.Text = Strings.AiStatusWriteDone + "：" + System.IO.Path.GetFileName(path);
            BtnAccept.IsEnabled = false;
            // 清空预览，保留描述/参数，便于连续生成下一条
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
