# 更新日志（Changelog）

## [Unreleased]

### 新增功能
- **配置编辑器分两个分区 + AI「最大追问轮次」**：设置 ▸ 编辑配置拆为「工作路径」（脚本索引 / 各目录选择 / 默认执行超时）与「模型配置」（API 密钥 / 地址 / 模型 / 最大追问轮次）两个带标题的分区。新增 `[ai] max_rounds`：一次编辑会话中、**首轮生成之外**允许的追问轮数，默认 0 = 不追问（仅首轮生成）；N = 原始会话 + N 轮追问。达到上限后「生成」按钮禁用、状态栏提示收尾，生成失败不计入轮数，「接受并写入」后轮次归零重新起算（保存即生效，无需重启）。

### 脚本树右键
- **脚本树右键「重命名」**（目录 / 脚本通用）：InputDialog 预填当前名（全选，直接输入即覆盖）；仅改显示名 `name`，不移动 / 改名物理文件；**按条目 id（GUID）定位，允许脚本同级同名**（目录名仍需同级唯一，重名会拒绝并提示）。
- **条目唯一 id（GUID）体系**：索引每个条目（含目录）都有 `id`；旧索引缺少时由 `ScriptIndexStore` 在读写时自动补齐写回（零手工迁移）。右键的创建 / 重命名 / 编辑 / 删除全部改为按 id 定位，彻底摆脱「树路径 + 名字匹配」在同名场景下的歧义。

### 索引与存储（重构）
- **脚本物理文件平铺化**：所有脚本文件一律以「`{id}.{扩展名}`」命名、平铺在 script 目录下（与 index.json 同级），不再分子目录；索引只保留层级语义（children），重命名 / 拖拽移动层级等后续操作只改 JSON、不动物理文件。存量 34 个脚本已迁移改名、13 个空子目录已清除（原目录与索引备份于 `D:\.Backup\knife-script-manager-flat-20260915-154514`）。
- AI 生成脚本落盘到 script 根目录（`{id}.{ext}`），`ai-generated/` 隔离目录随之取消；扩展名按 `lang` 映射（py/js/cmd/ps1/sh/java/go/rs），无映射时回退 AI 建议文件名的扩展名。
- **编辑模式切换语言时，物理文件扩展名跟随变更**（`{id}.{旧ext}` → `{id}.{新ext}`），并同步更新条目 `path`。
- AI 多轮对话历史持久化到缓存：每个脚本一个以脚本 id 命名的文件夹（`cache/{脚本id}/`），每次编辑会话一个文件（会话内每轮成功后覆盖更新）；删除脚本不清除对应缓存。

### 界面与交互
- **脚本树右键菜单**：在目录树区域按节点类型动态弹出右键菜单，删除操作均带二次确认（MessageBox，默认「否」）：
  - **面板空白处**右键：`创建目录`、`创建脚本（AI）`——在根层级新建；
  - **目录节点**右键：`创建目录`、`创建脚本（AI）`（lucide `bot` 图标）、`删除目录`——创建发生在所点目录之下；
  - **脚本节点**右键：`编辑脚本`、`删除脚本`。
  - `创建目录` 用新的通用 `InputDialog` 输入名称；同级重名会拒绝并提示。
  - `创建脚本（AI）` 打开 AI 编辑器（创建模式）：脚本文件仍落 `script/ai-generated/`，索引条目插入所点目录节点下，写入后自动刷新树。
  - `编辑脚本` 打开 AI 编辑器（编辑模式）：载入现有脚本内容与参数作为种子，AI 按描述的「修改要求」改写；接受后**覆盖原脚本文件**并更新索引条目（`path` 不变、其余字段保留）。
  - `删除目录` 仅从索引移除该目录及其子树条目，**不删除任何脚本文件**（确认框中已说明）；`删除脚本` 同时删除索引条目与脚本文件（以索引记录的相对路径为准，并限定在 script 目录内防路径穿越）。
  - 同步移除顶部「设置 ▸ AI 生成脚本…」菜单项（功能由树右键的「创建脚本（AI）」承接）；新增 `src/ScriptIndexStore.cs`（唯一索引按树路径的增删改查）、`src/Views/InputDialog.xaml(.cs)` 与图标 `bot.svg` / `folder-plus.svg` / `pencil.svg`。

