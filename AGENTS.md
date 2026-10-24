# Repository Agent Notes

For work on the WDT branch or WDT-specific AtlasTexture / AutoAtlasTexture extensions, read and follow:

- `Development~/WDT_DEVELOPMENT_POLICY.md`

In particular, do not treat audits as an opportunity to add AutoAtlasTexture-only conservative guards for theoretical corner cases that ordinary AtlasTexture already accepts. Any additional AutoAtlas-specific safety restriction must have a concrete automation-specific failure mode and justified real-world value.
