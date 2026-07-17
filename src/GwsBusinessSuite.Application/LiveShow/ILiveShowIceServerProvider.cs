namespace GwsBusinessSuite.Application.LiveShow;

public interface ILiveShowIceServerProvider
{
    bool IsTurnConfigured { get; }
    LiveShowIceConfiguration CreateConfiguration();
}
