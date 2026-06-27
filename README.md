<img src="shadowsocks-csharp/Resources/ssw128.png" width="32"/>ShadowsocksR for Windows
=======================

[中文](README.zh-CN.md)

## Forked from [HMBSbige/ShadowsocksR-Windows](https://github.com/HMBSbige/ShadowsocksR-Windows)

## Fork Release Channel
Current fork release series: `v6.1.0-net10`.
In-app update checks and downloads use [LonelyZJ/ShadowsocksR-Windows releases](https://github.com/LonelyZJ/ShadowsocksR-Windows/releases).

## Key Updates
Based on [ShadowsocksR-Windows 6.1.0](https://github.com/HMBSbige/ShadowsocksR-Windows/releases/tag/6.1.0), this fork includes the following updates:
- Upgraded to .NET 10: migrated the main project and unit tests from net7.0 to net10.0.
- Removed Syncfusion dependencies: rebuilt the affected UI with WPF.
- Improved single-connection throughput: reused and expanded TCP send/receive buffers, increased socket send/receive buffer sizes, and provides a notable speed boost for ShadowsocksR servers using the BBR congestion control algorithm.
- Improved shutdown and system proxy restoration: prevents proxy mode changes or service reloads during shutdown.
- Cleaned up build warnings: addressed code related to NU1902, VSTHRD, SYSLIB0014, and similar warnings.

## Develop

Visual Studio Community 2026 with the .NET 10 SDK is recommended.

## License

GPLv3

Forked from Copyright © 2019 - 2022 HMBSbige -> Forked from ShadowsocksR by BreakWa11
