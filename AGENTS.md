# AGENTS.md

本文件为自动化代理和协作者提供仓库级上下文。修改代码前请先阅读本文件，并优先沿用项目现有架构、命名和目录约定。

## 构建与测试命令

```powershell
# 构建 .NET 应用，Release，框架依赖模式
dotnet build -c Release shadowsocks-csharp.sln

# 发布框架依赖应用
# 输出目录：shadowsocks-csharp/bin/Release/net10.0-windows/publish/
dotnet publish -c Release -f net10.0-windows shadowsocks-csharp/shadowsocksr.csproj

# 发布自包含版本
dotnet publish -c Release -f net10.0-windows -r win-x64 --self-contained true shadowsocks-csharp/shadowsocksr.csproj
dotnet publish -c Release -f net10.0-windows -r win-x86 --self-contained true shadowsocks-csharp/shadowsocksr.csproj

# 完整 CI 构建脚本：app + x86 + x64，并修补 DLL 加载路径
.\build.ps1                 # 构建全部目标
.\build.ps1 -buildtfm app   # 仅构建框架依赖版本
.\build.ps1 -buildtfm x64   # 仅构建 x64 自包含版本
.\build.ps1 -buildtfm x86   # 仅构建 x86 自包含版本

# 运行全部单元测试
dotnet test UnitTest/UnitTest.csproj

# 运行指定测试类或测试方法
dotnet test UnitTest/UnitTest.csproj --filter "FullyQualifiedName~UnitTest.UnitTest"
dotnet test UnitTest/UnitTest.csproj --filter "FullyQualifiedName~EncryptionTest"
```

CI 构建还需要配置私有 NuGet 源，用于 Syncfusion 许可依赖：

```powershell
dotnet nuget add source https://nuget.pkg.github.com/HMBSbige/index.json -n GitHub-HMBSbige -u HMBSbige -p <TOKEN> --store-password-in-clear-text
```

CI 环境必须设置 `SyncfusionLicenseKey` 环境变量。Syncfusion 许可会影响构建和运行；单元测试通常不依赖该许可。

## 项目概览

ShadowsocksR for Windows 是一个基于 .NET 10 WPF 的桌面应用，实现 ShadowsocksR 代理协议。应用通过系统托盘 GUI 管理 SSR 服务器，并集成 Windows 系统代理设置。项目采用 GPLv3 许可证。

解决方案 `shadowsocks-csharp.sln` 包含两个项目：

- `shadowsocks-csharp/shadowsocksr.csproj`：主 WPF 应用，输出类型为 `WinExe`，程序集名为 `ShadowsocksR`
- `UnitTest/UnitTest.csproj`：基于 MSTest 的单元测试项目

## 启动流程

入口文件：`shadowsocks-csharp/Program.cs`

1. `SingleInstanceService` 通过基于目录哈希的 mutex 保证单实例运行；第二个实例会把命令行参数转发给第一个实例。
2. 加载 `gui-config.json` 到 `Global.GuiConfig`，类型为 `Configuration`。
3. 设置 I18N 语言，创建 `MainController`，再创建 `MenuViewController`。
4. 调用 `MainController.Reload()` 启动所有网络服务。
5. 如果配置为默认配置，则显示首次运行对话框；同时注册 `ssr://` 和 `sub://` URL 协议。

## 核心类

- `Global`（`Model/Global.cs`）：静态全局状态，持有 `GuiConfig`、`Controller`、`ViewController`，连接配置、控制器和 UI。
- `MainController`（`Controller/MainController.cs`）：核心协调器，负责启动和停止代理服务、管理服务器增删改、切换系统代理模式，并触发 `ConfigChanged`、`Errored`、`ShowConfigFormEvent` 等 UI 事件。
- `MenuViewController`（`Controller/MenuViewController.cs`）：GUI 编排层，负责系统托盘图标、上下文菜单和窗口生命周期。该文件体量很大，是主要 UI 逻辑集中点。
- `Configuration`（`Model/Configuration.cs`）：序列化到 `gui-config.json` 的主配置对象，包含服务器列表、代理模式、负载均衡、DNS、订阅 URL 和端口映射规则。
- `Server`（`Model/Server.cs`）：单个 SSR 服务器配置。构造函数可解析 `ss://` 和 `ssr://` URL，字段包括 host、port、password、method、protocol、obfs 及其参数。

## 代理连接链路

```text
客户端应用 -> Listener（默认端口 1080）-> Local（识别 SOCKS4/5/HTTP）
  -> ProxyAuthHandler（握手）-> Handler（双向转发）
    -> ProxyEncryptSocket（远端 SSR 连接）
```

- `Listener`（`Controller/Service/Listener.cs`）：TCP accept 循环，检查首包，并按链式责任模式交给已注册的 `IService` 处理器，包括 `Local`、`PACServer`、`HttpPortForwarder`。
- `Handler`（`Proxy/Handler.cs`）：主要代理连接处理器。状态机为 `READY -> HANDSHAKE -> CONNECTING -> CONNECTED -> END`，负责 TTL、keepalive、重连、自动封禁和 UDP-over-TCP。
- `ProxyEncryptSocket`（`Proxy/ProxyEncryptSocket.cs`）：按包执行 SSR 数据处理管线：
  - 发送：`IObfs.ClientPreEncrypt -> IEncryptor.Encrypt -> IObfs.ClientEncode`
  - 接收：`IObfs.ClientDecode -> IEncryptor.Decrypt -> IObfs.ClientPostDecrypt`

