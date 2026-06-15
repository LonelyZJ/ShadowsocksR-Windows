<img src="shadowsocks-csharp/Resources/ssw128.png" width="32"/>ShadowsocksR for Windows
=======================

[English](README.md)

## Forked from [HMBSbige/ShadowsocksR-Windows](https://github.com/HMBSbige/ShadowsocksR-Windows)

## 主要更新
基于 [ShadowsocksR-Windows 6.1.0](https://github.com/HMBSbige/ShadowsocksR-Windows/releases/tag/6.1.0)，做出如下更新：
- 升级到 .NET 10：主项目和单元测试从 net7.0 迁移到 net10.0。
- 移除 Syncfusion 依赖：使用 WPF 重构相关 UI。
- 优化单连接吞吐：复用并扩容 TCP 收发缓冲，扩大 socket 收发缓冲区，对启用 BBR 拥塞控制的 ShadowsocksR 服务端有明显速度提升。
- 优化退出和系统代理恢复：防止退出过程中再次切换代理或重载服务。
- 清理构建告警：处理 NU1902、VSTHRD、SYSLIB0014 等告警相关代码。

## 开发

推荐使用 Visual Studio Community 2026 和 .NET 10 SDK。

## 许可证

GPLv3

Forked from Copyright © 2019 - 2022 HMBSbige -> Forked from ShadowsocksR by BreakWa11
