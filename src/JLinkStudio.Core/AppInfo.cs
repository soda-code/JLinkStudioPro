namespace JLinkStudio.Core;

/// <summary>
/// 产品信息的唯一来源：界面标题、设置页「关于」、Skill 网关 manifest 都从这里取值，
/// 避免版本号散落在多处后忘记同步。
/// </summary>
public static class AppInfo
{
    /// <summary>产品名。</summary>
    public const string ProductName = "JLink-Studio-Pro";

    /// <summary>对外展示的版本标识。</summary>
    public const string DisplayVersion = "V1.00.001.R";

    /// <summary>不带前缀的版本号，供接口与程序集信息使用。</summary>
    public const string VersionNumber = "1.00.001.R";
}
