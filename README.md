# Win11 Conda 环境管理器

Windows 11 桌面应用，用于管理电脑上已有的 Conda、Miniconda 和 Miniforge 安装。

## 当前进度

第三阶段已实现环境创建与删除。应用会列出已验证的 Conda 安装和其已知环境；选中一个环境后，可查看完整存放位置、Python 版本、软件包数量，以及包的名称、版本、构建和渠道，并按名称或版本搜索。

点击“新建”可输入环境名称与可选的 Python 版本。对话框会显示所选 Conda 安装的首个可写默认环境目录，允许改为其他绝对路径，并在执行前预览最终环境路径。目标路径已存在时不能创建。留空 Python 版本仍会安装 Python，由现有 Conda 渠道选择版本。

选中环境后可点击“删除环境”；确认框会显示完整路径。删除会移除该目录内的**所有文件**，且不能撤销。应用禁止删除 base、已发现的 Conda 安装根目录及其父目录、当前激活环境及其父目录、非 Conda 目录，以及无法安全比对的路径别名。删除前还会对现存目录核对 Windows 文件系统身份；如果无法确认目标没有包含受保护的安装或激活环境，操作会停止。创建和删除期间会显示进度状态，可请求取消；取消或失败后请检查目标目录是否留有部分内容。如果无法确认 Conda 子进程已停止，应用会暂停后续创建和删除，提示检查进程与目录后重启。正常完成后会刷新环境列表；自定义位置创建的环境会记录在 `%LOCALAPPDATA%\CondaManager\known-prefixes.json`，便于重启后查找。

## 开发与运行

需要 Windows 11 和 .NET 10 SDK。运行界面：

```powershell
dotnet run --project .\src\CondaManager.App\CondaManager.App.csproj
```

构建解决方案并运行离线检查：

```powershell
pwsh -NoProfile -File .\scripts\verify.ps1
```

检查程序不依赖额外 NuGet 测试包，使用模拟进程结果验证查询、创建和删除行为，不会对本机 Conda 环境执行这些操作。`verify.ps1` 在当前开发沙箱中限制 MSBuild 为单节点，避免并行工作进程失败。

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

Conda 自身的 `env list` 可能清理过期的环境登记，`list` 在某些 pip 互操作情形下可能更新包元数据，因此这些标准查询不能保证对文件系统严格只读。创建和删除经由所选安装的已验证启动器，以完整路径调用 Conda；应用给 Conda 子进程设置 `CONDA_REPORT_ERRORS=false`，不会修改用户的 Conda 配置。自动检查只使用模拟结果，不会对本机环境运行创建或删除。
