# Issue 53: PR #56 reconciliation after #55

Rebase baseline: reviewed main `f13256a440a72a87cf69d0262ef2aceb1c66423d` (#55).
The original #56 head was `f17f07c60042a64e8260e8dd31892ead72e0684f`.

## Resolution

The first four implementation commits duplicate work already delivered by #55.
Drop them while rebasing; retain main's controller gate, native capability 2048,
independent bounded preparation/presentation loops, worker cache/render preparation,
`PreviewPresentation` ownership, `PreviewWorkMetrics` and array-based WPF WritePixels.
No second telemetry pipeline or prepared-pixel API is introduced.

Keep main's exact ABI: `kn_playback_present` takes two ready ticks (no ready-count
argument); `kn_rgba_to_bgra` takes destination before source. The parallel #56
implementations used different signatures for the same entry points and must not
be mixed with the reviewed DLL/adapter.

Retain the useful #56 teardown change: pause the physical device under the
controller gate before joining cancelled producers. A failing video producer can
leave audio preparation blocked; queued audio must not continue while joining it.
Decode, pixel preparation and producer joins remain outside the gate.

## Retained evidence and regression coverage

- Existing #55 tests continue to protect held decode presentation, bounded slots,
  latest-intent quality changes, failure cleanup and callers without a host context.
- Additional tests cover newest-due coalescing with a held subsequent decode,
  quality replacement, native BGRA alpha/source ownership and buffer validation.
- A failure test holds an uncancellable audio request and fails video preparation;
  audio must pause before the join completes and disposal must wait for that join.
- Windows viewer smoke checks Full/Half/Quarter dimensions and actual prepared BGRA
  bytes against WPF pixels, recording the existing presentation/conversion metrics.
- `PreviewPixelBench` uses main's `NativeComposition.RgbaToBgra` API. Run:

```bash
dotnet run --project test/Kachinco.RuntimeBench -c Release -- --pixels pixel-metrics.json
```

`issue-53-pixel-preparation.json` is historical pixel-only evidence from the original
#56 implementation, not measurements of this rebased head. Both paths allocate one
output. Means improved there, but Full p95 did not consistently improve. It measures
neither WPF copies nor physical playback. WPF still copies into its bitmap.

Current validation is recorded in the PR description after the rebased-head checks.
Keep #53 open until `staging/windows-issue53.md` physical Full/Half/Quarter, audible
sync, dropped-frame/underrun and working-set acceptance is complete.
