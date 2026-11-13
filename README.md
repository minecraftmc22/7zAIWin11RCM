# 7-Zip 右键菜单助手（7zAIWin11RCM）

把 7-Zip 的解压 / 压缩命令重新放回 **Windows 11 的右键菜单** —— 既支持新版（紧凑）菜单，也支持旧版（经典）菜单，一个开关搞定。

![设置页](docs/screenshot-settings.png)

> 界面基于 WinUI 3（Windows App SDK）+ Mica 材质，自动跟随系统浅色 / 深色主题。

---

## 功能

| 功能 | 说明 |
| --- | --- |
| 自动查找 7-Zip | 依次查注册表（`HKLM\SOFTWARE\7-Zip` 的 `Path64` / `Path`）、Program Files、PATH、便携版目录 |
| 手动指定 | 「浏览…」选择安装目录；也接受直接填入 `7zG.exe` 的路径 |
| 新版右键菜单 | 在 Windows 11 紧凑菜单里直接出现 7-Zip 条目（稀疏 MSIX + COM 扩展） |
| 旧版右键菜单 | 写入经典菜单（`显示更多选项` 或 `Shift + 右键`），**默认开启** |
| 保存 / 恢复默认 / 取消保存 | 「取消保存」= 放弃未保存的改动，界面回滚到上次保存的状态 |
| 状态回显 | 实时显示 7-Zip 版本、两项菜单的注册状态、未保存改动提示 |
| 关于页 | 作者、GitHub、博客、仓库地址（可一键复制 / 打开） |
| 自愈 | 程序被移动到别处后，启动时自动重写注册表里失效的命令行 |

## 系统要求

- **Windows 10 1809+ / Windows 11**
  - 新版右键菜单需要 Windows 11（内部版本 22000+）
- **7-Zip**：任意包含 `7zG.exe` 与 `7zFM.exe` 的版本（21.x 及以上均可）
- **运行时**：.NET 8 运行时（x64）
- **构建**：.NET 8 SDK；Windows App SDK 已随应用自包含，目标机无需另行安装

## 快速开始

```powershell
git clone https://github.com/Minecraftmc22/7zAIWin11RCM.git
cd 7zAIWin11RCM
.\build.ps1            # 默认 Release / win-x64
# 或： .\build.ps1 -Configuration Debug -Run
```

也可以用 Visual Studio 2022 直接打开 `7zAIWin11RCM.sln`。

构建产物：

```
src\7zAIWin11RCM\bin\Release\net8.0-windows10.0.19041.0\win-x64\SevenZipAIWin11RCM.exe
```

该目录是自包含的（含 `shell\`、`scripts\`、`packaging\`），整体复制即可运行。

### 使用步骤

1. 打开程序，确认「7-Zip 安装位置」已被自动识别（绿色徽标显示版本号）；
2. 勾选需要的菜单项；
3. 点「保存」。

菜单项（目录 / 文件 / 驱动器 / 文件夹空白处）：

- 解压到当前文件夹
- 解压到新文件夹
- 添加到压缩包（生成 `名称.7z`）
- 用 7-Zip 打开

## 两种菜单有什么区别

| | 旧版（经典）菜单 | 新版（紧凑）菜单 |
| --- | --- | --- |
| 出现位置 | `显示更多选项` / `Shift + 右键` | 一级右键菜单 |
| 实现方式 | `HKCU\Software\Classes\...\shell` 注册表 verb | 稀疏 MSIX 包 + `IExplorerCommand` COM 扩展 |
| 需要管理员 | 否 | 是（安装证书 + 注册应用包） |
| 需要额外组件 | 无 | Windows SDK 的 `MakeAppx.exe` / `signtool.exe` |
| 生效时间 | 立即 | 需重启「Windows 资源管理器」 |
| 适用系统 | Windows 10 / 11 | Windows 11 22000+ |

> Windows 11 的紧凑菜单**只接受带「应用包标识」的 `IExplorerCommand` 扩展**，普通注册表项无法进入该菜单 —— 这就是新版菜单需要单独安装扩展包的原因。

## 新版菜单是怎么实现的

```
7zAIWin11RCM.exe            WinUI 3 主程序（设置界面 / 旧版菜单注册 / 调用安装脚本）
 └─ shell\
     SevenZipAIWin11RCM.ShellExt.comhost.dll   .NET COM 宿主，内含 3 个 IExplorerCommand 实现
     SevenZipAIWin11RCM.ShellExt.dll
 └─ packaging\
     AppxManifest.xml       稀疏包清单（uap10:AllowExternalContent + ComServer + FileExplorerContextMenus）
     Assets\*.png
 └─ scripts\
     install-modern-menu.ps1 / uninstall-modern-menu.ps1
