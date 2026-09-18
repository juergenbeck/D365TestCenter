# Customization Guide

## Demo Metadata

The demo mode includes sample metadata with custom entities and attributes.
This demonstrates that the Test Center works with
any custom entity schema.

To customize the demo metadata for your organization:

1. Open `d365testcenter.html`
2. Find the `_sharedIttMeta` object (entities, attributes, optionsets)
3. Replace the attribute arrays with your own entity metadata
4. The metadata format follows the Dataverse EntityMetadata API response structure

In live mode (inside Dynamics 365), metadata is always loaded dynamically from the
current environment. The demo metadata is only used when the app runs outside Dynamics 365.

## Waiting for Asynchronous Plugins

Tests run in the C# engine (CLI, custom API or trigger plugin), not in the HTML page. The engine does not
poll implicitly. A test waits for asynchronous effects with explicit steps: `WaitForRecord`,
`WaitForFieldValue`, `WaitForNotExists`, `WaitForAsyncCompletion` (CLI only) or a fixed `Wait`.
The former `waitForAsync` step flag has no effect in the engine; the pre-run validation reports it as
`STEP_KEY_UNKNOWN`.

## CONFIG Block

The `CONFIG` object at the top of `webresource/d365testcenter.html` holds the schema names the UI uses:

- `version`, `buildDate`: version shown in the UI
- `prefix`: publisher prefix without underscore (default: `jbe`)
- `optionSetBase`: base of the option set values (default: `105710000`)
- `entities`, `fields`: logical names of the tables and columns
- `optionSets`: numeric values for status, chunk status, outcome and category
- `actions.runTests`: name of the custom API that starts a run
- `storyLinkPattern`: link template for user story keys (empty disables links)
- `governance`: optional editor helpers (field suggestions, source systems); shipped empty

`CONFIG` only drives the UI and the demo mode. It does not change how the engine runs a test.
