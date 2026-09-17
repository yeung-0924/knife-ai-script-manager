namespace AIScriptManager;

/// <summary>
/// 集中管理所有用户可见的文本（按钮、标题、状态消息、占位提示、对话框文案等），
/// 方便统一维护与本地化。内部实现细节（Debug 日志、运行时命令参数）不在此列。
/// </summary>
public static class Strings
{
    #region 版本号
    // 发版时在此修改；UI 右下角以 "v {Version}" 形式展示，常量写死，运行时不暴露给用户修改
    public const string Version = "1.1.0";
    #endregion

    #region 标题（面板/分区）
    public const string TitleScriptList = "脚本列表";
    public const string TitleScriptParams = "脚本参数";
    public const string TitleScriptPreview = "脚本预览";
    public const string TitleExecLog = "执行日志";
    public const string TitleWindow = "AI脚本管理器";
    #endregion

    #region 按钮
    public const string BtnExport = "导出";
    public const string BtnOpen = "打开";
    public const string BtnSave = "保存";
    public const string BtnReset = "重置";
    public const string BtnDefault = "默认值";
    public const string BtnCopy = "复制";
    public const string BtnSettings = "配置";
    public const string BtnClear = "清空";
    public const string BtnRun = "执行";
    public const string BtnStop = "停止";
    public const string BtnAuto = "自动检测";
    public const string BtnAutoToolTip = "自动检测：优先使用运行时目录（runtime），其次系统环境变量 PATH";
    public const string BtnExpandAll = "展开全部";
    public const string BtnCollapseAll = "收起全部";
        public const string BtnExpandCollapseToolTip = "展开/收起全部目录";
        #endregion

        #region 顶部菜单（工具栏）
        public const string MenuFile = "文件";
        public const string MenuSettings = "设置";
        public const string MenuEditConfig = "编辑配置...";
        public const string TitleConfigEditor = "配置编辑";
        public const string ConfigEditorBrowseFolder = "选择目录";
        public const string ConfigEditorBrowseFile = "选择脚本索引文件 (index.json)";
        public const string ConfigEditorTimeoutLabel = "默认执行超时(秒)";
        public const string ConfigEditorTimeoutPlaceholder = "0（不限制）";
        #endregion

    #region AI 生成脚本
    public const string TitleAiCreate = "创建脚本";
    public const string TitleAiEdit = "编辑脚本";
    public const string TitleAiGenerate = "生成脚本";
    public const string AiGenDescLabel = "脚本功能描述";
    // 占位提示 = 一段可直接照抄的完整示例：示范「语言 + 功能 + 参数（类型/必填/默认值/选项）」的写法，并列出可选语言与参数类型，让用户一眼知道本功能能做什么。\n 为换行，TextBlock 会分两段显示。
    public const string AiGenDescPlaceholder = "请用 python 帮我实现「批量重命名」脚本：读取参数「目录」（folder 类型，必填）下的全部文件，按「前缀」（text 类型，如 img_）加序号依次重命名；再用「起始序号」（text 类型，默认 1）和「是否处理子目录」（select 类型，选项 是/否，默认 否）两个参数控制行为；执行完成后打印新旧文件名对照表。\n（语言也可写 node / java / go / rust / powershell / pwsh / cmd / bash；参数可标注 text、folder、file、select 类型，并说明是否必填、默认值与可选项。描述越具体，生成越准。）";
    public const string AiGenEditLabel = "修改要求（要改什么，越具体越好）";
    // 编辑模式的占位提示：同样给出一段完整示例，示范「在现有脚本上要改什么」的写法
    // （新增 / 删除参数、调整类型与默认值、更换语言、改变处理逻辑与输出格式）。\n 为换行，TextBlock 分两段显示。
    public const string AiGenEditPlaceholder = "请在当前脚本基础上改用 pwsh 重写，并新增「重试次数」参数（text 类型，默认 3）：失败时自动重试，每次间隔 2 秒；再把「目录」参数改为可选，未填写时使用脚本所在目录；最后把输出改为按时间倒序排列。\n（可以指定要改的部分：新增 / 删除参数、调整参数类型与默认值、更换语言、改变处理逻辑与输出格式。说得越具体，改动越贴合预期。）";
    public const string AiGenPreviewScript = "脚本预览（生成后展示，接受前可检查）";
    public const string BtnAiGenerate = "生成";
    public const string BtnAiAccept = "接受并写入";
    public const string AiStatusNoApi = "未配置 AI API：请到「设置 ▸ 编辑配置」填写 API 密钥 / 地址 / 模型";
    public const string AiStatusNeedDesc = "请先在描述框填写内容。";
    public const string AiStatusEditMode = "编辑现有脚本「{0}」：输入修改要求后点「生成」，预览无误再点「接受并写入」";
    public const string AiStatusGenerating = "正在生成脚本…";
    public const string AiStatusStreaming = "正在生成… 已接收 {0} 字（预览区实时显示模型原始回复，完成后自动解析）";
    public const string AiGenStreamingLabel = "模型实时回复（生成中，完成后自动解析为下方脚本）";
    // 追问态（多轮对话）的占位提示。
    public const string AiGenFollowUpPlaceholder = "可继续输入修改要求（多轮对话）：把语言换成 go；参数「关键字」改为必填；输出再追加一列汇总。\n（每一轮都基于上一轮的结果修改，直到点「接受并写入」为止。）";
    public const string AiStatusGenerated = "已生成，请检查预览后点「接受并写入」";
    public const string AiStatusWriteDone = "已写入并刷新脚本列表";
    public const string AiStatusEditDone = "已覆盖脚本文件并更新索引条目";
    public const string AiStatusGenFail = "生成失败：{0}";
    public const string AiStatusRoundLimit = "已达最大追问轮次（{0}）：请点「接受并写入」保存结果，或关闭窗口后重新发起";
    // 配置编辑器内两个分区标题
    public const string WorkPathSection = "工作路径";
    // 配置编辑器内 AI 配置区（模型配置）
    public const string AiConfigSection = "模型配置";
    public const string AiApiKeyLabel = "API 密钥";
    public const string AiBaseUrlLabel = "API 地址";
    public const string AiModelLabel = "模型";
    public const string AiMaxRoundsLabel = "最大追问轮次";
    public const string AiApiKeyPlaceholder = "sk-...（留空即不使用 AI）";
    public const string AiBaseUrlPlaceholder = "https://api.openai.com/v1 或 https://openrouter.ai/api/v1（填到 /v1 即可）";
    public const string AiModelPlaceholder = "gpt-4o-mini";
    public const string AiMaxRoundsPlaceholder = "0（不追问，仅首轮生成）";
    #endregion

