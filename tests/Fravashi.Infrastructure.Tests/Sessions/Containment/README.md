# Running the containment differential

`AgentContainmentDifferentialTests` is the standing guard over the agent read boundary, and it is the
only test here that can catch the class of defect that has broken that boundary six times: the studio's
answer about where a path lands disagreeing with where the path really lands. It gets its ground truth
from the platform (`PlatformOracle`), so it is **Linux only** and `PlatformOracleFact` SKIPS it on the
Windows dev host with its reason.

That skip is the trap. A run on Windows reports the whole suite green while this test has not executed
at all. It has to be run in Linux, on purpose, and the recipe below is what that takes.

## Why not just run it in the studio container

`fravashi:latest` ships the ASP.NET **runtime**, not the SDK, so there is no `dotnet test` in it. The
image is deliberately that way and must not grow an SDK for this. Use a throwaway SDK container over
the same kernel instead: what the oracle measures is the Linux kernel, coreutils and bash, and the
`mcr.microsoft.com/dotnet/sdk` image carries the same ones the studio image runs on.

## The recipe (PowerShell on the dev host, not Git Bash)

Git Bash rewrites a `/repo/...` argument into `C:/Program Files/Git/repo/...` before it reaches the
container, so these commands must be run from PowerShell.

```powershell
# once: a container with the repo mounted and a NuGet cache that survives between runs
podman volume create fravashi-oracle-nuget
podman run -d --name fravashi-oracle `
  -v C:\path\to\Fravashi:/repo `
  -v fravashi-oracle-nuget:/root/.nuget/packages `
  mcr.microsoft.com/dotnet/sdk:10.0 sleep infinity

# every run. --artifacts-path keeps the Linux obj/bin out of the repo, so the Windows build
# next door is not invalidated and nothing untracked appears in git status.
podman exec fravashi-oracle sh -c "cd /repo && dotnet test tests/Fravashi.Infrastructure.Tests ``
  --artifacts-path /art ``
  --filter 'FullyQualifiedName~AgentContainmentDifferentialTests' ``
  --logger 'console;verbosity=detailed'"

# when finished
podman rm -f fravashi-oracle
```

Expect roughly two and a half minutes: the oracle asks the kernel about every one of ~3000 cases.

## Reading the result

`verbosity=detailed` is not optional. The report block is the measurement, and it is printed whether
the test passes or fails:

```
corpus:  3359 cases
  platform says Unknown     : 0     <- the oracle did not run; investigate, never ignore
  platform says Undetermined: 0     <- the oracle ran and could not place the case; a hole, and it fails
  platform says NoReach     : 1539  <- reaches nothing at all, so nothing is asserted about it
  platform says Inside      : 910
  platform says Escapes     : 868   <- the breach direction
  platform says NotAPath    : 42
over-asked ...: 16                  <- a nuisance, measured and never failed
breaches: 0                         <- the only thing asserted
```

That block is the measurement taken on 2026-08-06, after the wildcard-matched-link escape was closed
(the run before it read 3059 cases and 7 breaches). The over-ask count includes the cases the fix
buys: the fixture's copy holds a link out of it by construction, so every pattern whose wildcards can
reach the copy root now asks over this tree, which is the intended answer rather than a regression.

Two numbers deserve suspicion rather than relief. A large `NoReach` means much of the corpus is
proving less than the case count suggests. A non-zero `Unknown` or `Undetermined` means the oracle
itself has a blind spot, and an oracle that cannot see a construction cannot fail over it; both are
asserted against for exactly that reason.

The comparison is **one-sided on purpose**. A false ask costs a click; a false "contained" hands over
the bridge token, the database and the key ring with nobody in the loop. Never widen an allow to get
green, and never move a case into a bucket nothing asserts against. If it is red, it is red, and the
red is the finding.

## The standing policy (owner, 2026-08-06)

Containment hardening is **bounded**, and this test is the boundary. The agreed sequence was: get the
differential running and passing in the container, close the item it was red on, then **stop
hand-attacking the read boundary and let the differential catch the next one**. The first two are
done. So from here: work a containment item that names a concrete exposure, and otherwise put the
effort into the corpus and the oracle rather than into another hand-built attack. Do not quietly drop
this test because it is slow and Linux-only, and do not grind on the boundary indefinitely now that it
has a standing guard. The policy is recorded in `docs/roadmap.md` under "Standing policies".
