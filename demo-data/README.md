# Portable staged demo data

This folder contains **two staged inspection examples**, their saved robot JPEGs,
and Nemotron observations. They are illustrative demonstrations, not verified
property losses, appraisals, or insurance claims. Both records still need human
review. Glossy flooring alone does not establish water or flooding; the first
record explicitly identifies that uncertainty in the original model response.

`manifest.json` names each JSON record and JPEG and includes its SHA-256 hash.
Only selected demo evidence is included. The raw `.spacetime/` directory, database
service identity, API keys, credentials, voice transcripts, general audit logs,
and live control state are excluded. One staged photo shows shoes and a person's
lower body. This snapshot is being shared in the project's private GitHub repo.

## Restore on another laptop

1. Install the existing project dependencies and configure the local services as
   described in `server/README.md` (`python3 scripts/local-stack.py start`).
2. From the repository root, run:

   ```sh
   npm --prefix agent run demo:restore
   ```

3. Open `http://localhost:8090/damage` to see the examples and build reports.

The restore verifies every photo before writing, uses the new laptop's own service
identity, and preserves an existing review on repeat imports. It never enables
robot motion or starts a survey. These are evidence fixtures, not a complete backup
of operational history or a copy of the source laptop's runtime database.

## Refresh the selected export

Pass explicit demo incident IDs; ordinary production records are rejected:

```sh
npm --prefix agent run demo:export -- INCIDENT_ID_1 INCIDENT_ID_2
```

Inspect the selected photos and record text before committing a refreshed export.