    #region 脚本树右键菜单
    public const string TreeMenuCreateDir = "创建目录";
    public const string TreeMenuCreateScript = "创建脚本";
    public const string TreeMenuEditScript = "编辑脚本";
    public const string TreeMenuRename = "重命名";
    public const string TreeMenuDeleteDir = "删除目录";
    public const string TreeMenuDeleteScript = "删除脚本";
    // 二次确认文案（{0} = 节点名）
    public const string TreeDeleteDirConfirm = "确定删除目录「{0}」吗？\n\n仅从索引中移除该目录及其下全部条目，不删除任何脚本文件。";
    public const string TreeDeleteScriptConfirm = "确定删除脚本「{0}」吗？\n\n将同时删除索引条目、脚本文件与其历史记录，此操作不可恢复。";
    public const string TreeDeleteDone = "已删除：{0}";
    public const string TreeDeleteFail = "删除失败：{0}";
    #endregion

    #region 输入对话框（新建目录等）
    public const string TitleInputNewDir = "新建目录";
    public const string InputNewDirPrompt = "目录名称：";
    public const string TitleInputRename = "重命名";
    public const string InputRenamePrompt = "新名称（重命名只改显示名，不移动/改名脚本文件；同级内脚本允许同名，目录名需同级唯一）：";
    public const string TreeRenameNoId = "该条目缺少唯一标识（id），无法重命名。请重启应用让索引自动补齐后重试。";
    public const string BtnOk = "确定";
    public const string BtnCancel = "取消";
    #endregion

    #region 状态消息（StatusText）
    public const string StatusReady = "就绪";
    public const string StatusRunningFormat = "正在执行：{0}";
    public const string StatusStoppedFormat = "已停止：{0}";
    public const string StatusCompletedFormat = "完成：{0}（退出码 0）";
    public const string StatusExitedFormat = "结束：{0}（退出码 {1}）";
    public const string StatusExceptionFormat = "执行异常：{0}";
    // 执行超时（{0} = 脚本名）：自动终止后的状态栏提示
    public const string StatusTimeoutFormat = "执行超时，已终止：{0}";
    public const string StatusStopping = "正在停止…";
    // 可执行文件版本校验进行中（覆盖 StatusText 显示）
    public const string StatusRuntimeChecking = "可执行文件检测中…";
    // 执行耗时（状态栏右侧独立文本块，不并入左侧状态文字）：{0} = mm:ss.fff，如 00:05.123
    public const string StatusElapsedFormat = "已用时 {0}";       // 执行进行中（数值持续跳变）
    public const string StatusTotalElapsedFormat = "总用时 {0}";  // 执行结束后（定格为总耗时）
    public const string StatusRuntimePickedFormat = "已为 {0} 指定可执行文件：{1}";
    // 执行器（可执行文件）校验失败时的状态栏后缀：与 StatusReady 拼接为「就绪 · 未检测到有效的可执行文件」
    public const string StatusRuntimeInvalid = "未检测到有效的可执行文件";
    public const string StatusExportedTo = "已导出到：{0}";
    public const string StatusExportEmpty = "导出失败：没有可导出的脚本";
    public const string StatusExportSameDir = "导出目标与源脚本目录相同，请另选目录";
    public const string StatusExportSourceMissingFormat = "脚本目录不存在：{0}";
    public const string StatusExportFailFormat = "导出失败：{0}";
    public const string StatusCopied = "已复制脚本内容到剪贴板";
    public const string StatusLogCopied = "已复制日志内容到剪贴板";
    #endregion

