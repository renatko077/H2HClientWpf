using System.Security.Cryptography;
using System.Text;

namespace H2HClientWeb.Services;

public static class HmacSigner
{
    public static string ComputeHmacSha256Hex(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
}
