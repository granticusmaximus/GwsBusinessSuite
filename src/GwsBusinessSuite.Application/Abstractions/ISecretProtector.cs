namespace GwsBusinessSuite.Application.Abstractions;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
