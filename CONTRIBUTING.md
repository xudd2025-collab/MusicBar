# 参与开发

欢迎提交问题和 Pull Request。请附 Windows 和播放器版本，以及暂停、拖动、切歌时的表现。截图请先遮盖账号和个人信息。

## 本地构建

Windows 10 / 11，.NET Framework 4.8：

```powershell
.\build.ps1 -Check
.\checks\Run-OverlayChecks.ps1 -RenderOnly
.\installer\Build-Installer.ps1
```

生产程序编入 `build.ps1` 列出的 15 个 C# 文件。更新检查使用本地模拟网络，验证线路切换、版本排序、缓存和下载校验，不访问音乐平台。

修改接入时验证暂停、恢复、拖动、倍速和切歌；匹配保留版本、歌手、专辑和时长校验。翻译逐句对应，长句原文和译文分别滚动，每句只滚动一次，字形与阴影留在任务栏边界内。

提交前检查差异，避免提交缓存、凭据、本机截图和日志。请勿把禁用安全软件或添加排除项作为解决方案。
