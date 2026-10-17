using OPCAgentSample.Domain;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Tests.Kpi;

public sealed class TrafficLightRulesTests
{
    [Theory]
    [InlineData(1.01, TrafficLight.Running)]
    [InlineData(0.89, TrafficLight.Warning)]
    [InlineData(0.72, TrafficLight.Problem)]
    public void Plan_fulfillment_thresholds(double ratio, TrafficLight expected) =>
        Assert.Equal(expected, TrafficLightRules.Evaluate(new StationSignals(OperatingState.Running, ratio)).Light);

    [Fact]
    public void Worst_value_wins()
    {
        var signals = new StationSignals(OperatingState.Running, PlanRatio: 1.01, MaterialReachMinutes: 38, MouldWear: 1.02);

        var result = TrafficLightRules.Evaluate(signals);

        Assert.Equal(TrafficLight.Problem, result.Light);
        Assert.Equal(2, result.Findings.Count);
    }

    [Fact]
    public void Overproduction_without_running_order_is_a_warning_even_if_everything_else_is_fine()
    {
        var withoutOrder = new StationSignals(OperatingState.RunningWithoutOrder, PlanRatio: 1.18, HasRunningOrder: false);
        var withOrder = new StationSignals(OperatingState.Running, PlanRatio: 1.18, HasRunningOrder: true);

        Assert.Equal(TrafficLight.Warning, TrafficLightRules.Evaluate(withoutOrder).Light);
        Assert.Equal(TrafficLight.Running, TrafficLightRules.Evaluate(withOrder).Light);
    }

    [Fact]
    public void Waiting_for_upstream_caps_the_plan_rating_at_warning()
    {
        var signals = new StationSignals(OperatingState.WaitingForUpstream, PlanRatio: 0.64);

        Assert.Equal(TrafficLight.Warning, TrafficLightRules.Evaluate(signals).Light);
        Assert.Equal(TrafficLight.Problem, TrafficLightRules.Evaluate(signals with { State = OperatingState.Running }).Light);
    }

    [Theory]
    [InlineData(0.09, TrafficLight.Warning)]
    [InlineData(0.20, TrafficLight.Problem)]
    [InlineData(0.02, TrafficLight.Running)]
    public void Cycle_time_deviation(double deviation, TrafficLight expected) =>
        Assert.Equal(expected, TrafficLightRules.Evaluate(new StationSignals(OperatingState.Running, CycleDeviation: deviation)).Light);

    [Theory]
    [InlineData(1.4, TrafficLight.Warning)]
    [InlineData(1.6, TrafficLight.Warning)]
    [InlineData(2.1, TrafficLight.Problem)]
    [InlineData(0.4, TrafficLight.Running)]
    public void Fill_weight_mean_uses_warning_and_action_limits(double deviation, TrafficLight expected) =>
        Assert.Equal(expected, TrafficLightRules.Evaluate(new StationSignals(OperatingState.Running, FillWeightDeviationGrams: deviation)).Light);

    [Fact]
    public void Oven_is_a_warning_when_full()
    {
        Assert.Equal(TrafficLight.Warning, TrafficLightRules.Evaluate(new StationSignals(OperatingState.Running, OvenOccupancy: 0.96)).Light);
        Assert.Equal(TrafficLight.Running, TrafficLightRules.Evaluate(new StationSignals(OperatingState.Running, OvenOccupancy: 0.78)).Light);
    }
}