## 加密模块

目录：`Encryption/`

项目使用工厂和策略模式。`EncryptorFactory` 将加密方法名映射到对应的 `IEncryptor` 实现。

- OpenSSL 系列：通过 P/Invoke 调用 `libsscrypto.dll`，覆盖 AES、Camellia、Blowfish、CAST5、IDEA、RC2、RC4、SEED 等；实现见 `StreamOpenSSLEncryptor`。
- libsodium 系列：通过 P/Invoke 调用 `libsodium`，覆盖 Salsa20、ChaCha20、XSalsa20、XChaCha20；实现见 `StreamSodiumEncryptor`。
- 无加密模式：table/no encryption。

原生 DLL 以压缩资源形式存放在 `Data/`，例如 `libsscrypto.dll.gz`、`libsscrypto64.dll.gz`，运行时解压。

## 混淆模块

目录：`Obfs/`

`ObfsFactory` 将混淆方法名映射到 `IObfs` 实现。项目包含约 20 种协议，例如：

- `plain`、`http_simple`、`http_post`、`random_head`
- `tls1.2_ticket_auth`、`tls1.2_ticket_fastauth`
- `verify_deflate`、`verify_simple`
- `auth_sha1_v4`、`auth_aes128_md5`、`auth_aes128_sha1`
- `auth_chain_a` 到 `auth_chain_f`
- `auth_akarin_rand`、`auth_akarin_spec_a`

每个 `IObfs` 同时实现 TCP 和 UDP 处理流程。TCP 主要方法包括 `ClientPreEncrypt`、`ClientEncode`、`ClientDecode`、`ClientPostDecrypt`。

## UI 架构

项目使用 WPF，并采用偏实用主义的 MVVM。`ViewModel/` 下的 ViewModel 继承 `ViewModelBase`，该基类实现 `INotifyPropertyChanged`。大量 UI 逻辑仍位于 View 的 `.xaml.cs` 代码后置文件中。

关键窗口：

- `ServerConfigWindow`：服务器树和详情编辑器，服务器树使用 Syncfusion `SfTreeView`
- `SettingsWindow`：全局代理设置、负载均衡、DNS、端口转发
- `SubscribeWindow`：订阅 URL 管理

I18N 逻辑位于 `Util/I18NUtil.cs`。它从 `I18N/WindowName.{lang}.xaml` 加载窗口级本地化资源字典，支持 `en-US`、`zh-CN`、`zh-TW`。语言由 `gui-config.json` 中的 `LangName` 指定。

系统托盘菜单资源定义在 `View/NotifyIconResources.xaml`，实际菜单项由 `MenuViewController` 动态填充。

## 负载均衡

实现文件：`Model/ServerSelectStrategy.cs`

服务器选择策略基于加权随机选择，支持 `OneByOne`、`Random`、`FastDownloadSpeed`、`LowLatency`、`LowException`、`SelectedFirst`、`Timer` 等算法。权重由服务器错误率、连接耗时和活动连接数等指标计算。

## 配置文件

主配置文件是应用目录下的 `gui-config.json`。

- 读写工具：`Util/JsonUtils.cs`
- 序列化框架：`System.Text.Json`
- 加载后修正：`Configuration.FixConfiguration()`
- 配置复制：`Configuration.CopyFrom()`，该方法会复制除 `PortMap` 外的大部分字段

## 关键设计模式

- 责任链：`Listener` 依次调用 `IService` 处理器，直到某个处理器接受连接。
- 策略与工厂：`EncryptorFactory`、`ObfsFactory`、`ServerSelectStrategy`。
- 管线和装饰：`ProxyEncryptSocket` 中按“协议混淆 -> 加密 -> 传输混淆”处理发送和接收数据。
- 事件驱动：`MainController` 触发事件，`MenuViewController` 订阅并更新 UI。
- 全局单例状态：`Global` 持有配置、控制器和视图控制器引用。

## 重要约束

- `auth_sha1` 和 `auth_sha1_v2` 混淆协议只在 DEBUG 构建中编译。
- 自包含构建需要设置 csproj 中的 `SelfContained` 属性，并触发 `SelfContained`、`Is64Bit` 编译符号。
- 原生 DLL 运行时会从嵌入资源解压。
- Syncfusion 构建和运行都需要许可证。构建前后事件会运行 `SyncfusionLicenseRegister.bat`。
- `dotnet publish` 后必须运行 `Build/DotNetDllPathPatcher.ps1`，对 exe 进行二进制修补，使其从 `bin\` 子目录加载 DLL，以规避 Windows 路径长度限制。

## 开发注意事项

- 优先保持现有架构和代码风格，不要为局部改动引入无关重构。
- 修改网络、加密、混淆或配置序列化逻辑时，应补充或运行相关单元测试。
- 修改 UI 时注意同步 I18N 资源，避免只更新默认语言文本。
- 不要提交真实订阅地址、服务器凭据、Syncfusion key 或 GitHub token。
- 发布相关改动需要验证 `build.ps1`、框架依赖发布和对应自包含目标是否仍能工作。