```

勾选「新版右键菜单」并保存时，主程序以管理员权限调用 `install-modern-menu.ps1`：

1. 用 `MakeAppx.exe` 把 `AppxManifest.xml` 打成稀疏 MSIX；
2. 创建自签名证书（`CN=7zAIWin11RCM`）并加入「受信任人」存储，用 `signtool.exe` 签名；
3. `Add-AppxPackage -Path ... -ExternalLocation <应用目录>` 注册，外部内容指向应用自身目录；
4. 资源管理器随后即可通过包内声明的 CLSID 加载 COM 扩展，弹出菜单命令。

**排错**：点左下角「查看日志」，或查看
`%LOCALAPPDATA%\7zAIWin11RCM\app.log`；脚本的完整输出也会写在临时目录的 `setup-*.log` 里并在界面提示中回显。

如果系统没有 Windows SDK 打包工具，脚本会直接返回「系统缺少打包工具」，此时只使用旧版菜单即可，功能完全不受影响。

## 项目结构

```
.
├─ 7zAIWin11RCM.sln
├─ build.ps1                     一键构建（先扩展、后主程序）
├─ src
│  ├─ 7zAIWin11RCM               WinUI 3 主程序
│  │  ├─ Program.cs              自定义入口（右侧菜单动词走无窗口快速路径）
│  │  ├─ MainWindow.xaml         导航外壳（Mica / 自定义标题栏）
│  │  ├─ Views\SettingsPage      设置页（路径 / 两个勾选框 / 保存·恢复默认·取消保存）
│  │  ├─ Views\AboutPage         关于页（作者 / 仓库 / 运行环境）
│  │  └─ Services
│  │     ├─ SevenZipLocator.cs   7-Zip 自动查找与校验
│  │     ├─ LegacyMenuService.cs 旧版菜单注册表读写
│  │     ├─ ModernMenuService.cs 新版菜单扩展安装/卸载（调用 PowerShell）
│  │     ├─ ShellVerbHandler.cs  右键菜单实际执行的命令（解压 / 压缩 / 打开）
│  │     └─ SettingsStore.cs     %LOCALAPPDATA% 下的设置与日志
│  └─ SevenZipAIWin11RCM.ShellExt COM 宿主扩展（IExplorerCommand × 3）
├─ packaging                    稀疏 MSIX 清单与图标
├─ scripts                      新版菜单安装 / 卸载脚本
├─ tools\make_assets.py         生成图标与 Logo（纯 Python）
└─ docs                         截图
```

菜单命令并不直接拼注册表命令串，而是回调主程序：

```
"…\SevenZipAIWin11RCM.exe" --verb extract-here "%1"
```

路径计算、目标目录、压缩包命名都在 C# 里完成，避免注册表里 `%1` 拼接的坑；这类调用不会初始化 XAML，秒开秒退。

## 常见问题

**勾了新版菜单但菜单里没有？**
重启「Windows 资源管理器」（任务管理器 → 右键 → 重新启动），再确认包已安装：

```powershell
Get-AppxPackage -Name 7zAIWin11RCM.ShellExt
```

**提示安装失败 / 需要 Windows SDK？**
安装 [Windows SDK](https://developer.microsoft.com/windows/downloads/windows-sdk/)（含 MakeAppx 与 SignTool），或只使用旧版菜单。

**如何彻底清理？**

```powershell
# 旧版菜单
Remove-Item -Recurse "HKCU:\Software\Classes\*\shell\7zAIWin11RCM"
Remove-Item -Recurse "HKCU:\Software\Classes\Directory\shell\7zAIWin11RCM"
Remove-Item -Recurse "HKCU:\Software\Classes\Directory\Background\shell\7zAIWin11RCM"
Remove-Item -Recurse "HKCU:\Software\Classes\Drive\shell\7zAIWin11RCM"
Remove-Item -Recurse "HKCU:\Software\Classes\Folder\shell\7zAIWin11RCM"

# 新版菜单
Get-AppxPackage -Name 7zAIWin11RCM.ShellExt | Remove-AppxPackage

# 设置与日志
Remove-Item -Recurse "$env:LOCALAPPDATA\7zAIWin11RCM"
```

**程序目录被移动过，右键菜单点了没反应？**
重新打开程序并保存一次；程序启动时也会自动检测并修复失效的命令行。

## 作者

- 作者：**Minecraftmc22**
- GitHub：<https://github.com/Minecraftmc22>
- 博客：<https://minecraftmc22.top>
- 仓库：<https://github.com/Minecraftmc22/7zAIWin11RCM>

## 许可

[MIT License](LICENSE) © 2026 Minecraftmc22

本项目与 7-Zip 官方无关，仅调用其 `7zG.exe` / `7zFM.exe` 命令行与图形界面组件。7-Zip 版权归 Igor Pavlov 所有。