- **脚本索引归集为唯一 `script/index.json`（配合右键菜单的前置重构）**：原先根索引以 `include` 分片到 windows/hyper/toolkit/crawler/runtime/demo/test/ai-generated 等 13 个子索引文件（含嵌套），右键增删改需精确定位条目所在文件、且后续拖拽归类难以实现——现全部**递归归集进顶层唯一 `script/index.json`**（嵌套 `children` 结构不变）：
  - 归集时把各子索引条目的 `path` **重写为相对 script 根目录**（如 `./show-ip.ps1` → `./windows/network/show-ip.ps1`），脚本物理文件一律不动；
  - 迁移脚本先校验后执行：脚本条目数前后一致（34 = 34）、每个重写后的 `path` 真实存在、无残留 `include`；原根索引与 13 个子索引备份至 `D:\.Backup\knife-script-manager-index-merge\` 后删除；
  - `ConfigLoader` 的 include 解析逻辑保留（向后兼容旧外部索引），程序内此后只维护这一个索引文件。

- **脚本跑完自动打开输出目录**：`params[]` 新增可选字段 `open_after_run`。标了它的参数，在脚本**执行成功（退出码 0）**后会被自动在资源管理器中打开
  ——目录参数直接打开该目录，文件参数打开所在目录并选中该文件；参数留空或路径尚未产出则静默跳过。
  失败 / 超时 / 被停止时不打开（此时多半没有产出，弹窗只会干扰）。已应用到 `爬虫 ▸ 中国行政区划` 的「导出目录」参数。
  字段说明同步进 `script/README.md` 的 `params` 表与 `script/.skills/SKILL.md`。

- **AI 脚本编辑器（脚本树右键 ▸ 创建脚本（AI）/ 编辑脚本）**：把「新增/编辑脚本」交给 AI 完成——用户只需在一个描述框里用自然语言写清功能，并可在同一段话里一并说明需要的参数与语言（语言覆盖 cmd/powershell/pwsh/bash/java/node/python/go/rust 共 9 种，由 AI 自行解析），
  AI 基于 `script/.skills/SKILL.md` 的编写规范生成结构化脚本，界面先预览「脚本正文」与「将写入 index.json 的条目」，用户点「接受并写入」后才落盘，避免误写。
  - 生成的脚本文件统一隔离到 `script/ai-generated/`，索引条目经 `ScriptIndexStore` 写入唯一 `script/index.json` 的目标目录节点下；删除在树右键操作。
  - AI API 在「设置 ▸ 编辑配置」的 `[ai]` 节配置：`api_key`（密钥框，可勾选「显示」明文）/ `base_url`（默认 OpenAI 官方，兼容 DeepSeek/通义/Ollama 等 OpenAI 协议）/ `model`（默认 `gpt-4o-mini`）；留空即视为未配置，生成按钮禁用并提示先配置。
  - 输入精简：去掉原先独立的「语言」下拉与「参数说明」输入框，二者并入描述框，并以灰字占位提示（含参数/语言示例）引导用户一次性写清需求。
  - 新增 `src/Ai/AiClient.cs`（OpenAI 兼容聊天补全客户端）与 `src/Ai/ScriptGenerator.cs`（拼装 system prompt、解析结构化 JSON、落盘脚本文件并构建索引条目）。

### 构建（首次构建提速）
- 发布命令显式把 `RuntimeIdentifiers` 收窄为**单个目标架构**（`-p:RuntimeIdentifiers=$Runtime`）。
  根因：csproj 声明的是 `<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>`，而**只要该属性含多个值，
  RID 特定还原就会把两个架构的运行时包全部拉下来**——实测 7 个包 / 641.9 MB，其中 ARM64 那套 337.6 MB
  在 x64 机器上完全用不到。收窄后只剩 3 个包 / 304.3 MB，**首次构建的下载量减少约 52%**。
- 该现象**与 `--self-contained` 无关**：标准版（依赖框架）本不需要任何运行时包，此前同样会把 641.9 MB 下完，
  故便携版与标准版两处发布都收窄。纯构建期属性，产物与产物结构不变。

### 新增脚本
- **`爬虫 ▸ 中国行政区划`**（Python，`script/crawler/fetch_xzqh.py`）：从民政部·国家地名信息库抓取全国行政区划并导出，四个参数——
  - **导出目录**（必选，文件夹选择框）：结果文件写入该目录；
  - **省市县区代码**（可选）：留空导出全部，填写则只导出该区划及其下级；并据此裁剪乡级下钻请求（导出单个省由 451 次降到十余次）；
  - **导出级别**（下拉）：`省市县区` 或 `乡镇街道`（后者 = 省市县区 + 乡镇街道）；
  - **导出类型**（下拉，默认 `csv`）：`csv` / `txt` / `json` / `sql`（`sql` 输出通用建表 + 列注释 + 索引 + 分批 INSERT，默认按 **MySQL** 可直接执行）。
- 结果一律**按编码正序排序**；`乡镇街道` 时文件名带 `_l4` 后缀（如 `china_xzqh_l4.json`）；接口响应缓存默认放 `%LOCALAPPDATA%\knife-script-manager\cache\xzqh`（7 天过期，不写进导出目录）。
- 脚本索引新增顶层分类 **`爬虫`**（`script/crawler/index.json`），根 `script/index.json` 以 `include` 汇聚。
- **SQL 导出改为通用语法、默认兼容 MySQL**：主体为标准 SQL（`CREATE TABLE` / `INSERT INTO`），按 **MySQL 5.7+ / 8.0 / 8.4 可直接执行**落地——
  列注释改用内联 `COMMENT '...'`（原为 `COMMENT ON COLUMN` 独立语句）、布尔改用 `TINYINT(1)` + `1/0`（原为 `BOOLEAN` + `TRUE`/`FALSE`）、
  日期字面量去掉 `DATE '...'` 前缀、建表尾部补 `ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci` 与表注释、
  文件开头补 `SET NAMES utf8mb4;` 防中文按 latin1 写入乱码；产物文件名由 `china_xzqh[_l4]_gaussdb.sql` 改为 `china_xzqh[_l4].sql`。
  文件末尾保留「其他数据库适配」注释，说明换库时删哪几处（GaussDB 的 `DISTRIBUTE BY HASH(code)` 也记在其中）。
  已在 **MySQL 8.0.46 与 8.4.11** 真库导入验证：全量 42,041 行导入零错误零警告、中文无乱码、16 列注释与 7 个索引均生效、重复导入幂等。

### 缺陷修复
- **配置编辑弹窗 ▸ AI「API 密钥」三个连环问题**（同一根因：PasswordBox / 明文 TextBox 的可见性转换器绑反）：
  - 未勾选「显示」时可见的其实是明文 TextBox（PasswordBox 被隐藏），密钥以明文输入与展示；
  - 明文框的输入事件被「仅勾选态记录」守卫拦下，密钥从未进入待保存状态 → 点「保存」存的是空值，表现为「填了存不上」；
  - 点「显示」后把空的 PasswordBox 内容同步回 TextBox，表现为「一点显示输入框就被清空」。
  - 修复：两个可见性转换器对调回正确绑定（未勾选=PasswordBox 打码、勾选=明文 TextBox）；两个输入事件改为无条件记录明文，杜绝状态不同步。
- AI「API 地址」占位符补充示例与提示（`https://api.openai.com/v1 或 https://openrouter.ai/api/v1（填到 /v1 即可）`）；客户端对粘贴完整端点（以 `/chat/completions` 结尾）自动剥离后缀再拼接，避免拼出重复路径。
- 脚本执行日志目录改为遵循 `config.ini` 的 `log_dir`（此前写死为 exe 同级 `log`，该配置项对执行日志不生效、仅 `error.log` 生效）。
- 「文件▸打开」切换脚本索引后，导出改为打包当前索引所在目录（此前索引路径与脚本目录被 `ConfigLoader` 的静态字段在类型初始化时冻结，导出仍指向启动时的旧目录）。
- 脚本预览的语法高亮改为按当前脚本 `lang` 映射：PowerShell / PowerShell 7 / Python / Node / Java 各自正确高亮，bat / bash / go / rust 等未收录语言改用无高亮，避免此前一律按 PowerShell 标错色（视觉误导）。

