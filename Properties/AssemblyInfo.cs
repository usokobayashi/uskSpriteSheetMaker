using System.Reflection;
using System.Runtime.InteropServices;

// アセンブリの全般情報。バージョン番号の実体は AppInfo.Version（AppInfo.cs）に一本化してあり、
// ここでは参照するだけにする（タイトルバー表示・exeのファイル情報・プロジェクトファイルの
// appVersionを、常に同じ値へ揃えるため）。
[assembly: AssemblyTitle(SpriteSheetMaker.AppInfo.Name)]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct(SpriteSheetMaker.AppInfo.Name)]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("6f1e8d3a-7b8b-4a1a-9e8b-3d6a2f8c9b41")]

[assembly: AssemblyVersion(SpriteSheetMaker.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(SpriteSheetMaker.AppInfo.Version + ".0")]
[assembly: AssemblyInformationalVersion(SpriteSheetMaker.AppInfo.Version)]
