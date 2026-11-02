using OPCAgentSample.Server.Live;

namespace OPCAgentSample.Tests.Web;

public class LiveHubTests
{
    /// <summary>Dispatch arbeitet mit einer Kopie der Liste und kann so noch eine gerade geschlossene Verbindung treffen.</summary>
    [Fact]
    public void Eine_geschlossene_Verbindung_bricht_den_Versand_nicht_ab()
    {
        var hub = new LiveHub();
        var closed = hub.TryRegister("besitzer", maxPerOwner: 1)!;
        closed.Dispose();

        closed.Raise(LiveChangeKind.Feed, ownerKey: null);

        Assert.Equal(0, hub.ConnectionCount);
    }
}
