namespace GwsBusinessSuite.Application.TrafficIncidents;

// Remembers incidents as they're looked up, for Overwatch's time machine. Must be cheap and never
// block: implementations buffer and write in the background.
public interface IIncidentHistoryRecorder
{
    void Record(IEnumerable<TrafficIncident> incidents);
}
