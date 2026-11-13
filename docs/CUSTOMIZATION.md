# 外观与语言定制

主题（颜色、字体、圆角）和语言包都是只含数据的外部文件，不会执行代码；解析失败只跳过该文件。

## 主题与 DIY 接口（v1）

内置主题为 **WHITE**（`white`）、**BLACK**（`black`，默认，与托盘 deck 一致）、**PURPLE-BLACK**（`purple-black`）；三套色板与 Rust deck 一致（白色的强调色略深，以满足 4.5:1 对比度）。三套主题采用同一套语义色板；紫黑是用户指定的配色。选择只影响界面，后台温控参数和硬件操作不读取主题。

入口是主界面“设置 → 外观”：内置与外部主题以色卡缩略图显示，点击即时生效，并写入 `HKCU\Software\DellFans\Ui\Theme`（`white`、`black`、`purple-black` 或外部主题 id），托盘 deck 读取同一个值。“工具”里可打开皮肤文件夹；外部皮肤在界面启动时加载。卡片、仪表和对话框共用语义色与同一组圆角／阴影／间距 token；JSON 的 `cornerRadius` 与 `shadowStrength` 控制卡片圆角和阴影，其他布局／形状不能通过皮肤改变，也不能加载脚本样式。

### 添加主题

1. 复制项目 `themes/` 中任意一份 JSON 模板到 `%LOCALAPPDATA%\DellFans\Themes\`，没有该目录时手动创建。
2. 修改文件名、`id` 和 `name`。例如文件 `my-white.json` 必须包含 `"id": "my-white"`。
3. 修改需要的颜色；没有填写的颜色从 `base` 指定的内置主题继承。保存为 UTF-8，可带 BOM。
4. 在界面重新载入主题后选择，或重新打开界面。修改文件后也需要重新载入。

`themes/diy-white.json`、`themes/diy-black-gray.json`、`themes/diy-purple-black.json` 是完整示例。模板不会自动安装，也不会覆盖内置主题。

最小示例：

```json
{
  "schemaVersion": 1,
  "id": "my-white",
  "name": "我的白色",
  "base": "white",
  "colors": { "accent": "#075985" }
}
```

### v1 文件约定

| 字段 | 规则 |
| --- | --- |
| `schemaVersion` | 必填整数 `1`；新版本需要显式升级 |
| `id` | 必填；1–40 位小写英文字母、数字或连字符，首位为字母；与文件名完全一致 |
| `name` | 必填；1–48 个可见字符，不含控制字符或首尾空格 |
| `base` | 必填；`white`、`black`、`purple-black` 之一；自定义主题不能互相继承 |
| `colors` | 可选对象；只允许下表中的颜色；每项必须为不透明 `#RRGGBB`，不接受颜色名称、透明度或路径 |
| `fontFamily` | 可选；`Segoe UI`、`Microsoft YaHei UI`、`Microsoft YaHei`、`Arial`、`Tahoma` 之一；不加载字体文件，缺少字体时使用 Windows 字体回退 |
| `cornerRadius` | 可选整数 `0`–`28`，默认 `20`；卡片圆角（逻辑像素） |
| `shadowStrength` | 可选整数 `0`–`100`，默认由 `base` 决定；卡片软阴影的浓度 |

未知字段或颜色名称会使整个文件失效。主题没有脚本、CSS、图片、网络地址、程序集、命令、温控参数或插件入口。内置 id 保留，任何自定义文件都不能替换内置主题。

文件使用 .NET Framework 的 JSON 数据解析器；字段和值按上述规则严格验证。解析器对重复字段采用最后一个值，最终值仍必须通过全部验证。请使用示例中的标准双引号 JSON，每个字段只填写一次。

| 颜色 | 用途 |
| --- | --- |
| `background` | 窗口底色 |
| `surface` | 主面板、菜单、表格和页签内容底色 |
| `surfaceAlt` | 次级表面、悬停与禁用控件底色 |
| `ink` | 正文、数值和选中行文字 |
| `muted` | 次要说明与禁用控件文字 |
| `accent` | 主要操作、图表线条和选中标记 |
| `accentInk` | 主要操作按钮文字 |
| `edge` | 面板分隔与控件边界 |
| `warning` | 警告或错误文字；状态含义同时由文字表达 |
| `input` | 原生输入框底色 |
| `selection` | 表格选中行和菜单选中项底色 |