### 界面与交互
- **AI 编辑器移除 JSON 条目预览**：条目由程序按 id 自动维护，非用户关心内容；生成中预览区显示模型实时回复、完成后只显示脚本正文。
- **AI 生成改为流式输出**：生成过程中模型回复逐段实时刷进「脚本预览」区（含自动滚动），状态栏显示已接收字数；完成后自动解析为脚本正文。`AiClient` 新增 `ChatStreamAsync`（SSE 逐段解析、`ResponseHeadersRead` 边读边回调；兼容思考型模型的 `reasoning_content` 等非正文增量，忽略之），`ScriptGenerator.GenerateAsync` 增加可选流式回调。
- **AI 生成支持多轮对话**：首轮生成成功后对话上下文保留（新增 `AiConversation`，历史含此前各轮 user/assistant 原文），描述框清空、占位符切换为追问提示，再次点「生成」即把新修改要求连同历史发给 AI 迭代改写（追问自动附加「仍返回完整 JSON」提醒）；某轮失败不残留半截对话，下次从当前状态重试。「接受并写入」后也可继续追问微调。
- **预览区状态切换**：生成中「脚本预览」标题临时切换为「模型实时回复（生成中）」、JSON 预览隐藏（此时还没有可解析的条目）；生成完成自动恢复标题并显示解析后的脚本正文与索引条目，避免原始 JSON 与最终结果混淆。
- **输入框内容/光标垂直对齐修复**：`BaseTextBox` 模板的内容宿主此前硬编码垂直居中，控件上的 `VerticalContentAlignment="Top"` 完全无效——多行框（AI 描述框、脚本/索引预览框）光标与内容上下居中而占位符却顶左，观感割裂。现模板改为 `{TemplateBinding VerticalContentAlignment}`（默认仍居中，单行框视觉不变），多行框显式置顶。
- 配置编辑弹窗：
  - 未自定义的配置项一律显示为空，仅以占位符提示内置默认值（此前会把默认值直接填进输入框，看起来像用户改过）。
  - 与内置默认等价的显式配置（如 `lib_dir = lib`、`default_timeout = 0`）同样按「未自定义」处理，显示为空 + 占位符。
  - 「默认执行超时(秒)」移至「日志目录」下方（此前误落在底部操作按钮之下）。
  - 输入框右侧预留 × 清空按钮的位置，长路径不再压住 × 而看不见按钮。
