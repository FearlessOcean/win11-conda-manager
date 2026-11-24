# Win11 Conda 环境管理器

Windows 11 桌面应用，用于管理电脑上已有的 Conda、Miniconda 和 Miniforge 安装。

## 当前进度

第二阶段已实现环境浏览：应用会列出已验证的 Conda 安装和其已知环境。选中一个环境后，可查看完整存放位置、Python 版本、软件包数量，以及包的名称、版本、构建和渠道；搜索框可按名称或版本筛选。环境列表和软件包详情可分别刷新，读取失败时可重试。创建和删除界面属于后续阶段。

## 开发与运行

需要 Windows 11 和 .NET 10 SDK。运行界面：

```powershell
dotnet run --project .\src\CondaManager.App\CondaManager.App.csproj
```

构建解决方案并运行离线检查：

```powershell
pwsh -NoProfile -File .\scripts\verify.ps1
```

检查程序不依赖额外 NuGet 测试包，使用模拟进程结果验证 Conda 查询，不会对本机 Conda 环境执行命令。`verify.ps1` 在当前开发沙箱中限制 MSBuild 为单节点，避免并行工作进程失败。

只读验证指定安装的启动器（将路径换成自己的安装根目录）：

```powershell
dotnet run --project .\tests\CondaManager.Checks\CondaManager.Checks.csproj -- --probe-root "D:\Software\ANACONDA"
```

发布包含 .NET 运行时的 Windows x64 文件夹：

```powershell
dotnet publish .\src\CondaManager.App\CondaManager.App.csproj -c Release -r win-x64 --self-contained true --source . -p:NuGetAudit=false -m:1 -p:UseSharedCompilation=false
```

产物位于 `src\CondaManager.App\bin\Release\net10.0-windows\win-x64\publish`，将整个目录一起分发。

## Conda 安装识别

应用按已保存的选择、`CONDA_EXE`、`PATH` 和常见安装目录的顺序寻找候选安装；也可在界面中手动选择**安装根目录**。仅当候选入口的 `info --json` 成功，且报告的根目录与候选根目录一致时，安装才会进入可选列表。

同一安装会先尝试 `Scripts\conda.exe`，再尝试该安装自己的 `python.exe -m conda`。本机曾出现前者无法启动、后者可用的情况，因此验证的是实际运行结果，而非文件是否存在。

Conda 的安装信息来自 `info --json`，环境路径来自 `env list --json`，包记录来自 `list --prefix <完整路径> --json`。环境的完整路径是唯一标识；重名环境不会混淆。Python 版本取自所选环境的 `python` 包记录，未安装时显示“未安装 Python”。只有选中环境才会查询其软件包。

界面没有创建、删除或修改环境的操作。不过 Conda 自身的 `env list` 可能清理过期的环境登记，`list` 在某些 pip 互操作情形下可能更新包元数据。因此这些标准查询不能保证对文件系统严格只读；自动检查只使用模拟结果，不会对本机环境运行包枚举。