主题验证会检查 `ink`、`muted`、`warning`、`accent` 分别相对 `background`、`surface`、`surfaceAlt`、`input` 的对比度至少为 **4.5:1**；`accentInk`/`accent` 与 `ink`/`selection` 也必须达到 4.5:1。分隔线是装饰性的弱边界，不代替原生焦点与控件状态。

每个文件最多 16 KiB；每次载入最多检查顶层目录前 32 个 `.json` 文件，不遍历子目录，不接受主题文件或主题目录的符号链接、junction。超过数量的文件不保证顺序，请把主题数保持在限制内。无效文件会跳过并留下诊断，其他有效主题照常可用。当前主题消失或失效时回到 WHITE。重新载入不执行文件内容。

选择记录由界面保存在 `HKCU\Software\DellFans\Ui\Theme`。复制主题只提供资源，不会更改用户当前选择。主题列表在界面启动或显式重新载入时读取；后台服务与 CLI 不扫描主题目录。

### C# 接口

`UiTheme.Current` 返回不可变 `UiPalette`；`Available` 和 `Diagnostics` 是只读列表。色板公开 `Id`、`Name`、`IsCustom`、`FontFamily`、`CornerRadius` 及同名语义颜色属性（C# 使用 PascalCase）。

```csharp
string[] errors = UiTheme.ReloadCustomThemes();
bool found = UiTheme.Select(savedId); // 找不到时回到 WHITE，返回 false
UiTheme.Apply(form);
UiTheme.Changed += RefreshTheme;
```

`ThemesDirectory` 公开当前用户的资源目录；`ReloadCustomThemes()` 返回本次错误数组。资源适配器还可以显式调用 `ReloadCustomThemes(directory)` 载入一个本机绝对目录；不支持网络或设备路径，该入口用于独立资源目录与隔离测试。`TryParse(json, out palette, out error)` 只验证资源，不修改主题列表或当前选择。`Changed` 仅在色板变化时触发，调用者在界面线程更新自绘部分、调用 `Apply` 并持久化选择，释放窗口时解除订阅。

`Apply(Control)` 递归更新原生表格、输入框、按钮、页签与 ToolStrip，不修改文本、数字输入、选中页签或监测值。原生页签由 `TextRenderer` 绘制文字，保留键盘焦点；菜单使用平面色板；禁用按钮保留次要文字。不会通过锯齿 Region 裁切来改变原生轮廓。

代码可在控件的 `Tag` 指定 `theme:background`、`theme:surface`、`theme:surface-alt`、`theme:muted`、`theme:accent`、`theme:warning`、`theme:primary`。这些标记由程序源码提供，不是 JSON 的可执行布局接口。首轮应用会记住已有表面和文字语义；动态警告、图表与其他自绘内容由所属窗口使用 `Current` 的颜色更新。

字体仅在字体族变化时替换，并保留控件字号和字重。主题系统只释放自己创建的字体；不会释放继承的系统字体或调用者的字体。

### 验证范围

v1 中 `cornerRadius` 当前只影响展开浮窗。主面板卡片、按钮和窗口外形有各自固定的源码参数，不能把该 JSON 字段当作全局布局或圆角开关；其他形态可通过公开圆角控件的 `Radius` / `FillRole` 在源码中 DIY。

运行 `tests/run-themes.ps1`。测试覆盖内置选择与回退、颜色对比、公开 JSON 验证边界、三份模板、原生控件递归换色和输入保留；还会在隔离临时目录检查 UTF-8/BOM、大小与文件数量上限、无效文件诊断、选中主题的重新载入和删除回退。测试不显示窗口，不连接传感器或服务，不读写实际用户主题目录。符号链接与权限错误路径由代码审查覆盖，未执行实际文件系统权限或链接操作；真实桌面视觉效果仍须单独检查，不能把离线断言当作硬件或视觉测量。

## 内置语言与外部翻译

