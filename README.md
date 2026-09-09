<p align="center">
  <img src="https://capsule-render.vercel.app/api?type=waving&color=gradient&customColorList=12&height=180&section=header&text=Hanime1%20Downloader&fontSize=48&fontColor=fff&animation=fadeIn&fontAlignY=38&desc=视频下载工具&descAlignY=58&descSize=16" width="100%"/>
</p>

<div align="center">

<img src="https://github.com/user-attachments/assets/9ba96833-9610-42b0-9dbf-95114fdc3be7" width="96" alt="App Icon"/>

<br><br>

![Stars](https://img.shields.io/github/stars/yxxawa/hanime1-downloader?style=flat-square&color=ff6b9d&labelColor=2d3436)
![Forks](https://img.shields.io/github/forks/yxxawa/hanime1-downloader?style=flat-square&color=a29bfe&labelColor=2d3436)
![Issues](https://img.shields.io/github/issues/yxxawa/hanime1-downloader?style=flat-square&color=74b9ff&labelColor=2d3436)
![License](https://img.shields.io/github/license/yxxawa/hanime1-downloader?style=flat-square&labelColor=2d3436)

<br>

![Platform](https://img.shields.io/badge/Windows-x64%20%7C%20x86%20%7C%20arm64-0078d4?style=flat-square&logo=windows11&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-9.0-512bd4?style=flat-square&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-68217a?style=flat-square)
![WebView2](https://img.shields.io/badge/WebView2-Required-0078d4?style=flat-square&logo=microsoftedge&logoColor=white)

</div>

---

## 界面预览

<table align="center">
  <tr>
    <td align="center" width="50%">
      <img src="https://github.com/user-attachments/assets/f95681a9-dc23-4fb9-bb7b-ed99afc53b0b" width="100%"/>
      <sub>主界面 · 浅色模式</sub>
    </td>
    <td align="center" width="50%">
      <img src="https://github.com/user-attachments/assets/31c3e4c3-ae8d-4ad7-88eb-68a645715251" width="100%"/>
      <sub>主界面 · 深色模式</sub>
    </td>
  </tr>
</table>


## 功能特性

**搜索与浏览**

- 视频搜索，支持关键词与 ID 直达
- 搜索历史下拉（聚焦搜索框显示，右键删除单项）
- 高级筛选：标签、日期、时长、排序、广泛配对
- 搜索 / 收藏 / 相关视频列表显示「✓ 已下载」角标（下载过的视频一眼可见）
- 详情面板：简介、标签、相关视频推荐、封面预览
- 自定义站点支持（粘贴自定义站点的 watch 链接可直接解析）

**下载与队列**

- 自动解析多清晰度视频源（1080P / 720P / 480P 等）
- HLS/m3u8 分片下载、AES-128 解密与合并
- 下载队列：并发下载、暂停 / 恢复、失败自动重试（可配置 0-8 次）
- 不限速下载（旧版「下载限速」已整体移除，速度取决于带宽与站点）
- 磁盘空间预检、瞬时速度显示
- 断点续传（服务器文件变化自动从头重下）
- 下载历史记录，避免重复下载
- 失败后可手动触发重新解析视频源
- 文件命名规则预设（默认 `{title}_{videoId}`）

**收藏与数据**

- 多收藏夹管理：新建、重命名、删除、导入 / 导出
- 数据统一保存在 `%LOCALAPPDATA%\Hanime1Downloader.CSharp`，exe 目录保持干净

**个性化**

- 浅色 / 深色主题切换
- 详情面板显示项自由开关
- 内置视频播放器（按视频记忆播放进度，下次打开自动续播）
- Cloudflare 会话复用，减少重复验证
- 启动时自动检查 GitHub Release 新版本（发现新版后工具栏显示「有新版本」按钮）
- 列表封面缩略图缓存

---

## 运行要求

| 环境 | 要求 |
|:---|:---|
| 开发运行 | Windows、[.NET 9 SDK](https://dotnet.microsoft.com/download)、WebView2 Runtime |
| 发布版运行 | Windows（x64 / x86 / arm64）、[.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)、WebView2 Runtime |

> WebView2 Runtime 通常已预装于 Windows 11，若缺失可[点此下载](https://developer.microsoft.com/microsoft-edge/webview2/)。

---

## 构建与发布

```bash
# 构建
dotnet build "Hanime1Downloader.CSharp.csproj"

# 运行
dotnet run --project "Hanime1Downloader.CSharp.csproj"

# 发布（单文件、非自包含、win-x64）
dotnet publish "Hanime1Downloader.CSharp.csproj" -c Release -p:DebugType=None -p:DebugSymbols=false
```

发布输出：`bin/Release/net9.0-windows/win-x64/publish/`

---

## 数据文件

设置 / 收藏 / 历史 / 队列 / Cookie / 封面缓存 / 日志统一保存在 `%LOCALAPPDATA%\Hanime1Downloader.CSharp`（每个 Windows 用户一份）；下载的视频默认保存在 **exe 所在目录的 `Downloads\`**。旧版放在程序目录里的数据会在首次运行时自动迁移过来。

| 文件 | 说明 |
|:---|:---|
| `settings.json` | 程序设置（含主窗口位置、搜索历史） |
| `favorites.json` | 收藏夹数据 |
| `download_history.json` | 下载历史 |
| `download_queue.json` | 下载队列（可配置是否保留） |
| `cookies.{host}.json` | 各站点的 Cloudflare 会话缓存（`cookies.json` 为旧版格式） |
| `app.log` | 运行日志（超过 5MB 自动轮转） |
| `crash.log` | 崩溃日志 |

---

## 项目结构

```
hanime-downloader/
├── Hanime1Downloader.CSharp.csproj   项目文件（入口，位于仓库根目录）
├── App.xaml(.cs)                     应用入口与全局资源
├── AssemblyInfo.cs
├── Views/                            所有窗口
│   └── MainWindow/                   主界面：MainWindow.xaml + 11 个 partial 代码文件
├── Services/                         业务服务：HTTP、下载、更新检查、主题、缩略图缓存等
├── Models/                           数据模型：设置、视频信息、下载项等
├── Assets/                           静态资源（筛选项数据、图标、hls.min.js）
├── Themes/                           浅色 / 深色主题 XAML 资源
└── Converters/                       XAML 绑定值转换器
```

---

## 许可证

本项目遵循 [MIT License](https://github.com/yxxawa/hanime1DownLoader/blob/main/LICENSE) 开源协议。

欢迎 Star、Fork 与 PR。

<p align="center">
  <img src="https://capsule-render.vercel.app/api?type=waving&color=gradient&customColorList=12&height=100&section=footer&text=Made%20with%20%E2%9D%A4%EF%B8%8F%20by%20yxxawa&fontSize=18&fontColor=fff&animation=fadeIn" width="100%"/>
</p>

---

### 打包发布

```powershell
# x64 / x86 / arm64 单文件（框架依赖），输出到 dist\<架构>\
dotnet publish "Hanime1Downloader.CSharp.csproj" -c Release -r win-x64   --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o dist\x64
dotnet publish "Hanime1Downloader.CSharp.csproj" -c Release -r win-x86   --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o dist\x86
dotnet publish "Hanime1Downloader.CSharp.csproj" -c Release -r win-arm64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o dist\arm64
```

发布包只需要放 exe：运行时数据都在 `%LOCALAPPDATA%`，不会污染 exe 目录。

### 数据目录

| 内容 | 位置 |
|:---|:---|
| 下载的视频（默认） | **exe 所在目录 `\Downloads`**（可在设置里改） |
| 设置 / 收藏 / 下载历史 / 下载队列 / Cookie 缓存 | `%LOCALAPPDATA%\Hanime1Downloader.CSharp` |
| 封面缓存 `thumbcache\` | `%LOCALAPPDATA%\Hanime1Downloader.CSharp\thumbcache` |
| WebView2 / Cloudflare 会话 | `%LOCALAPPDATA%\Hanime1Downloader.CSharp\WebView2` |
| 日志 | 发布版不生成 `app.log`；仅崩溃时生成 `crash.log` |

### 其他注意

- 运行要求：WebView2 Runtime（Win11 自带，Win10 可能需安装）。用 `-SelfContained` 发布可免装 .NET 9 运行时。
- 如果很多人共用同一个代理/VPN 出口 IP，Cloudflare 会把大家当成同一个客户端，验证更频繁；
  程序会在后台自动通过，无需人工点击。
