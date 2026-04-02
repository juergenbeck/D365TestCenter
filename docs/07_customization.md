# Customization Guide

## Demo Metadata

The demo mode includes metadata from a real Dynamics 365 environment with custom entities
and attributes (prefixed `markant_`). This demonstrates that the Test Center works with
any custom entity schema.

To customize the demo metadata for your organization:

1. Open `d365testcenter.html`
2. Find the `_DEMO_METADATA` section (~line 1680)
3. Replace the attribute arrays with your own entity metadata
4. The metadata format follows the Dataverse EntityMetadata API response structure

In live mode (inside Dynamics 365), metadata is always loaded dynamically from the
current environment. The demo metadata is only used when the app runs outside Dynamics 365.

## Governance Polling

The async plugin polling (`waitForAsync`) is configured to query specific logging entities
after ContactSource operations. This behavior is customizable through the test case JSON:

- Set `"waitForAsync": true` on any step to enable polling
- Set `"waitForAsync": false` to disable it
- The polling entity, interval, and timeout are configurable in the `CONFIG` block

To adapt the polling for your own plugin chains, modify the `_pollForGovernanceCompletion`
function in the HTML source.

## CONFIG Block

All environment-specific settings are in the `CONFIG` object at the top of the HTML file:

- `version`: Current version number
- `publisherPrefix`: Entity prefix (default: `itt`)
- `optionSetPrefix`: OptionSet value prefix
- `pollingIntervalMs`: Async polling interval (default: 2000)
- `maxPollingDurationMs`: Polling timeout (default: 120000)
