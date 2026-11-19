using System.Reflection;
using System.Runtime.InteropServices;

// v1.1.3 新增：程序集元数据。
// 命令行编译（csc）默认不生成任何版本/产品信息，产出的 exe 在资源里"一片空白"，
// 安全软件（如火绒）容易把这类无签名、无厂商、无版本描述的新 PE 判为可疑程序。
// 补上完整的元数据后，文件属性里能看到产品名、版本、说明，可显著降低启发式误报。
//
// v2.3：程序名改为 MultiWallCycle（规避个别系统对中文程序名的兼容性问题）。
// 文件属性（右键 → 属性 → 详细信息）：
//   文件说明 = AssemblyDescription，产品名称 = AssemblyProduct，版权 = AssemblyCopyright。

[assembly: AssemblyTitle("MultiWallCycle")]
[assembly: AssemblyDescription("MultiWallCycle 多屏幻灯壁纸：多显示器独立幻灯片壁纸（每屏独立文件夹/间隔/轮换进度，支持必应每日壁纸与图片文字说明，本地图片标题自动识别，中英文界面可切换）。v2.4")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("༺格桑仁青༻ & Old Newbie")]
[assembly: AssemblyProduct("MultiWallCycle多屏幻灯壁纸")]
[assembly: AssemblyCopyright("Copyright © 2026 ༺格桑仁青༻ & Old Newbie")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("3f9a1c6e-5b2d-4a87-9e31-7c4d8f2a6b10")]

[assembly: AssemblyVersion("2.4.0.0")]
[assembly: AssemblyFileVersion("2.4.0.0")]
