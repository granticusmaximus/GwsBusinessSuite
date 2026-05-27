using GwsBusinessSuite.Application.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        protector = provider.CreateProtector("GwsBusinessSuite.Secrets.v1");
    }

    public string Protect(string plaintext)
    {
        return string.IsNullOrWhiteSpace(plaintext)
            ? string.Empty
            : protector.Protect(plaintext);
    }

    public string Unprotect(string protectedValue)
    {
        return string.IsNullOrWhiteSpace(protectedValue)
            ? string.Empty
            : protector.Unprotect(protectedValue);
    }
}
