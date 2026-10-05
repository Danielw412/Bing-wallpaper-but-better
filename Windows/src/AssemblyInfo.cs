using System.Reflection;
using System.Runtime.Versioning;

// csc.exe does not add this attribute automatically. It also selects the
// runtime's modern compatibility defaults, including OS-managed TLS.
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Daily Wallpaper")]
[assembly: AssemblyDescription("A lightweight, once-per-day Bing wallpaper updater for Windows")]
[assembly: AssemblyVersion("0.1.0.0")]
