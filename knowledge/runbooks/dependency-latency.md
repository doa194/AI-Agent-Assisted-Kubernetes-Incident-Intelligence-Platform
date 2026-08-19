# Downstream dependency latency

## Symptoms

A service returns HTTP 503 or 504 for a share of its requests while its own
pods stay Running and Ready with no restarts. Request duration at the 95th
percentile rises to roughly the caller's configured timeout and then flattens
there, because every affected request fails at the same deadline rather than
taking however long the dependency needs.

Errors appear in the caller, not in the service that is actually slow. The slow
dependency itself often looks healthy: it is answering, just late.

## How to confirm

Compare the 95th percentile call duration for each dependency of the failing
service. A latency incident shows one dependency far slower than the others
while the rest are normal. If a service calls both a payment provider and a
database, and the database is answering in single-digit milliseconds while the
provider takes seconds, the provider is the problem.

Check that no pod restarted. Restarts, CrashLoopBackOff or OOMKilled point at a
different problem entirely and mean this runbook does not apply.

Look for timeout errors that name the dependency. A log line reporting that a
call to a named dependency timed out after almost exactly the configured
timeout is the strongest single indicator.

## Likely causes

The dependency is genuinely slower: resource starvation, a slow query, garbage
collection pauses, or an upstream provider of its own having trouble.

The caller's timeout is too short for normal variation, so ordinary slow
responses are being turned into errors.

Connection pool exhaustion in the caller, where requests queue waiting for a
connection and time out before they are ever sent.

## What NOT to conclude

Do not report the service showing the errors as the root cause. It is behaving
correctly: it called something, that something was too slow, and it gave up at
its deadline. Naming it as the cause sends the fix to the wrong team.

Do not treat a rising error rate on its own as evidence of a crash. Check pod
state before concluding anything about the workload's health.

## Recommended actions

Investigate the slow dependency's own resource use and its downstream calls.
Consider whether the caller's timeout is appropriate. Check whether the
dependency recently changed - a deployment, a configuration change, or a change
in the volume of traffic it receives.
