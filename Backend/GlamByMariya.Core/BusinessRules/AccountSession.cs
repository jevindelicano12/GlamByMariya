using System.Security.Cryptography;
using System.Text;

namespace GlamByMariya.Core.BusinessRules;

public static class AccountSession
{
    public static string Version(string passwordHash) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)));
}
