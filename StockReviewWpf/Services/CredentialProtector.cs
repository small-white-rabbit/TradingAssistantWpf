using System.Security.Cryptography;
using System.Text;

namespace StockReviewWpf.Services;

/// <summary>
/// 凭据保护（DPAPI CurrentUser 范围）：WebDAV 密码等敏感字段写入数据库前加密、读取时解密。
/// 兼容策略：解密失败（旧版明文 / Electron 导入数据）时原样返回，不阻断读取。
/// </summary>
public static class CredentialProtector
{
    // 附加熵：与应用绑定，防止同机其他程序直接解密
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StockReviewWpf.Credential.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            // 加密失败退化为明文存储，保证功能可用
            return plain;
        }
    }

    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return cipher;
        try
        {
            var bytes = Convert.FromBase64String(cipher);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            // 旧明文数据（Electron 导入）：原样返回
            return cipher;
        }
    }
}
