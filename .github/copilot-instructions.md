# EhangNAS-Sync 项目铁律

## 发布目录（永远不变，禁止修改）

发布命令必须使用 `-o` 指向第一个目录，发布后必须复制到其余两个目录：

1. `C:\Users\hhz\source\repos\EhangNAS-Sync\EhangNAS-Sync\bin\publish`
2. `\\192.168.16.171\EhangSoft`
3. `\\192.168.16.83\Games`

## 发布命令模板

```powershell
cd "C:\Users\hhz\source\repos\EhangNAS-Sync"
dotnet publish EhangNAS-Sync\EhangNAS-Sync.csproj -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o EhangNAS-Sync\bin\publish
Copy-Item "EhangNAS-Sync\bin\publish\EhangNAS-Sync.exe" "\\192.168.16.171\EhangSoft\" -Force
Copy-Item "EhangNAS-Sync\bin\publish\EhangNAS-Sync.exe" "\\192.168.16.83\Games\" -Force
```

## 机器说明

- **Machine A（本机）**：当前 VS Code 机器，同步目录 `C:\Users\hhz\EhangSync`，日志 `C:\Users\hhz\AppData\Local\YihangDrive\debug.log`
- **Machine B（远程 192.168.16.171）**：Win11-Mary，同步目录 `C:\Users\hhz\EhangDrive`，日志 `\\192.168.16.171\YihangDrive\debug.log`
- **服务器**：192.168.16.15:8000（Python FastAPI + SQLite）
