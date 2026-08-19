# Container terminated for exceeding its memory limit

## Symptoms

A container's last termination reason is OOMKilled and its exit code is 137.
The restart count increases. The pod may cycle between Running and
CrashLoopBackOff as it is killed shortly after each start.

Memory use climbs toward the container's configured limit and the container is
terminated on reaching it. The kill is abrupt: there is no graceful shutdown and
usually no error in the application's own logs, because the kernel stopped the
process without warning it.

## How to confirm

Check the container's last termination reason. OOMKilled with exit code 137 is
conclusive; no other failure produces that combination.

Compare working-set memory against the container's memory limit. The two
converging just before termination confirms the limit was the binding
constraint rather than a host-level shortage.

The absence of an application error at the moment of death is itself
informative. A process that logged an exception and exited is a crash, not an
out-of-memory kill.

## Likely causes

The memory limit is set lower than the application genuinely needs, often after
a limit was copied from a smaller service.

A memory leak, where usage grows steadily over hours rather than spiking.

An unbounded in-memory buffer or cache that grows with traffic volume.

A large allocation triggered by an unusually large request or payload.

## What NOT to conclude

Do not report this as an application crash without qualification. The
distinction matters for the fix: a crash needs a code change, whereas an
out-of-memory kill is often resolved by adjusting a limit.

Do not assume the node was under memory pressure. A container hitting its own
cgroup limit is killed regardless of how much memory the node has spare.

## Recommended actions

Compare the limit against observed usage over a longer window. Distinguish a
steady climb, which suggests a leak, from a sharp spike, which suggests a single
large allocation. Check whether the limit or the workload's traffic changed
recently.
