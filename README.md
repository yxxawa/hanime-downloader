# Hanime1 Downloader

Windows WPF 视频搜索、详情查看、在线播放与下载工具。

## 功能

- 关键词搜索与视频 ID / watch 链接直达
- 搜索请求并发、短期缓存与取消控制，减少重复请求
- 详情面板按设置项生成请求字段；隐藏条目不会继续请求
- 相关视频标题优先使用真实标题，缺失时回退到可读的标识文本
- 多清晰度视频源解析与下载队列
- 断点续传、临时文件、路径规范化和下载失败重试
- 内置播放器支持普通直链和 HLS（`.m3u8`）播放
- Cloudflare 会话复用、封面缩略图缓存、浅色 / 深色主题和精简模式

## 运行环境

- Windows x64
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)

## 构建

在仓库根目录执行：

```powershell
dotnet restore .\Hanime1Downloader.CSharp.csproj
dotnet build .\Hanime1Downloader.CSharp.csproj -c Release
```

## 本地运行

```powershell
dotnet run --project .\Hanime1Downloader.CSharp.csproj
```

## 发布单文件

以下命令生成非自包含的 `win-x64` 单文件发布包：

```powershell
dotnet publish .\Hanime1Downloader.CSharp.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

发布目录：`bin\Release\net9.0-windows\win-x64\publish\`

## 运行时数据

程序会在可执行文件旁保存设置、收藏、下载历史、下载队列和站点会话缓存。常见文件如下：

- `settings.json`
- `favorites.json`
- `download_history.json`
- `download_queue.json`
- `cookies.json` 或 `cookies.<host>.json`
- `app.log`
- `downloads\`

这些内容已经写入 `.gitignore`，不会随源码提交。

## 项目结构

```text
Hanime1Downloader.CSharp/
├── App.xaml(.cs)                 应用入口与全局资源
├── MainWindow.xaml(.cs)          主界面与交互逻辑
├── Views/                        设置、筛选、验证、播放器等窗口
├── Services/                     HTTP、搜索、详情、下载、会话等服务
├── Models/                       设置、视频、下载和状态模型
├── Assets/                       图标、筛选数据和 HLS 播放脚本
├── Themes/                       浅色 / 深色主题资源
└── Converters/                   WPF 绑定值转换器
```

## 说明

- 站点页面结构、接口字段和媒体地址可能变化；相关解析逻辑集中在 `Services/`。
- `Assets/hls.min.js` 是播放器使用的前端资源，构建时会随程序集打包。
- 使用前请确认目标站点、内容和下载行为符合当地法律、站点条款及版权要求。

## 许可证

[MIT License](LICENSE)