- 统一内嵌 × 的输入框样式：新增 `ClearableTextBox` / `ClearableTextBoxInline`（右内边距 24px 让位给 ×），
  参数面板的文件/目录框、普通文本框与参数下拉框一并改用，修复这些地方长文本同样会压住 × 的问题。

### 配置（config.ini）
- `config.ini.example`：脚本索引与各项目录、超时默认全部注释掉（= 使用内置默认），需要自定义时取消注释填写即可。

## [1.1.0] - 2026-09-05

### 界面与交互
- 顶部新增工具栏：`文件`（打开 / 导出，从脚本列表移入并保留图标）、`设置 ▸ 编辑配置...`（调出 config.ini 编辑弹窗）。
- 配置编辑弹窗重构：
  - 目录/文件项改为只读浏览式选择框（只能浏览、不可手输）；未自定义时显示默认相对路径占位符（script\index.json / lib / runtime / cache / log），由 AppConfig 在读取时回落。
  - 每项右侧「×」可清除当前选择、恢复默认相对路径；「默认值」一键全部还原。
  - 加回「默认执行超时(秒)」可编辑数字框（弹窗内唯一允许手输项），占位符「0（不限制）」，仅允许数字输入。
  - 标题去掉「 - config.ini」后缀；移除原「默认：…」说明行。
