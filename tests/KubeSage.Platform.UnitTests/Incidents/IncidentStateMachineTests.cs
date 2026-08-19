using KubeSage.Platform.Modules.Incidents;

namespace KubeSage.Platform.UnitTests.Incidents;

// The state machine is what makes the pipeline recoverable. After a restart
// the platform decides what to do next purely by looking at stored states, so
// an incident reaching an impossible state would strand it forever or, worse,
// cause a second report to be written for one event.
public sealed class IncidentStateMachineTests
{
    [Theory]
    [InlineData(IncidentState.Candidate, IncidentState.Triaging)]
    [InlineData(IncidentState.Triaging, IncidentState.Investigating)]
    [InlineData(IncidentState.Triaging, IncidentState.Ignored)]
    [InlineData(IncidentState.Investigating, IncidentState.Reported)]
    [InlineData(IncidentState.Investigating, IncidentState.Inconclusive)]
    [InlineData(IncidentState.Failed, IncidentState.Investigating)]
    [InlineData(IncidentState.Reported, IncidentState.Recovered)]
    public void The_normal_pipeline_transitions_are_allowed(IncidentState from, IncidentState to)
    {
        IncidentStateMachine.CanTransition(from, to).ShouldBeTrue();
    }

    [Theory]
    // Skipping triage would let an incident be investigated without anything
    // deciding it was worth the model time.
    [InlineData(IncidentState.Candidate, IncidentState.Reported)]
    // Reopening a terminal state would allow a second report for one event.
    [InlineData(IncidentState.Reported, IncidentState.Investigating)]
    [InlineData(IncidentState.Ignored, IncidentState.Investigating)]
    [InlineData(IncidentState.Inconclusive, IncidentState.Reported)]
    // Recovered is final; a recurrence is a new incident with its own history.
    [InlineData(IncidentState.Recovered, IncidentState.Triaging)]
    [InlineData(IncidentState.Recovered, IncidentState.Candidate)]
    public void Invalid_transitions_are_refused(IncidentState from, IncidentState to)
    {
        IncidentStateMachine.CanTransition(from, to).ShouldBeFalse();

        Should.Throw<InvalidIncidentTransitionException>(
            () => IncidentStateMachine.EnsureTransition(from, to));
    }

    [Fact]
    public void A_transition_to_the_same_state_is_a_no_op()
    {
        // Retry paths can legitimately re-assert the state they are already
        // in; that must not be treated as an error.
        Should.NotThrow(() => IncidentStateMachine.EnsureTransition(
            IncidentState.Investigating, IncidentState.Investigating));
    }

    [Theory]
    [InlineData(IncidentState.Candidate)]
    [InlineData(IncidentState.Triaging)]
    [InlineData(IncidentState.Investigating)]
    [InlineData(IncidentState.Failed)]
    public void In_flight_states_are_recognised_for_restart_recovery(IncidentState state)
    {
        // These are exactly the incidents the platform must requeue after a
        // crash. Missing one would silently drop work.
        IncidentStateMachine.IsInFlight(state).ShouldBeTrue();
        IncidentStateMachine.IsTerminal(state).ShouldBeFalse();
    }

    [Theory]
    [InlineData(IncidentState.Reported)]
    [InlineData(IncidentState.Ignored)]
    [InlineData(IncidentState.Inconclusive)]
    [InlineData(IncidentState.Recovered)]
    public void Terminal_states_schedule_no_further_work(IncidentState state)
    {
        IncidentStateMachine.IsTerminal(state).ShouldBeTrue();
        IncidentStateMachine.IsInFlight(state).ShouldBeFalse();
    }

    [Fact]
    public void An_incident_can_recover_from_any_non_recovered_state()
    {
        // A condition can stop at any point, including while it is being
        // investigated. Recovery must never be blocked by where the pipeline
        // happened to be.
        foreach (var state in Enum.GetValues<IncidentState>().Where(s => s != IncidentState.Recovered))
        {
            IncidentStateMachine.CanTransition(state, IncidentState.Recovered)
                .ShouldBeTrue($"{state} should be able to recover");
        }
    }

    [Fact]
    public void Inconclusive_is_a_terminal_outcome_not_a_failure()
    {
        // Returning "the evidence does not support a conclusion" is a correct
        // result. It must not be retried like an error, or the platform would
        // grind against an unanswerable incident forever.
        IncidentStateMachine.IsTerminal(IncidentState.Inconclusive).ShouldBeTrue();
        IncidentStateMachine.CanTransition(IncidentState.Inconclusive, IncidentState.Investigating).ShouldBeFalse();
    }
}
