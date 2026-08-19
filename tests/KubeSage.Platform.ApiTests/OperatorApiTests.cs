using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace KubeSage.Platform.ApiTests;

// Contract tests for the endpoints an operator and the automation depend on.
//
// The API is the only way anything outside the platform sees what it found, so
// these protect its shape: status codes, field names, and the behaviour when
// dependencies are unavailable.
[Collection(ApiCollection.Name)]
public sealed class OperatorApiTests
{
    private readonly KubeSageApiFactory _factory;

    public OperatorApiTests(KubeSageApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Liveness_stays_healthy_even_though_every_dependency_is_down()
    {
        // The whole reason liveness and readiness are separate. Telemetry and
        // the model are unreachable in this fixture, and the process is still
        // perfectly alive - restarting it would help nothing and would destroy
        // any in-flight work.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_reports_healthy_when_only_the_database_is_reachable()
    {
        // Readiness means "can record what it finds". The database is up, so
        // the platform is ready even with Loki, Prometheus and Ollama down:
        // it can still detect nothing, serve stored incidents, and queue work.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("status").GetString().ShouldBe("Healthy");
        body.GetProperty("checks").EnumerateArray()
            .ShouldContain(check => check.GetProperty("name").GetString() == "database");
    }

    [Fact]
    public async Task Incidents_returns_an_empty_list_rather_than_failing_when_there_are_none()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/incidents", TestContext.Current.CancellationToken);
        var incidents = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        incidents.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task An_unknown_incident_state_is_rejected_with_the_allowed_values()
    {
        // The error names what IS allowed. An operator filtering by a state
        // that does not exist should not have to read source code.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/incidents?state=Nonsense", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().ShouldBe("unknown_state");
        body.GetProperty("allowed").EnumerateArray().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_missing_incident_returns_not_found_rather_than_an_error()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(
            $"/incidents/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Latest_report_returns_not_found_before_any_investigation_has_run()
    {
        // Distinct from an error: "nothing yet" is a normal state for a fresh
        // install, and the automation relies on being able to tell them apart.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/reports/latest", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        body.GetProperty("error").GetString().ShouldBe("no_reports_yet");
    }

    [Fact]
    public async Task Cluster_status_reports_queue_depth_so_a_backlog_is_visible()
    {
        // This is how an operator sees work piling up behind a slow or absent
        // model, which is the most likely operational problem on this hardware.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/cluster/status", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.TryGetProperty("openIncidents", out _).ShouldBeTrue();
        body.TryGetProperty("workQueue", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Evidence_collection_degrades_instead_of_failing_when_telemetry_is_down()
    {
        // The behaviour the project requires: an unreachable telemetry source
        // produces a PARTIAL result that says which sources were missing, not
        // a 500 and not a silent empty success that looks like "all clear".
        var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/evidence?workload=order-api&windowMinutes=5", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        body.GetProperty("isComplete").GetBoolean()
            .ShouldBeFalse("with every telemetry source unreachable the bundle cannot be complete");

        body.GetProperty("unavailableSources").EnumerateArray()
            .ShouldNotBeEmpty("the response must name what could not be reached");
    }

    [Fact]
    public async Task An_invalid_workload_name_is_rejected_rather_than_passed_to_a_query()
    {
        // Input validation at the API boundary. A workload name carrying LogQL
        // syntax must never reach the query builder.
        var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/evidence/log-signatures?workload=order-api%22%7D%20%7C%3D%20%22secret",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("error").GetString().ShouldBe("query_rejected");
    }

    [Fact]
    public async Task A_namespace_outside_the_allow_list_is_rejected()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/evidence/kubernetes?ns=kube-system", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("not in the allowed list");
    }

    [Fact]
    public async Task Running_analysis_with_telemetry_down_reports_zero_incidents_rather_than_failing()
    {
        // Detection is required to keep working when telemetry is unavailable.
        // It cannot see anything, so it must find nothing - and must not throw,
        // because the loop that calls it has to survive.
        var client = _factory.CreateClient();

        var response = await client.PostAsync(
            "/analysis/run", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("incidentsCreated").GetInt32().ShouldBe(0);
    }
}
