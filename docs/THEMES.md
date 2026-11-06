# 主题与 DIY 接口（v1）

内置主题为 **WHITE**（`white`）、**BLACK**（`black`，默认，与托盘 deck 一致）、**PURPLE-BLACK**（`purple-black`）；三套色板与 Rust deck 一致（白色的强调色略深，以满足 4.5:1 对比度）。三套主题采用同一套语义色板；紫黑是用户指定的配色。选择只影响界面，后台温控参数和硬件操作不读取主题。

入口是主界面“设置 → 外观”：内置与外部主题以色卡缩略图显示，点击即时生效，并写入 `HKCU\Software\DellFans\Ui\Theme`（`white`、`black`、`purple-black` 或外部主题 id），托盘 deck 读取同一个值。“工具”里可打开皮肤文件夹；外部皮肤在界面启动时加载。卡片、仪表和对话框共用语义色与同一组圆角／阴影／间距 token；JSON 的 `cornerRadius` 与 `shadowStrength` 控制卡片圆角和阴影，其他布局／形状不能通过皮肤改变，也不能加载脚本样式。

## 添加主题

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

## v1 文件约定

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

## C# 接口

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

## 验证范围

v1 中 `cornerRadius` 当前只影响展开浮窗。主面板卡片、按钮和窗口外形有各自固定的源码参数，不能把该 JSON 字段当作全局布局或圆角开关；其他形态可通过公开圆角控件的 `Radius` / `FillRole` 在源码中 DIY。

运行 `tests/run-themes.ps1`。测试覆盖内置选择与回退、颜色对比、公开 JSON 验证边界、三份模板、原生控件递归换色和输入保留；还会在隔离临时目录检查 UTF-8/BOM、大小与文件数量上限、无效文件诊断、选中主题的重新载入和删除回退。测试不显示窗口，不连接传感器或服务，不读写实际用户主题目录。符号链接与权限错误路径由代码审查覆盖，未执行实际文件系统权限或链接操作；真实桌面视觉效果仍须单独检查，不能把离线断言当作硬件或视觉测量。
