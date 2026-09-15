using System.Windows;
using System.Windows.Controls;

namespace ScriptManager.Views;

/// <summary>
/// 通用单行输入对话框（新建目录等）：标题与提示由调用方传入，Value 取去空格后的输入值。
/// 空输入时「确定」禁用；回车 = 确定（IsDefault），Esc = 取消（IsCancel）。
/// </summary>
public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        BtnOk.IsEnabled = false;    // 初始为空，禁用确定
        Loaded += (_, _) => ValueBox.Focus();
    }

    /// <summary>用户输入（去首尾空格）。</summary>
    public string Value => ValueBox.Text?.Trim() ?? "";

    private void ValueBox_TextChanged(object sender, TextChangedEventArgs e)
        => BtnOk.IsEnabled = !string.IsNullOrWhiteSpace(ValueBox.Text);

    private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
