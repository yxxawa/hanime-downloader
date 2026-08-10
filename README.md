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
    <td align="center" width="33%">
      <img src="https://github.com/user-attachments/assets/f95681a9-dc23-4fb9-bb7b-ed99afc53b0b" width="100%"/>
      <sub>主界面 · 浅色模式</sub>
    </td>
    <td align="center" width="33%">
      <img src="https://github.com/user-attachments/assets/31c3e4c3-ae8d-4ad7-88eb-68a645715251" width="100%"/>
      <sub>主界面 · 深色模式</sub>
    </td>
    <td align="center" width="33%">
      <img src="https://github.com/user-attachments/assets/a87d7e28-7fca-4c24-aef1-142fa0ce4022" width="100%"/>
      <sub>精简模式</sub>
    </td>
  </tr>
</table>


## 功能特性

**搜索与浏览**

- 视频搜索，支持关键词与 ID 直达
- 高级筛选：标签、日期、时长、排序、广泛配对
- 详情面板：简介、标签、相关视频推荐、封面预览

**下载与队列**

- 自动解析多清晰度视频源（1080P / 720P / 480P 等）
- 下载队列：并发下载、暂停 / 恢复、失败自动重试
- 下载历史记录，避免重复下载
- 失败后可手动触发重新解析视频源

**收藏与数据**

- 多收藏夹管理：新建、重命名、删除、导入 / 导出
- 所有数据保存在程序目录，便携无需安装

**个性化**

- 浅色 / 深色主题切换
- 精简模式，隐藏详情面板
- 详情面板显示项自由开关
- 内置视频播放器
- Cloudflare 会话复用，减少重复验证
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

所有数据均存储在程序同目录下，拷贝文件夹即可完整迁移。

| 文件 | 说明 |
|:---|:---|
| `settings.json` | 程序设置 |
| `favorites.json` | 收藏夹数据 |
| `download_history.json` | 下载历史 |
| `download_queue.json` | 下载队列（可配置是否保留） |
| `cookies.json` | Cloudflare 会话缓存 |

---

## 项目结构

```
Hanime1Downloader.CSharp/
├── MainWindow.xaml(.cs)       主界面与核心交互逻辑
├── Views/                     子窗口：设置、筛选、验证、播放器等
├── Services/                  业务服务：HTTP、下载、主题、缩略图缓存等
├── Models/                    数据模型：设置、视频信息、下载项等
├── Assets/                    静态资源（筛选项数据、图标）
├── Themes/                    浅色 / 深色主题 XAML 资源
└── Converters/                XAML 绑定值转换器
```

---

## 许可证

本项目遵循 [MIT License](https://github.com/yxxawa/hanime1DownLoader/blob/main/LICENSE) 开源协议。

欢迎 Star、Fork 与 PR。

<p align="center">
  <img src="https://capsule-render.vercel.app/api?type=waving&color=gradient&customColorList=12&height=100&section=footer&text=Made%20with%20%E2%9D%A4%EF%B8%8F%20by%20yxxawa&fontSize=18&fontColor=fff&animation=fadeIn" width="100%"/>
</p>
