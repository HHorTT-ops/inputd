using System.Security.Cryptography;
using System.Text;

namespace InputExecutor.Service;

public sealed class ServiceAccessToken
{
    private readonly byte[] _hash;

    public ServiceAccessToken()
    {
        TokenPath = Environment.GetEnvironmentVariable("INPUTD_ACCESS_TOKEN_PATH") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "inputd", "access.token");
        var configured = Environment.GetEnvironmentVariable("INPUTD_ACCESS_TOKEN");
        string token;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            token = configured.Trim();
        }
        else if (File.Exists(TokenPath))
        {
            token = File.ReadAllText(TokenPath).Trim();
        }
        else
        {
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
            File.WriteAllText(TokenPath, token + Environment.NewLine, new UTF8Encoding(false));
        }
        if (token.Length < 32) throw new InvalidOperationException("INPUTD_ACCESS_TOKEN must contain at least 32 characters.");
        _hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public string TokenPath { get; }

    public bool Validate(string supplied)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(_hash, hash);
    }
}
