namespace GwsBusinessSuite.Web.Services;

public sealed class LiveShowIceOptions
{
    public List<string> StunUrls { get; set; } = ["stun:stun.l.google.com:19302"];
    public LiveShowTurnOptions Turn { get; set; } = new();
}

public sealed class LiveShowTurnOptions
{
    public List<string> Urls { get; set; } = [];
    public string SharedSecret { get; set; } = string.Empty;
    public int CredentialLifetimeMinutes { get; set; } = 60;
}
