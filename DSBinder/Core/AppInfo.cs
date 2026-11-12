using System.Reflection;

namespace DSBinder.Core
{
    public static class AppInfo
    {
        public const string UpdateRepoUrl = "https://github.com/DmitriySitarskiy/DSBinder";

        public static bool IsUpdateRepoConfigured => !UpdateRepoUrl.Contains("DmitriySitarskiy");

        public static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
