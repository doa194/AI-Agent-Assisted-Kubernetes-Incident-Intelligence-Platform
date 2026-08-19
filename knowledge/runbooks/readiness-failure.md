# Readiness probe failing

## Symptoms

A pod is Running but not Ready. Its restart count does NOT increase: the
process is alive and the container has not been killed. Kubernetes events show
Unhealthy entries reporting that the readiness probe failed.

Kubernetes removes the pod from its Service endpoints, so it receives no
traffic. If every replica of a workload is affected, callers see connection
failures because the Service has no endpoints at all.

For a background worker with no inbound traffic, the only visible effect may be
that the pod reports NotReady while continuing to run.

## How to confirm

Check readiness and restart count together. This is the distinguishing pair:
not Ready with zero restarts is a readiness problem, whereas not Ready with a
rising restart count is a crash loop and a different runbook applies.

Read the readiness endpoint's response. A well-built service reports why it
considers itself unready, which usually names the cause directly.

Check the Service endpoints. A workload whose pods are all unready has no
endpoints, which explains connection failures in its callers.

## Likely causes

A dependency the readiness check tests is unavailable, so the service correctly
reports that it cannot serve traffic.

The readiness probe's thresholds are too strict for the service's real start-up
time, marking a healthy pod unready during normal initialisation.

The application deliberately marked itself unready, for example while draining
or while a required background task has not finished.

Probe configuration pointing at the wrong path or port, which makes every pod
unready immediately after any deployment that introduced the mistake.

## What NOT to conclude

Do not describe this as a crash. The process never stopped, and reporting it as
a crash sends the investigation looking for an exception that does not exist.

Do not assume the workload is at fault. A readiness check that fails because a
dependency is down is the service behaving correctly.

## Recommended actions

Read what the readiness endpoint actually reports before changing anything.
Check whether the probe configuration or its thresholds changed recently, and
whether any dependency the check tests is itself unhealthy.
