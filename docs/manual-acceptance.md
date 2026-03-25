# CDriveMaster Manual Acceptance

## 1. Rule Load Failure Isolation
- Start the app with at least one malformed rule file under `Rules/`.
- Verify the app still opens and other valid rule providers are available.
- Trigger a scan from a valid provider and confirm scan succeeds.
- Expected: invalid rule is skipped, app does not crash, and valid providers work normally.

## 2. SafeAuto Real Apply + Audit
- Open a provider that supports `SafeAuto` and run scan.
- Execute real cleanup flow and confirm operation in dialog.
- After completion, open logs folder and verify an audit JSON file is generated in `Logs/`.
- Expected: execution summary is shown, and audit file contains non-skipped executed buckets.

## 3. SafeWithPreview Selective Apply (High Volume)
- Use a provider that supports preview mode and produces many entries.
- In preview dialog, select part of the items and run apply.
- Validate only selected entries are executed.
- Expected: UI remains responsive during preview, and result summary reflects selected subset.

## 4. System Maintenance Guarded Cleanup
- Open System Maintenance page and run analysis.
- Verify cleanup button is disabled before confirmation.
- Check consent box, run cleanup, and wait for completion.
- Expected: cleanup requires analysis + consent; after run, guard is locked again and result section is visible.

## 5. System Maintenance Re-Analyze + Logging
- On System Maintenance page, click audit log button and confirm `Logs/` opens.
- After one cleanup run, verify a `Audit_SystemMaintenance_*.json` file exists.
- Click re-analyze and verify previous result is cleared before new analysis values show.
- Expected: no stale metrics, cleanup state resets, and logs are accessible.
