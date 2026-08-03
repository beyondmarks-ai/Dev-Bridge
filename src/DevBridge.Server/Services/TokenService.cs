using System.Security.Cryptography;
using System.Text;

namespace DevBridge.Services;

public sealed class TokenService
{
    public string CreateToken(int bytes = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public bool Verify(string? token, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), Convert.FromHexString(expectedHash));
    }
}
