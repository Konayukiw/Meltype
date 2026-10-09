# Composition performance check

```sh
python3 tools/benchmark-composition.py /path/to/baseline-checkout \
  --output /path/to/results
```

Use the original v1.0.0 commit `467255bfe3e36b803a3fd3f5a1480fe35d5058c9`
for the PR's baseline. The command builds both versions with the same C# harness,
SDK and NativeAOT settings, then alternates which version runs first by scenario.
Each scenario uses 64, 128 and 256 keys, three warmups and 15 measured repetitions.
Use an otherwise idle machine. `--common-dictionaries` embeds the current
repository's dictionaries in both versions as a control for dictionary changes.
`--jit` runs release JIT code instead and disables tiered compilation.

The timer covers `HandleKey`, result JSON serialization and Enter commit. It
excludes dictionary loading, startup and session creation. Both versions have the built-in spelling checker and the same empty language
memory enabled. The converter is a stub returning no candidates. It records total time, thread allocations, per-key
p50/p95/max and the committed output SHA-256. Non-URL output must match the
baseline. The URL intentionally changes to preserve the raw token.

`report.json` contains raw samples, medians, ratios, dictionary/program hashes,
SDK and machine information. The command fails if the current median total time
exceeds 50% of the baseline or unchanged-output checks fail. This is a local
synthetic performance gate; it does not measure IMK/IBus/Windows GUI latency or
conversion-engine time. NativeAOT build diagnostics are saved separately.