紧凑面板 A 使用 WinForms 与 UiText 文案表，不增加第三方依赖。无已保存语言偏好时，正常启动使用进程系统 UI 语言（CultureInfo.CurrentUICulture）。“设置 → 语言”可选择简中、繁中、英文、有效扩展语言或跟随系统；选择存入 `HKCU\Software\DellFans\Ui\Language`（`zh-CN` / `zh-TW` / `en` 或扩展语言的区域名），与 Rust 托盘共用，不写入服务配置。

语言在新建窗口前确定。修改后关闭主窗口（窗口关闭即退出进程）再从托盘重新打开即可生效；后台控速程序持续运行。`--language` 仍可临时覆盖偏好，用于调试，不更改已保存值。

### 语言边界

| 系统 UI 语言或调试标签 | 界面语言 |
|---|---|
| zh、zh-CN、zh-SG、zh-Hans、zh-CHS | 简体中文 |
| zh-TW、zh-HK、zh-MO、zh-Hant、zh-CHT | 繁体中文 |
| 带显式 Hans/Hant 的地区标签 | 以文字体系为准，如 zh-Hans-HK 为简中、zh-Hant-CN 为繁中 |
| en 及各地区英文；其他系统语言 | 英文 |
| 空值或不支持/损坏标签，例如 zhfoo、zh-bogus | 英文 |

匹配不区分大小写。显式文字体系后可带一个标准形状的地区（两位英文字母或三位数字）；无显式文字体系时识别上表列出的中文地区。没有按 zh 字符前缀猜测语言，也没有逐字繁简转换。

### 公开语言接口

- 没有匹配外部翻译时，Resolve 返回 zh-Hans、zh-Hant 或 en；Select 用同一规则选择本进程界面语言。有匹配翻译文件时返回该文件的规范 culture。它们不改服务、硬件或持久配置。
- T 用完整简中原文查表；F 在同一查表结果上替换参数。数值格式采用 CurrentCulture，界面语言与数字地区格式独立。技术路径、设备名和厂商传感器名没有查表项时保留原样，不做全局替换。
- Apply 对简中原文控件递归翻译一次 Form、Panel、Label、Button 等标题。已通过 T/F 赋值的标题不应再 Apply：任意语言的翻译结果可能恰好等于另一个消息 ID，不能可靠反向识别。TextBoxBase（包括 RichTextBox）和 UpDownBase（包括 NumericUpDown）的编辑文本不参与翻译，数值和正在输入的值保留。表格列头、工具提示、动态状态及对话框应在赋值时调用 T/F。
- Error 取首个非空诊断行，翻译已知错误及已知服务错误前缀。未知内部错误显示本地化概述“操作未完成，请导出诊断数据查看详情。”；调用方应保留原始诊断供导出。Error 是显示接口，不承诺翻译操作系统、WMI、固件或厂商自带文本的每一种内容。

切换语言用于创建窗口之前；没有实现窗口运行中的即时重新翻译。该取舍避免为已经翻译的控件文字反向猜原始文案。

### 文案范围

紧凑主窗口的常见标题、温度/风扇卡片、传感器指标、最低转速编辑、保存/撤销、探测说明、恢复 BIOS 确认、能力检测与“当前电脑不支持”对话框、保存失败、缺读数、断连/过期、服务初始化/故障、资源占用和资源保护状态都有英文与繁中映射。

对已知中文前端及服务消息使用逐条审阅的英文与繁中映射，不依据字符替换推断术语。原始设备/传感器名称和导出的技术诊断可以保留原语言。文字宽度及实际系统对话框的视觉效果需要在目标语言下人工检查。

### 添加自己的语言

界面启动时从 `%LOCALAPPDATA%\DellFans\Languages` 读取顶层 `*.json` 文件。先按完整 culture 匹配，再按主语言匹配：`fr-CA` 优先使用 `fr-CA` 文件，再尝试 `fr`；`fr-FR` 不会使用仅有的 `fr-CA` 文件。匹配不区分大小写。没有有效匹配时，继续使用上文原有的简中、繁中与英文规则。外部文件中缺少的已知消息使用内置英文；设备名、路径等未知消息保持原样。内置语言的文件也只有匹配 culture 时生效，例如自定义 `en` 不会影响德文系统的内置英文回退。

