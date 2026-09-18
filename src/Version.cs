namespace DeepSeekHarness
{
    // 应用版本号。build.ps1 编译时会用 -Version 覆盖这里的常量，
    // 供「检查更新」与 GitHub Releases 上的最新版比对。
    // 仓库里的默认值仅用于直接手工编译 src/App.cs 的场景。
    internal static class AppInfo
    {
        public const string Version = "0.0.0-auto";
    }
}