- 参数下拉框：切换其他窗口不再置顶屏幕；点击输入框或选项之外任意位置即收起。

### 配置（config.ini）
- 脚本索引由双键（`default_script_file` + `user_script_file`）合并为单一 `script_index_file`：移除 `default_script_file`；原 `user_script_file` 重命名为 `script_index_file`（旧值自动迁移）。默认值仍为 `script\setminus.json`。
- 「文件▸打开」与「设置▸编辑配置▸脚本索引文件」现在写同一个键、效果完全一致；配置编辑器首行 label 改为「脚本索引文件」。

### 脚本运行时
- 运行时环境检测改为逐项流式输出（不再憋约 10 秒后一次性刷出）。
- 安装脚本（PS7 / Java / Node / Python / Go / Rust）标题对齐 index.json 名称；PS7 下载源尊重用户所选、失败如实提示原因与建议（不再回退 GitHub 源）。

## [1.0.0] - 2026-09-03
首个公开版本（以当前代码为基准重新起步，整合此前实验性 1.0.0 / 1.1.0 的全部能力）。

### 脚本管理
- 读取 exe 同级 `script/index.json` 管理多语言脚本（PowerShell / Bat / Python / Java / Node 等），按 `group` 分组展示，配套实时日志与报错面板。
- 脚本执行超时自动终止：超过设定时长仍未结束则强杀进程树（含子进程）。单脚本可在 `index.json` 设 `timeout`（秒）单独控制；全局 `config.ini` 的 `default_timeout` 兜底；两者皆未设或值为 0/负数时**不限制**（兼容既有长时脚本）。

### 目录树工具条
- 新增「打开」按钮（folder-open 图标）：直接选择脚本索引文件 `index.json` 加载任意脚本目录（结构同内置 `script` 目录）；文件不存在/解析为空时目录树不渲染，且不弹窗报错。
- 选中有效 `index.json` 后写 `config.ini` 的 `[script] user_script_file`（绝对路径），重启自动加载该文件（文件失效则回退 `default_script_file`），无需每次手动重新选择。
- 已移除「刷新」按钮（启动即按配置加载，无需手动刷新）。

### 配置（config.ini，[script] 节）
- `default_script_file`：默认脚本索引文件，默认 `script\index.json`。
- `user_script_file`：记忆「打开」选择的索引文件（绝对路径），优先于 `default_script_file`。
- `lib_dir` / `runtime_dir` / `cache_dir` / `log_dir`：第三方依赖、运行时安装、缓存、日志目录（相对路径相对 exe 目录，绝对/UNC 路径直接使用）。
- `default_timeout`：脚本默认执行超时（秒），0 表示不限制。

### 交付与开源合规
- 便携版（自包含 .NET）与标准版双交付：便携版内置运行时、开箱即用；标准版依赖本机已装 .NET 10。
- README 去除私人路径、补充「配置文件」章节；新增 `config.ini.example` 模板（缺配置时自动复制）、`THIRD-PARTY.md`（登记 Hutool Apache-2.0 许可证与来源）；新增 `.gitattributes`（Windows 脚本统一 CRLF）、补全 `.gitignore`。