1. 复制 [英文原文模板](../translations/en.template.json)，改名为自己的 culture，例如 `fr.json`。
2. 修改 `culture` 和 `displayName`，仅翻译 `translations` 中冒号右边的英文文本。左边的简中消息 ID 必须保持不变；这是稳定的查表键。可删除尚未翻译的条目，保留至少一个条目。
3. 把文件保存到上述语言目录，然后重新打开界面。语言文件在启动时读取；运行中的“重新加载皮肤”只更新外观。调试时也可在创建窗口前使用 `--language fr-CA` 验证匹配结果。

最小文件如下；显示名称用于资源元数据，当前没有额外的语言选择菜单：

```json
{
  "schemaVersion": 1,
  "culture": "fr",
  "displayName": "Français",
  "translations": {
    "保存": "Enregistrer",
    "取消": "Annuler",
    "更新 {0}": "Mis à jour {0}"
  }
}
```

文件是纯文本数据，不能包含脚本、HTML、远程资源或其他配置字段。根对象仅允许上例四个字段。culture 必须是 .NET Framework 识别的有效名称，并符合两至三位主语言、至多三个二至八位字母数字子标签的形状；不能包含路径。显示名称最多 80 字符且不能换行；译文最多 4096 字符，可使用换行和制表符，不能包含其他控制字符或 `<`、`>` 标记。未知消息 ID、非字符串译文和不支持的 schemaVersion 会使整个文件失效，其他有效文件仍能加载。

`{0}`、`{1:0.0}` 等参数可以调整顺序，但索引、出现次数、对齐和格式必须与英文原文一致。不要翻译格式符。损坏的大括号、增加/删除参数、把数字格式改成 `{0:Q}` 等内容会被拒绝。`F` 仍按系统 CurrentCulture 格式化数字；对外部译文的格式异常会尝试使用内置英文格式。调用方自身传错参数不属于翻译资源的可修复范围。

每个文件最多 **64 KiB**，每个目录最多 **32** 个 JSON 文件。超过数量上限时全部使用内置资源；同一 culture 出现多份文件时，这个 culture 的全部文件均忽略。加载不递归进入子目录，拒绝 UNC 目录、作为重解析点的语言目录及文件。读取失败或内容无效只形成诊断并回退，不会写入后台配置、访问硬件或执行翻译内容。界面加载失败提示使用内置文字，具体原因可从下列诊断接口读取。

公开资源接口均位于 `UiText`：

- `PacksDirectory` 返回固定的当前用户语言目录；`InitializePacks()` 读取该目录，不创建或写入文件。带 `directory` 的重载供离线测试或明确指定的本地扩展目录使用。
- `InitializePacks` 返回本次读取的 `string[]` 诊断；`PackDiagnostics` 返回诊断的副本。调用初始化会重新解析上一次请求的 culture，正常产品调用只在创建控件前进行。
- `ValidatePack(string json)` 校验内存中的 JSON，返回错误数组；空数组表示有效，不改变当前语言。
- `ExportTemplate(string culture, string displayName)` 返回带所有已知消息和英文原文的可编辑 JSON。无效的 culture 或显示名称抛出 ArgumentException，不生成文件。模板可以由调用方按 UTF-8 保存；仓库模板由此接口生成。

### 离线测试

`./tests/run-localization.ps1` 与 `./tests/run-external-language.ps1` 只编译本地语言资源与测试，使用临时语言目录、公开 `UiText` 接口及离线控件，不读取真实用户语言目录，不启动产品 GUI、CLI、服务或硬件程序。覆盖：culture 匹配与英文回退、格式参数保留、模板导出/验证、内置回退不借用未匹配文件、完整 culture 优先、输入文本/数值保留、无效内容、文件大小与数量上限、重复 culture、顶层文件边界及 UNC 拒绝。`-TemplatePath ./translations/en.template.json` 会在测试通过后用公开导出接口生成英文模板。

此处没有验证所有语言的翻译质量或实际长文字布局；外部资源的文字宽度仍需要翻译者在目标语言下检查。
