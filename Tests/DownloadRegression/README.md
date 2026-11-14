# 下载与队列回归测试

在 Windows + .NET 9 Desktop Runtime 上运行：

    dotnet run --project Tests/DownloadRegression/DownloadRegression.csproj

测试运行实际下载服务、WPF 主窗口与 WebView2 验证流程。HTTP 与浏览器响应全部由测试替身提供；不会访问真实站点或读取用户配置。文件、日志与历史使用独立的系统临时目录，浏览器用独立的保留域名隔离。

覆盖断点续传、同名防覆盖、队列暂停和失败隔离、合并保存与退出落盘、HLS 流式落盘与顺序合并、AES-128 和字节范围、空间预检、重试上限，以及验证窗口后台交接和各请求独立取消。

右键菜单测试覆盖子菜单打开、重复移动项合并、按选中任务下载、失败项菜单状态，以及移除历史记录时保留成品。
