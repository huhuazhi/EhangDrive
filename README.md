# 亿航Drive (EhangDrive)

一款基于 Windows Cloud Filter API 的本地 NAS 文件同步客户端，支持多客户端实时同步、按需下载（云端占位符）和托盘常驻运行。

## 功能特性

- **云端占位符**：利用 Windows Cloud Filter API (CldApi)，文件以占位符形式存在于本地，双击时按需下载，不占用本地磁盘空间
- **多客户端同步**：支持多台 PC 同时连接同一 NAS，文件的创建、修改、删除、重命名、移动操作自动同步到其他客户端
- **全量同步**：首次连接时自动执行全量同步，对比本地与服务端文件时间戳，双向同步
- **增量轮询**：通过 ModList 轮询机制，每 5 秒检测其他客户端的变更并自动同步
- **实时文件监听**：使用 FileSystemWatcher 监听本地文件变更，自动上传/重命名/移动/删除
- **断点续传**：大文件上传支持分块传输，显示实时进度和速度
- **系统托盘**：最小化到托盘常驻运行，右键菜单快速打开同步目录、设置或退出
- **开机自启**：支持设置开机自动启动
- **自动登录**：保存登录凭据，下次启动自动连接

## 界面预览

应用包含两个主要页面：

- **同步页**：上半区显示传输任务（文件名、进度条、速度、状态），下半区显示同步日志
- **设置页**：账户信息、同步目录配置、已注册客户端列表、开机自启等设置

## 技术栈

| 组件 | 技术 |
|------|------|
| 框架 | .NET 8 / WPF |
| 目标平台 | Windows 10 19041+ |
| 云文件 API | Windows Cloud Filter API (CldApi P/Invoke) |
| 网络通信 | HttpClient + REST API |
| 序列化 | System.Text.Json |
| 后端 | Python FastAPI + SQLite（独立部署） |

## 项目结构

```
EhangNAS-Sync/
├── App.xaml / App.xaml.cs          # 应用入口，会话管理（登录→同步→启动）
├── LoginWindow.xaml/.cs            # 登录窗口
├── MainWindow.xaml/.cs             # 主窗口（同步页 + 设置页）
├── Models/
│   ├── LoginConfig.cs              # 登录配置模型
│   └── SyncStatus.cs               # 传输项、日志、状态管理器
├── Native/
│   └── CldApi.cs                   # Windows Cloud Filter API P/Invoke 声明
├── Services/
│   ├── AuthService.cs              # 登录认证服务
│   ├── ConfigService.cs            # 配置持久化（config.json）
│   ├── FileLogger.cs               # 文件日志
│   ├── FileWatcherService.cs       # 本地文件变更监听
│   ├── InitialSyncService.cs       # 全量同步服务
│   ├── ModListPollingService.cs    # 增量变更轮询服务
│   ├── SyncApiService.cs           # REST API 客户端
│   ├── SyncEngine.cs               # 同步引擎（上传/下载/重命名/移动/删除）
│   ├── SyncProviderConnection.cs   # Cloud Filter 连接管理
│   ├── SyncRootRegistrar.cs        # 同步根目录注册
│   └── TrayIconService.cs          # 系统托盘图标服务
└── EhangNAS-Sync.csproj            # 项目文件
```

## 构建与发布

### 环境要求

- Windows 10 (19041+) 或 Windows 11
- .NET 8 SDK
- Visual Studio 2022（可选）

### 构建

```bash
cd EhangNAS-Sync
dotnet build
```

### 发布（单文件可执行）

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o bin\Publish
```

发布后在 `bin\Publish\EhangNAS-Sync.exe` 即可独立运行，无需安装 .NET 运行时。

## 使用说明

1. 启动程序，输入 NAS 服务端地址和账号登录
2. 选择本地同步目录
3. 程序自动执行全量同步，将服务端文件以占位符形式同步到本地
4. 之后文件变更会自动双向同步
5. 最小化后在系统托盘运行

## 配置文件

配置保存在 `%LOCALAPPDATA%\YihangDrive\config.json`，包含：

- 服务器地址
- 用户名和 Token
- 同步目录路径
- 自动登录开关

## 许可证

私有项目，仅供内部使用。
