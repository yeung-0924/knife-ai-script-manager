using System.Windows;
using System.Windows.Controls;

namespace AIScriptManager.Views;

/// <summary>
/// 通用单行输入对话框（新建目录 / 重命名等）：标题与提示由调用方传入，Value 取去空格后的输入值。
/// 可传 initialValue 预填（重命名时带入当前名称）；空输入时「确定」禁用；回车 = 确定（IsDefault），Esc = 取消（IsCancel）。
/// </summary>
public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
        BtnOk.IsEnabled = !string.IsNullOrWhiteSpace(ValueBox.Text);
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();   // 重命名场景：全选当前名，直接输入即覆盖
        };
    }

    /// <summary>用户输入（去首尾空格）。</summary>
    public string Value => ValueBox.Text?.Trim() ?? "";

    private void ValueBox_TextChanged(object sender, TextChangedEventArgs e)
        => BtnOk.IsEnabled = !string.IsNullOrWhiteSpace(ValueBox.Text);

    private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
