using Opc.Ua;

namespace OPCAgentSample.Tests.Simulator;

/// <summary>Der Simulator bietet SecurityPolicy None und Basic256Sha256 (SignAndEncrypt) mit selbstsigniertem Zertifikat an.</summary>
[Collection(SimulatorFixtureGroup.Name)]
public sealed class SimulatorSecurityTests(SimulatorFixture fixture)
{
    [Fact]
    public async Task Encrypted_connection_with_Basic256Sha256_serves_values()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki, useSecurity: true);

        Assert.Equal(SecurityPolicies.Basic256Sha256, client.Session.Endpoint.SecurityPolicyUri);
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, client.Session.Endpoint.SecurityMode);
        Assert.Equal("alle Ø 180", await client.ReadValueAsync<string>("HalleA.Oefen.OF-02.Planung.Zuordnungsregel"));
    }

    [Fact]
    public async Task Unencrypted_connection_with_policy_None_is_offered_as_well()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki, useSecurity: false);

        Assert.Equal(SecurityPolicies.None, client.Session.Endpoint.SecurityPolicyUri);
    }
}
