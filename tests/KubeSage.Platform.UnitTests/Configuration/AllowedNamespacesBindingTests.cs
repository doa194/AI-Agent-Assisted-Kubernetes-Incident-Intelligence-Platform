using KubeSage.Platform.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace KubeSage.Platform.UnitTests.Configuration;

// These tests protect the one direction of the namespace allow-list that
// actually matters for security: making it SMALLER.
//
// The allow-list decides which namespaces the platform may read at all, so a
// change that widens it is visible and intentional, while a change that fails
// to narrow it is silent. An operator who removes a namespace and sees the
// platform start cleanly has every reason to believe the namespace is now off
// limits.
//
// This was a real defect. The list was declared twice - once as a C# property
// initialiser and once in appsettings.json - and the configuration binder ADDS
// to an array that already holds values rather than replacing it. The result
// was an allow-list containing every entry twice that could be widened but
// never narrowed.
public sealed class AllowedNamespacesBindingTests
{
    [Fact]
    public void A_narrower_configured_allow_list_actually_narrows_it()
    {
        // Arrange - configuration asking for ONE namespace, where the platform
        // normally runs with two.
        var options = Bind(new Dictionary<string, string?>
        {
            ["KubeSage:Kubernetes:AllowedNamespaces:0"] = "kubesage-demo"
        });

        // Act / Assert - the removed namespace must really be gone.
        options.Kubernetes.AllowedNamespaces.ShouldBe(["kubesage-demo"]);
    }

    [Fact]
    public void Binding_does_not_duplicate_entries()
    {
        // The original symptom, and the cheapest thing to assert: a list bound
        // once should not come back twice as long.
        var options = Bind(new Dictionary<string, string?>
        {
            ["KubeSage:Kubernetes:AllowedNamespaces:0"] = "kubesage-demo",
            ["KubeSage:Kubernetes:AllowedNamespaces:1"] = "kubesage-observability"
        });

        options.Kubernetes.AllowedNamespaces.ShouldBe(["kubesage-demo", "kubesage-observability"]);
    }

    [Fact]
    public void An_absent_allow_list_stops_the_platform_rather_than_defaulting()
    {
        // Because there is no built-in list, forgetting to configure one must
        // be a start-up failure. Falling back to a default would reintroduce
        // exactly the problem these tests exist to prevent, and falling back to
        // "allow everything" would be far worse.
        var options = Bind(new Dictionary<string, string?>());

        var result = new KubeSageOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("AllowedNamespaces"));
    }

    // Binds a real configuration tree the same way Program.cs does, because
    // the defect lived in the binder rather than in any code we wrote.
    private static KubeSageOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = new KubeSageOptions();
        configuration.GetSection(KubeSageOptions.SectionName).Bind(options);
        return options;
    }
}
