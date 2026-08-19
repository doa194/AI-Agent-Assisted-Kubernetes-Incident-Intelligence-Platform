using System.Text.Json;
using KubeSage.Platform.Modules.Persistence;

namespace KubeSage.Platform.UnitTests.Persistence;

// Guards a bug that was genuinely hit during development and was almost
// invisible.
//
// Work payloads are written from anonymous objects, which use C# property
// casing, and read back into records that use their own. With a case-sensitive
// reader the deserialisation "succeeded" but produced a default-valued object,
// so the dispatcher found no incident id, quietly marked the item complete,
// and moved on. Every symptom pointed at a healthy queue draining normally:
// items went to Completed, no errors were logged, and no investigation ever
// ran.
public sealed class WorkItemPayloadTests
{
    [Fact]
    public void A_payload_written_with_camel_case_reads_back_into_a_pascal_case_record()
    {
        // Arrange - exactly how the detection engine writes it.
        var incidentId = Guid.CreateVersion7();
        var written = JsonSerializer.Serialize(new
        {
            incidentId,
            trigger = "detection",
            category = "dependency_latency"
        });

        var item = new WorkItem
        {
            Id = Guid.CreateVersion7(),
            Kind = WorkKind.Investigation,
            DedupKey = incidentId.ToString(),
            Payload = written,
            Attempt = 1,
            MaxAttempts = 4
        };

        // Act
        var payload = item.PayloadAs<TestPayload>();

        // Assert
        payload.ShouldNotBeNull();
        payload.IncidentId.ShouldBe(incidentId);
        payload.Trigger.ShouldBe("detection");
        payload.Category.ShouldBe("dependency_latency");
    }

    [Fact]
    public void Mixed_casing_from_an_anonymous_object_still_binds()
    {
        // Anonymous objects inherit the casing of whatever expression created
        // them, so a payload can legitimately mix the two styles.
        var incidentId = Guid.CreateVersion7();
        var item = new WorkItem
        {
            Id = Guid.CreateVersion7(),
            Kind = WorkKind.Investigation,
            DedupKey = "x",
            Payload = $$"""{"incidentId":"{{incidentId}}","Trigger":"detection","Category":"out_of_memory"}""",
            Attempt = 1,
            MaxAttempts = 4
        };

        var payload = item.PayloadAs<TestPayload>();

        payload.ShouldNotBeNull();
        payload.IncidentId.ShouldBe(incidentId);
        payload.Category.ShouldBe("out_of_memory");
    }

    [Fact]
    public void An_unrelated_payload_yields_a_default_incident_id_the_caller_must_reject()
    {
        // Documents the shape the dispatcher has to defend against: valid JSON
        // that simply does not contain what was expected.
        var item = new WorkItem
        {
            Id = Guid.CreateVersion7(),
            Kind = WorkKind.Investigation,
            DedupKey = "x",
            Payload = """{"somethingElse":true}""",
            Attempt = 1,
            MaxAttempts = 4
        };

        var payload = item.PayloadAs<TestPayload>();

        payload.ShouldNotBeNull();
        payload.IncidentId.ShouldBe(Guid.Empty);
    }

    private sealed record TestPayload(Guid IncidentId, string? Trigger, string? Category);
}
