# Pod crash loop

## Symptoms

A pod's restart count climbs steadily. Its status shows CrashLoopBackOff, and
Kubernetes events include repeated BackOff entries reporting that it is backing
off restarting the failed container. The container's last termination reason is
Error with a non-zero exit code.

Between restarts the service may briefly serve traffic normally, so the error
rate looks intermittent rather than total. With several replicas, some requests
succeed while others fail, which can look like a partial outage.

## How to confirm

Read the restart count and compare it with the previous observation. A high
absolute count means little on its own: a pod that crashed repeatedly last week
still carries those restarts. What matters is the increase over the current
window.

Check the container's last termination reason. Error with a non-zero exit code
means the process itself exited. OOMKilled means something different and is
covered by the out-of-memory runbook.

Read the application logs immediately before each termination. The last lines a
process writes before exiting usually name the cause directly.

## Likely causes

An unhandled exception on a code path reached shortly after start-up.

Missing or invalid configuration, so the process fails during initialisation.
This produces a very short time between start and exit, often only seconds.

A failing dependency the application treats as fatal at start-up rather than
retrying.

A liveness probe that is too aggressive, killing a healthy but slow-starting
container before it becomes ready.

## What NOT to conclude

Do not attribute a crash loop to whatever the service was calling unless the
logs actually show a dependency failure. A crashing process and a slow
dependency produce very different evidence, and restarts point at the process.

## Recommended actions

Read the logs from the previous container instance rather than the current one,
since the current instance may not have failed yet. Check whether the pod's
configuration or image changed recently. If the process exits within seconds of
starting, suspect configuration before suspecting logic.
