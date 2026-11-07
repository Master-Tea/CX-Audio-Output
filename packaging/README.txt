CX Audio Output 1.2.0
====================

发布者：MasterTea

把手机、平板或另一台电脑变成 Windows 的无线扬声器。
本版本包含 WinUI 3 原生控制台、网卡单选/多选、实时硬件状态、
全新猫咪/传声筒图标，以及针对偶发杂音和卡顿的稳定性增强。

快速开始
--------
1. 双击 CXAudioOutput.exe（或 Start CX Audio Output.cmd）。
2. 原生控制台会自动启动服务，默认端口 5240。
3. 在“网络接口”中勾选要使用的网卡（可单选，也可多选）。
4. 让接收设备和电脑连接同一个 Wi-Fi / 局域网。
5. 扫描控制台二维码，在接收设备上打开链接，点“开始聆听”。

重要说明
--------
- 整个文件夹必须一起保留；不要只复制 exe。wwwroot、DLL 和资源目录都是运行所需。
- 首次运行 Windows 防火墙询问时，请选择“允许访问”，并勾选“专用网络”。
- 只有勾选网卡的 IP 会生成链接；访问未选中的网卡会返回 403。
- 局域网 HTTP 下接收端会自动使用“兼容模式”（ScriptProcessor）。
  AudioWorklet 只在 HTTPS 或 localhost 下启用；兼容模式可正常使用，延迟略高。
- 兼容模式会额外增加约 60 ms 安全缓冲；如果网络抖动，建议选“稳定”（450 ms）。
- 接收设备卡片显示“稳定”或“欠载 N 次”，可用于判断播放连续性。
- 电脑本机扬声器仍会发声；应用只复制系统声音，不会关闭本机输出。
- 仅用于可信的本地网络。访问码和二维码请勿公开分享。

防火墙
------
如果其他设备打不开链接，请以管理员身份运行：

  New-NetFirewallRule -DisplayName "CX Audio Output" -Direction Inbound -Protocol TCP -LocalPort 5240 -Action Allow -Profile Private

端口被占用时：
  CXAudioOutput.exe --port 8080

测试信号：
  CXAudioOutput.exe --demo

配置与日志
----------
默认目录：%LOCALAPPDATA%\CXAudioOutput\
如果该目录不可写，会使用程序目录下的 data\。

更多说明见 README.md。
