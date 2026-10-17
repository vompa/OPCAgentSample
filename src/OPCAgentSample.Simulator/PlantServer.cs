using Opc.Ua;
using Opc.Ua.Server;

namespace OPCAgentSample.Simulator;

/// <summary>OPC-UA-Server mit genau einem eigenen Node Manager für die simulierte Halle.</summary>
internal sealed class PlantServer(SimulatorRuntime runtime) : StandardServer
{
    public PlantNodeManager? NodeManager { get; private set; }

    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        NodeManager = new PlantNodeManager(server, configuration, runtime);
        return new MasterNodeManager(server, configuration, null, NodeManager);
    }

    protected override ServerProperties LoadServerProperties() => new()
    {
        ManufacturerName = "vompa",
        ProductName = "OPCAgentSample.Simulator",
        ProductUri = "urn:vompa:opc-agent-sample:simulator:product",
        SoftwareVersion = Utils.GetAssemblySoftwareVersion(),
        BuildNumber = Utils.GetAssemblyBuildNumber(),
        BuildDate = Utils.GetAssemblyTimestamp(),
    };
}
