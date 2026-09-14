# 更新日志（Changelog）

## [Unreleased]

### 新增功能
- **脚本跑完自动打开输出目录**：`params[]` 新增可选字段 `open_after_run`。标了它的参数，在脚本**执行成功（退出码 0）**后会被自动在资源管理器中打开
  ——目录参数直接打开该目录，文件参数打开所在目录并选中该文件；参数留空或路径尚未产出则静默跳过。
  失败 / 超时 / 被停止时不打开（此时多半没有产出，弹窗只会干扰）。已应用到 `爬虫 ▸ 中国行政区划` 的「导出目录」参数。
  字段说明同步进 `script/README.md` 的 `params` 表与 `script/.skills/SKILL.md`。

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
- 脚本执行日志目录改为遵循 `config.ini` 的 `log_dir`（此前写死为 exe 同级 `log`，该配置项对执行日志不生效、仅 `error.log` 生效）。
- 「文件▸打开」切换脚本索引后，导出改为打包当前索引所在目录（此前索引路径与脚本目录被 `ConfigLoader` 的静态字段在类型初始化时冻结，导出仍指向启动时的旧目录）。
- 脚本预览的语法高亮改为按当前脚本 `lang` 映射：PowerShell / PowerShell 7 / Python / Node / Java 各自正确高亮，bat / bash / go / rust 等未收录语言改用无高亮，避免此前一律按 PowerShell 标错色（视觉误导）。

### 界面与交互
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