    #region 参数清空按钮
    public const string ClearButtonToolTip = "清空";
    // 浏览按钮已改为图标样式，文本常量（Bz"浏览…"）已不再被 XAML 引用，保留无意义故删除；ToolTip 仍由 Bz BrowseButtonToolTip 提供
    //（保留作死代码清理记录，2026-09-01 复检若仍无引用即可删除）
    public const string BrowseButtonToolTip = "选择文件或目录";
    #endregion

    #region 脚本复制（结果统一反馈在底部状态栏，不弹窗）
    public const string StatusCopyEmpty = "没有可复制的脚本内容";
    public const string StatusCopyFailFormat = "复制失败：{0}";
    #endregion

    #region 操作反馈（临时提示，数秒后自动恢复为就绪态）
    // StatusReloaded / StatusReloadedEnv：原「刷新」按钮提示，刷新功能下线后已废弃（保留作清理记录）。
    public const string StatusLogCleared = "已清空执行日志";
    public const string StatusParamsReset = "已重置为默认值";
    public const string StatusRuntimeAutoSet = "已自动获取可执行文件（运行时目录或环境变量）";
    // 「打开」脚本索引文件的反馈（状态栏轻提示，不弹窗）：成功加载并记住 / 所选文件非有效脚本索引
    public const string StatusOpenScriptFileDone = "已打开脚本文件（已记住，重启后自动加载）";
    public const string StatusOpenScriptFileInvalid = "所选文件不是有效的脚本索引（index.json）";
    public const string StatusRuntimeAutoFail = "运行时目录与环境变量中均未检测到该语言的可执行文件，请配置 runtime 目录或自行选择";
    #endregion

    #region 占位提示
    public const string RuntimePlaceholderMissing = "未检测到有效的可执行文件，请配置 runtime 目录或环境变量，或自行选择";
    // lang 取值不在支持列表内（≠ 本机缺运行时）：检测与版本探针都无从下手，必须点明是「语言标注」问题，
    // 否则用户会误以为是环境没装好而反复折腾。{0} = 脚本声明的 lang 原值。
    public const string RuntimePlaceholderUnsupportedLang = "脚本语言「{0}」不受支持，无法校验可执行文件；请检查脚本索引里的 lang 取值";
    #endregion

    #region 可执行文件路径输入框 ToolTip
    public const string ExePathBoxToolTip = "点击输入框选择可执行文件";
    #endregion

    #region 脚本读取/渲染提示
    public const string ScriptFileMissingFormat = "(脚本文件不存在: {0})";
    public const string ScriptReadFailFormat = "(读取脚本失败: {0})";
    #endregion

    #region 对话框
    public const string DlgPickRuntimeTitleFormat = "选择 {0} 的可执行文件（exe）";
    public const string DlgPickRuntimeFilter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
    public const string DlgPickFileTitle = "请选择{0}";
    public const string DlgPickFileFilter = "所有文件 (*.*)|*.*";
    public const string DlgPickFolderTitle = "请选择{0}";
    public const string DlgExportDirTitle = "选择导出目录";
    public const string DlgExportScriptDonePrefix = "导出的脚本已保存到：";
    public const string DlgOpenScriptFileTitle = "选择脚本索引文件（index.json）";
    #endregion

    #region 执行日志（输出到日志面板与 log/ 文件，用户可见）
    public const string LogRuntimeUnresolvedFormat = "✗ 无法解析运行时：语言 [{0}] 未检测到有效的可执行文件（请点击顶部右侧输入框选择 exe）";
    // 必填参数未填写（{0} = 未填写的参数名列表，以「、」分隔）
    public const string LogRequiredMissingFormat = "✗ 以下必填参数未填写：{0}";
    public const string StatusRequiredMissingFormat = "必填参数未填写：{0}";
    public const string LogProcessStartFormat = "── 开始执行 ──";
    public const string LogProcessStartAdminFormat = "── 开始执行（管理员权限）──";
    public const string LogProcessExitFormat = "── 结束执行（退出码 {0}）──";
    public const string LogExecExceptionFormat = "✗ 执行异常：{0}";
    // 执行超时（{0} = 超时秒数）：自动终止进程树
    public const string LogTimeoutFormat = "✗ 执行超时（{0} 秒），已自动终止进程";
    public const string LogElevatedFailFormat = "✗ 提权执行失败：{0}";
    // 执行成功后按参数声明自动打开输出位置（{0} = 被打开的目录或文件路径）
    public const string LogOpenedOutputPathFormat = "▸ 已打开输出位置：{0}";
    #endregion
}
