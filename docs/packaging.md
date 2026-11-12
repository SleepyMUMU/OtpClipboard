# 打包与发布

## 发布步骤

1. 提高 `src/OtpClipboard/Package.appxmanifest` 中 `Identity` 的 `Version`。版本号不变时，已安装同版本的电脑会跳过安装。
2. 在 [CHANGELOG.md](../CHANGELOG.md) 中把“未发布”的内容整理到新版本下。
3. 生成安装程序：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
   ```

   不安装 Inno Setup 时可加 `-ZipOnly`，生成完整离线 ZIP；解压后双击 `安装.cmd`。x64 发布配置包含 .NET 运行时，ZIP 另附 Windows App Runtime，无需目标电脑安装开发环境。GitHub 的 `Publish configured Windows package` 工作流会执行测试、打包与完整性检查后发布到本仓库 Releases。

4. 输出在 `dist\`：
   - `OtpClipboard-<版本>-Setup.exe`：安装程序，上传到 Releases。
   - `OtpClipboard-<版本>-Installer\`：脚本版安装目录，包含 MSIX、运行时、证书和 `安装.cmd` / `卸载.cmd`。
   - `OtpClipboard-<版本>-x64-Configured.zip`：上述目录的完整离线安装包及 `SHA256SUMS.txt` 校验文件。

`Build-Installer.ps1` 的参数：

| 参数 | 说明 |
|---|---|
| `-Arch x64\|arm64\|x86` | 目标架构，默认 x64。安装程序脚本目前只允许 x64 兼容的系统。 |
| `-CertThumbprint <指纹>` | 签名证书指纹，默认取 `packaging\OtpClipboard-Dev.cer` 的指纹。 |
| `-NoBuild` | 跳过构建，直接签名并打包已有的 MSIX。 |

## 签名证书

MSIX 必须用证书签名，并且证书主题必须与清单中的 `Publisher`（当前为 `CN=AppPublisher`）一致。

仓库里只有公钥证书 `packaging/OtpClipboard-Dev.cer`，对应的私钥只在维护者的电脑上，**私钥绝不能提交到仓库**（`.gitignore` 已排除 `*.pfx` 等文件）。所以：

- 普通贡献者不需要证书，用 `scripts\Restart-Dev.ps1` 或 Visual Studio 调试即可，开发模式不需要签名。
- 想自己生成安装程序的人，需要一张自己的证书：

  ```powershell
  # 生成自签名证书（主题必须与清单的 Publisher 一致），存放在当前用户的证书库
  $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=AppPublisher' -KeyUsage DigitalSignature `
      -FriendlyName 'OtpClipboard 开发证书' -CertStoreLocation Cert:\CurrentUser\My `
      -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
  # 导出公钥证书，替换 packaging\OtpClipboard-Dev.cer
  Export-Certificate -Cert $cert -FilePath .\packaging\OtpClipboard-Dev.cer
  ```

  用不同证书签名的包，与官方发布的包不能互相升级，需要先卸载。

正式分发时，最好换成受信任 CA 签发的代码签名证书，或者发布到 Microsoft Store。这样用户不需要导入自签名证书，安装程序也不会再被 SmartScreen 拦截。

## 安装程序做了什么

安装程序源文件在 `packaging/`：

- `OtpClipboard.iss`：Inno Setup 脚本，简体中文向导。安装程序以管理员权限运行，先用 `certutil` 把证书导入“本地计算机 → 受信任人”，再以当前用户的身份运行 `Install.ps1` 安装 MSIX。MSIX 是按用户安装的。
- `Install.ps1`：检查并安装 Windows App Runtime。如果系统里有开发模式注册的版本，先移除它并保留本地数据。最后安装或升级应用，不会降级。
- `Uninstall.ps1` / `卸载.cmd`：卸载应用，并可选删除测试证书。
- `ChineseSimplified.isl`：来自 Inno Setup 官方仓库的非官方简体中文翻译。

安装程序不生成自己的卸载程序。应用已经作为 MSIX 出现在“设置 → 应用”里，再加一个卸载项，列表中会出现两个同名条目。

## 手动安装

在管理员 PowerShell 中，进入脚本版安装目录执行：

```powershell
Import-Certificate -FilePath .\OtpClipboard-Dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path .\OtpClipboard_<版本>_x64.msix -DependencyPath .\Microsoft.WindowsAppRuntime.2.msix
```

## 手动打包 MSIX

```powershell
$winapp = "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools.winapp\0.7.0\tools\win-x64\winapp.exe"
& $winapp package .\src\OtpClipboard\OtpClipboard.csproj -c Release --arch x64 --no-sign --output .\OtpClipboard.msix
& $winapp tool signtool sign /fd SHA256 /sha1 <证书指纹> .\OtpClipboard.msix
```

已知问题：

- `dotnet build -p:GenerateAppxPackageOnBuild=true` 在 .NET 9 SDK 上会报 MSB4018，原因是打包任务缺少 `System.Security.Permissions`。请改用上面的 `winapp` 流程。
- 清单中 `<Application>` 以外的位置（例如开机自启扩展）不能使用 `$targetnametoken$` 占位符，要写实际的文件名 `OtpClipboard.exe`，否则 MakeAppx 校验失败。
- 加 `--self-contained` 可以把 Windows App SDK 一起打进包里，安装时不用再装运行时，代价是包会大约多出 30 MB。
