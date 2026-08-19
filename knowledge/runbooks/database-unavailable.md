# Database dependency unavailable

## Symptoms

Several services fail at once, and they are exactly the services that share a
database. Logs show connection failures rather than timeouts or error
responses: the connection is refused or cannot be established at all.

Error rates rise sharply and stay high, rather than fluctuating. Unlike a
latency problem, requests fail quickly, because a refused connection returns
immediately instead of waiting for a deadline.

Background workers stop making progress. A worker may keep running and appear
healthy to Kubernetes while doing no useful work, because its polling loop
fails and retries indefinitely.

## How to confirm

Check whether more than one independent service reports failures against the
same dependency. Several unrelated callers failing on one dependency is much
stronger evidence than any single caller's errors.

Check the database workload's own state: its deployment's desired and ready
replica counts, and whether its pods exist at all. A deployment scaled to zero
replicas, or pods that are not Ready, explains everything downstream.

Distinguish connection failures from timeouts in the logs. A connection refusal
means nothing is listening; a timeout means something is listening but slow.
They have different causes and different fixes.

## Likely causes

The database workload is not running: scaled to zero, evicted, failed to
schedule, or crash-looping itself.

Connection limits exhausted, so new connections are refused while existing ones
continue to work. This produces partial rather than total failure.

A network policy or service change that removed the route between caller and
database.

Credentials or configuration changed so authentication now fails. This usually
produces an authentication error rather than a connection refusal.

## What NOT to conclude

Do not name the services showing the errors as the root cause. They are all
victims of one shared dependency, and the fact that several of them fail
together is the clearest sign of that.

## Recommended actions

Confirm the database workload is running and Ready before investigating any
caller. Restore it first, then check whether dependent services recover on
their own or need restarting because they cached a broken connection pool.
