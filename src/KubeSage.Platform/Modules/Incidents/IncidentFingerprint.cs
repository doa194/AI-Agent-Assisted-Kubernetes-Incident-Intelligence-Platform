using System.Security.Cryptography;
using System.Text;

namespace KubeSage.Platform.Modules.Incidents;

// Builds the identity of a recurring condition.
//
// This is the single most important piece of deduplication logic in the
// platform. The detection loop runs every minute; a five minute outage would
// therefore raise five identical incidents, each triggering its own
// investigation, each taking many minutes of a slow local model's time. The
// fingerprint is what collapses those into one incident with an occurrence
// count.
//
// The design tension is real and worth stating:
//
//   * too COARSE, and two genuinely different problems merge into one
//     incident, so the second one is never investigated;
//   * too FINE, and the same ongoing problem looks new on every pass,
//     which is exactly the flood the fingerprint exists to prevent.
//
// The chosen inputs are the ones that stay stable while a condition persists
// but differ between distinct conditions: the category, the namespace, the
// affected workloads, and - where the rule has one - the normalised error
// signature. Deliberately excluded are timestamps, pod names, counts and
// measured values, all of which change on every evaluation.
public static class IncidentFingerprint
{
    public static string Create(
        string category,
        string namespaceName,
        IEnumerable<string> affectedWorkloads,
        string? errorSignature = null)
    {
        // Workloads are sorted so that the same set discovered in a different
        // order produces the same fingerprint.
        var workloads = affectedWorkloads
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        builder.Append(category).Append('|');
        builder.Append(namespaceName).Append('|');
        builder.Append(string.Join(",", workloads));

        // Only included when the rule actually has a signature. Two different
        // errors from the same workload are different incidents; a rule based
        // purely on a metric threshold has no signature and does not need one.
        if (!string.IsNullOrWhiteSpace(errorSignature))
        {
            builder.Append('|').Append(errorSignature);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash)[..20];
    }
}
